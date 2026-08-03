using System.Runtime.InteropServices;
using System.Windows.Threading;
using ManagedBass;
using ManagedBass.Aac;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// One headless harvesting connection (DJ-MODE-SPEC-HARVEST.md §6.1): downloads a station into
/// its own <see cref="StreamRecorder"/> and cuts complete songs at ICY boundaries — no output
/// device, no playback coupling. Promoted from tools/DjHarvest's PoC <c>Harvester</c> class;
/// segment QC (reject/edge-trim) is deliberately NOT this class's job — it just raises
/// <see cref="SegmentCompleted"/> with the raw cut, same as <see cref="StreamRecorder"/> itself,
/// so <see cref="DjHarvestService"/> owns the QC/indexing/eviction decisions.
///
/// Threading: must be constructed and driven (Start/Stop, and BASS calls in general) from the
/// dedicated harvest thread <see cref="DjHarvestService"/> owns — that thread's BASS
/// "current device" is device 0 (the silent device) for its whole lifetime, isolated by BASS's
/// per-thread device selection from the UI thread's real output device (see the field comment
/// on <c>RadioEngine._realDeviceIndex</c> for the full explanation of why this matters).
/// </summary>
public sealed class StreamHarvester : IDisposable
{
    private const int MaxReconnects = 5;

    private readonly string _url;
    private readonly StreamFormat _format;
    private readonly StreamRecorder _recorder;
    private readonly Dispatcher _dispatcher;
    private readonly DownloadProcedure _dl; // rooted so BASS's native pointer stays valid
    private SyncProcedure? _metaSync, _endSync, _stallSync;

    private int _handle;
    private int _session;
    private int _titles;
    private int _reconnects;
    private volatile bool _dead;

    // Bounded title history for the duplicate check. Read from the harvest dispatcher (the
    // watchdog) and written from it too, but the lock is cheap and keeps the contract obvious.
    private const int TitleHistory = 8;
    private readonly object _titleLock = new();
    private readonly LinkedList<string> _recentTitles = new();

    public string Label { get; }
    public bool Dead => _dead;
    public int TitlesSeen => Volatile.Read(ref _titles);

    /// <summary>When <see cref="Start"/> first connected. Paired with <see cref="TitlesSeen"/>
    /// this is how the pool spots a station that serves no ICY metadata at all: no title changes
    /// means no song boundaries, which means it can never produce a segment however long it runs.
    /// Deliberately not reset by reconnects — metadata support is a property of the station.</summary>
    public DateTime ConnectedAt { get; private set; }

    /// <summary>
    /// When this harvester last produced a completed segment, or <see cref="DateTime.MinValue"/>
    /// if it never has. Together with <see cref="ConnectedAt"/> this is how the pool spots a
    /// station that isn't contributing — most often one playing a long DJ set or extended mix,
    /// which announces a single ICY title and therefore yields no song boundaries for as long as
    /// it runs. Deliberately measured on segments rather than title changes: some stations
    /// re-announce the same title mid-track, so "the title changed" is not the same question as
    /// "did we get a song".
    /// </summary>
    public DateTime LastSegmentAt { get; private set; }

    /// <summary>
    /// The stream's own <c>icy-name</c>, read on connect — the identity of the AUDIO, as opposed
    /// to <see cref="Label"/>, which is the directory's name for one of possibly several entries
    /// pointing at it. Two directory entries can carry different names and different URLs and
    /// still be the same stream (observed: "Liquid DnB" at <c>antares.dribbcast.com/proxy/dave1/</c>
    /// and "DnB Liquified" at <c>antares.dribbcast.com:5000</c> — same icy-name, byte-identical
    /// StreamTitles), which no amount of pre-connect deduplication can catch. Null/blank when the
    /// station serves no icy-name, in which case it simply can't be compared.
    /// </summary>
    public string? StreamName { get; private set; }

