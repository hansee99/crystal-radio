using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>A fully-captured song segment, announced when the song's end boundary arrives.</summary>
public sealed record CompletedSegment(
    string FileName, long Bytes, string Title, string? Artist, string? Station, DateTime StartedAt);

/// <summary>
/// The rolling audio cache: receives the raw encoded bytes of the stream being played (via the
/// engine's download callback — same connection as playback, no re-encode) and cuts them into
/// per-song segment files at ICY title boundaries, in %LocalAppData%\RadioPlayer\cache.
///
/// <para><b>Deferred cuts.</b> Stations push the new title when a song starts in the studio,
/// but the audio passes through their encoder pipeline first — so in the delivered byte stream
/// the metadata LEADS the audio by several seconds. Cutting at the metadata instant would give
/// every file the previous song's tail and cost it its own ending. Each boundary therefore
/// starts a countdown of (offset seconds × stream byte rate) bytes that continue to flow into
/// the CURRENT segment; the cut executes byte-exactly when the countdown hits zero. The byte
/// rate comes from the decoder's reported bitrate, falling back to the measured session rate.</para>
///
/// Only COMPLETE segments survive — ones that both start and end at an (offset-corrected)
/// boundary within a single connection. The head segment of every session (joined mid-song),
/// anything cut short by stop/reconnect, titles failing the ad/jingle filter, and blips smaller
/// than <see cref="MinSegmentBytes"/> are deleted on the spot, so the cache only ever holds
/// saveable audio. <see cref="SegmentCompleted"/> fires (marshalled to the UI thread) for each
/// survivor; the view model owns attaching segments to history rows and the size cap.
///
/// Threading: <see cref="Write"/> runs on BASS network threads; <see cref="OnTrackChanged"/> and
/// the session calls on the UI thread. One lock guards the segment/pending state — boundaries
/// are inherently approximate, so the trivial contention is harmless.
/// </summary>
public sealed class StreamRecorder : IDisposable
{
    /// <summary>Default cache directory — the live "Save Song"/RadioEngine recorder's own
    /// scratch space. A <see cref="StreamRecorder"/> instance backing a headless harvester
    /// passes its own directory to the constructor instead (see <see cref="_cacheDir"/>), so
    /// concurrent instances (N harvesters, or a harvester alongside this live recorder) never
    /// write into the same folder.</summary>
    public static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioPlayer", "cache");

    private readonly string _cacheDir;

    // A "complete" segment shorter than this is almost certainly a sweeper/ident that slipped
    // the title filter (~12s at 128 kbps), not a song worth offering to save.
    private const long MinSegmentBytes = 200 * 1024;

    // Byte-rate fallback (128 kbps) until the decoder reports a bitrate or enough of the
    // session has flowed to measure one.
    private const double FallbackBytesPerSecond = 16_000;

    private readonly Dispatcher _dispatcher;
    private readonly double _boundaryOffsetSeconds;

    private readonly object _lock = new();
    private int _sessionSeq;
    private int _activeSession;              // 0 = no active session
    private string _extension = ".mp3";
    private bool _seenBoundary;              // whether this session has executed a cut yet

    private FileStream? _current;            // the segment being captured right now
    private string? _currentPath;
    private bool _currentIsHead;             // opened at the session's FIRST boundary → joined mid-song
    private (string Title, string? Artist, string? Station, DateTime StartedAt) _pending;

    // Deferred cut: bytes still owed to the current segment before the next one begins.
    // -1 = no cut pending.
    private long _cutRemaining = -1;
    private (string Title, string? Artist, string? Station) _cutNext;

    // Measured session byte rate (fallback when the decoder doesn't report a bitrate).
    private long _sessionBytes;
    private readonly Stopwatch _sessionClock = new();

    private byte[] _copyBuffer = new byte[64 * 1024];

    /// <summary>Raised on the UI thread when a song's segment finished capturing completely.</summary>
    public event EventHandler<CompletedSegment>? SegmentCompleted;

