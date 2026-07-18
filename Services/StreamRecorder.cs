using System.IO;
using System.Runtime.InteropServices;
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
/// Only COMPLETE segments survive — ones that both start and end at a title boundary within a
/// single connection. The head segment of every session (joined mid-song), anything cut short
/// by stop/reconnect, and titles that fail the ad/jingle filter are deleted on the spot, so the
/// cache only ever holds saveable audio. <see cref="SegmentCompleted"/> fires for each survivor;
/// the view model owns attaching segments to history rows and enforcing the size cap.
///
/// Threading: <see cref="Write"/> is called on BASS network threads; everything else on the UI
/// thread (via the engine's marshalled callbacks). A single lock guards the segment switch —
/// boundaries are inherently approximate (seconds), so contention is trivial and harmless.
/// </summary>
public sealed class StreamRecorder : IDisposable
{
    public static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioPlayer", "cache");

    // A "complete" segment shorter than this is almost certainly a sweeper/ident that slipped
    // the title filter (~12s at 128 kbps), not a song worth offering to save.
    private const long MinSegmentBytes = 200 * 1024;

    private readonly object _lock = new();
    private int _sessionSeq;
    private int _activeSession;              // 0 = no active session
    private string _extension = ".mp3";

    private FileStream? _current;            // the segment being captured right now
    private string? _currentPath;
    private bool _currentIsHead;             // opened at the session's FIRST boundary → joined mid-song
    private bool _seenBoundary;              // whether this session has had a title boundary yet
    private (string Title, string? Artist, string? Station, DateTime StartedAt) _pending;

    private byte[] _copyBuffer = new byte[64 * 1024];

    /// <summary>Raised (on the UI thread) when a song's segment finished capturing completely.</summary>
    public event EventHandler<CompletedSegment>? SegmentCompleted;

    /// <summary>Absolute path of a cached segment file.</summary>
    public static string PathFor(string fileName) => Path.Combine(CacheDir, fileName);

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
            return _activeSession;
        }
    }

    /// <summary>
    /// Appends raw stream bytes to the current segment. Called on a BASS network thread; must
    /// stay cheap. No-ops before the first title boundary (that audio belongs to a song we
    /// joined mid-way and could never save) and for superseded sessions.
    /// </summary>
    public void Write(int session, IntPtr buffer, int length)
    {
        if (buffer == IntPtr.Zero || length <= 0)
            return; // BASS signals end-of-download with a null buffer

        lock (_lock)
        {
            if (session != _activeSession || _current is null)
                return;
            if (_copyBuffer.Length < length)
                _copyBuffer = new byte[length];
            Marshal.Copy(buffer, _copyBuffer, 0, length);
            try
            {
                _current.Write(_copyBuffer, 0, length);
            }
            catch
            {
                // Disk full/locked — drop this segment rather than disturb playback.
                DiscardCurrentLocked();
            }
        }
    }

    /// <summary>
    /// A title boundary: finalize the segment that just ended (keep only if complete and
    /// song-like) and start capturing the next one.
    /// </summary>
    public void OnTrackChanged(int session, string title, string? artist, string? station)
    {
        CompletedSegment? done;
        lock (_lock)
        {
            if (session != _activeSession)
                return;

            done = CloseCurrentLocked(complete: true);

            // Open the next segment. The session's first boundary announces the song already
            // in progress — capture it anyway but flag it as head (mid-song) so it's discarded
            // at its close.
            try
            {
                Directory.CreateDirectory(CacheDir);
                _currentPath = $"{DateTime.UtcNow.Ticks}{_extension}";
                _current = new FileStream(PathFor(_currentPath), FileMode.Create, FileAccess.Write, FileShare.Read);
                _currentIsHead = !_seenBoundary;
                _pending = (title, artist, station, DateTime.Now);
            }
            catch
            {
                _current = null;
                _currentPath = null;
            }
            _seenBoundary = true;
        }
        if (done is not null)
            SegmentCompleted?.Invoke(this, done);
    }

    /// <summary>Stream ended (stop, reconnect, or superseded connect) — the tail is partial.</summary>
    public void EndSession(int session)
    {
        lock (_lock)
        {
            if (session != _activeSession)
                return;
            CloseCurrentLocked(complete: false);
            _activeSession = 0;
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
            _activeSession = 0;
        }
    }
}
