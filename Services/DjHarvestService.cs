using System.IO;
using System.Threading;
using System.Windows.Threading;
using ManagedBass;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>One currently-connected harvester, for UI display. Dead harvesters are removed from
/// the active pool (and replaced from reserve) the instant they die, so there's no "dead" state
/// to carry here — only genuinely live connections are ever in a snapshot.</summary>
public sealed record HarvesterInfo(string Label, int TitlesSeen);

/// <summary>Snapshot of the harvest pool for status-line reporting.</summary>
public sealed record HarvestStatus(int ActiveHarvesters, int Kept, int Rejected, IReadOnlyList<HarvesterInfo> Harvesters);

/// <summary>
/// Phases of <see cref="DjHarvestService.StartAsync"/>, reported so the UI can show real staged
/// progress during a start-up that can run 30s+ (UX audit). Deliberately an enum, not display
/// copy — the wording belongs to the view model.
/// </summary>
public enum HarvestStartStage
{
    /// <summary>Interpreting the prompt and sourcing/ranking candidate stations — the long one.</summary>
    FindingStations,

    /// <summary>Stations chosen; harvesters are connecting to them.</summary>
    Connecting
}

/// <summary>
/// Owns the DJ-mode harvest pool (DJ-MODE-SPEC-HARVEST.md §6.1): sources stations for a prompt,
/// runs N <see cref="StreamHarvester"/>s plus a cold reserve on a dedicated background thread,
/// replaces dead harvesters from the reserve, runs each completed segment through
/// <see cref="SegmentQualityChecker"/>, indexes kept ones via
/// <see cref="ISongLibraryService.AddAndEnrich"/>, and enforces a simple folder-size-cap
/// eviction. Playback knows nothing about any of this — <see cref="DjQueueService"/> is the only
/// consumer of <see cref="SegmentIndexed"/>.
///
/// Threading: owns a dedicated background thread that calls <c>Bass.Init(0)</c> (the "no sound"
/// device) once and runs its own <see cref="Dispatcher"/> — isolated from the UI thread's real
/// output device by BASS's per-calling-thread device selection (see
/// <c>RadioEngine._realDeviceIndex</c>'s field comment for the full explanation). All
/// harvester lifecycle calls happen on that thread. <see cref="SegmentIndexed"/> and
/// <see cref="StatusChanged"/> fire on a background thread (never the UI thread) — subscribers
/// must marshal explicitly, same discipline <see cref="DjQueueService"/> follows.
/// </summary>
public sealed class DjHarvestService : IDisposable, IDjHarvestSource
{
    private readonly IStationSearchService _searchService;
    private readonly IPromptInterpreter _interpreter;
    private readonly IAgenticSearchService _agenticSearch;
    private readonly ISearchRanker _ranker;
    private readonly IEnrichmentService _enrichment;
    private readonly ISongLibraryService _songLibrary;
    private readonly SegmentQualityChecker _qc;
    private readonly string _harvestDir;
    private readonly string _scratchDir;
    private readonly string _rejectedDir;
    private readonly int _harvesterCount;
    private readonly int _reserveCount;
    private readonly double _offsetSeconds;
    private readonly long _maxHarvestCacheBytes;
    private readonly long _maxRejectedCacheBytes;

    // Mutated only on the harvest dispatcher thread (StartHarvester/OnHarvesterDied/Stop), but
    // read from OnSegmentCompleted's background Task.Run continuation too (for RaiseStatus's
    // per-harvester snapshot) — a plain List isn't safe for that, so every access is locked.
    private readonly object _activeLock = new();
    private readonly List<StreamHarvester> _active = new();
    private Queue<Station> _reserve = new();
    private Thread? _harvestThread;
    private Dispatcher? _harvestDispatcher;
    private DispatcherTimer? _watchdog;

    /// <summary>How long a harvester may serve no ICY metadata before it's assumed not to
    /// support it. Generous: a station that only announces on change, connected mid-song, still
    /// gets a full long track to prove itself.</summary>
    private static readonly TimeSpan MetadataGrace = TimeSpan.FromMinutes(6);
    private volatile bool _running;
    private int _kept;
    private int _rejected;

    /// <summary>Raised (background thread) for each harvested song kept after QC.</summary>
    public event EventHandler<SavedSong>? SegmentIndexed;

