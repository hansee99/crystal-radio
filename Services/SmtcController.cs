using System.Windows.Threading;
using Windows.Media;
using Windows.Storage.Streams;
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

    /// <summary>The app icon, served to SMTC as the cover art. Held for the life of the controller
    /// because the reference is a live handle onto the stream behind it, not a copy — the OS opens
    /// it again on every flyout, long after the constructor has returned.</summary>
    private readonly RandomAccessStreamReference? _thumbnail;

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

        // Internet radio has no cover art, and without a thumbnail the Win11 flyout draws a grey
        // music-note placeholder — which reads as a broken app rather than as a station that
        // didn't send a picture. The app icon stands in.
        //
        // Set once, before any Update(): the updater keeps its properties between calls and this
        // one never changes. Assigning Type does NOT drop it — measured against the real OS, both
        // orders round-tripped the full 256x256, so there is no reason to re-attach per track.
        _thumbnail = LoadThumbnail();
        if (_thumbnail is not null)
            _smtc.DisplayUpdater.Thumbnail = _thumbnail;

        // Engine StateChanged is already marshalled to the UI thread by the engine.
        _stateHandler = (_, state) => UpdateStatus(state);
        _engine.StateChanged += _stateHandler;
    }

    /// <summary>
    /// Reads the app icon out of the assembly's WPF resources and wraps it for WinRT.
    ///
    /// <para>In memory rather than from disk on purpose. <c>CreateFromUri</c> with <c>ms-appx:</c>
    /// is for packaged apps, and a <c>file:</c> path would mean shipping a loose copy of an image
    /// the assembly already carries — and then keeping the two in step. This reads the one that is
    /// already there.</para>
    ///
    /// <para>Best-effort: cover art is decoration. Anything that goes wrong here leaves the
    /// thumbnail unset, which is exactly where this feature started.</para>
    /// </summary>
    internal static RandomAccessStreamReference? LoadThumbnail()
    {
        try
        {
            // Assembly-qualified, not the shorter "/Assets/app-256.png": the short form resolves
            // against Application.ResourceAssembly — the ENTRY assembly — which is this app when it
            // runs and the test host when it doesn't, where it would silently find nothing.
            var uri = new Uri("pack://application:,,,/crystal-radio;component/Assets/app-256.png",
                UriKind.Absolute);
            var resource = System.Windows.Application.GetResourceStream(uri);
            if (resource is null) return null;

            using var source = resource.Stream;
            var bytes = new byte[source.Length];
            source.ReadExactly(bytes);

            // DataWriter rather than the AsStreamForWrite() extension: the
            // System.Runtime.InteropServices.WindowsRuntime helpers that used to bridge
            // Stream and IRandomAccessStream are gone from modern .NET, so the WinRT type is
            // the only way across. StoreAsync on an in-memory stream has nothing to wait for
            // and the type is agile, so blocking the UI thread here can't deadlock.
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();   // leaves the stream open; disposing the writer would close it
            writer.Dispose();
            stream.Seek(0);

            return RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Smtc] cover art unavailable: {ex.Message}");
            return null;
        }
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
        updater.Update();   // the cover art is already on the updater; see the constructor
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
