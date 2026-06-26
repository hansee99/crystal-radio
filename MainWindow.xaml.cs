using System.IO;
using System.Net.Http;
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
    private readonly EnrichmentStore _enrichmentStore;
    private readonly MiniLmEmbeddingProvider _embeddingProvider;
    private SmtcController? _smtc;

    public MainWindow()
    {
        InitializeComponent();

        _engine = new RadioEngine();

        // AI-assisted search services (raw HttpClient; key from the environment, never committed).
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var searchService = new StationSearchService(new HttpClient());
        var interpreter = new PromptInterpreter(new HttpClient(), apiKey);              // Pattern A

        // Phase 1 enrichment (SQLite cache) + Phase 2 local embeddings (offline ONNX).
        _enrichmentStore = new EnrichmentStore();
        var mlDir = Path.Combine(AppContext.BaseDirectory, "MlAssets");
        _embeddingProvider = new MiniLmEmbeddingProvider(
            Path.Combine(mlDir, "all-MiniLM-L6-v2.onnx"), Path.Combine(mlDir, "vocab.txt"));

        var enrichment = new EnrichmentService(
            new HttpClient(), new HttpClient(), _enrichmentStore, _embeddingProvider, apiKey);
        var semanticSearch = new SemanticSearchService(_embeddingProvider, _enrichmentStore, searchService);
        var agenticSearch = new AgenticSearchService(            // Pattern B
            new HttpClient(), searchService, enrichment, apiKey);
        var ranker = new LlmSearchRanker(new HttpClient(), apiKey);     // relevance re-rank
        var classifier = new LlmQueryClassifier(new HttpClient(), apiKey); // literal vs fuzzy routing

        _viewModel = new MainViewModel(_engine, new StationStore(), new SettingsStore(),
            new StationDialogService(this), interpreter, searchService, agenticSearch, enrichment,
            semanticSearch, ranker, classifier);
        DataContext = _viewModel;

        // One-time/background: embed any enriched rows lacking a current-model vector.
        enrichment.BackfillEmbeddingsInBackground();
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
        _embeddingProvider.Dispose();
        _enrichmentStore.Dispose();
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