    /// <summary>Raised (background thread) whenever the pool/kept/rejected counts change.</summary>
    public event EventHandler<HarvestStatus>? StatusChanged;

    public DjHarvestService(
        IStationSearchService searchService,
        IPromptInterpreter interpreter,
        IAgenticSearchService agenticSearch,
        ISearchRanker ranker,
        IEnrichmentService enrichment,
        ISongLibraryService songLibrary,
        string harvestDir,
        int harvesterCount = 4,
        int reserveCount = 15,
        double offsetSeconds = 0.0,
        double rejectBelow = 0.30,
        bool trimEdges = true,
        long maxHarvestCacheBytes = 500L * 1024 * 1024,
        long maxRejectedCacheBytes = 250L * 1024 * 1024)
    {
        _searchService = searchService;
        _interpreter = interpreter;
        _agenticSearch = agenticSearch;
        _ranker = ranker;
        _enrichment = enrichment;
        _songLibrary = songLibrary;
        _harvestDir = harvestDir;
        _scratchDir = Path.Combine(harvestDir, "_scratch");
        _rejectedDir = Path.Combine(harvestDir, "_rejected");
        _harvesterCount = harvesterCount;
        _reserveCount = reserveCount;
        _offsetSeconds = offsetSeconds;
        _maxHarvestCacheBytes = maxHarvestCacheBytes;
        _maxRejectedCacheBytes = maxRejectedCacheBytes;
        _qc = new SegmentQualityChecker(rejectBelow, trimEdges);
    }

    public bool IsRunning => _running;

    /// <summary>
    /// Optional per-session diagnostics (see <see cref="DjSessionLog"/>). Set by the view model
    /// around a session so harvester tuning can be measured rather than guessed; null disables
    /// it entirely and nothing in the harvest path depends on it.
    /// </summary>
    public DjSessionLog? SessionLog { get; set; }

    /// <summary>The single best-ranked station from the most recent <see cref="StartAsync"/> —
    /// the one already-connected harvester most worth playing live during warm-up (see
    /// <c>MainViewModel.BeginDjWarmupLivePlayback</c>). Null until a session has started.</summary>
    public Station? TopStation { get; private set; }

    /// <summary>
    /// Sources stations for the prompt and starts the harvest thread. Returns once the first
    /// batch of harvesters has been told to connect (not once any song has actually arrived —
    /// that's <see cref="SegmentIndexed"/>'s job). An empty candidate list (dead API key, no
    /// results at all) leaves the service not running — the caller surfaces "no stations found."
    /// <paramref name="progress"/> reports the start-up phases so the UI can show real staged
    /// progress rather than an undifferentiated spinner.
    /// </summary>
    public async Task StartAsync(string prompt, IProgress<HarvestStartStage>? progress = null,
        CancellationToken ct = default)
    {
        Stop();
        Directory.CreateDirectory(_harvestDir);
        Directory.CreateDirectory(_scratchDir);

        progress?.Report(HarvestStartStage.FindingStations);
        var stations = await SourceStationsAsync(prompt, _harvesterCount + _reserveCount, ct).ConfigureAwait(false);
        if (stations.Count == 0)
        {
            TopStation = null;
            return;
        }

        progress?.Report(HarvestStartStage.Connecting);
        var hot = stations.Take(_harvesterCount).ToList();
        _reserve = new Queue<Station>(stations.Skip(_harvesterCount));
        TopStation = hot[0]; // stations are already best-first out of RankRelevantAsync
        _kept = 0;
        _rejected = 0;
        _running = true;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harvestThread = new Thread(() => HarvestThreadMain(hot, ready)) { IsBackground = true };
        _harvestThread.SetApartmentState(ApartmentState.STA);
        _harvestThread.Start();
        await ready.Task.ConfigureAwait(false);
    }

