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
    private readonly RadioEngine _engine;
    private readonly Dispatcher _dispatcher;

    public SmtcController(IntPtr hwnd, RadioEngine engine)
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

        // Engine events are already marshalled to the UI thread by the engine.
        _engine.StateChanged += (_, state) => UpdateStatus(state);
        _engine.MetadataChanged += (_, meta) => UpdateDisplay(meta);
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

    private void UpdateDisplay(TrackMetadata meta)
    {
        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = meta.Title ?? string.Empty;
        updater.MusicProperties.Artist = meta.Artist ?? meta.StationName ?? string.Empty;
        updater.Update();
    }

    public void Dispose()
    {
        _smtc.ButtonPressed -= OnButtonPressed;
        _smtc.IsEnabled = false;
    }
}