    /// <summary>
    /// The most recent ICY titles this harvester has seen, newest first. The duplicate check's
    /// fallback when <see cref="StreamName"/> is unavailable: two harvesters on one stream see the
    /// same titles, and ORF's feeds serve no icy-name at all — measured 2026-08-03, both FM4
    /// bitrates returned <c>icy-name: (absent)</c> with byte-identical StreamTitles, so nothing
    /// else could tell they were one broadcast.
    /// </summary>
    public IReadOnlyCollection<string> RecentTitles
    {
        get { lock (_titleLock) return _recentTitles.ToArray(); }
    }

    /// <summary>Raised on the harvest dispatcher thread for each complete, song-like segment —
    /// unfiltered by QC (the caller decides reject/trim/keep).</summary>
    public event EventHandler<CompletedSegment>? SegmentCompleted;

    /// <summary>
    /// Raised when a real ICY boundary produced a segment we then threw away. Purely diagnostic,
    /// but the diagnosis is one the session log otherwise can't make: a station that announces
    /// nothing and a station whose every announcement is filtered out both look like silence
    /// after the connect line.
    /// </summary>
    public event EventHandler<DiscardedSegment>? SegmentDiscarded;

    /// <summary>Raised once this harvester gives up after <see cref="MaxReconnects"/> failed
    /// attempts — the pool should drop it and promote a reserve station.</summary>
    public event EventHandler? Died;

    /// <param name="cacheDir">This harvester's own scratch directory — must be distinct from
    /// every other concurrent recorder's (other harvesters, and the live "Save Song" recorder)
    /// so cut segments never collide (see StreamRecorder's cacheDir parameter).</param>
    /// <param name="offsetSeconds">Deferred-cut boundary offset; DJ-mode harvesting uses 0 (see
    /// DjHarvestService) — PoC testing found stations' true metadata lead is near zero, and the
    /// residual talk/next-song bleed this leaves is cleaned up downstream by
    /// SegmentQualityChecker's edge-trim rather than chased via a larger offset.</param>
    public StreamHarvester(string label, string url, StreamFormat format, string cacheDir,
        double offsetSeconds, Dispatcher dispatcher)
    {
        Label = label;
        _url = url;
        _format = format;
        _dispatcher = dispatcher;
        _recorder = new StreamRecorder(offsetSeconds, cacheDir);
        _recorder.SegmentCompleted += (_, seg) =>
        {
            LastSegmentAt = DateTime.UtcNow;
            SegmentCompleted?.Invoke(this, seg);
        };
        _recorder.SegmentDiscarded += (_, d) => SegmentDiscarded?.Invoke(this, d);
        _dl = (buffer, length, _) => _recorder.Write(_session, buffer, length);
    }

    /// <summary>
    /// Re-reads <see cref="StreamName"/> if it isn't known yet. ICY headers normally arrive during
    /// stream creation, but a slow server can leave the tags empty at that moment — and an
    /// identity that never turns up means the duplicate check silently skips this harvester
    /// forever. Must run on the harvest dispatcher thread (it calls BASS).
    /// </summary>
    public void RefreshStreamName()
    {
        if (StreamName is null && _handle != 0 && !_dead)
            StreamName = ReadIcyName(_handle);
    }

    /// <summary>Connect and begin harvesting. Must run on the harvest dispatcher thread.</summary>
    public bool Start()
    {
        _session = _recorder.BeginSession(new Station(Label, _url, _format), _format);
        _handle = _format == StreamFormat.Aac
            ? BassAac.CreateStream(_url, 0, BassFlags.Default, _dl)
            : Bass.CreateStream(_url, 0, BassFlags.Default, _dl);
        if (_handle == 0)
        {
            _recorder.EndSession(_session);
            return false;
        }

        // Syncs fire on BASS threads → marshal to the harvest dispatcher so all BASS lifecycle
        // calls for this harvester stay single-threaded (mirrors RadioEngine).
        _metaSync = (_, _, _, _) => _dispatcher.BeginInvoke(OnMeta);
        Bass.ChannelSetSync(_handle, SyncFlags.MetadataReceived, 0, _metaSync);
        _endSync = (_, _, _, _) => _dispatcher.BeginInvoke(OnEnd);
        Bass.ChannelSetSync(_handle, SyncFlags.End, 0, _endSync);
        _stallSync = (_, _, _, _) => { }; // transient network hiccups need no action here
        Bass.ChannelSetSync(_handle, SyncFlags.Stalled, 0, _stallSync);

        // Play to the no-sound device: drives download + metadata syncs at real-time, silently.
        Bass.ChannelPlay(_handle);
        StreamName = ReadIcyName(_handle) ?? StreamName;  // keep the last known across reconnects
        if (ConnectedAt == default)
            ConnectedAt = DateTime.UtcNow;
        return true;
    }