    /// <summary>Stops every harvester, tears down the harvest thread's BASS device, and joins
    /// the thread. Safe to call when not running.</summary>
    public void Stop()
    {
        if (!_running)
            return;
        _running = false;

        // Finish here rather than only in the view model: closing the window disposes this
        // service directly, and a session log without its summary is the one case where the
        // measurement is lost exactly when the session was long enough to be interesting.
        SessionLog?.Finish();

        var dispatcher = _harvestDispatcher;
        if (dispatcher is not null)
        {
            dispatcher.BeginInvoke(() =>
            {
                _watchdog?.Stop();
                _watchdog = null;
                lock (_activeLock)
                {
                    foreach (var h in _active)
                    {
                        h.SegmentCompleted -= OnSegmentCompleted;
                        h.Died -= OnHarvesterDied;
                        h.Dispose();
                    }
                    _active.Clear();
                }
                Bass.Free(); // frees only THIS (harvest) thread's device (0) — per-thread, confirmed via ManagedBass docs
                dispatcher.InvokeShutdown();
            });
        }
        _harvestThread?.Join(TimeSpan.FromSeconds(5));
        _harvestThread = null;
        _harvestDispatcher = null;
        _reserve = new Queue<Station>();
    }

    public void Dispose() => Stop();

    private void HarvestThreadMain(List<Station> hot, TaskCompletionSource ready)
    {
        if (!Bass.Init(0) && Bass.LastError != Errors.Already)
        {
            ready.TrySetException(new InvalidOperationException($"DJ harvest BASS init failed: {Bass.LastError}"));
            return;
        }
        _harvestDispatcher = Dispatcher.CurrentDispatcher;

        foreach (var station in hot)
            StartHarvester(station);

        // Watchdog for metadata-silent stations. Lives on this thread so its retire/replace path
        // is the same single-threaded one every other harvester lifecycle call uses.
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _watchdog.Tick += (_, _) => RetireSilentHarvesters();
        _watchdog.Start();

        ready.TrySetResult();
        Dispatcher.Run(); // returns once Stop()'s dispatched action calls InvokeShutdown
    }

    private void StartHarvester(Station station)
    {
        var harvester = new StreamHarvester(station.Name, station.Url, station.Format,
            _scratchDir, _offsetSeconds, _harvestDispatcher!);
        harvester.SegmentCompleted += OnSegmentCompleted;
        harvester.Died += OnHarvesterDied;
        lock (_activeLock) _active.Add(harvester);
        SessionLog?.HarvesterStarted(station.Name);
        if (!harvester.Start())
        {
            AppLog.Warn($"[Dj] harvester failed to connect: {station.Name} ({station.Url})");
            OnHarvesterDied(harvester, EventArgs.Empty);
        }
        RaiseStatus();
    }

    private void OnHarvesterDied(object? sender, EventArgs e) =>
        Retire((StreamHarvester)sender!, "died");

    /// <summary>Drops a harvester and promotes a reserve station in its place. Runs on the
    /// harvest dispatcher thread (both callers are on it).</summary>
    private void Retire(StreamHarvester harvester, string reason)
    {
        harvester.SegmentCompleted -= OnSegmentCompleted;
        harvester.Died -= OnHarvesterDied;
        lock (_activeLock)
        {
            if (!_active.Remove(harvester))
                return; // already retired
        }
        SessionLog?.HarvesterDied(harvester.Label, reason);
        harvester.Dispose();

        if (_running && _reserve.Count > 0)
            StartHarvester(_reserve.Dequeue());
        else
            RaiseStatus();
    }

    /// <summary>
    /// Drops harvesters that have served no ICY metadata at all. Without title changes there are
    /// no song boundaries, so such a station can never yield a segment however long it runs — it
    /// just burns bandwidth and a pool slot. Measured sessions had one in four sourced stations
    /// like this, silently producing nothing for half an hour.
    ///
    /// Only zero titles counts. A station that has shown even one is alive and merely slow, and
    /// the grace period is generous enough that a metadata-bearing station connected mid-song
    /// will have announced the next one.
    /// </summary>
    private void RetireSilentHarvesters()
    {
        if (!_running)
            return;

        List<StreamHarvester> silent;
        lock (_activeLock)
            silent = _active
                .Where(h => h.TitlesSeen == 0 && DateTime.UtcNow - h.ConnectedAt > MetadataGrace)
                .ToList();

        foreach (var harvester in silent)
        {
            AppLog.Info($"[Dj] dropping {harvester.Label}: no ICY metadata after "
                        + $"{MetadataGrace.TotalMinutes:0} min — it can never produce a segment");
            Retire(harvester, "no metadata");
        }
    }

