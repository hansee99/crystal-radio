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
public class LocalPlaybackEngine : IPlaybackEngine, ILocalQueuePlayer
{
    /// <summary>How long the first track of a queue takes to reach full volume. Much shorter than
    /// the track-to-track <see cref="FadeSeconds"/>: there is no outgoing track to trade against
    /// here, so a long ramp just sounds like the player is slow to start.</summary>
    private const int FadeInMs = 1200;

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

    // True when playback stopped because the queue ran out, as opposed to someone pressing Stop.
    // Only the first case should be resumed by a later Append — see that method.
    private bool _ranDry;

    private SyncProcedure? _endSync;
    private SyncProcedure? _fadeTriggerSync;
    // The running-dry warning for the final track (#47). Rooted for the same reason as the two
    // above: BASS keeps a native pointer to it until the channel is freed.
    private SyncProcedure? _runningDrySync;
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

        InitAudio();
    }

    // --- Audio seam -----------------------------------------------------------
    // Every BASS call the queue logic depends on goes through these members. This class's bugs
    // have all been in the queue state machine, not in the audio: a played-to-the-end queue is
    // not empty, so Append silently stopped resuming and a DJ session would sit in silence for
    // the rest of its life. None of that needs a sound device to reproduce. Overriding these
    // lets the real transitions run headless; nothing else about the class changes.

    /// <summary>Share the process-wide BASS init done by RadioEngine; init here too in case this
    /// engine is constructed first. Errors.Already is expected and fine.</summary>
    private protected virtual void InitAudio()
    {
        if (!Bass.Init() && Bass.LastError != Errors.Already)
            throw new InvalidOperationException($"BASS init failed: {Bass.LastError}");
    }

    /// <summary>What happened when we tried to put a track on the device. The two failure modes
    /// get different responses from the queue, so they stay distinct.</summary>
    private protected enum AudioStart
    {
        Started,
        /// <summary>The file would not open — skip it rather than stalling the queue.</summary>
        OpenFailed,
        /// <summary>It opened but would not play — a device-level problem; stop.</summary>
        PlayFailed
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

    /// <summary>
    /// Seconds skipped at the start of every track; 0 = off. A backstop for boundary cuts that
    /// landed early and left the previous song's tail at the head of this one — the content-aware
    /// edge-trim only removes that when the detector recognises it as non-music.
    /// </summary>
    public double IntroSkipSeconds { get; set; }

    /// <summary>
    /// Seconds every track stops short of its end; 0 = off. The mirror of
    /// <see cref="IntroSkipSeconds"/>, for cuts that landed late. With a crossfade this brings the
    /// fade forward so the outgoing track is silent before the residue; without one the track
    /// simply ends early and the queue advances.
    /// </summary>
    public double OutroGuardSeconds { get; set; }

    /// <summary>
    /// Where a track should be treated as ending. Never shortens a track to less than
    /// <see cref="MinDurationForCrossfade"/>: a guard bigger than the track itself would skip it
    /// entirely, and a two-second stub is worse than a couple of seconds of residue.
    /// </summary>
    /// <summary>Test hook for the derivation below; not part of the public surface.</summary>
    private protected double InvokeEffectiveEnd(double duration) => EffectiveEnd(duration);

    /// <summary>Test hook for the derivation below; not part of the public surface.</summary>
    private protected double InvokeEffectiveStart(double duration) => EffectiveStart(duration);

    private double EffectiveEnd(double duration) =>
        duration <= MinDurationForCrossfade
            ? duration
            : Math.Max(MinDurationForCrossfade, duration - Math.Max(0, OutroGuardSeconds));

    /// <summary>How far into a track playback starts, bounded so it can't overrun a short one.</summary>
    private double EffectiveStart(double duration)
    {
        var skip = Math.Max(0, IntroSkipSeconds);
        if (skip <= 0 || duration <= 0)
            return 0;
        return skip < duration - MinDurationForCrossfade ? skip : 0;
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

    /// <summary>
    /// Raised when playback stops because the queue ran out — NOT when someone pressed Stop.
    /// DJ mode uses this to fall back to live radio: running dry is the normal condition
    /// whenever harvesting hasn't kept up, and silence is never the right answer to it.
    /// </summary>
    public event EventHandler? QueueExhausted;

    /// <summary>
    /// Raised <see cref="RunningDrySeconds"/> before the LAST queued track ends — a warning, where
    /// <see cref="QueueExhausted"/> is a report.
    ///
    /// <para>DJ mode bridges to live radio when the mix runs out, and connecting a stream takes
    /// seconds. Discovering the problem only once the music has stopped therefore guarantees a
    /// silent gap, which is what made the handover sound like a hard cut (#47). Given warning, the
    /// bridge can be connecting and rising underneath the last song instead.</para>
    ///
    /// <para>Fires once per track, and only for the final one in the queue — a track with another
    /// behind it is already covered by the crossfade.</para>
    /// </summary>
    public event EventHandler? QueueRunningDry;

    /// <summary>
    /// How much warning <see cref="QueueRunningDry"/> gives. Enough for a station to connect,
    /// buffer and fade in — an ICY connect is routinely two or three seconds — without being so
    /// early that two different pieces of music overlap for long enough to be muddle.
    /// </summary>
    private const double RunningDrySeconds = 6.0;

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
    /// Appends tracks to the end of the queue and starts playing them if nothing is playing and
    /// nobody asked for that.
    ///
    /// The subtlety is DJ mode's whole premise: a queue that has been played to the end is NOT
    /// empty — played tracks stay in it, only <see cref="_index"/> moves — so "resume if the
    /// queue was empty" silently never fired once the mix had caught up with itself. A session
    /// would play its warm-start songs, run dry, and then sit in silence for the rest of its life
    /// while harvesters happily kept collecting into a queue nobody was reading.
    /// <see cref="_ranDry"/> is what distinguishes that from a deliberate Stop, which must NOT be
    /// undone by the next harvested song landing.
    /// </summary>
    public void Append(IReadOnlyList<LocalTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
            return;

        // Where the new run begins — and where playback picks up if we'd run dry, so we continue
        // into the new material rather than replaying the queue from the top.
        var resumeAt = _queue.Count;
        var shouldResume = _queue.Count == 0 || _ranDry;

        _queue.AddRange(tracks);
        QueueChanged?.Invoke(this, EventArgs.Empty);

        if (shouldResume)
            PlayAt(resumeAt);
    }

    /// <summary>
    /// Drops every queued track after the one playing now. The current track keeps playing; when
    /// it ends the queue runs dry, which raises <see cref="QueueExhausted"/> — so a caller that
    /// bridges to live radio on that event does so naturally.
    /// </summary>
    public void TruncateAfterCurrent()
    {
        var keep = _index + 1;
        if (keep <= 0 || keep >= _queue.Count)
            return; // nothing playing, or nothing queued after it
        _queue.RemoveRange(keep, _queue.Count - keep);
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Play the queue entry at <paramref name="index"/>.</summary>
    public void PlayAt(int index)
    {
        if (index < 0 || index >= _queue.Count)
            return;
        _ranDry = false;
        _index = index;
        StartStream(_queue[index]);
    }

    public void Next()
    {
        if (_index + 1 < _queue.Count)
            PlayAt(_index + 1);
        else
            StopInternal(ranDry: true); // end of queue — a later Append should pick up from here
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

    /// <summary>Stops because someone asked. A subsequent <see cref="Append"/> will NOT resume —
    /// that's the difference from running off the end of the queue.</summary>
    public void Stop() => StopInternal(ranDry: false);

    private void StopInternal(bool ranDry)
    {
        FreeStream();
        _index = -1;
        _ranDry = ranDry;
        SetState(PlaybackState.Stopped);
        if (ranDry)
            QueueExhausted?.Invoke(this, EventArgs.Empty);
    }

    // --- Position / seek ------------------------------------------------------

    public virtual double PositionSeconds
    {
        get
        {
            if (_stream == 0) return 0;
            var bytes = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
            return bytes < 0 ? 0 : Bass.ChannelBytes2Seconds(_stream, bytes);
        }
    }

    public virtual double DurationSeconds
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

        switch (TryStartAudio(track, generation))
        {
            case AudioStart.OpenFailed:
                ErrorOccurred?.Invoke(this, $"Couldn't open \"{track.Title}\": {Bass.LastError}");
                // Skip a dead file rather than stalling the queue. If it was the last one we've
                // run dry, not stopped — the next harvested song should start us again.
                if (_index + 1 < _queue.Count)
                    PlayAt(_index + 1);
                else
                    StopInternal(ranDry: true);
                return;

            case AudioStart.PlayFailed:
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

    /// <summary>Opens the track on the device and starts it — the BASS half of StartStream.</summary>
    private protected virtual AudioStart TryStartAudio(LocalTrack track, int generation)
    {
        var handle = track.Format == StreamFormat.Aac
            ? BassAac.CreateStream(track.Path, 0, 0, BassFlags.Default)
            : Bass.CreateStream(track.Path, 0, 0, BassFlags.Default);
        if (handle == 0)
            return AudioStart.OpenFailed;

        _stream = handle;

        // Fade in rather than starting at full volume. Track-to-track transitions already ramp
        // (BeginCrossfade), so the only hard edge left was the FIRST track of a queue — which in DJ
        // mode is the bridge → mix handover, the one seam a listener hits on every cold start.
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, 0f);
        SeekToEffectiveStart();

        _endSync = (_, _, _, _) => _dispatcher.BeginInvoke(() => OnTrackEnded(generation));
        Bass.ChannelSetSync(_stream, SyncFlags.End, 0, _endSync);

        if (!Bass.ChannelPlay(_stream))
            return AudioStart.PlayFailed;

        // Slide AFTER ChannelPlay, never before: a slide on a stopped channel can run to completion
        // against nothing, and the track then arrives at full volume with no fade at all.
        Bass.ChannelSlideAttribute(_stream, ChannelAttribute.Volume, (float)_volume, FadeInMs);
        return AudioStart.Started;
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
    private protected virtual void ArmCrossfadeTrigger(int generation)
    {
        if (_stream == 0)
            return;
        var duration = DurationSeconds;
        if (duration <= 0)
            return;
        var end = EffectiveEnd(duration);

        // Last track, or one too short to fade: no crossfade, but the guard still applies —
        // otherwise the residue at the end of the final song plays in full.
        if (_index + 1 >= _queue.Count || duration <= MinDurationForCrossfade)
        {
            if (_index + 1 >= _queue.Count)
                ArmRunningDryWarning(generation, end);

            if (end >= duration)
                return; // no guard in play; the natural End sync handles it
            var endBytes = Bass.ChannelSeconds2Bytes(_stream, end);
            _fadeTriggerSync = (_, _, _, _) => _dispatcher.BeginInvoke(() => OnTrackEnded(generation));
            Bass.ChannelSetSync(_stream, SyncFlags.Position, endBytes, _fadeTriggerSync);
            return;
        }

        // Fade so the outgoing track reaches silence AT the guarded end rather than the file's.
        var triggerBytes = Bass.ChannelSeconds2Bytes(_stream, Math.Max(0, end - FadeSeconds));
        _fadeTriggerSync = (_, _, _, _) => _dispatcher.BeginInvoke(() => BeginCrossfade(generation));
        Bass.ChannelSetSync(_stream, SyncFlags.Position, triggerBytes, _fadeTriggerSync);
    }

    /// <summary>
    /// Arms the one-shot <see cref="QueueRunningDry"/> warning for the final track of the queue.
    /// Skipped when the track is already inside the warning window — firing immediately would give
    /// the bridge no more notice than the old exhausted-after-the-fact path did, and a short last
    /// track is exactly when the mix is most likely to refill first anyway.
    /// </summary>
    private void ArmRunningDryWarning(int generation, double end)
    {
        if (_stream == 0)
            return;
        var at = end - RunningDrySeconds;
        if (at <= PositionSeconds)
            return;

        _runningDrySync = (_, _, _, _) => _dispatcher.BeginInvoke(() =>
        {
            // Re-check on arrival: a track appended in the meantime means the queue is no longer
            // about to run dry, and the crossfade will handle the handover instead.
            if (generation != _generation || _index + 1 < _queue.Count)
                return;
            QueueRunningDry?.Invoke(this, EventArgs.Empty);
        });
        Bass.ChannelSetSync(_stream, SyncFlags.Position,
            Bass.ChannelSeconds2Bytes(_stream, at), _runningDrySync);
    }

    /// <summary>
    /// Fades the current track to silence over <paramref name="milliseconds"/> without stopping it —
    /// the End sync still advances or ends the queue as usual.
    ///
    /// <para>For the bridge handover (#47): once the station is genuinely audible, the song recedes
    /// into it instead of stopping dead. Deliberately driven by the caller rather than armed with
    /// the warning above, because a station that fails to connect must not leave the listener with
    /// a faded-out song and nothing underneath it.</para>
    /// </summary>
    public void FadeOutCurrent(int milliseconds)
    {
        if (_stream == 0 || _state != PlaybackState.Playing)
            return;
        Bass.ChannelSlideAttribute(_stream, ChannelAttribute.Volume, 0f, Math.Max(1, milliseconds));
    }

    /// <summary>Positions a freshly-created stream at <see cref="IntroSkipSeconds"/>.</summary>
    private void SeekToEffectiveStart()
    {
        if (_stream == 0)
            return;
        var start = EffectiveStart(DurationSeconds);
        if (start <= 0)
            return;
        Bass.ChannelSetPosition(_stream, Bass.ChannelSeconds2Bytes(_stream, start), PositionFlags.Bytes);
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
        _fadeOutKeepAlive = [_endSync, _fadeTriggerSync, _runningDrySync];
        _fadeOutSlidedSync = (_, _, _, _) => _dispatcher.BeginInvoke(FreeFadeOutStream);
        Bass.ChannelSetSync(outgoing, SyncFlags.Slided, 0, _fadeOutSlidedSync);
        Bass.ChannelSlideAttribute(outgoing, ChannelAttribute.Volume, 0f, FadeMs);

        var incomingGeneration = ++_generation;
        _index = nextIndex;
        _stream = handle;
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, 0f);
        SeekToEffectiveStart();

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
        StopAudio();
    }

    /// <summary>Releases whatever is on the device — the BASS half of FreeStream.</summary>
    private protected virtual void StopAudio()
    {
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
