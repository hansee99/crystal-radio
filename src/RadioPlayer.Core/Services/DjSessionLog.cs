using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace RadioPlayer.Services;

/// <summary>
/// Records what a DJ session actually did, so harvester tuning is a measurement rather than a
/// guess. Writes a plain-text log per session under %LocalAppData%\RadioPlayer\dj-sessions\ —
/// a timestamped line per event while it runs, then a summary block with the numbers the
/// tuning question turns on: keep rate, fill rate vs play rate, the resulting surplus, and
/// which stations actually earned their slot.
///
/// Deliberately best-effort and self-contained: every public method swallows its own I/O
/// failures, because a diagnostic must never be able to take down a harvest session. Events
/// arrive from the harvest thread, QC worker threads and the UI thread, so all state is locked.
/// </summary>
public sealed class DjSessionLog
{
    private const int KeepSessions = 10; // prune older logs; they're small but not worth hoarding

    private readonly object _gate = new();
    private readonly string _path;
    private readonly DateTime _started = DateTime.Now;
    private readonly Dictionary<string, StationTally> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _playedPaths = new(StringComparer.OrdinalIgnoreCase);

    private int _kept;
    private int _rejected;
    private int _played;
    private int _evicted;
    private int _evictedUnplayed;
    private int _vibeChanges;
    private double _trimmedSeconds;
    private bool _finished;

    private DjSessionLog(string path) => _path = path;

    /// <summary>Where this session's log is being written.</summary>
    public string FilePath => _path;

    /// <summary>Opens a log for a new session. Returns null if logging can't be set up — callers
    /// treat the log as optional throughout. <paramref name="directory"/> overrides the default
    /// location (tests use it to stay out of the real session folder).</summary>
    public static DjSessionLog? Start(string prompt, int harvesters, int reserve, long cacheCapBytes,
        string? directory = null)
    {
        try
        {
            var dir = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "RadioPlayer", "dj-sessions");
            Directory.CreateDirectory(dir);
            Prune(dir);

            var path = Path.Combine(dir, $"dj-session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var log = new DjSessionLog(path);

            var header = new StringBuilder()
                .AppendLine("Crystal Radio · DJ session log")
                .AppendLine($"Started  {log._started:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"Vibe     \"{prompt}\"")
                .AppendLine($"Config   {harvesters} harvesters · {reserve} in reserve · "
                            + $"{cacheCapBytes / (1024 * 1024)} MB cache cap")
                .AppendLine()
                .AppendLine("  time  event     detail");
            File.WriteAllText(path, header.ToString(), Encoding.UTF8);

            AppLog.Debug($"[DjSession] logging to {path}");
            return log;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[DjSession] couldn't start logging: {ex.Message}");
            return null;
        }
    }

    public void HarvesterStarted(string station)
    {
        lock (_gate)
        {
            Tally(station).Started++;
            Write("station+", station);
        }
    }

    /// <summary>A harvester left the pool. <paramref name="reason"/> separates a connection that
    /// failed from one dropped for serving no ICY metadata — the latter is a sourcing problem,
    /// not a network one, and they need telling apart when reading a session back.</summary>
    public void HarvesterDied(string station, string reason = "died")
    {
        lock (_gate)
        {
            Tally(station).Died++;
            Write("station-", $"{station} ({reason})");
        }
    }

    /// <summary>
    /// One segment finished QC. <paramref name="confidence"/> is the percentage of one-second
    /// windows the detector called music, <paramref name="seconds"/> the segment's length, and
    /// <paramref name="leadTrim"/>/<paramref name="tailTrim"/> how much audio the edge-trim cut.
    /// The trims matter as much as the verdict: they're driven by the SAME per-window
    /// classification, so on material the detector reads badly it will also be shaving real
    /// music off the songs it does keep — silently, unless it's recorded here.
    /// </summary>
    public void SegmentCompleted(string station, string? title, bool kept, double confidence,
        double seconds, string? verdict = null, double leadTrim = 0, double tailTrim = 0,
        string? note = null)
    {
        lock (_gate)
        {
            var tally = Tally(station);
            if (kept) { tally.Kept++; _kept++; }
            else { tally.Rejected++; _rejected++; }

            // Only kept segments count toward the trim totals: a rejected file is discarded, so
            // its measured trim costs nothing. The number that matters is audio actually lost
            // from songs that made it into the mix.
            if (kept && (leadTrim > 0 || tailTrim > 0))
            {
                tally.Trimmed++;
                _trimmedSeconds += leadTrim + tailTrim;
            }

            var detail = new StringBuilder()
                .Append(station).Append(" · ").Append(title ?? "(untitled)")
                .Append(" (conf ").Append(confidence.ToString("0.00", CultureInfo.InvariantCulture));
            if (verdict is not null)
                detail.Append('/').Append(verdict);
            detail.Append(", ").Append(Mmss(seconds));
            if (leadTrim > 0 || tailTrim > 0)
                detail.Append(", trimmed ")
                      .Append(leadTrim.ToString("0.0", CultureInfo.InvariantCulture)).Append("s+")
                      .Append(tailTrim.ToString("0.0", CultureInfo.InvariantCulture)).Append('s');
            detail.Append(')');
            if (!string.IsNullOrWhiteSpace(note))
                detail.Append(" — ").Append(note); // why it was rejected, or what the trim did

            Write(kept ? "kept" : "rejected", detail.ToString());
        }
    }