    // Runs the QC/index/evict pipeline for one completed segment. Not marshaled to the harvest
    // dispatcher — none of this touches BASS/the harvester pool, only file I/O and the
    // (independently thread-safe) library store, so it can run wherever the QC task lands.
    private void OnSegmentCompleted(object? sender, CompletedSegment seg)
    {
        var label = ((StreamHarvester)sender!).Label;
        var src = Path.Combine(_scratchDir, seg.FileName);

        _ = Task.Run(async () =>
        {
            var ext = Path.GetExtension(seg.FileName);
            var baseName = Sanitize($"{label} · {seg.Artist} - {seg.Title}");
            var dest = Path.Combine(_harvestDir, baseName + ext);
            for (var n = 2; File.Exists(dest); n++)
                dest = Path.Combine(_harvestDir, $"{baseName} ({n}){ext}");

            var verdict = await _qc.EvaluateAsync(src, dest).ConfigureAwait(false);

            // Keep rejects for inspection BEFORE the scratch copy goes — they're the only
            // evidence that a rejection was wrong, and until now they were deleted unseen.
            if (!verdict.Kept)
                QuarantineRejected(src, label, seg, verdict);

            StreamRecorder.TryDelete(src); // scratch copy no longer needed either way

            // Recording is real-time, so the wall-clock span the segment covered is its length.
            SessionLog?.SegmentCompleted(label, seg.Title, verdict.Kept, verdict.MusicPercent,
                (DateTime.Now - seg.StartedAt).TotalSeconds,
                verdict.Verdict, verdict.LeadTrimSeconds, verdict.TailTrimSeconds);

            if (!verdict.Kept)
            {
                Interlocked.Increment(ref _rejected);
                RaiseStatus();
                return;
            }

            Interlocked.Increment(ref _kept);
            var saved = new SavedSong(dest, seg.Title, seg.Artist ?? "", seg.Station,
                ext.TrimStart('.'), DateTimeOffset.Now, Source: SongSource.Harvested);
            _songLibrary.AddAndEnrich(saved);
            SegmentIndexed?.Invoke(this, saved);
            RaiseStatus();

            EvictIfNeeded();
        });
    }

    /// <summary>
    /// Copies a QC-rejected segment into <c>_rejected/</c> instead of dropping it. The score
    /// leads the filename, zero-padded, so a plain name sort triages the folder: the 000s are
    /// the ad breaks and station IDs the QC is supposed to catch, and anything scoring near the
    /// threshold with a real title and a real duration is a misclassification worth listening to.
    /// This is the corpus a detector re-fit needs — see tools/DjDetector.
    /// </summary>
    private void QuarantineRejected(string src, string label, CompletedSegment seg, SegmentVerdict verdict)
    {
        if (_maxRejectedCacheBytes <= 0)
            return; // quarantine disabled

        try
        {
            Directory.CreateDirectory(_rejectedDir);

            var ext = Path.GetExtension(seg.FileName);
            var baseName = Sanitize(
                $"{verdict.MusicPercent:000} · {verdict.Verdict} · {label} · {seg.Artist} - {seg.Title}");
            var dest = Path.Combine(_rejectedDir, baseName + ext);
            for (var n = 2; File.Exists(dest); n++)
                dest = Path.Combine(_rejectedDir, $"{baseName} ({n}){ext}");

            File.Copy(src, dest);
            EvictRejectedIfNeeded();
        }
        catch (Exception ex)
        {
            // Diagnostics must never break harvesting.
            AppLog.Debug($"[Dj] couldn't quarantine a rejected segment: {ex.Message}");
        }
    }

    /// <summary>Same oldest-first size cap as the harvest folder. These files were never indexed,
    /// so unlike <see cref="EvictIfNeeded"/> there are no library rows to remove.</summary>
    private void EvictRejectedIfNeeded()
    {
        try
        {
            if (!Directory.Exists(_rejectedDir))
                return;

            var files = new DirectoryInfo(_rejectedDir).GetFiles()
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            var totalBytes = files.Sum(f => f.Length);

            foreach (var f in files)
            {
                if (totalBytes <= _maxRejectedCacheBytes)
                    break;
                totalBytes -= f.Length;
                try { f.Delete(); } catch { /* best effort */ }
            }
        }
        catch
        {
            // Best effort — eviction failing should never break harvesting.
        }
    }

