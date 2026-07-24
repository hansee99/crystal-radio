using System.Windows.Threading;
using Windows.Media;
using RadioPlayer.Services;

namespace RadioPlayer.Services;

/// <summary>
/// Owns the SystemMediaTransportControls instance: pushes now-playing info via the
/// DisplayUpdater and maps the hardware/flyout buttons onto the RadioEngine.
///
/// In a WPF (Win32) app there is no CoreWindow, so SMTC must be obtained per-HWND via
/// the projected interop class <see cref="SystemMediaTransportControlsInterop"/> rather
/// than GetForCurrentView(). Construct this only after the window's HWND exists.
/// </summary>
public sealed class SmtcController : IDisposable
{
    private readonly SystemMediaTransportControls _smtc;
    private readonly Dispatcher _dispatcher;

    // The engine SMTC currently drives. Swapped on mode change so the media keys / flyout
    // control whatever is actually playing (radio or the local library player).
    private IPlaybackEngine _engine;
    private readonly EventHandler<PlaybackState> _stateHandler;

    public SmtcController(IntPtr hwnd, IPlaybackEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dispatcher = Dispatcher.CurrentDispatcher;

        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;

        // Engine StateChanged is already marshalled to the UI thread by the engine.
        _stateHandler = (_, state) => UpdateStatus(state);
        _engine.StateChanged += _stateHandler;
    }

    /// <summary>Point SMTC at a different engine (on mode switch). Now-playing display is pushed
    /// separately via <see cref="SetNowPlaying"/>.</summary>
    public void SetActiveEngine(IPlaybackEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (ReferenceEquals(engine, _engine)) return;
        _engine.StateChanged -= _stateHandler;
        _engine = engine;
        _engine.StateChanged += _stateHandler;
        UpdateStatus(_engine.State);
    }

    /// <summary>Push the current now-playing title/artist to the OS controls (both modes).</summary>
    public void SetNowPlaying(string title, string artist)
    {
        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = title ?? string.Empty;
        updater.MusicProperties.Artist = artist ?? string.Empty;
        updater.Update();
    }

    /// <summary>
    /// Raised on the UI thread when the SMTC/media-key "next track" button is pressed.
    /// "Next station" depends on the station list, which lives in the view model, so the
    /// caller wires this up rather than the controller acting on it directly.
    /// </summary>
    public event EventHandler? NextRequested;

    private void OnButtonPressed(
        SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // ButtonPressed fires on a background thread; the engine is UI-thread-affine.
        var button = args.Button;
        _dispatcher.BeginInvoke(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    _engine.Resume();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    _engine.Pause();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    _engine.Stop();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NextRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        });
    }

    private void UpdateStatus(PlaybackState state)
    {
        _smtc.PlaybackStatus = state switch
        {
            PlaybackState.Playing => MediaPlaybackStatus.Playing,
            PlaybackState.Paused => MediaPlaybackStatus.Paused,
            PlaybackState.Buffering or PlaybackState.Reconnecting => MediaPlaybackStatus.Changing,
            PlaybackState.Stopped => MediaPlaybackStatus.Stopped,
            _ => MediaPlaybackStatus.Stopped
        };
    }

    public void Dispose()
    {
        _smtc.ButtonPressed -= OnButtonPressed;
        _engine.StateChanged -= _stateHandler;
        _smtc.IsEnabled = false;
    }
}
