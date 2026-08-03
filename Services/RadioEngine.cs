using System.Runtime.InteropServices;
using System.Windows.Threading;
using ManagedBass;
using ManagedBass.Aac;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

public enum PlaybackState
{
    Stopped,
    Buffering,
    Playing,
    Paused,
    Reconnecting,
    Error
}

/// <summary>Now-playing info parsed from ICY metadata.</summary>
public sealed record TrackMetadata(string Title, string? Artist, string? StationName);

/// <summary>
/// The only class that talks to BASS. Public surface is UI-thread-affine; BASS sync
/// callbacks fire on BASS-owned threads and are marshalled to the UI thread here so
/// callers never have to think about it.
/// </summary>
public sealed class RadioEngine : IPlaybackEngine
{
    private readonly Dispatcher _dispatcher;
    private readonly StreamRecorder? _recorder;

    // BASS's device selection is per-CALLING-THREAD, and a thread that has never explicitly
    // touched Bass.CurrentDevice auto-selects the lowest initialized device index. That's
    // harmless while this is the only device the process ever initializes, but DJ mode's
    // harvest thread initializes device 0 ("no sound") — always the lowest possible index —
    // so any ThreadPool thread servicing our Task.Run callbacks below would otherwise silently
    // create streams against the silent device instead of this one. Captured once, right after
    // our own successful Init, and set explicitly before every CreateStream call.
    private readonly int _realDeviceIndex;

    private int _stream;
    private Station? _currentStation;
    private double _volume = 0.5;
    private PlaybackState _state = PlaybackState.Stopped;

    // Keep delegate references alive for the lifetime of the stream so the GC does not
    // collect them while BASS still holds the native function pointers.
    private SyncProcedure? _metaSync;
    private SyncProcedure? _stallSync;
    private SyncProcedure? _endSync;

    // Download callbacks (rolling cache) keyed by generation. Kept in a dictionary rather
    // than a single field because a superseded connect may still invoke its callback until
    // its stream is freed — entries are pruned once attempts are safely dead.
    private readonly Dictionary<int, DownloadProcedure> _downloadProcs = new();

    // The recorder session belonging to the current/most recent connection attempt.
    private int _recSession;

    // Bumped whenever the stream is (re)created or torn down, so a stale reconnect
    // attempt scheduled for an older stream can detect it lost the race and bail.
    private int _generation;

    public RadioEngine(StreamRecorder? recorder = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _recorder = recorder;

        // Init the default output device. Returns false if already initialised; that's fine.
        if (!Bass.Init() && Bass.LastError != Errors.Already)
            throw new InvalidOperationException($"BASS init failed: {Bass.LastError}");

        // Whether we just initialized it or it was already up (e.g. LocalPlaybackEngine got
        // there first on this same UI thread), CurrentDevice on THIS thread now correctly
        // reflects the real output device — capture it for the background-thread call sites.
        _realDeviceIndex = Bass.CurrentDevice;

        // ICY/Shoutcast metadata is requested by default in this BASS version, so no
        // extra configuration is needed here — we just subscribe to the metadata sync.
    }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<TrackMetadata>? MetadataChanged;
    public event EventHandler<string>? ErrorOccurred;

    public PlaybackState State => _state;
    public Station? CurrentStation => _currentStation;