    /// <summary>Simple folder-size-cap eviction: delete the oldest harvested files once the
    /// harvest folder exceeds <see cref="_maxHarvestCacheBytes"/>, removing each evicted file's
    /// library row synchronously (rather than waiting on the existing backfill reconciliation).
    /// Never touches the user's own Library folder — harvested songs live in their own directory.
    /// </summary>
    private void EvictIfNeeded()
    {
        try
        {
            if (!Directory.Exists(_harvestDir))
                return;

            var files = new DirectoryInfo(_harvestDir).GetFiles() // top-level only — excludes _scratch
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            var totalBytes = files.Sum(f => f.Length);

            foreach (var f in files)
            {
                if (totalBytes <= _maxHarvestCacheBytes)
                    break;
                totalBytes -= f.Length;
                _songLibrary.Remove(f.FullName);
                SessionLog?.Evicted(f.FullName);
                try { f.Delete(); } catch { /* best effort */ }
            }
        }
        catch
        {
            // Best effort — eviction failing should never break harvesting.
        }
    }

    private void RaiseStatus()
    {
        List<HarvesterInfo> harvesters;
        lock (_activeLock)
            harvesters = _active.Select(h => new HarvesterInfo(h.Label, h.TitlesSeen)).ToList();
        StatusChanged?.Invoke(this, new HarvestStatus(
            harvesters.Count, Volatile.Read(ref _kept), Volatile.Read(ref _rejected), harvesters));
    }

    // --- Station sourcing ------------------------------------------------------
    //
    // The spec's "headless RunUnifiedSearchAsync variant" (DJ-MODE-SPEC.md §6.2) is never
    // actually specified anywhere — prose only, no signature. Rather than extract/refactor the
    // existing ~80-line UI-coupled pipeline in MainViewModel (real risk against working, shipped
    // search code), this is a small, independent routine built on the same underlying,
    // confirmed headless-safe services — but it DOES reuse ISearchRanker (also headless-safe,
    // already decoupled from the UI pipeline) for genuine relevance judgment, not just count.
    //
    // Earlier version of this method escalated to web search only when the cheap pool's raw
    // COUNT fell short of quota — which let a real bug through: a mainstream tag search (e.g.
    // "rock") easily clears the quota by count while including genre-irrelevant results (a metal
    // station self-tagged "rock", ranked high by Radio Browser vote count), so escalation never
    // fired and nothing ever checked whether the results actually fit the prompt. Fixed by
    // judging every candidate pool for relevance via ISearchRanker (same LLM-backed ranker the
    // visible search uses) BEFORE deciding whether more candidates are needed — mirroring the
    // visible search's own rank-then-gate-escalation-on-the-ranked-count pattern exactly, not a
    // cheaper approximation of it. Per this project's "quality over token cost" principle for DJ
    // mode: no candidate reaches a harvester without passing a real relevance judgment call, and
    // a thin GENUINELY-relevant result is left thin (fewer harvesters) rather than padded with
    // an irrelevant one to hit the target count.

    private sealed record SourceCandidate(Station Station, string DescriptiveText, string? Country);