    private void OnMeta()
    {
        var title = ParseStreamTitle(ReadTag(_handle, TagType.META));
        if (string.IsNullOrWhiteSpace(title)) return;

        var (artist, trackTitle) = IcyTitleParser.Split(title);

        Interlocked.Increment(ref _titles);

        lock (_titleLock)
        {
            // Keep the raw "Artist - Title" — it's what makes a shared title strong evidence.
            // A repeated announcement of the same title mustn't fill the history.
            if (_recentTitles.First?.Value != title)
            {
                _recentTitles.AddFirst(title);
                while (_recentTitles.Count > TitleHistory)
                    _recentTitles.RemoveLast();
            }
        }

        double bps = 0;
        if (Bass.ChannelGetAttribute(_handle, ChannelAttribute.Bitrate, out var kbps) && kbps > 0)
            bps = kbps * 1000.0 / 8.0;
        _recorder.OnTrackChanged(_session, trackTitle, artist, Label, bps);
    }

    private void OnEnd()
    {
        if (_dead) return;
        if (_handle != 0) { Bass.StreamFree(_handle); _handle = 0; }
        _recorder.EndSession(_session);

        if (++_reconnects > MaxReconnects)
        {
            _dead = true;
            Died?.Invoke(this, EventArgs.Empty);
            return;
        }
        // Bounded reconnect after a short delay, back on the harvest dispatcher thread.
        Task.Delay(3000).ContinueWith(_ => _dispatcher.BeginInvoke(() =>
        {
            if (_dead) return;
            if (!Start())
            {
                _dead = true;
                Died?.Invoke(this, EventArgs.Empty);
            }
        }));
    }

    public void Stop()
    {
        _dead = true;
        if (_handle != 0) { Bass.StreamFree(_handle); _handle = 0; }
        _recorder.EndSession(_session);
    }

    public void Dispose()
    {
        Stop();
        _recorder.Dispose();
    }

    private static string? ReadTag(int handle, TagType type)
    {
        var ptr = Bass.ChannelGetTags(handle, type);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    /// <summary>
    /// Reads <c>icy-name</c> out of TagType.ICY, which BASS returns as a series of
    /// null-terminated "key:value" strings ended by a double null (mirrors RadioEngine's
    /// ReadMultiStringTags).
    /// </summary>
    private static string? ReadIcyName(int handle)
    {
        var ptr = Bass.ChannelGetTags(handle, TagType.ICY);
        if (ptr == IntPtr.Zero) return null;

        const string key = "icy-name:";
        while (true)
        {
            var s = Marshal.PtrToStringUTF8(ptr);
            if (string.IsNullOrEmpty(s)) return null;
            if (s.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                var name = s[key.Length..].Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            ptr += System.Text.Encoding.UTF8.GetByteCount(s) + 1;
        }
    }

    // ICY terminates StreamTitle with '; (apostrophe-semicolon) — match the app's parser so a
    // title containing an apostrophe ("Don't...") isn't truncated.
    private static string? ParseStreamTitle(string? meta)
    {
        if (string.IsNullOrEmpty(meta)) return null;
        const string key = "StreamTitle='";
        var start = meta.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = meta.IndexOf("';", start, StringComparison.Ordinal);
        var raw = end < 0 ? meta[start..].TrimEnd('\'') : meta[start..end];
        return TrackTitleCleaner.Clean(raw); // same cleanup as RadioEngine, so harvest
                                             // filenames/library rows stay artifact-free
    }
}
