using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace RadioPlayer;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "" : $"Version {version.Major}.{version.Minor}.{version.Build}";
    }

    public void SetStatus(string text) => StatusText.Text = text;
}

/// <summary>
/// Runs the splash on its <b>own</b> UI thread (#53).
///
/// <para>That is the whole point. Building the main window is a long stretch of synchronous work on
/// the app's UI thread — opening two SQLite databases, initialising BASS, and above all creating an
/// ONNX inference session over an 86 MB model, which on the older laptop this exists for is most of
/// a minute. A splash on that same thread would paint once and then freeze for the entire wait,
/// which looks more broken than no splash at all. On its own dispatcher it keeps animating and can
/// report each stage as it is reached.</para>
///
/// <para>Deliberately not owned by MainWindow and deliberately dependency-free: it has to be on
/// screen before the app's resources, engines or services exist.</para>
/// </summary>
public sealed class SplashHost
{
    private SplashWindow? _window;
    private Dispatcher? _dispatcher;
    private readonly ManualResetEventSlim _ready = new(false);

    public static SplashHost Show()
    {
        var host = new SplashHost();
        var thread = new Thread(host.Run) { IsBackground = true, Name = "Splash" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Wait for the window to exist so an immediate SetStatus isn't dropped. Bounded: a splash
        // that failed to come up must never be the reason the app doesn't start.
        host._ready.Wait(TimeSpan.FromSeconds(5));
        return host;
    }

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _window = new SplashWindow();
            _window.Show();
            _ready.Set();
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            Services.AppLog.Debug($"[Splash] failed: {ex.Message}");
            _ready.Set();   // never leave Show() waiting out its timeout for a splash that died
        }
    }

    /// <summary>Reports the current stage. Safe to call from any thread, and a no-op once closed.</summary>
    public void SetStatus(string text)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        // BeginInvoke, never Invoke: the caller is the UI thread doing the slow work, and blocking
        // it on the splash's thread to draw a status line would be the wrong way round.
        dispatcher.BeginInvoke(() => { try { _window?.SetStatus(text); } catch { /* going away */ } });
    }

    /// <summary>Closes the splash and shuts its thread down. Safe to call more than once.</summary>
    public void Close()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.Invoke(() =>
        {
            try { _window?.Close(); } catch { /* already gone */ }
            _window = null;
        });
        dispatcher.InvokeShutdown();
        _dispatcher = null;
    }
}