    /// <param name="boundaryOffsetSeconds">How far the stream's title changes lead its audio;
    /// cuts are delayed by this much. Station encoders differ — ~6s fits many.</param>
    /// <param name="cacheDir">Scratch directory for in-progress/completed segment files before
    /// the caller copies a keeper elsewhere. Defaults to <see cref="CacheDir"/> (the live
    /// recorder's shared space); pass a dedicated directory for a headless harvester instance
    /// so concurrent recorders never collide.</param>
    public StreamRecorder(double boundaryOffsetSeconds = 6.0, string? cacheDir = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _boundaryOffsetSeconds = Math.Clamp(boundaryOffsetSeconds, 0.0, 30.0);
        _cacheDir = cacheDir ?? CacheDir;
    }

    /// <summary>Absolute path of one of this instance's cached segment files.</summary>
    public string PathFor(string fileName) => Path.Combine(_cacheDir, fileName);

    /// <summary>
    /// Starts a capture session for a new stream connection. Returns the session id the engine
    /// threads through <see cref="Write"/>/<see cref="OnTrackChanged"/>/<see cref="EndSession"/>,
    /// so callbacks from a superseded connection can't write into the new one.
    /// </summary>
    public int BeginSession(Station station, StreamFormat format)
    {
        lock (_lock)
        {
            CloseCurrentLocked(complete: false); // a previous session's tail is partial by definition
            _activeSession = ++_sessionSeq;
            _extension = format == StreamFormat.Aac ? ".aac" : ".mp3";
            _seenBoundary = false;
            _cutRemaining = -1;
            _sessionBytes = 0;
            _sessionClock.Restart();
            return _activeSession;
        }
    }

    /// <summary>
    /// Appends raw stream bytes to the current segment, executing a pending cut byte-exactly
    /// when its countdown is used up. Called on a BASS network thread; must stay cheap.
    /// </summary>
    public void Write(int session, IntPtr buffer, int length)
    {
        if (buffer == IntPtr.Zero || length <= 0)
            return; // BASS signals end-of-download with a null buffer

        CompletedSegment? done = null;
        lock (_lock)
        {
            if (session != _activeSession)
                return;

            _sessionBytes += length;

            if (_copyBuffer.Length < length)
                _copyBuffer = new byte[length];
            Marshal.Copy(buffer, _copyBuffer, 0, length);

            if (_cutRemaining < 0)
            {
                WriteToCurrentLocked(_copyBuffer, 0, length);
                return;
            }

            // A cut is pending: the first part of this chunk still belongs to the old segment.
            var take = (int)Math.Min(_cutRemaining, length);
            if (take > 0)
                WriteToCurrentLocked(_copyBuffer, 0, take);
            _cutRemaining -= take;

            if (_cutRemaining > 0)
                return; // countdown continues into the next chunk

            done = ExecuteCutLocked();
            if (length - take > 0)
                WriteToCurrentLocked(_copyBuffer, take, length - take);
        }
        if (done is not null)
            _dispatcher.BeginInvoke(() => SegmentCompleted?.Invoke(this, done));
    }

    /// <summary>
    /// A title boundary: schedule the (offset-delayed) cut. The finished song's remaining bytes
    /// are still in flight — the actual cut happens inside <see cref="Write"/> once
    /// offset × byte-rate more bytes have flowed into it.
    /// </summary>
    /// <param name="bytesPerSecond">Decoder-reported stream byte rate; ≤ 0 when unknown.</param>
    public void OnTrackChanged(int session, string title, string? artist, string? station, double bytesPerSecond)
    {
        CompletedSegment? done = null;
        lock (_lock)
        {
            if (session != _activeSession)
                return;

            // Two boundaries within one offset window (short jingle): the first cut hasn't
            // executed yet — do it now at the current position rather than losing it. The
            // resulting sliver is discarded by the min-size/filter rules anyway.
            if (_cutRemaining >= 0)
                done = ExecuteCutLocked();

            _cutNext = (title, artist, station);
            _cutRemaining = (long)(_boundaryOffsetSeconds * EffectiveBytesPerSecondLocked(bytesPerSecond));
            if (_cutRemaining <= 0)
            {
                var immediate = ExecuteCutLocked(); // offset 0 → cut right at the boundary
                done ??= immediate;
            }
        }
        if (done is not null)
            _dispatcher.BeginInvoke(() => SegmentCompleted?.Invoke(this, done));
    }