    /// <summary>
    /// A boundary arrived but produced nothing. Without this the log can't tell a station that
    /// announced no titles at all from one whose every announcement was filtered out — both look
    /// like silence after the station+ line, and the two need entirely different fixes (drop the
    /// station vs. loosen the filter). Diagnosed exactly this way on 2026-08-02: two harvesters
    /// on the same stream, one logging a sliver the other silently dropped for carrying the
    /// station's own name.
    /// </summary>
    public void BoundarySkipped(string station, DiscardReason reason, string? title, string? artist)
    {
        lock (_gate)
        {
            Tally(station).Skipped++;
            var what = string.IsNullOrWhiteSpace(artist) ? title ?? "(untitled)" : $"{artist} - {title}";
            Write("skipped", $"{station} · {what} ({Describe(reason)})");
        }
    }

    /// <summary>
    /// How many ICY titles a station has announced. The counterpart to
    /// <see cref="BoundarySkipped"/>: a discard only happens when a segment gets CLOSED, which
    /// takes a second boundary, so a station that announces one title and never changes it
    /// (a long mix) produces no events at all. Only the title count separates that from a station
    /// serving no metadata whatsoever. Pushed periodically and again on retirement, so a dropped
    /// station keeps its final figure.
    /// </summary>
    public void TitlesSeen(string station, int count)
    {
        lock (_gate)
        {
            var tally = Tally(station);
            if (count > tally.Titles)
                tally.Titles = count;
        }
    }

    private static string Describe(DiscardReason reason) => reason switch
    {
        DiscardReason.MidSongHead => "joined mid-song — first boundary",
        DiscardReason.TooShort => "too short",
        DiscardReason.NotSongLike => "not song-like: no artist, an ad marker, or the station's own name",
        DiscardReason.WriteFailed => "capture file couldn't be closed",
        _ => reason.ToString()
    };

    /// <summary>
    /// The listener changed the vibe mid-session. Recorded as an event rather than starting a
    /// second log: it is one listening session, and the summary's rates only mean anything when
    /// measured across the whole of it. But every number after this line belongs to a different
    /// prompt, so reading a session back without knowing where the change happened would be
    /// misleading — a station retired for "no songs" right after a swap is a different story from
    /// one that sat idle for ten minutes on its own vibe.
    /// </summary>
    public void VibeChanged(string prompt, int stations)
    {
        lock (_gate)
        {
            _vibeChanges++;
            Write("vibe", $"\"{prompt}\" — {stations} station(s) swapped in");
        }
    }

    /// <summary>
    /// New stations joined the pool mid-session (#41). Worth a line for the same reason a vibe
    /// change is: reading a session back, a station that appears an hour in is otherwise
    /// inexplicable, and whether the top-up reached the directory or fell back to the local catalog
    /// is exactly the detail that explains a run of connect failures afterwards.
    /// </summary>
    public void PoolToppedUp(int added, int slotsFilled, bool fromLocalCatalog)
    {
        lock (_gate)
        {
            Write("pool", $"topped up with {added} station(s) from "
                          + $"{(fromLocalCatalog ? "the local catalog" : "the directory")}"
                          + $" — {slotsFilled} harvester slot(s) refilled");
        }
    }

    /// <summary>A song started playing — the consumption side of the ratio.</summary>
    public void SongPlayed(string path, string title, string artist)
    {
        lock (_gate)
        {
            _played++;
            if (!string.IsNullOrEmpty(path))
                _playedPaths.Add(path);
            Write("played", $"{artist} - {title}");
        }
    }

    /// <summary>A harvested file was evicted by the cache cap. Whether it was ever played is the
    /// clearest measure of wasted bandwidth and enrichment spend.</summary>
    public void Evicted(string path)
    {
        lock (_gate)
        {
            _evicted++;
            var unplayed = !_playedPaths.Contains(path);
            if (unplayed) _evictedUnplayed++;
            Write("evicted", $"{Path.GetFileName(path)}{(unplayed ? " (never played)" : "")}");
        }
    }

