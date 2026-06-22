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
public sealed class RadioEngine : IDisposable
{
    private readonly Dispatcher _dispatcher;

    private int _stream;
    private Station? _currentStation;
    private double _volume = 0.5;
    private PlaybackState _state = PlaybackState.Stopped;

    // Keep delegate references alive for the lifetime of the stream so the GC does not
    // collect them while BASS still holds the native function pointers.
    private SyncProcedure? _metaSync;
    private SyncProcedure? _stallSync;
    private SyncProcedure? _endSync;

    // Bumped whenever the stream is (re)created or torn down, so a stale reconnect
    // attempt scheduled for an older stream can detect it lost the race and bail.
    private int _generation;

    public RadioEngine()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Init the default output device. Returns false if already initialised; that's fine.
        if (!Bass.Init() && Bass.LastError != Errors.Already)
            throw new InvalidOperationException($"BASS init failed: {Bass.LastError}");

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
        FreeStream();
        _currentStation = station;
        SetState(PlaybackState.Buffering);

        if (!CreateAndStart(station))
        {
            SetState(PlaybackState.Error);
            ErrorOccurred?.Invoke(this, $"Could not open stream: {Bass.LastError}");
        }
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

    private bool CreateAndStart(Station station)
    {
        // AAC core is NOT in BASS core — the add-on path is required for aac streams.
        _stream = station.Format == StreamFormat.Aac
            ? BassAac.CreateStream(station.Url, 0, BassFlags.Default, null)
            : Bass.CreateStream(station.Url, 0, BassFlags.Default, null);

        if (_stream == 0)
            return false;

        _generation++;
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);

        // Read the station info that arrives with the headers.
        PublishIcyStationInfo(_stream, station);

        // Live track changes.
        _metaSync = (handle, channel, data, user) =>
            _dispatcher.BeginInvoke(() => OnMetadataReceived(channel));
        Bass.ChannelSetSync(_stream, SyncFlags.MetadataReceived, 0, _metaSync);

        // Stalls (network hiccups): data == 0 stalled, data == 1 resumed.
        _stallSync = (handle, channel, data, user) =>
            _dispatcher.BeginInvoke(() => OnStall(channel, data));
        Bass.ChannelSetSync(_stream, SyncFlags.Stalled, 0, _stallSync);

        // End of stream (the server dropped us): try to reconnect.
        _endSync = (handle, channel, data, user) =>
            _dispatcher.BeginInvoke(() => OnStreamEnded(channel));
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);

        if (!Bass.ChannelPlay(_stream))
            return false;

        SetState(PlaybackState.Playing);
        return true;
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

                FreeStream();
                _currentStation = station;
                if (!CreateAndStart(station))
                    ScheduleReconnect(station); // keep retrying
            });
        });
    }

    private void PublishIcyStationInfo(int handle, Station station)
    {
        var icy = ReadMultiStringTags(handle, TagType.ICY);
        var stationName = icy
            .FirstOrDefault(t => t.StartsWith("icy-name:", StringComparison.OrdinalIgnoreCase))
            ?["icy-name:".Length..]
            .Trim();

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

        // ICY StreamTitle is conventionally "Artist - Title".
        string trackTitle = title;
        string? artist = null;
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            artist = title[..dash].Trim();
            trackTitle = title[(dash + 3)..].Trim();
        }

        var stationName = _currentStation?.Name;
        MetadataChanged?.Invoke(this, new TrackMetadata(trackTitle, artist, stationName));
    }

    private static string? ParseStreamTitle(string? meta)
    {
        if (string.IsNullOrEmpty(meta)) return null;
        const string key = "StreamTitle='";
        var start = meta.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = meta.IndexOf('\'', start);
        return end < 0 ? meta[start..] : meta[start..end];
    }

    private void FreeStream()
    {
        if (_stream == 0) return;
        _generation++;
        Bass.StreamFree(_stream);
        _stream = 0;
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

    /// <summary>Reads a single null-terminated tag string (e.g. TagType.META).</summary>
    private static string? ReadStringTag(int handle, TagType type)
    {
        var ptr = Bass.ChannelGetTags(handle, type);
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    /// <summary>
    /// Reads a series of null-terminated strings terminated by a double null
    /// (e.g. TagType.ICY's "icy-name:...", "icy-br:...").
    /// </summary>
    private static IReadOnlyList<string> ReadMultiStringTags(int handle, TagType type)
    {
        var ptr = Bass.ChannelGetTags(handle, type);
        var result = new List<string>();
        if (ptr == IntPtr.Zero) return result;

        while (true)
        {
            var s = Marshal.PtrToStringUTF8(ptr);
            if (string.IsNullOrEmpty(s)) break;
            result.Add(s);
            ptr += Encoding_GetByteCount(s) + 1; // advance past this string and its null
        }
        return result;
    }

    private static int Encoding_GetByteCount(string s)
        => System.Text.Encoding.UTF8.GetByteCount(s);

    public void Dispose()
    {
        FreeStream();
        Bass.Free();
    }
}
