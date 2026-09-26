using System.IO;
using System.Net.Http;
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
    private readonly RadioEngine _engine;
    private readonly LocalPlaybackEngine _localEngine;
    private readonly StreamRecorder _recorder;
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly EnrichmentStore _enrichmentStore;
    private readonly LibraryStore _libraryStore;
    private readonly MiniLmEmbeddingProvider _embeddingProvider;
    private readonly DjHarvestService _djHarvest;
    private readonly DjIntroService _djIntro;
    private readonly ApiKeySource _apiKeys;
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

        Stage("Reading your settings");
        _settingsStore = new SettingsStore(new DpapiSecretProtector());
        _recorder = new StreamRecorder(_settingsStore.Load().CaptureBoundaryOffsetSeconds);
        Stage("Starting the audio engine");
        _engine = new RadioEngine(_recorder);
        _localEngine = new LocalPlaybackEngine();
        // Edge guards for imperfect boundary cuts; 0 = off. Engine-wide, so they apply to saved
        // songs as well as the DJ mix — both come from the same cutting mechanism.
        var edgeSettings = _settingsStore.Load();
        _localEngine.IntroSkipSeconds = edgeSettings.IntroSkipSeconds;
        _localEngine.OutroGuardSeconds = edgeSettings.OutroGuardSeconds;

        // AI-assisted search services (raw HttpClient; key never committed). Prefer the key
        // saved in-app (DPAPI-encrypted), then fall back to the ANTHROPIC_API_KEY env var.
        // Shared by reference, not copied: every service below reads this same slot, so saving a
        // key in the options dialog reaches all of them without a restart. See ApiKeySource.
        _apiKeys = new ApiKeySource(ResolveApiKey());
        var apiKey = _apiKeys;
        var searchService = new StationSearchService(new HttpClient());
        var interpreter = new PromptInterpreter(new HttpClient(), apiKey);              // Pattern A

        // Phase 1 enrichment (SQLite cache) + Phase 2 local embeddings (offline ONNX).
        Stage("Opening your station catalog");
        _enrichmentStore = new EnrichmentStore();
        var mlDir = Path.Combine(AppContext.BaseDirectory, "MlAssets");
        // The long one on a cold start: an 86 MB model off disk plus session init.
        Stage("Loading the language model");
        _embeddingProvider = new MiniLmEmbeddingProvider(
            Path.Combine(mlDir, "all-MiniLM-L6-v2.onnx"), Path.Combine(mlDir, "vocab.txt"));

        var enrichment = new EnrichmentService(
            new HttpClient(), new HttpClient(), _enrichmentStore, _embeddingProvider, apiKey);
        var semanticSearch = new SemanticSearchService(_embeddingProvider, _enrichmentStore, searchService);
        var agenticSearch = new AgenticSearchService(            // Pattern B
            new HttpClient(), searchService, enrichment, apiKey);
        var ranker = new LlmSearchRanker(new HttpClient(), apiKey);     // relevance re-rank
        var trackInfo = new TrackInfoService(new HttpClient(), apiKey); // "About this track" briefings
        // LRCLIB needs no key, so lyrics work on a fresh install with nothing configured.
        var lyrics = new LyricsService(new HttpClient());
        var notifications = new WindowsNotificationService();
        // DJ Mode's on-air intro line; its voice is a settings.json knob (DjPersonality).
        _djIntro = new DjIntroService(new HttpClient(), apiKey, _settingsStore.Load().ResolveDjPersonality());

        // Phase C: local song-library index (metadata + AI description + local embedding on save).
        Stage("Opening your song library");
        _libraryStore = new LibraryStore();
        var songLibrary = new SongLibraryService(new HttpClient(), _libraryStore, _embeddingProvider,
            apiKey, lyrics: lyrics);

        // Phase D: prompt-driven curation over the library index (cosine recall + LLM ordering).
        var curator = new SongCurator(new HttpClient(), _libraryStore, _embeddingProvider, apiKey);

        // DJ mode: harvest pool + edge-trim QC, feeding songs into the same library index above
        // (tagged SongSource.Harvested) so warm-starts and future curation both benefit from it.
        // Offset 0 + edge-trim on are the PoC-validated defaults for the harvest path specifically
        // — independent of CaptureBoundaryOffsetSeconds, which stays 6.0 for the live "Save Song"
        // recorder above.
        var djSettings = _settingsStore.Load();
        var harvestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioPlayer", "harvest");
        _djHarvest = new DjHarvestService(searchService, interpreter, agenticSearch, ranker, enrichment, songLibrary, harvestDir,
            harvesterCount: djSettings.DjHarvesterCount, reserveCount: djSettings.DjHarvestReserveCount,
            offsetSeconds: 0.0, trimEdges: true,
            rejectBelow: djSettings.DjMusicFractionFloor,   // backstop under the duration gate
            minSongSeconds: djSettings.DjMinSongSeconds,
            stationIdleMinutes: djSettings.DjStationIdleMinutes,
            maxHarvestCacheBytes: djSettings.ResolveHarvestCacheBytes(),
            maxRejectedCacheBytes: djSettings.ResolveRejectedCacheBytes(),
            // So a session can still start off the local catalog when the mirrors are down (#26).
            semanticSearch: semanticSearch);

        Stage("Almost there");
        _viewModel = new MainViewModel(_engine, new StationStore(), _settingsStore,
            new SongHistoryStore(), _recorder,
            new StationDialogService(this), interpreter, searchService, agenticSearch, enrichment,
            semanticSearch, ranker, trackInfo, lyrics, notifications, songLibrary, _localEngine, curator, _djHarvest, _djIntro,
            new ConfirmDialogService(this));   // asks before a mode switch throws something away (#46)
        DataContext = _viewModel;
        _viewModel.DjNotificationsEnabled = _settingsStore.Load().DjNotificationsEnabled;

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
        _viewModel.SaveSettings();
        _djHarvest.Dispose();   // stops harvesters and tears down its own (device 0) BASS thread first
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
    {
        if (ShowDialogSafely(() => new OptionsDialog(_settingsStore) { Owner = this }) == true)
            ApplySettings();
    }

    /// <summary>The key actually in effect: the one saved in-app (DPAPI-encrypted) if there is
    /// one, otherwise the environment's.</summary>
    private string? ResolveApiKey() =>
        _settingsStore.GetApiKey() ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    /// <summary>
    /// Pushes saved settings into the already-running app. Every one of these was previously read
    /// once in this constructor and never again, which is why the dialog used to carry a "restart
    /// Crystal Radio" notice.
    ///
    /// <para>Not all of it can be instant, and the dialog says which is which rather than
    /// pretending: <see cref="DjHarvestService.HarvesterCount"/> is consulted when a session
    /// starts, so changing it cannot re-deal the harvesters of a mix already playing.</para>
    /// </summary>
    private void ApplySettings()
    {
        var settings = _settingsStore.Load();

        _apiKeys.Current = ResolveApiKey();
        _viewModel.LibraryFolder = settings.ResolveLibraryFolder();
        _djIntro.Personality = settings.ResolveDjPersonality();

        // Read per track, so whatever is playing keeps the guards it started with.
        _localEngine.IntroSkipSeconds = settings.IntroSkipSeconds;
        _localEngine.OutroGuardSeconds = settings.OutroGuardSeconds;

        _viewModel.DjNotificationsEnabled = settings.DjNotificationsEnabled;

        _djHarvest.HarvesterCount = settings.DjHarvesterCount;   // next session
        _djHarvest.MaxHarvestCacheBytes = settings.ResolveHarvestCacheBytes();
        _djHarvest.MaxRejectedCacheBytes = settings.ResolveRejectedCacheBytes();
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