    /// <summary>Stream ended (stop, reconnect, or superseded connect) — the tail is partial.</summary>
    public void EndSession(int session)
    {
        lock (_lock)
        {
            if (session != _activeSession)
                return;
            CloseCurrentLocked(complete: false);
            _cutRemaining = -1;
            _activeSession = 0;
            _sessionClock.Stop();
        }
    }

    /// <summary>
    /// Deletes cache files not referenced by any history row (startup sweep for files orphaned
    /// by a crash or an old history file).
    /// </summary>
    public static void SweepOrphans(IEnumerable<string> referencedFileNames)
    {
        try
        {
            if (!Directory.Exists(CacheDir))
                return;
            var referenced = new HashSet<string>(referencedFileNames, StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(CacheDir))
            {
                if (!referenced.Contains(Path.GetFileName(path)))
                    TryDelete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Best byte-rate estimate: decoder-reported, else measured over the session so
    /// far (ignoring the first seconds, which include the server's connect burst), else the
    /// 128 kbps fallback. Must be called under the lock.</summary>
    private double EffectiveBytesPerSecondLocked(double reported)
    {
        if (reported > 1_000)
            return reported;
        var elapsed = _sessionClock.Elapsed.TotalSeconds;
        return elapsed > 20 ? _sessionBytes / elapsed : FallbackBytesPerSecond;
    }

    /// <summary>Closes the current segment as complete and opens the next (from the pending
    /// cut's metadata). Returns the closed segment when it's a keeper. Under the lock.</summary>
    private CompletedSegment? ExecuteCutLocked()
    {
        var done = CloseCurrentLocked(complete: true);

        try
        {
            Directory.CreateDirectory(_cacheDir);
            // Ticks alone risk collisions once several StreamRecorder instances (concurrent
            // harvesters, or a harvester alongside the live recorder) cut segments within the
            // same ~15ms clock-resolution window; the suffix makes that effectively impossible
            // even though instances no longer share a directory anyway (belt and braces).
            _currentPath = $"{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..8]}{_extension}";
            _current = new FileStream(PathFor(_currentPath), FileMode.Create, FileAccess.Write, FileShare.Read);
            _currentIsHead = !_seenBoundary; // first cut of the session opens a mid-song segment
            _pending = (_cutNext.Title, _cutNext.Artist, _cutNext.Station, DateTime.Now);
        }
        catch
        {
            _current = null;
            _currentPath = null;
        }
        _seenBoundary = true;
        _cutRemaining = -1;
        return done;
    }

    private void WriteToCurrentLocked(byte[] data, int offset, int count)
    {
        if (_current is null)
            return;
        try
        {
            _current.Write(data, offset, count);
        }
        catch
        {
            // Disk full/locked — drop this segment rather than disturb playback.
            DiscardCurrentLocked();
        }
    }

    /// <summary>
    /// Closes the in-progress segment. Returns its descriptor when it is a keeper — closed at a
    /// boundary, not the session head, song-like, and big enough — otherwise deletes the file.
    /// Must be called under the lock.
    /// </summary>
    private CompletedSegment? CloseCurrentLocked(bool complete)
    {
        if (_current is null)
            return null;

        long bytes = 0;
        try
        {
            bytes = _current.Length;
            _current.Dispose();
        }
        catch
        {
            complete = false;
        }
        var path = _currentPath!;
        var isHead = _currentIsHead;
        var pending = _pending;
        _current = null;
        _currentPath = null;
        _currentIsHead = false;

        var keep = complete
                   && !isHead
                   && bytes >= MinSegmentBytes
                   && SongHistoryFilter.IsLikelySong(pending.Title, pending.Artist, pending.Station);
        if (!keep)
        {
            TryDelete(PathFor(path));
            return null;
        }
        return new CompletedSegment(path, bytes, pending.Title, pending.Artist, pending.Station, pending.StartedAt);
    }

    private void DiscardCurrentLocked()
    {
        try { _current?.Dispose(); } catch { }
        if (_currentPath is not null)
            TryDelete(PathFor(_currentPath));
        _current = null;
        _currentPath = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseCurrentLocked(complete: false);
            _cutRemaining = -1;
            _activeSession = 0;
        }
    }
}
