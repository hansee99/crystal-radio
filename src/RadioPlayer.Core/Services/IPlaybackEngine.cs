namespace RadioPlayer.Services;

/// <summary>
/// The transport surface shared by every playback engine (radio streams, local files), so the
/// view model and <see cref="SmtcController"/> can drive "what's active" without caring which
/// engine it is. Engine-specific concerns — ICY metadata and reconnection for radio, a queue
/// and seeking for local files — live on the concrete engines, not here.
///
/// All members are UI-thread-affine; engines marshal their own BASS callbacks before raising events.
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    PlaybackState State { get; }

    /// <summary>Output volume, 0.0–1.0.</summary>
    double Volume { get; set; }

    event EventHandler<PlaybackState>? StateChanged;
    event EventHandler<string>? ErrorOccurred;

    /// <summary>Pause if playing, resume if paused.</summary>
    void TogglePause();

    void Pause();
    void Resume();
    void Stop();
}
