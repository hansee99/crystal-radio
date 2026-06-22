using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RadioPlayer.Services;
using RadioPlayer.ViewModels;

namespace RadioPlayer;

/// <summary>
/// Interaction logic for MainWindow.xaml. Code-behind is limited to view wiring and the
/// HWND/SMTC bootstrap, per the project conventions.
/// </summary>
public partial class MainWindow : Window
{
    private readonly RadioEngine _engine;
    private readonly MainViewModel _viewModel;
    private SmtcController? _smtc;

    public MainWindow()
    {
        InitializeComponent();

        _engine = new RadioEngine();
        _viewModel = new MainViewModel(_engine, new StationStore(), new SettingsStore(),
            new StationDialogService(this));
        DataContext = _viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // SMTC must be obtained per-HWND, and the HWND only exists once the window is shown.
        var hwnd = new WindowInteropHelper(this).Handle;
        _smtc = new SmtcController(hwnd, _engine);
        // Media-key / flyout "next track" → next station (logic lives in the view model).
        _smtc.NextRequested += (_, _) => _viewModel.NextStationCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SaveSettings();
        _smtc?.Dispose();
        _engine.Dispose();
        base.OnClosed(e);
    }

    // --- Custom window chrome (we set WindowStyle=None) ---------------------

    private void Window_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
        => new AboutDialog { Owner = this }.ShowDialog();

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
