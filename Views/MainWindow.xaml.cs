using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly LocalPlaybackEngine _localEngine;
    private readonly StreamRecorder _recorder;
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly EnrichmentStore _enrichmentStore;
    private readonly LibraryStore _libraryStore;
    private readonly MiniLmEmbeddingProvider _embeddingProvider;
    private SmtcController? _smtc;

    public MainWindow()
    {
        InitializeComponent();
        Loaded       += (_, _) => UpdateShellClip();
        SizeChanged  += (_, _) => UpdateShellClip();
        StateChanged += (_, _) => UpdateShellClip();

        _settingsStore = new SettingsStore();
        _recorder = new StreamRecorder(_settingsStore.Load().CaptureBoundaryOffsetSeconds);
        _engine = new RadioEngine(_recorder);
        _localEngine = new LocalPlaybackEngine();

        // AI-assisted search services (raw HttpClient; key never committed). Prefer the key
        // saved in-app (DPAPI-encrypted), then fall back to the ANTHROPIC_API_KEY env var.
        var apiKey = _settingsStore.GetApiKey() ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
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
        var trackInfo = new TrackInfoService(new HttpClient(), apiKey); // "About this track" briefings

        // Phase C: local song-library index (metadata + AI description + local embedding on save).
        _libraryStore = new LibraryStore();
        var songLibrary = new SongLibraryService(new HttpClient(), _libraryStore, _embeddingProvider, apiKey);

        // Phase D: prompt-driven curation over the library index (cosine recall + LLM ordering).
        var curator = new SongCurator(new HttpClient(), _libraryStore, _embeddingProvider, apiKey);

        _viewModel = new MainViewModel(_engine, new StationStore(), _settingsStore,
            new SongHistoryStore(), _recorder,
            new StationDialogService(this), interpreter, searchService, agenticSearch, enrichment,
            semanticSearch, ranker, trackInfo, songLibrary, _localEngine, curator);
        DataContext = _viewModel;

        // Reset the About reading view to the top whenever fresh content loads (a new briefing
        // or a regenerate), so the previous track's scroll offset isn't carried over.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.AboutState)
                && _viewModel.AboutState is AboutViewState.Loading or AboutViewState.Result)
            {
                AboutScroll.ScrollToTop();
            }
        };

        // Seek slider: suspend the position timer while the user drags the thumb, so ticks
        // don't fight the drag. Click-to-seek still flows through the Value TwoWay binding.
        SeekSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => _viewModel.BeginSeekDrag()));
        SeekSlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) => _viewModel.EndSeekDrag()));

        // One-time/background: embed any enriched rows lacking a current-model vector.
        enrichment.BackfillEmbeddingsInBackground();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // SMTC must be obtained per-HWND, and the HWND only exists once the window is shown.
        var hwnd = new WindowInteropHelper(this).Handle;
        _smtc = new SmtcController(hwnd, _engine);
        // Media-key / flyout "next track" → next station / next queue track (VM decides per mode).
        _smtc.NextRequested += (_, _) => _viewModel.NextStationCommand.Execute(null);
        // Mirror now-playing text to the OS controls in both modes, and repoint SMTC at the
        // active engine when the player mode changes.
        _viewModel.SetNowPlayingSink((title, artist) => _smtc.SetNowPlaying(title, artist));
        _viewModel.ActiveEngineChanged += engine => _smtc.SetActiveEngine(engine);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.SaveSettings();
        _smtc?.Dispose();
        _localEngine.Dispose();  // free its stream before RadioEngine frees the shared BASS device
        _engine.Dispose();       // ends the capture session and calls Bass.Free()
        _recorder.Dispose();
        _embeddingProvider.Dispose();
        _enrichmentStore.Dispose();
        _libraryStore.Dispose();
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
        => ShowDialogSafely(() => new OptionsDialog(_settingsStore) { Owner = this });

    /// <summary>
    /// Open a modal dialog, surfacing any construction/display failure as a message box instead
    /// of letting it bubble up as an unhandled exception that silently kills the whole app.
    /// </summary>
    private void ShowDialogSafely(Func<Window> create)
    {
        try
        {
            create().ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the window:\n\n{ex.Message}", "Crystal Radio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
