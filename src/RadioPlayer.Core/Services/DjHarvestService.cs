using System.IO;
using System.Net.Http;
using System.Threading;
using RadioPlayer.Threading;
using ManagedBass;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// One currently-connected harvester, for UI display. Dead harvesters are removed from the
/// active pool (and replaced from reserve) the instant they die, so there's no "dead" state to
/// carry here — only genuinely live connections are ever in a snapshot.
///
/// <paramref name="TitlesSeen"/> counts ICY title changes, which is NOT a song count: two
/// boundaries are needed to complete one segment, and QC then rejects some. Showing it as
/// "songs" overstated every station by roughly a factor of two, so the UI uses
/// <paramref name="Kept"/>/<paramref name="Rejected"/> — what actually reached the mix — and
/// TitlesSeen stays for the metadata watchdog, which only cares whether it's zero.
/// </summary>
public sealed record HarvesterInfo(string Label, int TitlesSeen, int Kept, int Rejected);

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
    private readonly ISemanticSearchService? _semanticSearch;
    private readonly ISongLibraryService _songLibrary;
    private readonly SegmentQualityChecker _qc;
    private readonly string _harvestDir;
    private readonly string _scratchDir;
    private readonly string _rejectedDir;
    private int _harvesterCount;
    private readonly int _reserveCount;
    private readonly double _offsetSeconds;
    private long _maxHarvestCacheBytes;
    private long _maxRejectedCacheBytes;

    // Mutated only on the harvest dispatcher thread (StartHarvester/OnHarvesterDied/Stop), but
    // read from OnSegmentCompleted's background Task.Run continuation too (for RaiseStatus's
    // per-harvester snapshot) — a plain List isn't safe for that, so every access is locked.
    private readonly object _activeLock = new();
    private readonly List<StreamHarvester> _active = new();

    // Per-station keep/reject counts, so the Sources list can report what each one actually
    // contributed rather than how many titles went past. Keyed by label; written from QC worker
    // threads, read by RaiseStatus.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StationTally> _tallies =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class StationTally
    {
        public int Kept;
        public int Rejected;
    }
    private Queue<Station> _reserve = new();
    private Thread? _harvestThread;
    private IDispatcher? _harvestDispatcher;
    private IDispatcherTimer? _watchdog;

    /// <summary>How long a harvester may serve no ICY metadata before it's assumed not to
    /// support it. Generous: a station that only announces on change, connected mid-song, still
    /// gets a full long track to prove itself.</summary>
    private static readonly TimeSpan MetadataGrace = TimeSpan.FromMinutes(6);

    /// <summary>How long a harvester may go without completing a segment before its slot goes to
    /// a reserve station. Configurable because the right value is genre-dependent: the longest
    /// real track measured so far was 10:04, but a station playing hour-long sets produces
    /// nothing at all.</summary>
    private readonly TimeSpan _idleLimit;
    private volatile bool _running;
    private int _kept;
    private int _rejected;

    // --- Mid-session pool top-up (#41) ----------------------------------------
    //
    // A session used to source its stations once and never again, so a pool could only shrink: when
    // the reserve is empty, Retire loses the slot for good. Usually there is plenty of slack — a
    // real 183-minute session used 9 stations out of a 4+15 pool — but a session started while the
    // directory was down (#26) begins thin AND dies faster, because its cached urls were never
    // re-verified. The outage's cost outlived the outage.

    /// <summary>Minimum gap between top-up attempts. Each one costs an interpreter call and a
    /// ranker call, and a burst of harvester deaths shouldn't turn into a burst of those.</summary>
    private static readonly TimeSpan TopUpCooldown = TimeSpan.FromMinutes(10);

    /// <summary>How long an offline-started session waits before trying to heal itself. Short: its
    /// urls are stale and its pool thin, so reaching a live directory early is worth one query.</summary>
    private static readonly TimeSpan OfflineHealAfter = TimeSpan.FromMinutes(3);

    private string _prompt = string.Empty;
    private bool _startedOffline;
    private bool _reachedDirectorySinceOfflineStart;
    private DateTime _poolSourcedUtc;
    private DateTime _lastTopUpUtc = DateTime.MinValue;
    private int _topUpInFlight;

    // Dedupe keys for every station this session has tried, so a top-up can't return one that is
    // already playing or was retired earlier for being useless. Written from the harvest dispatcher
    // (InstallTopUp) and from whichever thread starts/changes the session; read from a background
    // top-up task — so it's locked.
    private readonly object _triedLock = new();
    private readonly HashSet<string> _triedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (background thread) for each harvested song kept after QC.</summary>
    public event EventHandler<HarvestedSong>? SegmentIndexed;

    private int _vibeGeneration;

    /// <summary>Bumped by <see cref="ChangeVibeAsync"/>; stamped onto every song collected after
    /// it, so the queue can tell a current-vibe arrival from one still in flight from the old.</summary>
    public int VibeGeneration => Volatile.Read(ref _vibeGeneration);

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
        double rejectBelow = 0,
        bool trimEdges = true,
        double minSongSeconds = 60,
        double stationIdleMinutes = 15,
        long maxHarvestCacheBytes = 500L * 1024 * 1024,
        long maxRejectedCacheBytes = 250L * 1024 * 1024,
        // Last, and optional, so every existing call site and test keeps its positional arguments.
        // Null just means "no offline fallback" — sourcing then fails the way it always did.
        ISemanticSearchService? semanticSearch = null)
    {
        _searchService = searchService;
        _interpreter = interpreter;
        _agenticSearch = agenticSearch;
        _ranker = ranker;
        _enrichment = enrichment;
        _semanticSearch = semanticSearch;
        _songLibrary = songLibrary;
        _harvestDir = harvestDir;
        _scratchDir = Path.Combine(harvestDir, "_scratch");
        _rejectedDir = Path.Combine(harvestDir, "_rejected");
        _harvesterCount = harvesterCount;
        _reserveCount = reserveCount;
        _offsetSeconds = offsetSeconds;
        _maxHarvestCacheBytes = maxHarvestCacheBytes;
        _maxRejectedCacheBytes = maxRejectedCacheBytes;
        _idleLimit = TimeSpan.FromMinutes(stationIdleMinutes);
        _qc = new SegmentQualityChecker(rejectBelow, trimEdges, minSongSeconds);
    }

    public bool IsRunning => _running;

    /// <summary>
    /// Optional per-session diagnostics (see <see cref="DjSessionLog"/>). Set by the view model
    /// around a session so harvester tuning can be measured rather than guessed; null disables
    /// it entirely and nothing in the harvest path depends on it.
    /// </summary>
    public DjSessionLog? SessionLog { get; set; }

    /// <summary>
    /// How many stations are listened to at once. Read when a session starts and when the vibe
    /// changes, so a change from the options dialog takes effect on the next session rather than
    /// re-pooling a running one — you cannot re-deal harvesters mid-mix without dropping the
    /// segments already in the QC pipeline.
    /// </summary>
    public int HarvesterCount
    {
        get => _harvesterCount;
        set => _harvesterCount = Math.Max(1, value);
    }

    /// <summary>
    /// The disk budget, split by <c>AppSettings.ResolveHarvestCacheBytes</c>/<c>...Rejected...</c>.
    /// Unlike <see cref="HarvesterCount"/> these ARE live: eviction consults them on every
    /// completed segment, so lowering the cap starts reclaiming space within a song or two.
    /// </summary>
    public long MaxHarvestCacheBytes
    {
        get => _maxHarvestCacheBytes;
        set => _maxHarvestCacheBytes = Math.Max(0, value);
    }

    /// <inheritdoc cref="MaxHarvestCacheBytes"/>
    public long MaxRejectedCacheBytes
    {
        get => _maxRejectedCacheBytes;
        set => _maxRejectedCacheBytes = Math.Max(0, value);
    }

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
        var sourced = await SourceStationsAsync(prompt, _harvesterCount + _reserveCount, ct).ConfigureAwait(false);
        var stations = sourced.Stations;
        if (stations.Count == 0)
        {
            TopStation = null;
            return;
        }

        progress?.Report(HarvestStartStage.Connecting);
        var hot = stations.Take(_harvesterCount).ToList();
        _reserve = new Queue<Station>(stations.Skip(_harvesterCount));
        TopStation = hot[0]; // stations are already best-first out of RankRelevantAsync
        ResetSessionTotals();
        BeginPoolTracking(prompt, stations, sourced.FromLocalCatalog);
        _running = true;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _harvestThread = new Thread(() => HarvestThreadMain(hot, ready)) { IsBackground = true };
        // STA dates from when this thread ran a WPF Dispatcher; kept on Windows because that is the
        // configuration DJ sessions were verified with. Elsewhere apartments don't exist and the
        // call would throw PlatformNotSupportedException — and the MessageLoop doesn't need one.
        if (OperatingSystem.IsWindows())
            _harvestThread.SetApartmentState(ApartmentState.STA);
        _harvestThread.Start();
        await ready.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Re-points the harvest pool at a new vibe, mid-session, without stopping anything first.
    ///
    /// <para>Order matters: the new stations are sourced <b>before</b> the old harvesters are
    /// touched. Sourcing is a directory query plus an LLM re-rank, sometimes a web escalation —
    /// five to thirty seconds — and tearing the pool down first would mean collecting nothing for
    /// all of it. The old vibe keeps harvesting until the moment the new pool is ready.</para>
    ///
    /// <para>The harvest thread and its BASS device are NOT torn down; only the harvesters on it
    /// are swapped. <see cref="Stop"/> would take the whole thread with it, which is a session
    /// ending, not a vibe changing.</para>
    ///
    /// Returns false when nothing relevant was found — in which case the current pool is left
    /// exactly as it was, since collecting for the old vibe beats collecting for nothing.
    /// </summary>
    public async Task<bool> ChangeVibeAsync(string prompt, CancellationToken ct = default)
    {
        if (!_running || string.IsNullOrWhiteSpace(prompt))
            return false;

        AppLog.Info($"[Dj] changing vibe to \"{prompt}\" — sourcing before the swap");
        var sourced = await SourceStationsAsync(prompt, _harvesterCount + _reserveCount, ct)
            .ConfigureAwait(false);
        var stations = sourced.Stations;
        if (stations.Count == 0)
        {
            AppLog.Warn($"[Dj] nothing relevant for \"{prompt}\" — keeping the current pool");
            return false;
        }

        ct.ThrowIfCancellationRequested();
        var dispatcher = _harvestDispatcher;
        if (dispatcher is null || !_running)
            return false; // session ended while we were sourcing

        var hot = stations.Take(_harvesterCount).ToList();
        var reserve = stations.Skip(_harvesterCount).ToList();

        // Bump BEFORE the swap: anything still in the QC pipeline from the old stations belongs to
        // the old vibe, and stamping it with the new generation would let it through.
        Interlocked.Increment(ref _vibeGeneration);

        // Reset the counters with it. DjQueueService.ChangeVibe truncates the queue, so every song
        // counted so far has just been removed from the mix — leaving the totals running made the
        // session card report songs that were deliberately discarded. Tallies too: the pool is
        // fully replaced, and they are keyed by station label, so a re-sourced station would
        // otherwise inherit a count from earlier in the session.
        //
        // A segment already in QC when this runs will land against the new vibe's count. The old
        // harvesters are retired in the same breath, so there are few, and over-counting by one is
        // a better failure than the whole total being wrong.
        ResetSessionTotals();

        // A new vibe wants different stations, so the "already tried" set starts over with it — a
        // station that was wrong for the old prompt may be exactly right for this one.
        BeginPoolTracking(prompt, stations, sourced.FromLocalCatalog);

        dispatcher.Send(() => SwapPool(hot, reserve));

        TopStation = hot[0]; // best-first out of RankRelevantAsync — the live bridge uses this
        SessionLog?.VibeChanged(prompt, hot.Count);
        AppLog.Info($"[Dj] vibe changed: {hot.Count} station(s) hot, {reserve.Count} in reserve");
        return true;
    }

    /// <summary>
    /// Replaces every harvester with the new vibe's stations. Runs on the harvest dispatcher.
    ///
    /// Retires the old ones directly rather than through <see cref="Retire"/>, which promotes a
    /// reserve station to fill the gap — during a swap that would start an old-vibe station
    /// moments before the new pool takes its place.
    /// </summary>
    private void SwapPool(List<Station> hot, List<Station> reserve)
    {
        List<StreamHarvester> outgoing;
        lock (_activeLock)
        {
            outgoing = _active.ToList();
            _active.Clear();
        }

        foreach (var h in outgoing)
        {
            h.SegmentCompleted -= OnSegmentCompleted;
            h.SegmentDiscarded -= OnSegmentDiscarded;
            h.Died -= OnHarvesterDied;
            SessionLog?.TitlesSeen(h.Label, h.TitlesSeen);
            SessionLog?.HarvesterDied(h.Label, "vibe changed");
            h.Dispose();
        }

        _reserve = new Queue<Station>(reserve);
        foreach (var station in hot)
            StartHarvester(station);
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
        RecordTitleCounts();   // the watchdog's last tick can be 30s stale; the summary uses these
        SessionLog?.Finish();

        var dispatcher = _harvestDispatcher;
        if (dispatcher is not null)
        {
            dispatcher.Post(() =>
            {
                _watchdog?.Stop();
                _watchdog = null;
                lock (_activeLock)
                {
                    foreach (var h in _active)
                    {
                        h.SegmentCompleted -= OnSegmentCompleted;
                        h.SegmentDiscarded -= OnSegmentDiscarded;
                        h.Died -= OnHarvesterDied;
                        h.Dispose();
                    }
                    _active.Clear();
                }
                Bass.Free(); // frees only THIS (harvest) thread's device (0) — per-thread, confirmed via ManagedBass docs
                dispatcher.Shutdown();
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
        // Installed before StartHarvester: each StreamRecorder captures DispatcherContext.Current
        // in its constructor, and that must be this loop, not a lazily-made one.
        var loop = MessageLoop.InstallOnCurrentThread();
        _harvestDispatcher = loop;

        foreach (var station in hot)
            StartHarvester(station);

        // Watchdog for slots that aren't earning their keep. Lives on this thread so its
        // retire/replace path is the same single-threaded one every other harvester lifecycle
        // call uses. Duplicates go first: that verdict is available as soon as icy-name is read,
        // so there's no reason to make a redundant harvester wait out the idle limit.
        _watchdog = loop.CreateTimer();
        _watchdog.Interval = TimeSpan.FromSeconds(30);
        _watchdog.Tick += (_, _) =>
        {
            RecordTitleCounts();
            RetireDuplicateStreams();
            RetireUnproductive();
            HealOfflineStartIfDue();
        };
        _watchdog.Start();

        ready.TrySetResult();
        loop.Run(); // returns once Stop()'s dispatched action calls Shutdown
    }

    private void StartHarvester(Station station)
    {
        var harvester = new StreamHarvester(station.Name, station.Url, station.Format,
            _scratchDir, _offsetSeconds, _harvestDispatcher!);
        harvester.SegmentCompleted += OnSegmentCompleted;
        harvester.SegmentDiscarded += OnSegmentDiscarded;
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
        harvester.SegmentDiscarded -= OnSegmentDiscarded;
        harvester.Died -= OnHarvesterDied;
        lock (_activeLock)
        {
            if (!_active.Remove(harvester))
                return; // already retired
        }
        SessionLog?.TitlesSeen(harvester.Label, harvester.TitlesSeen); // final figure before it goes
        SessionLog?.HarvesterDied(harvester.Label, reason);
        harvester.Dispose();

        if (_running && _reserve.Count > 0)
        {
            StartHarvester(_reserve.Dequeue());
        }
        else
        {
            RaiseStatus();
            // The slot is gone until something replaces it, so this is the moment to go looking (#41)
            // — including for a session that started offline and may now be able to reach the
            // directory. Throttled inside; no-op when the session is stopping.
            if (_running)
                BeginTopUp("reserve empty");
        }
    }

    /// <summary>
    /// Whether a harvester has earned its slot, and if not, why. Pure so the policy can be tested
    /// without a live stream — it decides whether a station is dropped, and getting it wrong
    /// either churns the pool needlessly or leaves dead weight in it.
    ///
    /// Two ways to fail:
    /// <list type="bullet">
    /// <item><b>No ICY metadata at all.</b> No title changes means no song boundaries, so the
    /// station can never produce a segment however long it runs. Caught early — a shorter grace
    /// than the idle rule, because this one is definitive rather than a judgement about rate.</item>
    /// <item><b>No completed segment for a long time.</b> Most often a long DJ set or extended
    /// mix: one ICY title announced for an hour, so no boundaries and nothing for the curator.
    /// Also catches a stream that quietly stalled without erroring. Measured on SEGMENTS, not
    /// title changes, because some stations re-announce the same title mid-track — "the title
    /// changed" and "we got a song" are different questions.</item>
    /// </list>
    /// </summary>
    internal static bool ShouldRetire(int titlesSeen, DateTime connectedAt, DateTime lastSegmentAt,
        DateTime now, TimeSpan metadataGrace, TimeSpan idleLimit, out string reason)
    {
        if (titlesSeen == 0 && now - connectedAt > metadataGrace)
        {
            reason = "no metadata";
            return true;
        }

        // A station that has already delivered has answered the question this rule asks. Silence
        // from it is far more likely to be one long track than a dead slot, so it gets longer —
        // see ProvenIdleMultiplier.
        var hasDelivered = lastSegmentAt != default;
        var since = hasDelivered ? lastSegmentAt : connectedAt;
        var limit = hasDelivered ? idleLimit * ProvenIdleMultiplier : idleLimit;

        if (now - since > limit)
        {
            reason = hasDelivered ? "stopped producing" : "no songs";
            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// How much longer a station that has already produced a segment may stay quiet before losing
    /// its slot.
    ///
    /// <para>The flat limit is a reasonable question to ask a station that has produced
    /// <i>nothing</i> — it may not announce titles at all, and the slot is better spent. It is the
    /// wrong question for one that has already delivered: on 2026-08-06 an art-rock session set
    /// <c>DjStationIdleMinutes</c> to 10, and Radio Caprice delivered a song and was then retired
    /// 10:12 later for "no songs". In prog and art rock a single track <i>is</i> ten minutes of
    /// silence. Three of the four slots churned that way inside half an hour, and since every
    /// replacement loses its first partial song to "joined mid-song", the churn cost more than the
    /// stations did.</para>
    ///
    /// A station that has stalled for good still goes, just at twice the patience.
    /// </summary>
    private const int ProvenIdleMultiplier = 2;

    /// <summary>
    /// Pushes each live harvester's ICY title count into the session log. Cheap, and it's the
    /// only way the log can tell "announced nothing" from "announced one title and sat on it" —
    /// a station doing the latter never closes a segment, so it raises no events at all.
    /// </summary>
    private void RecordTitleCounts()
    {
        if (SessionLog is null)
            return;
        lock (_activeLock)
        {
            foreach (var h in _active)
                SessionLog.TitlesSeen(h.Label, h.TitlesSeen);
        }
    }

    /// <summary>One harvester's stream identity, for the duplicate check.</summary>
    internal readonly record struct StreamIdentity(
        string? StreamName, DateTime ConnectedAt, IReadOnlyCollection<string>? RecentTitles = null);

    /// <summary>
    /// How many titles two harvesters must have in common before the titles alone are taken as
    /// proof of one stream. One is not enough: two pop stations can genuinely be playing the same
    /// chart single at the same moment. Two is: agreeing on a second song as well means they are
    /// carrying the same programme, and even a true simulcast is just as redundant to harvest from.
    /// </summary>
    private const int SharedTitlesForDuplicate = 2;

    /// <summary>
    /// Indices of harvesters listening to the same audio as another one, and so worth replacing.
    /// A directory lists the same broadcast several times, and pre-connect deduplication (resolved
    /// URL, identity key) can't always see it. Two observed cases, each defeating a different key:
    /// <list type="bullet">
    /// <item>2026-08-02 — "Liquid DnB" (<c>antares.dribbcast.com/proxy/dave1/</c>) and
    /// "DnB Liquified" (<c>antares.dribbcast.com:5000</c>): different names AND different URLs,
    /// but one <c>icy-name</c>.</item>
    /// <item>2026-08-03 — "FM4 | ORF" and "FM4 | ORF | HQ": <b>no icy-name at all</b>, and URLs
    /// differing only in <c>q1a</c>/<c>q2a</c>. Invisible to every key, identifiable only by the
    /// titles they announce.</item>
    /// </list>
    /// So both signals are consulted, icy-name first. Duplicates are worth dropping even when
    /// productive — the queue dedupes on artist+title, so the second copy is harvested, enriched
    /// and embedded only to be thrown away.
    ///
    /// Keeps the earliest-connected of each group (it has the most history), and never treats a
    /// blank name or an empty title history as matching anything.
    /// </summary>
    internal static List<int> FindDuplicateStreams(IReadOnlyList<StreamIdentity> harvesters)
    {
        var duplicates = new List<int>();
        var keptPerName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < harvesters.Count; i++)
        {
            var name = harvesters[i].StreamName?.Trim();
            if (string.IsNullOrEmpty(name))
                continue; // no icy-name — the title pass below is the only way to judge this one

            if (!keptPerName.TryGetValue(name, out var incumbent))
            {
                keptPerName[name] = i;
                continue;
            }

            // Later connection loses; on a tie the higher index does, so the result is stable.
            if (harvesters[i].ConnectedAt < harvesters[incumbent].ConnectedAt)
            {
                keptPerName[name] = i;
                duplicates.Add(incumbent);
            }
            else
            {
                duplicates.Add(i);
            }
        }

        // Second pass, for streams the first one couldn't judge: identical titles. ORF's feeds
        // serve no icy-name whatsoever, so "FM4 | ORF" and "FM4 | ORF | HQ" were invisible to the
        // name check while announcing byte-identical StreamTitles from one broadcast. Only
        // consulted when a pair isn't already settled by name, so it can never override it.
        for (var i = 0; i < harvesters.Count; i++)
        {
            if (duplicates.Contains(i))
                continue;
            for (var j = i + 1; j < harvesters.Count; j++)
            {
                if (duplicates.Contains(j))
                    continue;
                if (HasName(harvesters[i]) && HasName(harvesters[j]))
                    continue; // the name pass had both and kept them apart — trust it
                if (SharedTitleCount(harvesters[i], harvesters[j]) < SharedTitlesForDuplicate)
                    continue;

                var later = harvesters[j].ConnectedAt < harvesters[i].ConnectedAt ? i : j;
                duplicates.Add(later);
                if (later == i)
                    break; // i is gone; nothing else to compare it against
            }
        }

        duplicates.Sort();
        return duplicates;
    }

    private static bool HasName(StreamIdentity h) => !string.IsNullOrWhiteSpace(h.StreamName);

    private static int SharedTitleCount(StreamIdentity a, StreamIdentity b)
    {
        if (a.RecentTitles is not { Count: > 0 } x || b.RecentTitles is not { Count: > 0 } y)
            return 0;
        var seen = new HashSet<string>(x.Where(t => !string.IsNullOrWhiteSpace(t)),
            StringComparer.OrdinalIgnoreCase);
        return y.Count(t => !string.IsNullOrWhiteSpace(t) && seen.Contains(t));
    }

    /// <summary>Drops harvesters that turned out to be on a stream another one already has.</summary>
    private void RetireDuplicateStreams()
    {
        if (!_running)
            return;

        var victims = new List<(StreamHarvester Harvester, string Of)>();
        lock (_activeLock)
        {
            // Only reached from the watchdog, which runs on the harvest dispatcher thread — the
            // one thread allowed to call BASS for these harvesters.
            foreach (var h in _active)
                h.RefreshStreamName();

            var identities = _active
                .Select(h => new StreamIdentity(h.StreamName, h.ConnectedAt, h.RecentTitles))
                .ToList();
            var dropping = FindDuplicateStreams(identities);
            foreach (var i in dropping)
            {
                // Name the one we're keeping in the log line: whichever surviving harvester this
                // one matched, by name or by title (matters when three share a stream).
                var keeper = Enumerable.Range(0, _active.Count)
                    .Where(j => j != i && !dropping.Contains(j))
                    .Where(j => (HasName(identities[i]) && HasName(identities[j])
                                 && string.Equals(identities[i].StreamName?.Trim(),
                                     identities[j].StreamName?.Trim(), StringComparison.OrdinalIgnoreCase))
                                || SharedTitleCount(identities[i], identities[j]) >= SharedTitlesForDuplicate)
                    .Select(j => _active[j].Label)
                    .FirstOrDefault() ?? "another harvester";
                victims.Add((_active[i], keeper));
            }
        }

        foreach (var (harvester, of) in victims)
        {
            // Say WHICH signal matched — the two cases need different follow-up if this ever
            // misfires, and a shared title is the weaker of the two.
            AppLog.Info($"[Dj] dropping {harvester.Label}: same stream as {of} "
                        + (harvester.StreamName is { Length: > 0 } n
                            ? $"(both serve icy-name \"{n}\")"
                            : "(no icy-name; matched on identical track titles)"));
            Retire(harvester, $"duplicate of {of}");
        }
    }

    /// <summary>
    /// Minimum ident boundaries before this is judged at all. An ad break legitimately produces a
    /// few, so judging early would drop a good station for having commercials. A carousel reaches
    /// eight in under three minutes, which is soon enough to stop wasting the slot.
    /// </summary>
    private const int MinIdentBoundariesToJudge = 8;

    /// <summary>
    /// Whether a station's ICY metadata is promotional text rather than track titles.
    ///
    /// Some stations cycle a show name, a phone number and a slogan through StreamTitle every
    /// twenty seconds while music plays. Every rotation looks like a track boundary, so the song
    /// underneath is chopped into fragments, each labelled with a promo and each correctly
    /// discarded as an ident. The audio is fine; the metadata simply never names the track, and
    /// nothing downstream can recover a title that was never sent — so the slot is better spent on
    /// a station that announces what it plays.
    ///
    /// Measured 2026-08-03 across two sessions: SWR3 produced 22 ident boundaries against 3 usable
    /// segments, Radio Eins 18 against 1. A station merely playing adverts sits nowhere near that
    /// ratio, which is what the 3x margin protects.
    /// </summary>
    internal static bool IsMetadataCarousel(int identBoundaries, int segmentsCompleted) =>
        identBoundaries >= MinIdentBoundariesToJudge
        && identBoundaries > 3 * segmentsCompleted;

    /// <summary>Drops harvesters that aren't contributing and promotes reserves in their place.</summary>
    private void RetireUnproductive()
    {
        if (!_running)
            return;

        var now = DateTime.UtcNow;
        var unproductive = new List<(StreamHarvester Harvester, string Reason)>();
        lock (_activeLock)
        {
            foreach (var h in _active)
            {
                if (ShouldRetire(h.TitlesSeen, h.ConnectedAt, h.LastSegmentAt, now,
                        MetadataGrace, _idleLimit, out var reason))
                    unproductive.Add((h, reason));
                else if (IsMetadataCarousel(h.IdentBoundaries, h.SegmentsCompleted))
                    unproductive.Add((h, "promos, not tracks"));
            }
        }

        foreach (var (harvester, reason) in unproductive)
        {
            AppLog.Info($"[Dj] dropping {harvester.Label}: {reason} — " + reason switch
            {
                "no metadata" =>
                    $"no ICY titles after {MetadataGrace.TotalMinutes:0} min, so it can never produce a segment",
                "promos, not tracks" =>
                    $"{harvester.IdentBoundaries} ident boundaries vs {harvester.SegmentsCompleted} usable "
                    + "segment(s) — it cycles promotional text through StreamTitle while music plays, "
                    + "so nothing it sends ever names a track",
                _ => $"nothing completed in {_idleLimit.TotalMinutes:0} min (long mix, or stalled)"
            });
            Retire(harvester, reason);
        }
    }

    // A boundary that produced nothing. Recorded, not acted on: the pool's retirement policy
    // deliberately measures completed segments, and a station whose boundaries all get filtered
    // is just as unproductive as one with no boundaries at all. The log is where the difference
    // matters, because the two need different fixes.
    private void OnSegmentDiscarded(object? sender, DiscardedSegment discarded)
    {
        var label = ((StreamHarvester)sender!).Label;
        SessionLog?.BoundarySkipped(label, discarded.Reason, discarded.Title, discarded.Artist);
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
                verdict.Verdict, verdict.LeadTrimSeconds, verdict.TailTrimSeconds, verdict.Note);

            RecordOutcome(label, verdict.Kept);

            if (!verdict.Kept)
            {
                RaiseStatus();
                return;
            }

            var saved = new SavedSong(dest, seg.Title, seg.Artist ?? "", seg.Station,
                ext.TrimStart('.'), DateTimeOffset.Now, Source: SongSource.Harvested);
            _songLibrary.AddAndEnrich(saved);
            // Stamped at the moment it lands. A segment recorded under the previous vibe can
            // finish QC minutes after a swap, and the queue must be able to tell the difference.
            SegmentIndexed?.Invoke(this, new HarvestedSong(saved, VibeGeneration));
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

    /// <summary>
    /// Records one QC outcome against the session totals and the station's own tally. Kept next to
    /// <see cref="ResetSessionTotals"/> on purpose: these are the only two places that touch the
    /// counters, so what gets counted and what gets cleared stay visibly in step.
    /// </summary>
    internal void RecordOutcome(string label, bool kept)
    {
        var tally = _tallies.GetOrAdd(label, _ => new StationTally());
        if (kept)
        {
            Interlocked.Increment(ref _kept);
            Interlocked.Increment(ref tally.Kept);
        }
        else
        {
            Interlocked.Increment(ref _rejected);
            Interlocked.Increment(ref tally.Rejected);
        }
    }

    /// <summary>
    /// Zeroes what the session card counts. Called from <see cref="StartAsync"/> and from
    /// <see cref="ChangeVibeAsync"/> — one method rather than two copies precisely so a counter
    /// added later cannot be reset in one place and not the other, which is the bug this fixes.
    /// </summary>
    internal void ResetSessionTotals()
    {
        Interlocked.Exchange(ref _kept, 0);
        Interlocked.Exchange(ref _rejected, 0);
        _tallies.Clear();
    }

    /// <summary>Everything <see cref="ResetSessionTotals"/> is responsible for, for tests to assert
    /// against without reaching into private fields one at a time.</summary>
    internal (int Kept, int Rejected, int TalliedStations) SessionTotals =>
        (Volatile.Read(ref _kept), Volatile.Read(ref _rejected), _tallies.Count);

    private void RaiseStatus()
    {
        List<HarvesterInfo> harvesters;
        lock (_activeLock)
            harvesters = _active.Select(h =>
            {
                _tallies.TryGetValue(h.Label, out var t);
                return new HarvesterInfo(h.Label, h.TitlesSeen, t?.Kept ?? 0, t?.Rejected ?? 0);
            }).ToList();
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

    /// <summary>
    /// The outcome of one sourcing pass. <paramref name="FromLocalCatalog"/> is returned rather than
    /// stored on the service because sourcing can run concurrently — a background top-up (#41)
    /// while the user changes the vibe — and a shared field would let one answer the other's
    /// question.
    /// </summary>
    private sealed record SourcingResult(List<Station> Stations, bool FromLocalCatalog);

    /// <summary>
    /// Why the last <see cref="StartAsync"/>/<see cref="ChangeVibeAsync"/> sourcing attempt returned
    /// what it did, so the caller can say something true about an empty result (#26).
    /// </summary>
    public DjSourcingOutcome LastSourcingOutcome { get; private set; } = DjSourcingOutcome.Ok;

    /// <param name="allowWebEscalation">
    /// False for a mid-session top-up (#41): Pattern B is a multi-round-trip Sonnet loop, and a thin
    /// top-up result means something different from a thin initial one — most of the good matches are
    /// already in the pool, which is why they were excluded.
    /// </param>
    /// <param name="exclude">
    /// Dedupe keys (as built by <see cref="AddUniqueCandidates"/>) for stations this session has
    /// already tried. Seeding them means a top-up can't hand back the stations already playing, or
    /// ones retired earlier for being useless.
    /// </param>
    private async Task<SourcingResult> SourceStationsAsync(string prompt, int count, CancellationToken ct,
        bool allowWebEscalation = true, IReadOnlyCollection<string>? exclude = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (exclude is not null)
            seen.UnionWith(exclude);
        var pool = new List<SourceCandidate>();
        LastSourcingOutcome = DjSourcingOutcome.Ok;

        var query = _interpreter.IsConfigured
            ? await _interpreter.InterpretAsync(prompt, ct).ConfigureAwait(false) ?? FallbackQuery(prompt)
            : FallbackQuery(prompt);

        AppLog.Info($"[Dj] sourcing \"{prompt}\" · interpreter={_interpreter.IsConfigured} "
                    + $"ranker={_ranker.IsConfigured} web={_agenticSearch.IsConfigured} "
                    + $"tags=[{string.Join(",", query.Tags ?? [])}] want={count}");

        // The observed failure (#26): every Radio Browser mirror answers 503 or not at all, and a
        // session that could have run off the local catalog never starts. The directory stays the
        // source of truth whenever it answers — this only catches the case where it doesn't.
        IReadOnlyList<StationCandidate> cheap = [];
        var directoryDown = false;
        try
        {
            cheap = await _searchService.SearchCandidatesAsync(query, 0, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (DirectoryFailure.IsUnreachable(ex, ct))
        {
            directoryDown = true;
            AppLog.Warn($"[Dj] directory unreachable ({ex.GetType().Name}: {ex.Message}) "
                        + "— falling back to the local catalog");
        }

        if (directoryDown)
        {
            var offline = await SourceFromLocalCatalogAsync(prompt, count, ct).ConfigureAwait(false);
            AddUniqueCandidates(pool, seen, offline);
        }
        else
        {
            AddUniqueCandidates(pool, seen, cheap.Select(ToCandidate));
            AppLog.Info($"[Dj] directory returned {cheap.Count} playable candidate(s)");

            // Grow the local catalog from DJ sessions too (fire-and-forget, same as the visible
            // search does): next time these stations are judged, the ranker gets a real description.
            // This is also what fills the offline catalog the branch above draws on.
            if (cheap.Count > 0)
                _enrichment.EnrichInBackground(cheap);
        }

        // Offline, the ranker is the only relevance judgment left, so it becomes mandatory rather
        // than a nicety: without it there is nothing standing between a thin catalog's least-bad
        // cosine hits and a whole session of unrelated stations.
        var relevant = await RankRelevantAsync(prompt, pool, count, directoryDown, ct).ConfigureAwait(false);
        AppLog.Info($"[Dj] ranker kept {relevant.Count} of {pool.Count} as genuinely relevant");

        // Escalate only when the RELEVANT count (not the raw pool size) falls short — the fix
        // for the bug above. A thin cheap pool that's ALSO fully relevant doesn't need escalation
        // just because it's smaller than `count`; a big cheap pool that's mostly irrelevant does.
        // maxResults: count — Pattern B's default answer cap is one visible-search page (6),
        // which would leave the reserve almost empty here.
        //
        // Never when the directory is down: Pattern B's own search_radio_browser tool resolves its
        // web findings through the same mirrors, so escalating would just spend a Sonnet loop to
        // arrive back at the outage.
        if (allowWebEscalation && !directoryDown && relevant.Count < count && _agenticSearch.IsConfigured)
        {
            AppLog.Info($"[Dj] escalating to web discovery ({relevant.Count} < {count})");
            var web = await _agenticSearch.SearchAsync(prompt, count, ct).ConfigureAwait(false);
            AddUniqueCandidates(pool, seen, web.Select(r => new SourceCandidate(r.Station, r.Reason, null)));
            relevant = await RankRelevantAsync(prompt, pool, count, directoryDown, ct).ConfigureAwait(false);
            AppLog.Info($"[Dj] web added {web.Count}; ranker now keeps {relevant.Count} of {pool.Count}");
        }

        if (relevant.Count == 0)
        {
            // OfflineUnranked is set by RankRelevantAsync and outranks everything else here: it
            // means we refused to answer, not that there was no answer.
            if (LastSourcingOutcome != DjSourcingOutcome.OfflineUnranked)
                LastSourcingOutcome = directoryDown
                    ? DjSourcingOutcome.OfflineNoMatch
                    : DjSourcingOutcome.NothingRelevant;
            AppLog.Warn($"[Dj] nothing relevant for \"{prompt}\" ({LastSourcingOutcome})"
                        + " — session will not start");
        }

        return new SourcingResult(relevant, directoryDown);
    }

    /// <summary>
    /// The next station in the live harvest pool after <paramref name="current"/>, wrapping, or
    /// null when nothing is connected.
    ///
    /// <para>For skipping during a live bridge (#49). The pool is the right list to move along
    /// there: those are the stations chosen for this vibe, and one of them is what the listener is
    /// hearing — whereas the transport's normal "next" walks the user's own saved stations, which
    /// have nothing to do with the session and jump the listener somewhere unrelated.</para>
    /// </summary>
    public Station? NextPoolStation(Station? current)
    {
        lock (_activeLock)
            return NextInPool(_active.Select(h => h.Station).ToList(), current);
    }

    /// <summary>
    /// The cycling itself, pure so the wrap and the awkward cases can be tested without a live
    /// pool — harvesters only exist while connected to real streams.
    ///
    /// <para>A <paramref name="current"/> that is not in the pool starts from the beginning rather
    /// than returning nothing: a station can be retired out from under the bridge at any moment,
    /// and "the station you were on is gone" is not a reason to refuse to move.</para>
    /// </summary>
    internal static Station? NextInPool(IReadOnlyList<Station> pool, Station? current)
    {
        if (pool is null || pool.Count == 0)
            return null;

        var at = -1;
        if (current is not null)
        {
            for (var i = 0; i < pool.Count; i++)
            {
                if (string.Equals(pool[i].Url, current.Url, StringComparison.OrdinalIgnoreCase))
                {
                    at = i;
                    break;
                }
            }
        }
        return pool[(at + 1) % pool.Count];
    }

    /// <summary>How many stations are connected right now — what makes "skip" meaningful while
    /// bridging.</summary>
    public int ActiveStationCount
    {
        get { lock (_activeLock) return _active.Count; }
    }

    // --- Mid-session top-up (#41) ---------------------------------------------

    /// <summary>Starts (or restarts, on a vibe change) the bookkeeping a top-up needs: the prompt to
    /// re-source with, the stations already spoken for, and whether this pool began handicapped.</summary>
    private void BeginPoolTracking(string prompt, List<Station> stations, bool fromLocalCatalog)
    {
        _prompt = prompt;
        _startedOffline = fromLocalCatalog;
        _reachedDirectorySinceOfflineStart = false;
        _poolSourcedUtc = DateTime.UtcNow;
        _lastTopUpUtc = DateTime.MinValue;
        lock (_triedLock)
        {
            _triedKeys.Clear();
            AddTriedKeys(stations);
        }
    }

    /// <summary>Caller holds <see cref="_triedLock"/>.</summary>
    private void AddTriedKeys(IEnumerable<Station> stations)
    {
        foreach (var s in stations)
            foreach (var key in DedupeKeys(s.Url, s.Name))
                _triedKeys.Add(key);
    }

    /// <summary>
    /// The identity of a station for pooling purposes, as two keys. Both the pool's own dedupe and
    /// the "already tried this session" exclusion (#41) go through here, because two implementations
    /// of the same rule is how exclusion silently stops working.
    /// <para>
    /// The name key is what stops codec variants of one station — "… (128k MP3)" and "… (128k AAC)"
    /// — from taking two harvester slots and recording every song twice. The prefixes keep a url
    /// from ever colliding with a name.
    /// </para>
    /// </summary>
    internal static IEnumerable<string> DedupeKeys(string url, string name)
    {
        yield return "url:" + url;
        yield return "name:" + StationNameFormatter.IdentityKey(name);
    }

    /// <summary>
    /// Whether a top-up attempt may run now. Pure so the throttle can be tested without waiting ten
    /// real minutes — and it is the part worth pinning: too eager and a run of harvester deaths
    /// turns into a run of paid ranker calls, too lazy and a shrinking pool stays shrunk.
    /// </summary>
    internal static bool MayTopUp(DateTime lastAttemptUtc, DateTime nowUtc, TimeSpan cooldown) =>
        nowUtc - lastAttemptUtc >= cooldown;

    /// <summary>
    /// Whether an offline-started session should try to heal itself yet. Keeps saying yes (subject to
    /// the cooldown above) until a top-up has actually reached the directory, because until then the
    /// pool really is degraded — stale urls, thin reserve — and there is always something to fix.
    /// </summary>
    internal static bool ShouldHealOfflineStart(bool startedOffline, bool reachedDirectory,
        DateTime poolSourcedUtc, DateTime nowUtc, TimeSpan healAfter) =>
        startedOffline && !reachedDirectory && nowUtc - poolSourcedUtc >= healAfter;

    /// <summary>
    /// One eager healing attempt for a session that started on the local catalog, driven off the
    /// watchdog tick. Keeps trying on the cooldown until it actually reaches the directory: unlike a
    /// healthy session, this pool is degraded for as long as the outage lasts, so there is always
    /// something to fix. It stops the moment a top-up comes back from the directory.
    /// </summary>
    private void HealOfflineStartIfDue()
    {
        if (!_running)
            return;
        if (ShouldHealOfflineStart(_startedOffline, _reachedDirectorySinceOfflineStart,
                _poolSourcedUtc, DateTime.UtcNow, OfflineHealAfter))
            BeginTopUp("started offline");
    }

    /// <summary>
    /// Fire-and-forget attempt to refill the reserve, throttled by <see cref="TopUpCooldown"/>.
    /// Never blocks the caller: both trigger points (<see cref="Retire"/> and the watchdog) run on
    /// the harvest dispatcher thread, and sourcing awaits the network.
    /// </summary>
    private void BeginTopUp(string reason)
    {
        if (Interlocked.CompareExchange(ref _topUpInFlight, 1, 0) != 0)
            return; // one at a time

        var now = DateTime.UtcNow;
        if (!MayTopUp(_lastTopUpUtc, now, TopUpCooldown))
        {
            Volatile.Write(ref _topUpInFlight, 0);
            return;
        }
        _lastTopUpUtc = now;

        var prompt = _prompt;
        // Compared, not stamped: if the vibe changes while this is in flight, the result belongs to
        // a pool that no longer exists and is thrown away (see the #30 regression for why a captured
        // generation must never be used to *label* an arrival).
        var generation = VibeGeneration;

        _ = Task.Run(async () =>
        {
            try
            {
                await TopUpAsync(prompt, generation, reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best-effort by design: a session with a shrinking pool is still a session.
                AppLog.Warn($"[Dj] pool top-up failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _topUpInFlight, 0);
            }
        });
    }

    private async Task TopUpAsync(string prompt, int generation, string reason)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !_running)
            return;

        var want = _harvesterCount + _reserveCount;
        AppLog.Info($"[Dj] topping up the pool ({reason}) — looking for up to {want} station(s) "
                    + "this session hasn't tried");

        List<string> exclude;
        lock (_triedLock)
            exclude = _triedKeys.ToList();

        // A background top-up must not overwrite what the UI is reporting about how the session
        // started — LastSourcingOutcome answers "why is there no session", not "how is it going".
        var reportedOutcome = LastSourcingOutcome;
        SourcingResult sourced;
        try
        {
            sourced = await SourceStationsAsync(prompt, want, CancellationToken.None,
                allowWebEscalation: false, exclude: exclude).ConfigureAwait(false);
        }
        finally
        {
            LastSourcingOutcome = reportedOutcome;
        }

        if (sourced.Stations.Count == 0)
        {
            AppLog.Info("[Dj] top-up found nothing this session hasn't already tried");
            return;
        }

        var dispatcher = _harvestDispatcher;
        if (dispatcher is null || !_running || VibeGeneration != generation)
        {
            AppLog.Info("[Dj] top-up discarded — the session or vibe moved on while it was sourcing");
            return;
        }

        dispatcher.Post(() => InstallTopUp(sourced, generation));
    }

    /// <summary>
    /// Adds the new stations to the reserve and immediately fills any harvester slot that was lost,
    /// which is the whole point — a slot lost to an empty reserve never came back before. Runs on
    /// the harvest dispatcher, the same thread every other harvester lifecycle call uses.
    /// </summary>
    private void InstallTopUp(SourcingResult sourced, int generation)
    {
        if (!_running || VibeGeneration != generation)
            return;

        lock (_triedLock)
            AddTriedKeys(sourced.Stations);

        foreach (var station in sourced.Stations)
            _reserve.Enqueue(station);

        int missing;
        lock (_activeLock)
            missing = _harvesterCount - _active.Count;

        var started = 0;
        for (var i = 0; i < missing && _reserve.Count > 0; i++)
        {
            StartHarvester(_reserve.Dequeue());
            started++;
        }

        // Reaching the directory is what ends the offline handicap — not the passage of time, and
        // not a top-up that fell back to the same local catalog the pool already came from.
        if (!sourced.FromLocalCatalog)
            _reachedDirectorySinceOfflineStart = true;

        SessionLog?.PoolToppedUp(sourced.Stations.Count, started, sourced.FromLocalCatalog);
        AppLog.Info($"[Dj] top-up added {sourced.Stations.Count} station(s) "
                    + $"({(sourced.FromLocalCatalog ? "local catalog" : "directory")}); "
                    + $"{started} slot(s) refilled; reserve now {_reserve.Count}");
        RaiseStatus();
    }

    /// <summary>
    /// Stations from the local catalog alone, for when the directory is unreachable. Asks for a
    /// wider net than the pool needs (the ranker still has to throw some away) but every hit is
    /// already above the cosine floor, so a small result here means the catalog genuinely doesn't
    /// cover this vibe.
    /// </summary>
    private async Task<List<SourceCandidate>> SourceFromLocalCatalogAsync(
        string prompt, int count, CancellationToken ct)
    {
        if (_semanticSearch is null || !_semanticSearch.IsAvailable)
        {
            AppLog.Warn("[Dj] no local semantic index available — nothing to fall back on");
            return [];
        }

        var hits = await _semanticSearch.SearchOfflineAsync(prompt, count * 2, ct: ct).ConfigureAwait(false);
        AppLog.Info($"[Dj] local catalog offered {hits.Count} station(s) above the relevance floor");
        return hits.Select(h => new SourceCandidate(
            h.Station,
            // The cached description IS the thing that matched — hand the ranker the same text.
            string.IsNullOrWhiteSpace(h.Description) ? h.Station.Name : h.Description!,
            h.Country)).ToList();
    }


    /// <summary>
    /// Judges the whole candidate pool for genuine relevance to the prompt via the same
    /// LLM-backed ranker the visible search uses, returning only stations that pass — best-first,
    /// never padded with weak matches to reach <paramref name="count"/>. Degrades to the raw pool
    /// (unfiltered) only when the ranker itself can't run at all (no API key) — that's a
    /// capability fallback, not a substitute for the real judgment call.
    /// </summary>
    /// <param name="rankerRequired">
    /// True when the pool came from the offline catalog (#26). That degrade-to-the-raw-pool escape
    /// hatch is the worst possible move there: the directory pool it was written for is at least
    /// sorted by votes and tag-matched, whereas an unranked cosine pool on a thin catalog is a
    /// list of the least-bad matches. So refuse instead — an empty result the caller can explain
    /// beats a session of stations that don't fit the vibe.
    /// </param>
    private async Task<List<Station>> RankRelevantAsync(
        string prompt, List<SourceCandidate> pool, int count, bool rankerRequired, CancellationToken ct)
    {
        if (pool.Count == 0)
            return [];

        List<Station> Unranked()
        {
            if (!rankerRequired)
                return pool.Take(count).Select(c => c.Station).ToList();

            AppLog.Warn("[Dj] directory down AND ranker unavailable — refusing to start a session "
                        + "on unranked local matches");
            LastSourcingOutcome = DjSourcingOutcome.OfflineUnranked;
            return [];
        }

        if (!_ranker.IsConfigured)
            return Unranked();

        var candidates = pool.Select((c, i) => new RankCandidate(i, c.Station.Name, c.DescriptiveText, c.Country)).ToList();
        var verdicts = await _ranker.RankAsync(prompt, candidates, count, ct).ConfigureAwait(false);
        if (verdicts is null) // ranker unavailable for this call specifically — degrade, don't block starting DJ mode
            return Unranked();

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
    /// Adds candidates the pool doesn't already have, by URL <b>and</b> by station identity — see
    /// <see cref="DedupeKeys"/> for what those are and why there are two. A pre-seeded
    /// <paramref name="seen"/> is also how a top-up excludes stations already tried this session.
    /// </summary>
    private static void AddUniqueCandidates(
        List<SourceCandidate> pool, HashSet<string> seen, IEnumerable<SourceCandidate> candidates)
    {
        foreach (var c in candidates)
        {
            // Short-circuits: a candidate rejected on its url does NOT register its name. Kept as
            // it was — see DuplicateStreamTests for the behaviour this pins.
            var fresh = true;
            foreach (var key in DedupeKeys(c.Station.Url, c.Station.Name))
            {
                if (!seen.Add(key))
                {
                    fresh = false;
                    break;
                }
            }
            if (fresh)
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