    /// <summary>Appends the summary. Safe to call more than once; only the first writes.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            try { File.AppendAllText(_path, BuildSummary(), Encoding.UTF8); }
            catch (Exception ex) { AppLog.Debug($"[DjSession] summary failed: {ex.Message}"); }
        }
    }

    private string BuildSummary()
    {
        var minutes = Math.Max((DateTime.Now - _started).TotalMinutes, 0.01);
        var completed = _kept + _rejected;
        var fill = _kept / minutes;
        var play = _played / minutes;

        var sb = new StringBuilder()
            .AppendLine()
            .AppendLine($"=== SUMMARY · ran {minutes:0} min ===")
            .AppendLine($"Segments completed  {completed}")
            .AppendLine($"  kept              {_kept}{Pct(_kept, completed)}")
            .AppendLine($"  rejected          {_rejected}{Pct(_rejected, completed)}")
            .AppendLine($"Fill rate           {fill:0.00} songs/min harvested")
            .AppendLine($"Play rate           {play:0.00} songs/min consumed  ({_played} played)");

        // The number the tuning decision turns on: anything above ~1.0 is harvesting we paid
        // bandwidth + a description + an embedding for and may never hear.
        sb.AppendLine(play > 0
            ? $"Surplus             {fill / play:0.0}x  ({Math.Max(_kept - _played, 0)} kept but not played)"
            : "Surplus             n/a (nothing played yet)");

        sb.AppendLine($"Evicted             {_evicted} ({_evictedUnplayed} never played)");
        if (_vibeChanges > 0)
            sb.AppendLine($"Vibe changed        {_vibeChanges}x  — rates below span ALL of them");

        // Driven by the same per-window classification as the reject decision, so a large figure
        // here on a genre the detector reads badly means real music is being shaved off the
        // songs that DID pass.
        if (_trimmedSeconds > 0)
            sb.AppendLine($"Edge-trimmed        {_trimmedSeconds:0} s total across kept songs");

        sb.AppendLine().AppendLine("Per station:");

        foreach (var (name, t) in _stations.OrderByDescending(s => s.Value.Kept))
        {
            var done = t.Kept + t.Rejected;
            sb.AppendLine($"  {Trim(name, 34),-34} titles {t.Titles,3}   kept {t.Kept,3}"
                          + $"   rejected {t.Rejected,3}   {Pct(t.Kept, done, pad: false),-8}"
                          + $" {t.Kept / minutes:0.00}/min"
                          + (t.Skipped > 0 ? $"   skipped {t.Skipped}" : "")
                          + (t.Trimmed > 0 ? $"   trimmed {t.Trimmed}" : "")
                          + (t.Died > 0 ? $"   died {t.Died}x" : ""));
        }

        // Why a station produced nothing, in the terms that decide what to do about it. Titles vs
        // segments is the whole diagnosis: no titles is a dead directory entry, one title is a
        // long mix, and many titles with nothing kept is a filtering or QC problem — and only the
        // last one is fixed by anything other than replacing the station.
        var barren = _stations.Where(s => s.Value.Kept == 0).ToList();
        if (barren.Count > 0)
        {
            sb.AppendLine().AppendLine("Produced nothing:");
            foreach (var (name, t) in barren)
                sb.AppendLine($"  {Trim(name, 34),-34} {(t.Titles, t.Rejected + t.Skipped) switch
                {
                    (0, _) => "no ICY metadata at all — can never produce a segment",
                    (1, 0) => "announced one title and never changed it — a long mix, or no per-track metadata",
                    (_, 0) => "announced titles but completed no segment — check for a stalled stream",
                    var (_, dropped) => $"{dropped} boundary(s), all dropped — a filtering/QC problem, not the station"
                }}");
        }

        return sb.ToString();
    }

    // --- helpers (all called under _gate) --------------------------------------

    private StationTally Tally(string station)
    {
        if (!_stations.TryGetValue(station, out var tally))
            _stations[station] = tally = new StationTally();
        return tally;
    }

    private void Write(string kind, string detail)
    {
        try
        {
            var at = DateTime.Now - _started;
            File.AppendAllText(_path,
                $"{(int)at.TotalMinutes,4}:{at.Seconds:00}  {kind,-9} {detail}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[DjSession] write failed: {ex.Message}");
        }
    }

    private static string Pct(int part, int total, bool pad = true) =>
        total == 0 ? "" : (pad ? $"  ({part * 100 / total}%)" : $"({part * 100 / total}%)");

    private static string Mmss(double seconds) =>
        seconds <= 0 ? "?:??" : $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void Prune(string dir)
    {
        try
        {
            var old = new DirectoryInfo(dir).GetFiles("dj-session-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepSessions - 1);
            foreach (var file in old)
                try { file.Delete(); } catch { /* best effort */ }
        }
        catch { /* best effort */ }
    }

    private sealed class StationTally
    {
        public int Started;
        public int Died;
        public int Kept;
        public int Rejected;
        public int Trimmed;
        public int Skipped;   // boundaries that produced no segment at all
        public int Titles;    // ICY titles announced, however they ended up
    }
}