    /// <summary>Output volume, 0.0–1.0.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            if (_stream != 0)
                Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
        }
    }

    /// <summary>Start playing a station, replacing whatever is currently playing.</summary>
    public void Play(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);
        StartStream(station, PlaybackState.Buffering);
    }

    /// <summary>Pause if playing, resume if paused.</summary>
    public void TogglePause()
    {
        if (_stream == 0) return;

        if (_state == PlaybackState.Playing)
        {
            if (Bass.ChannelPause(_stream)) SetState(PlaybackState.Paused);
        }
        else if (_state == PlaybackState.Paused)
        {
            if (Bass.ChannelPlay(_stream)) SetState(PlaybackState.Playing);
        }
    }

    public void Pause()
    {
        if (_stream != 0 && _state == PlaybackState.Playing && Bass.ChannelPause(_stream))
            SetState(PlaybackState.Paused);
    }

    public void Resume()
    {
        if (_stream != 0 && _state == PlaybackState.Paused && Bass.ChannelPlay(_stream))
            SetState(PlaybackState.Playing);
    }

    public void Stop()
    {
        FreeStream();
        _currentStation = null;
        SetState(PlaybackState.Stopped);
    }

    /// <summary>
    /// Probe whether a stream can actually be opened by the engine, WITHOUT disturbing current
    /// playback: it creates a throwaway BASS handle and frees it immediately. Used to validate
    /// search results before showing them — Radio Browser's <c>lastcheckok</c> can be stale.
    /// Returns false if the stream can't be opened (dead URL, undecodable, timeout).
    /// </summary>
    public Task<bool> TestStreamAsync(Station station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        var url = station.Url;
        var isAac = station.Format == StreamFormat.Aac;

        return Task.Run(() =>
        {
            // Decode-only: never plays, just checks connectivity, so it doesn't need — and by
            // using Decode, doesn't need to worry about — the calling thread's output device at
            // all (sidesteps the per-thread device-selection gotcha entirely, see _realDeviceIndex).
            var handle = isAac
                ? BassAac.CreateStream(url, 0, BassFlags.Decode, null)
                : Bass.CreateStream(url, 0, BassFlags.Decode, null);
            if (handle == 0)
                return false;
            Bass.StreamFree(handle);
            return true;
        }, ct);
    }

    /// <summary>
    /// (Re)starts a stream. The blocking network connect (<c>Bass*.CreateStream</c>) runs
    /// on a background thread so the UI thread stays responsive while connecting, then
    /// completion is marshalled back. The generation counter lets a newer Play/Stop
    /// discard a stale connect that finishes late (e.g. the user mashing "Next").
    /// </summary>
    private void StartStream(Station station, PlaybackState pendingState)
    {
        FreeStream();                 // frees any current stream and bumps the generation
        _currentStation = station;
        SetState(pendingState);

        var generation = _generation;
        var url = station.Url;
        var isAac = station.Format == StreamFormat.Aac;

        // Rolling cache: capture the raw stream bytes on the SAME connection as playback via a
        // download callback. The session id keeps a superseded connect's late callbacks from
        // writing into the new session; the dictionary roots the delegate while BASS may still
        // call it (see field comment).
        DownloadProcedure? downloadProc = null;
        if (_recorder is not null)
        {
            var session = _recSession = _recorder.BeginSession(station, station.Format);
            var recorder = _recorder;
            downloadProc = (buffer, length, _) => recorder.Write(session, buffer, length);
            _downloadProcs[generation] = downloadProc;
            // Attempts more than a few generations old are long dead (connects time out in
            // seconds) — prune so the dictionary doesn't grow with every station change.
            foreach (var g in _downloadProcs.Keys.Where(g => g < generation - 8).ToList())
                _downloadProcs.Remove(g);
        }

        Task.Run(() =>
        {
            // This runs on a ThreadPool thread that has never touched Bass.CurrentDevice — set
            // it explicitly to the real output device before creating a playback stream (see
            // _realDeviceIndex's field comment for why this is required, not just defensive).
            Bass.CurrentDevice = _realDeviceIndex;

            // AAC core is NOT in BASS core — the add-on path is required for aac streams.
            var handle = isAac
                ? BassAac.CreateStream(url, 0, BassFlags.Default, downloadProc)
                : Bass.CreateStream(url, 0, BassFlags.Default, downloadProc);
            var error = Bass.LastError; // BASS error state is per-thread
            _dispatcher.BeginInvoke(() => OnStreamCreated(generation, station, pendingState, handle, error));
        });
    }

    private void OnStreamCreated(int generation, Station station, PlaybackState pendingState, int handle, Errors error)
    {
        // A newer Play/Stop/reconnect superseded us while we were connecting.
        if (generation != _generation)
        {
            if (handle != 0) Bass.StreamFree(handle);
            _downloadProcs.Remove(generation); // freed → no more callbacks for this attempt
            return;
        }

        if (handle == 0)
        {
            _downloadProcs.Remove(generation);
            _recorder?.EndSession(_recSession);
            FailOrRetry(station, pendingState, $"Could not open stream: {error}");
            return;
        }

        _stream = handle;
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);

        // Read the station info that arrives with the headers.
        PublishIcyStationInfo(_stream, station);

        // Live track changes.
        _metaSync = (h, channel, data, user) =>
            _dispatcher.BeginInvoke(() => OnMetadataReceived(channel));
        Bass.ChannelSetSync(_stream, SyncFlags.MetadataReceived, 0, _metaSync);

        // Stalls (network hiccups): data == 0 stalled, data == 1 resumed.
        _stallSync = (h, channel, data, user) =>
            _dispatcher.BeginInvoke(() => OnStall(channel, data));
        Bass.ChannelSetSync(_stream, SyncFlags.Stalled, 0, _stallSync);

        // End of stream (the server dropped us): try to reconnect.
        _endSync = (h, channel, data, user) =>
            _dispatcher.BeginInvoke(() => OnStreamEnded(channel));
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);

        if (!Bass.ChannelPlay(_stream))
        {
            FailOrRetry(station, pendingState, $"Could not start playback: {Bass.LastError}");
            return;
        }

        SetState(PlaybackState.Playing);
    }

    private void FailOrRetry(Station station, PlaybackState pendingState, string message)
    {
        // A failed reconnect keeps retrying; a failed initial play surfaces an error.
        if (pendingState == PlaybackState.Reconnecting)
        {
            ScheduleReconnect(station);
        }
        else
        {
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(this, message);
        }
    }

    private void OnStall(int channel, int data)
    {
        if (channel != _stream) return;
        // data: 0 = stalled (buffering), 1 = resumed.
        if (data == 0)
            SetState(PlaybackState.Reconnecting);
        else if (_state == PlaybackState.Reconnecting)
            SetState(PlaybackState.Playing);
    }

    private void OnStreamEnded(int channel)
    {
        if (channel != _stream || _currentStation is null) return;
        ScheduleReconnect(_currentStation);
    }

    private void ScheduleReconnect(Station station)
    {
        SetState(PlaybackState.Reconnecting);
        var generation = _generation;

        Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                // User changed station / stopped in the meantime — abandon this attempt.
                if (generation != _generation || !ReferenceEquals(station, _currentStation))
                    return;

                StartStream(station, PlaybackState.Reconnecting);
            });
        });
    }

    private void PublishIcyStationInfo(int handle, Station station)
    {
        var stationName = IcyTags.ValueOf(ReadMultiStringTags(handle, TagType.ICY), "icy-name:");

        MetadataChanged?.Invoke(this, new TrackMetadata(
            Title: station.Name,
            Artist: null,
            StationName: string.IsNullOrWhiteSpace(stationName) ? station.Name : stationName));
    }

    private void OnMetadataReceived(int channel)
    {
        if (channel != _stream) return;

        var meta = ReadStringTag(channel, TagType.META);
        var title = ParseStreamTitle(meta);
        if (string.IsNullOrWhiteSpace(title)) return;

        // "Artist - Title" is only the commonest convention; see IcyTitleParser.
        var (artist, trackTitle) = IcyTitleParser.Split(title);

        var stationName = _currentStation?.Name;
        MetadataChanged?.Invoke(this, new TrackMetadata(trackTitle, artist, stationName));

        // Title boundary for the rolling cache: schedules the (offset-delayed) segment cut.
        // Raised AFTER MetadataChanged so the history row for the new song exists before the
        // finished song's SegmentCompleted looks for its row. The decoder-reported bitrate lets
        // the recorder convert its boundary offset from seconds to bytes. (The initial
        // per-connect station-info publish doesn't come through here — it isn't a boundary.)
        if (_recorder is not null)
        {
            double bytesPerSecond = 0;
            if (Bass.ChannelGetAttribute(channel, ChannelAttribute.Bitrate, out var kbps) && kbps > 0)
                bytesPerSecond = kbps * 1000.0 / 8.0;
            _recorder.OnTrackChanged(_recSession, trackTitle, artist, stationName, bytesPerSecond);
        }
    }

    private static string? ParseStreamTitle(string? meta)
    {
        if (string.IsNullOrEmpty(meta)) return null;
        const string key = "StreamTitle='";
        var start = meta.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        // ICY terminates the field with an apostrophe-semicolon ('; ), so search for that rather
        // than a bare apostrophe — otherwise a title like "Don't let me down" truncates at "Don".
        var end = meta.IndexOf("';", start, StringComparison.Ordinal);
        var raw = end < 0 ? meta[start..].TrimEnd('\'') : meta[start..end];
        // Cleaned at the parse point so every consumer — UI, history, SMTC, the recorder's
        // segment names — sees the same tidy title.
        return TrackTitleCleaner.Clean(raw);
    }

    private void FreeStream()
    {
        // The tail of the current capture session is mid-song by definition — discard it.
        _recorder?.EndSession(_recSession);

        // Always bump: this invalidates any in-flight async connect, even when no stream
        // exists yet (e.g. the user skips again while still buffering).
        _generation++;
        if (_stream != 0)
        {
            Bass.StreamFree(_stream);
            _stream = 0;
            _downloadProcs.Remove(_generation - 1); // freed → its callback can't fire again
        }
        _metaSync = null;
        _stallSync = null;
        _endSync = null;
    }

    private void SetState(PlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    // --- Native tag helpers ---------------------------------------------------

    // Tag reading lives in IcyTags — shared with StreamHarvester, which had its own copy of it
    // (and of its bugs). See that class for why the encoding is sniffed rather than assumed.
    private static string? ReadStringTag(int handle, TagType type) => IcyTags.ReadString(handle, type);

    private static IReadOnlyList<string> ReadMultiStringTags(int handle, TagType type)
        => IcyTags.ReadBlock(handle, type);

    public void Dispose()
    {
        FreeStream();
        Bass.Free();
    }
}
