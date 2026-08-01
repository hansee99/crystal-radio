using System.Windows.Threading;
using ManagedBass;
using ManagedBass.Aac;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>A local audio file queued for the library player.</summary>
/// <summary>One queued local track. <paramref name="Reason"/> is the curator's one-line "why
/// this song" when the track came from a curated playlist (null for harvested/direct plays) —
/// carried through so DJ mode's intro line can ground itself in the actual curation logic.</summary>
public sealed record LocalTrack(string Path, string Title, string Artist, StreamFormat Format, string? Reason = null);

/// <summary>
/// Plays local audio files from an ordered queue — the offline/AI-curated side of the app. The
/// sibling of <see cref="RadioEngine"/> behind <see cref="IPlaybackEngine"/>: same BASS output,
/// but finite files instead of an endless stream, so it adds seeking, a position timeline, and
/// auto-advance to the next track. Deliberately separate from RadioEngine so neither carries the
/// other's concerns (ICY/reconnection vs. queue/seek).
///
/// Auto-advance between tracks crossfades (volume-envelope only, no BASSmix needed — BASS mixes
/// simultaneously-playing channels on one device natively): a couple of seconds before a track's
/// natural end, the next track starts underneath it at zero volume, the two slide past each other
/// over <see cref="FadeSeconds"/>, and the outgoing stream is freed once its fade-out completes.
/// Manual transitions (<see cref="PlayAt"/>/<see cref="Next"/>/<see cref="Previous"/> called
/// directly, e.g. from a skip button) stay an immediate hard cut — a deliberate skip shouldn't
/// wait out a fade.
///
/// UI-thread-affine: constructed on the UI thread; BASS syncs marshal back via the dispatcher,
/// and a dispatcher timer publishes position while playing.
/// </summary>
public sealed class LocalPlaybackEngine : IPlaybackEngine, ILocalQueuePlayer
{
    private const double FadeSeconds = 5.0;
    private const int FadeMs = (int)(FadeSeconds * 1000);
    // Require real headroom before the crossfade trigger point AND enough runway that a second
    // crossfade can't plausibly fire before the first one's fade-out has finished (see the
    // defensive FreeFadeOutStream() call in BeginCrossfade if it ever does).
    private const double MinDurationForCrossfade = FadeSeconds * 2;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _positionTimer;

    private int _stream;
    private double _volume = 0.5;
    private PlaybackState _state = PlaybackState.Stopped;

    private readonly List<LocalTrack> _queue = new();
    private int _index = -1;

    private SyncProcedure? _endSync;
    private SyncProcedure? _fadeTriggerSync;
    private int _generation;

    // The stream currently fading out (0 = none) — kept alive only long enough to finish its
    // fade and get freed; Position/Duration/Pause/Seek/Volume never touch it, only `_stream` does.
    private int _fadeOutStream;
    private SyncProcedure? _fadeOutSlidedSync;
    // Roots the outgoing stream's own end/fade-trigger delegates for as long as that stream is
    // alive: BASS holds a native pointer to a sync callback until the channel is freed, and
    // reassigning `_endSync`/`_fadeTriggerSync` to the incoming stream's delegates would otherwise
    // leave the outgoing ones with no managed reference — eligible for GC while BASS could still
    // invoke them.
    private object?[] _fadeOutKeepAlive = [];

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

    /// <summary>
    /// The queue as it stands — played, current and upcoming, in order. Read-only: the engine
    /// owns it. DJ mode's "Mix" list renders this directly, which is why it needs
    /// <see cref="QueueChanged"/> as well as <see cref="TrackChanged"/>.
    /// </summary>
    public IReadOnlyList<LocalTrack> Queue => _queue;

    /// <summary>Raised when the queue's CONTENTS change (replaced or appended) — as opposed to
    /// <see cref="TrackChanged"/>, which fires when the position within it moves.</summary>
    public event EventHandler? QueueChanged;