    private async Task<List<Station>> SourceStationsAsync(string prompt, int count, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pool = new List<SourceCandidate>();

        var query = _interpreter.IsConfigured
            ? await _interpreter.InterpretAsync(prompt, ct).ConfigureAwait(false) ?? FallbackQuery(prompt)
            : FallbackQuery(prompt);

        AppLog.Info($"[Dj] sourcing \"{prompt}\" · interpreter={_interpreter.IsConfigured} "
                    + $"ranker={_ranker.IsConfigured} web={_agenticSearch.IsConfigured} "
                    + $"tags=[{string.Join(",", query.Tags ?? [])}] want={count}");

        var cheap = await _searchService.SearchCandidatesAsync(query, 0, ct).ConfigureAwait(false);
        AddUniqueCandidates(pool, seen, cheap.Select(ToCandidate));
        AppLog.Info($"[Dj] directory returned {cheap.Count} playable candidate(s)");

        // Grow the local catalog from DJ sessions too (fire-and-forget, same as the visible
        // search does): next time these stations are judged, the ranker gets a real description.
        if (cheap.Count > 0)
            _enrichment.EnrichInBackground(cheap);

        var relevant = await RankRelevantAsync(prompt, pool, count, ct).ConfigureAwait(false);
        AppLog.Info($"[Dj] ranker kept {relevant.Count} of {pool.Count} as genuinely relevant");

        // Escalate only when the RELEVANT count (not the raw pool size) falls short — the fix
        // for the bug above. A thin cheap pool that's ALSO fully relevant doesn't need escalation
        // just because it's smaller than `count`; a big cheap pool that's mostly irrelevant does.
        // maxResults: count — Pattern B's default answer cap is one visible-search page (6),
        // which would leave the reserve almost empty here.
        if (relevant.Count < count && _agenticSearch.IsConfigured)
        {
            AppLog.Info($"[Dj] escalating to web discovery ({relevant.Count} < {count})");
            var web = await _agenticSearch.SearchAsync(prompt, count, ct).ConfigureAwait(false);
            AddUniqueCandidates(pool, seen, web.Select(r => new SourceCandidate(r.Station, r.Reason, null)));
            relevant = await RankRelevantAsync(prompt, pool, count, ct).ConfigureAwait(false);
            AppLog.Info($"[Dj] web added {web.Count}; ranker now keeps {relevant.Count} of {pool.Count}");
        }

        if (relevant.Count == 0)
            AppLog.Warn($"[Dj] nothing relevant for \"{prompt}\" — session will not start");

        return relevant;
    }

    /// <summary>
    /// Judges the whole candidate pool for genuine relevance to the prompt via the same
    /// LLM-backed ranker the visible search uses, returning only stations that pass — best-first,
    /// never padded with weak matches to reach <paramref name="count"/>. Degrades to the raw pool
    /// (unfiltered) only when the ranker itself can't run at all (no API key) — that's a
    /// capability fallback, not a substitute for the real judgment call.
    /// </summary>
    private async Task<List<Station>> RankRelevantAsync(
        string prompt, List<SourceCandidate> pool, int count, CancellationToken ct)
    {
        if (!_ranker.IsConfigured || pool.Count == 0)
            return pool.Take(count).Select(c => c.Station).ToList();

        var candidates = pool.Select((c, i) => new RankCandidate(i, c.Station.Name, c.DescriptiveText, c.Country)).ToList();
        var verdicts = await _ranker.RankAsync(prompt, candidates, count, ct).ConfigureAwait(false);
        if (verdicts is null) // ranker unavailable for this call specifically — degrade, don't block starting DJ mode
            return pool.Take(count).Select(c => c.Station).ToList();

        return verdicts.OrderByDescending(v => v.Score).Select(v => pool[v.Id].Station).ToList();
    }

    // No API key / interpreter not configured → treat the raw prompt words as tags. Matches the
    // spec's explicit degrade mode: "Plain genre/tag... → direct Radio Browser tag search, no key
    // required. So DJ mode is usable without AI, just less 'smart' about the vibe."
    private static StationSearchQuery FallbackQuery(string prompt) => new()
    {
        Tags = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    };

    // Give the ranker the best text we hold for each candidate: a cached enriched description
    // when one exists (a real account of what the station plays — exactly what catches a metal
    // station self-tagged "rock"), falling back to the thin tags+country line otherwise.
    private SourceCandidate ToCandidate(StationCandidate c)
    {
        var description = _enrichment.GetCached(c.StationUuid)?.Description;
        if (string.IsNullOrWhiteSpace(description))
            description = string.Join(", ", new[] { c.Tags, c.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new SourceCandidate(c.Station, description, c.Country);
    }

    /// <summary>
    /// Adds candidates the pool doesn't already have, by URL <b>and</b> by station identity.
    /// The second check is what stops codec variants of one station — "… (128k MP3)" and
    /// "… (128k AAC)" — from occupying two harvester slots and recording every song twice.
    /// </summary>
    private static void AddUniqueCandidates(
        List<SourceCandidate> pool, HashSet<string> seen, IEnumerable<SourceCandidate> candidates)
    {
        foreach (var c in candidates)
        {
            // Both keys go in the same set; the prefixes keep a URL from ever colliding with a name.
            if (!seen.Add("url:" + c.Station.Url))
                continue;
            if (!seen.Add("name:" + StationNameFormatter.Clean(c.Station.Name).ToLowerInvariant()))
                continue;
            pool.Add(c);
        }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length > 120 ? name[..120] : name;
    }
}
