using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RadioPlayer.Services;
using RadioPlayer.ViewModels;

namespace RadioPlayer;

/// <summary>
/// Interaction logic for MainWindow.xaml. Code-behind is limited to view wiring and the
/// HWND/SMTC bootstrap, per the project conventions.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private SmtcController? _smtc;

    public MainWindow() : this(null) { }

    /// <param name="splash">Optional progress sink for the startup splash (#53). Every stage below
    /// is genuinely slow on a cold machine — the embedding model most of all — so the report names
    /// what is happening rather than counting steps.</param>
    public MainWindow(SplashHost? splash)
    {
        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        void Stage(string text)
        {
            AppLog.Debug($"[Startup] {startedAt.ElapsedMilliseconds,6} ms · {text}");
            splash?.SetStatus(text);
        }

        Stage("Preparing the window");
        InitializeComponent();
        Loaded       += (_, _) => UpdateShellClip();
        SizeChanged  += (_, _) => UpdateShellClip();
        StateChanged += (_, _) => UpdateShellClip();

        _services = AppServices.Build(
            new AppHooks(new DpapiSecretProtector(), new WindowsNotificationService(),
                new StationDialogService(this), new ConfirmDialogService(this)),
            Stage);
        _viewModel = _services.ViewModel;
        _settingsStore = _services.Settings;
        DataContext = _viewModel;

        // Reset the About reading view to the top whenever fresh content loads (a new briefing
        // or a regenerate), so the previous track's scroll offset isn't carried over.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.OverlayState)
                && _viewModel.OverlayState is OverlayViewState.Loading or OverlayViewState.Result)
            {
                AboutScroll.ScrollToTop();
            }

            // Opening the vibe editor puts the caret in it with the text selected, so retyping the
            // whole vibe is one gesture rather than select-all-then-type (#39). View wiring, not
            // view-model state: the box only exists once the template has realised it, hence the
            // dispatcher hop.
            if (e.PropertyName == nameof(MainViewModel.IsEditingDjVibe) && _viewModel.IsEditingDjVibe)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    DjVibeEditBox.Focus();
                    DjVibeEditBox.SelectAll();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        };

        // Seek slider: suspend the position timer while the user drags the thumb, so ticks
        // don't fight the drag. Click-to-seek still flows through the Value TwoWay binding.
        SeekSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => _viewModel.BeginSeekDrag()));
        SeekSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) => _viewModel.EndSeekDrag()));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // SMTC must be obtained per-HWND, and the HWND only exists once the window is shown.
        var hwnd = new WindowInteropHelper(this).Handle;
        _smtc = new SmtcController(hwnd, _services.Engine);
        // Media-key / flyout "next track" → next station / next queue track (VM decides per mode).
        _smtc.NextRequested += (_, _) => _viewModel.NextStationCommand.Execute(null);
        // Mirror now-playing text to the OS controls in both modes, and repoint SMTC at the
        // active engine when the player mode changes.
        _viewModel.SetNowPlayingSink((title, artist) => _smtc.SetNowPlaying(title, artist));
        _viewModel.ActiveEngineChanged += engine => _smtc.SetActiveEngine(engine);

        ShowWelcomeOnFirstRun();
    }

    /// <summary>
    /// First run only (#50). Dispatched rather than shown inline: this runs during
    /// OnSourceInitialized, before the window has painted, and a modal opening over a blank shell
    /// looks like a crash dialog rather than a welcome.
    /// </summary>
    private void ShowWelcomeOnFirstRun()
    {
        if (_settingsStore.Load().HasSeenWelcome)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            // Written before the dialog opens, not after: if anything here throws, or the app is
            // closed from the welcome itself, the alternative is greeting them again every launch.
            var settings = _settingsStore.Load();
            settings.HasSeenWelcome = true;
            _settingsStore.Save(settings);

            var welcome = new WelcomeDialog { Owner = this };
            if (ShowDialogSafely(() => welcome) == true && welcome.OpenOptions)
                Options_Click(this, new RoutedEventArgs());
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        // SMTC goes where it always went in the teardown: after the DJ harvest, before the engines.
        _services.Shutdown(detachOsIntegration: () => _smtc?.Dispose());
        base.OnClosed(e);
    }

    // --- Rounded-corner clipping ---
    // WPF ClipToBounds clips to the rectangular layout bound, not the visual rounded shape,
    // so child content would render in the corner areas. Setting UIElement.Clip to a matching
    // RectangleGeometry forces true rounded clipping on all four corners.
    private void UpdateShellClip()
    {
        if (WindowState == WindowState.Maximized)
            ShellBorder.Clip = null;
        else
            ShellBorder.Clip = new RectangleGeometry(
                new Rect(0, 0, ShellBorder.ActualWidth, ShellBorder.ActualHeight), 13, 13);
    }

    // --- Custom window chrome (WindowStyle=None + WindowChrome; caption drag is automatic) ---

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
        => ShowDialogSafely(() => new AboutDialog { Owner = this });

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        if (ShowDialogSafely(() => new OptionsDialog(_settingsStore) { Owner = this }) == true)
            _services.ApplySettings();
    }

    /// <summary>
    /// Open a modal dialog, surfacing any construction/display failure as a message box instead
    /// of letting it bubble up as an unhandled exception that silently kills the whole app.
    /// </summary>
    private bool? ShowDialogSafely(Func<Window> create)
    {
        try
        {
            return create().ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the window:\n\n{ex.Message}", "Crystal Radio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private void SearchResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click a search result plays it (without adding it to the fixed list).
        if (_viewModel.SelectedSearchResult is not null)
            _viewModel.PlaySelectedSearchResult();
    }

    private void Stations_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click a station plays it — notably when stopped, where selecting alone won't.
        if (_viewModel.SelectedStation is not null)
            _viewModel.PlaySelectedStation();
    }

    private void Queue_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click a curated queue row to play from that track.
        if (sender is System.Windows.Controls.ListBox { SelectedItem: ViewModels.CuratedQueueItem item })
            _viewModel.PlayQueueItemCommand.Execute(item);
    }

    private void LibrarySong_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-click a Songs-tab row to play the whole library starting from that track.
        if (sender is System.Windows.Controls.ListBox { SelectedItem: ViewModels.LibrarySongItem item })
            _viewModel.PlayLibrarySongCommand.Execute(item);
    }

    private void CopyNowPlaying_Click(object sender, RoutedEventArgs e)
    {
        var text = _viewModel.NowPlayingClipboardText;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // The clipboard can be transiently locked by another app; ignore.
        }
    }
}
