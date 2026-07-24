using System.Windows.Threading;
using ManagedBass;
using ManagedBass.Aac;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>A local audio file queued for the library player.</summary>
public sealed record LocalTrack(string Path, string Title, string Artist, StreamFormat Format);

/// <summary>
/// Plays local audio files from an ordered queue — the offline/AI-curated side of the app. The
/// sibling of <see cref="RadioEngine"/> behind <see cref="IPlaybackEngine"/>: same BASS output,
/// but finite files instead of an endless stream, so it adds seeking, a position timeline, and
/// auto-advance to the next track. Deliberately separate from RadioEngine so neither carries the
/// other's concerns (ICY/reconnection vs. queue/seek).
///
/// UI-thread-affine: constructed on the UI thread; the end-of-track BASS sync marshals back via
/// the dispatcher, and a dispatcher timer publishes position while playing.
/// </summary>
public sealed class LocalPlaybackEngine : IPlaybackEngine
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _positionTimer;

    private int _stream;
    private double _volume = 0.5;
    private PlaybackState _state = PlaybackState.Stopped;

    private readonly List<LocalTrack> _queue = new();
    private int _index = -1;

    private SyncProcedure? _endSync;
    private int _generation;

    public LocalPlaybackEngine()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _positionTimer.Tick += (_, _) => PublishPosition();

        // Share the process-wide BASS init done by RadioEngine; init here too in case this
        // engine is constructed first. Errors.Already is expected and fine.
        if (!Bass.Init() && Bass.LastError != Errors.Already)
            throw new InvalidOperationException($"BASS init failed: {Bass.LastError}");
    }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Raised when the current track changes (including queue start); index into the queue.</summary>
    public event EventHandler<(LocalTrack Track, int Index)>? TrackChanged;

    /// <summary>Raised ~2×/second while playing: current position and total duration, in seconds.</summary>
    public event EventHandler<(double Position, double Duration)>? PositionChanged;

    public PlaybackState State => _state;

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

    public bool HasQueue => _queue.Count > 0;
    public int CurrentIndex => _index;

    /// <summary>Replace the queue and start playing from <paramref name="startIndex"/>.</summary>
    public void SetQueue(IReadOnlyList<LocalTrack> tracks, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        _queue.Clear();
        _queue.AddRange(tracks);
        if (_queue.Count == 0)
        {
            Stop();
            return;
        }
        PlayAt(Math.Clamp(startIndex, 0, _queue.Count - 1));
    }

    /// <summary>Play the queue entry at <paramref name="index"/>.</summary>
    public void PlayAt(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;
        _index = index;
        StartStream(_queue[index]);
    }

    public void Next()
    {
        if (_index + 1 < _queue.Count)
            PlayAt(_index + 1);
        else
            Stop(); // end of queue
    }

    public void Previous()
    {
        // Restart the current track if we're more than a few seconds in; else go to the previous.
        if (PositionSeconds > 3 || _index <= 0)
            Seek(0);
        else
            PlayAt(_index - 1);
    }

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
        _index = -1;
        SetState(PlaybackState.Stopped);
    }

    // --- Position / seek ------------------------------------------------------

    public double PositionSeconds
    {
        get
        {
            if (_stream == 0) return 0;
            var bytes = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
            return bytes < 0 ? 0 : Bass.ChannelBytes2Seconds(_stream, bytes);
        }
    }

    public double DurationSeconds
    {
        get
        {
            if (_stream == 0) return 0;
            var bytes = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);
            return bytes < 0 ? 0 : Bass.ChannelBytes2Seconds(_stream, bytes);
        }
    }

    /// <summary>Seek to <paramref name="seconds"/> within the current track.</summary>
    public void Seek(double seconds)
    {
        if (_stream == 0) return;
        var bytes = Bass.ChannelSeconds2Bytes(_stream, Math.Max(0, seconds));
        Bass.ChannelSetPosition(_stream, bytes, PositionFlags.Bytes);
        PublishPosition();
    }

    // --- Stream lifecycle -----------------------------------------------------

    private void StartStream(LocalTrack track)
    {
        FreeStream();
        var generation = _generation;

        var handle = track.Format == StreamFormat.Aac
            ? BassAac.CreateStream(track.Path, 0, 0, BassFlags.Default)
            : Bass.CreateStream(track.Path, 0, 0, BassFlags.Default);

        if (handle == 0)
        {
            ErrorOccurred?.Invoke(this, $"Couldn't open \"{track.Title}\": {Bass.LastError}");
            // Skip a dead file rather than stalling the queue.
            if (_index + 1 < _queue.Count)
                PlayAt(_index + 1);
            else
                Stop();
            return;
        }

        _stream = handle;
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);

        _endSync = (_, _, _, _) => _dispatcher.BeginInvoke(() => OnTrackEnded(generation));
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);

        if (!Bass.ChannelPlay(_stream))
        {
            ErrorOccurred?.Invoke(this, $"Couldn't play \"{track.Title}\": {Bass.LastError}");
            Stop();
            return;
        }

        TrackChanged?.Invoke(this, (track, _index));
        SetState(PlaybackState.Playing);
        PublishPosition();
        _positionTimer.Start();
    }

    private void OnTrackEnded(int generation)
    {
        if (generation != _generation)
            return; // superseded by a newer stream
        Next();     // auto-advance (Next stops at the end of the queue)
    }

    private void FreeStream()
    {
        _positionTimer.Stop();
        _generation++;
        if (_stream != 0)
        {
            Bass.StreamFree(_stream);
            _stream = 0;
        }
        _endSync = null;
    }

    private void PublishPosition()
    {
        if (_stream != 0)
            PositionChanged?.Invoke(this, (PositionSeconds, DurationSeconds));
    }

    private void SetState(PlaybackState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        FreeStream();
        // Bass.Free() is owned by RadioEngine (shared init) — don't free the device here.
    }
}