    /// <summary>Replace the queue and start playing from <paramref name="startIndex"/>.</summary>
    public void SetQueue(IReadOnlyList<LocalTrack> tracks, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        _queue.Clear();
        _queue.AddRange(tracks);
        if (_queue.Count == 0)
        {
            Stop();
            QueueChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        QueueChanged?.Invoke(this, EventArgs.Empty);
        PlayAt(Math.Clamp(startIndex, 0, _queue.Count - 1));
    }

    /// <summary>Number of tracks in the queue (played + upcoming).</summary>
    public int QueueCount => _queue.Count;

    /// <summary>
    /// Appends tracks to the end of the queue without disturbing playback — the self-refilling
    /// queue's write side (a warm-start seed or an empty cold start both just keep growing via
    /// this). If the queue was empty (nothing playing yet), starts playback immediately.
    /// </summary>
    public void Append(IReadOnlyList<LocalTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
            return;
        var wasEmpty = _queue.Count == 0;
        _queue.AddRange(tracks);
        QueueChanged?.Invoke(this, EventArgs.Empty);
        if (wasEmpty)
            PlayAt(0);
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
        ArmCrossfadeTrigger(generation);
    }

    private void OnTrackEnded(int generation)
    {
        if (generation != _generation)
            return; // superseded by a newer stream (a crossfade already advanced us)
        Next();     // auto-advance (Next stops at the end of the queue)
    }

    /// <summary>
    /// Schedules <see cref="BeginCrossfade"/> to fire <see cref="FadeSeconds"/> before the current
    /// stream's natural end, if there's a next track and enough runway to make it worthwhile. Too
    /// short a track (or the last one in the queue) just falls through to the existing
    /// <see cref="SyncFlags.End"/>-driven hard stop/advance.
    /// </summary>
    private void ArmCrossfadeTrigger(int generation)
    {
        if (_stream == 0 || _index + 1 >= _queue.Count)
            return;
        var duration = DurationSeconds;
        if (duration <= MinDurationForCrossfade)
            return;

        var triggerBytes = Bass.ChannelSeconds2Bytes(_stream, duration - FadeSeconds);
        _fadeTriggerSync = (_, _, _, _) => _dispatcher.BeginInvoke(() => BeginCrossfade(generation));
        Bass.ChannelSetSync(_stream, SyncFlags.Position, triggerBytes, _fadeTriggerSync);
    }

    /// <summary>
    /// Starts the next track underneath the current one and slides both past each other over
    /// <see cref="FadeSeconds"/>: the outgoing stream fades to silence (freed once its slide
    /// completes, via a <see cref="SyncFlags.Slided"/> sync — more trustworthy than trying to
    /// time a manual cleanup ourselves), and the incoming one — which becomes "the" current
    /// stream immediately, so Position/Duration/Pause/Seek all reflect it right away — fades in.
    /// </summary>
    private void BeginCrossfade(int generation)
    {
        if (generation != _generation || _stream == 0 || _index + 1 >= _queue.Count)
            return; // superseded (manual skip/stop already happened) or nothing to crossfade into

        var nextIndex = _index + 1;
        var nextTrack = _queue[nextIndex];
        var handle = nextTrack.Format == StreamFormat.Aac
            ? BassAac.CreateStream(nextTrack.Path, 0, 0, BassFlags.Default)
            : Bass.CreateStream(nextTrack.Path, 0, 0, BassFlags.Default);
        if (handle == 0)
        {
            ErrorOccurred?.Invoke(this, $"Couldn't open \"{nextTrack.Title}\": {Bass.LastError}");
            return; // let the outgoing track keep playing to its own natural End sync
        }

        // Shouldn't normally happen (see MinDurationForCrossfade), but if a previous fade-out
        // hasn't finished yet, finish it now rather than risk two outgoing streams in flight.
        FreeFadeOutStream();

        var outgoing = _stream;
        _fadeOutStream = outgoing;
        _fadeOutKeepAlive = [_endSync, _fadeTriggerSync];
        _fadeOutSlidedSync = (_, _, _, _) => _dispatcher.BeginInvoke(FreeFadeOutStream);
        Bass.ChannelSetSync(outgoing, SyncFlags.Slided, 0, _fadeOutSlidedSync);
        Bass.ChannelSlideAttribute(outgoing, ChannelAttribute.Volume, 0f, FadeMs);

        var incomingGeneration = ++_generation;
        _index = nextIndex;
        _stream = handle;
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, 0f);

        _endSync = (_, _, _, _) => _dispatcher.BeginInvoke(() => OnTrackEnded(incomingGeneration));
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);

        if (!Bass.ChannelPlay(_stream))
        {
            // _stream still holds the failed handle here (not zeroed) so Stop()/FreeStream()
            // frees it — same pattern as StartStream's own failure path.
            ErrorOccurred?.Invoke(this, $"Couldn't play \"{nextTrack.Title}\": {Bass.LastError}");
            Stop();
            return;
        }
        Bass.ChannelSlideAttribute(_stream, ChannelAttribute.Volume, (float)_volume, FadeMs);

        TrackChanged?.Invoke(this, (nextTrack, _index));
        PublishPosition();
        ArmCrossfadeTrigger(incomingGeneration);
    }

    private void FreeFadeOutStream()
    {
        if (_fadeOutStream != 0)
        {
            Bass.StreamFree(_fadeOutStream);
            _fadeOutStream = 0;
        }
        _fadeOutSlidedSync = null;
        _fadeOutKeepAlive = [];
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
        _fadeTriggerSync = null;
        FreeFadeOutStream();
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
