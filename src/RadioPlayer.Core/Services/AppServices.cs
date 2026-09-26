using System.IO;
using System.Net.Http;
using RadioPlayer.ViewModels;

namespace RadioPlayer.Services;

/// <summary>What a head supplies: the four things that differ between a desktop window and a
/// browser. Everything else in the object graph is the same on every platform.</summary>
public sealed record AppHooks(
    ISecretProtector Secrets,
    INotificationService Notifications,
    IStationDialog StationDialog,
    IConfirmDialog ConfirmDialog);

/// <summary>
/// The app's object graph, built once per process and shared by both heads, so the WPF window and
/// the web host run exactly the same services wired exactly the same way.
///
/// <para>Construct on the thread that will own playback: the engines capture
/// DispatcherContext.Current in their constructors. Construction order, the splash stages and the
/// teardown order in <see cref="Shutdown"/> are the ones MainWindow used — the last one matters
/// (BASS device ownership, the harvest thread's own device).</para>
/// </summary>
public sealed class AppServices : IDisposable
{
    public required SettingsStore Settings { get; init; }
    public required StreamRecorder Recorder { get; init; }
    public required RadioEngine Engine { get; init; }
    public required LocalPlaybackEngine LocalEngine { get; init; }
    public required ApiKeySource ApiKeys { get; init; }
    public required EnrichmentStore EnrichmentStore { get; init; }
    public required MiniLmEmbeddingProvider EmbeddingProvider { get; init; }
    public required LibraryStore LibraryStore { get; init; }
    public required DjIntroService DjIntro { get; init; }
    public required DjHarvestService DjHarvest { get; init; }
    public required MainViewModel ViewModel { get; init; }

    private bool _shutDown;

    /// <param name="stage">Startup progress for a splash screen, called before each slow stage.</param>
    public static AppServices Build(AppHooks hooks, Action<string>? stage = null)
    {
        void Stage(string text) => stage?.Invoke(text);

        Stage("Reading your settings");
        var settingsStore = new SettingsStore(hooks.Secrets);
        var recorder = new StreamRecorder(settingsStore.Load().CaptureBoundaryOffsetSeconds);
        Stage("Starting the audio engine");
        var engine = new RadioEngine(recorder);
        var localEngine = new LocalPlaybackEngine();
        // Edge guards for imperfect boundary cuts; 0 = off. Engine-wide, so they apply to saved
        // songs as well as the DJ mix — both come from the same cutting mechanism.
        var edgeSettings = settingsStore.Load();
        localEngine.IntroSkipSeconds = edgeSettings.IntroSkipSeconds;
        localEngine.OutroGuardSeconds = edgeSettings.OutroGuardSeconds;

        // AI-assisted search services (raw HttpClient; key never committed). Prefer the key
        // saved in-app (protected by hooks.Secrets), then fall back to the ANTHROPIC_API_KEY env var.
        // Shared by reference, not copied: every service below reads this same slot, so saving a
        // key in the options dialog reaches all of them without a restart. See ApiKeySource.
        var apiKey = new ApiKeySource(ResolveApiKey(settingsStore));
        var searchService = new StationSearchService(new HttpClient());
        var interpreter = new PromptInterpreter(new HttpClient(), apiKey);              // Pattern A

        // Phase 1 enrichment (SQLite cache) + Phase 2 local embeddings (offline ONNX).
        Stage("Opening your station catalog");
        var enrichmentStore = new EnrichmentStore();
        var mlDir = Path.Combine(AppContext.BaseDirectory, "MlAssets");
        // The long one on a cold start: an 86 MB model off disk plus session init.
        Stage("Loading the language model");
        var embeddingProvider = new MiniLmEmbeddingProvider(
            Path.Combine(mlDir, "all-MiniLM-L6-v2.onnx"), Path.Combine(mlDir, "vocab.txt"));

        var enrichment = new EnrichmentService(
            new HttpClient(), new HttpClient(), enrichmentStore, embeddingProvider, apiKey);
        var semanticSearch = new SemanticSearchService(embeddingProvider, enrichmentStore, searchService);
        var agenticSearch = new AgenticSearchService(            // Pattern B
            new HttpClient(), searchService, enrichment, apiKey);
        var ranker = new LlmSearchRanker(new HttpClient(), apiKey);     // relevance re-rank
        var trackInfo = new TrackInfoService(new HttpClient(), apiKey); // "About this track" briefings
        // LRCLIB needs no key, so lyrics work on a fresh install with nothing configured.
        var lyrics = new LyricsService(new HttpClient());
        // DJ Mode's on-air intro line; its voice is a settings.json knob (DjPersonality).
        var djIntro = new DjIntroService(new HttpClient(), apiKey, settingsStore.Load().ResolveDjPersonality());

        // Phase C: local song-library index (metadata + AI description + local embedding on save).
        Stage("Opening your song library");
        var libraryStore = new LibraryStore();
        var songLibrary = new SongLibraryService(new HttpClient(), libraryStore, embeddingProvider,
            apiKey, lyrics: lyrics);

        // Phase D: prompt-driven curation over the library index (cosine recall + LLM ordering).
        var curator = new SongCurator(new HttpClient(), libraryStore, embeddingProvider, apiKey);

        // DJ mode: harvest pool + edge-trim QC, feeding songs into the same library index above
        // (tagged SongSource.Harvested) so warm-starts and future curation both benefit from it.
        // Offset 0 + edge-trim on are the PoC-validated defaults for the harvest path specifically
        // — independent of CaptureBoundaryOffsetSeconds, which stays 6.0 for the live "Save Song"
        // recorder above.
        var djSettings = settingsStore.Load();
        var harvestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "RadioPlayer", "harvest");
        var djHarvest = new DjHarvestService(searchService, interpreter, agenticSearch, ranker, enrichment, songLibrary, harvestDir,
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
        var viewModel = new MainViewModel(engine, new StationStore(), settingsStore,
            new SongHistoryStore(), recorder,
            hooks.StationDialog, interpreter, searchService, agenticSearch, enrichment,
            semanticSearch, ranker, trackInfo, lyrics, hooks.Notifications, songLibrary, localEngine, curator, djHarvest, djIntro,
            hooks.ConfirmDialog);   // asks before a mode switch throws something away (#46)
        var startupSettings = settingsStore.Load();
        viewModel.DjNotificationsEnabled = startupSettings.DjNotificationsEnabled;
        viewModel.DjRemarkSize = startupSettings.ResolveDjRemarkSize();

        // One-time/background: embed any enriched rows lacking a current-model vector.
        enrichment.BackfillEmbeddingsInBackground();

        return new AppServices
        {
            Settings = settingsStore,
            Recorder = recorder,
            Engine = engine,
            LocalEngine = localEngine,
            ApiKeys = apiKey,
            EnrichmentStore = enrichmentStore,
            EmbeddingProvider = embeddingProvider,
            LibraryStore = libraryStore,
            DjIntro = djIntro,
            DjHarvest = djHarvest,
            ViewModel = viewModel,
        };
    }

    /// <summary>The key actually in effect: the one saved in-app if there is one, otherwise the
    /// environment's.</summary>
    private static string? ResolveApiKey(SettingsStore settings) =>
        settings.GetApiKey() ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    /// <summary>
    /// Pushes saved settings into the already-running app. Every one of these was once read only at
    /// startup, which is why the options dialog used to carry a "restart Crystal Radio" notice.
    ///
    /// <para>Not all of it can be instant, and the dialog says which is which rather than
    /// pretending: <see cref="DjHarvestService.HarvesterCount"/> is consulted when a session
    /// starts, so changing it cannot re-deal the harvesters of a mix already playing.</para>
    /// </summary>
    public void ApplySettings()
    {
        var settings = Settings.Load();

        ApiKeys.Current = ResolveApiKey(Settings);
        ViewModel.LibraryFolder = settings.ResolveLibraryFolder();
        DjIntro.Personality = settings.ResolveDjPersonality();
        ViewModel.DjRemarkSize = settings.ResolveDjRemarkSize();   // live, mid-session included

        // Read per track, so whatever is playing keeps the guards it started with.
        LocalEngine.IntroSkipSeconds = settings.IntroSkipSeconds;
        LocalEngine.OutroGuardSeconds = settings.OutroGuardSeconds;

        ViewModel.DjNotificationsEnabled = settings.DjNotificationsEnabled;

        DjHarvest.HarvesterCount = settings.DjHarvesterCount;   // next session
        DjHarvest.MaxHarvestCacheBytes = settings.ResolveHarvestCacheBytes();
        DjHarvest.MaxRejectedCacheBytes = settings.ResolveRejectedCacheBytes();
    }

    /// <summary>
    /// Saves settings and tears everything down, in the order that keeps BASS happy.
    /// </summary>
    /// <param name="detachOsIntegration">The head's OS media integration (SMTC on Windows),
    /// disposed at the point MainWindow always did it: after the DJ harvest has stopped — stopping
    /// it can still push now-playing updates through the view model — and before the engines it
    /// watches are freed.</param>
    public void Shutdown(Action? detachOsIntegration = null)
    {
        if (_shutDown) return;
        _shutDown = true;

        ViewModel.SaveSettings();
        DjHarvest.Dispose();   // stops harvesters and tears down its own (device 0) BASS thread first
        detachOsIntegration?.Invoke();
        LocalEngine.Dispose();  // free its stream before RadioEngine frees the shared BASS device
        Engine.Dispose();       // ends the capture session and calls Bass.Free()
        Recorder.Dispose();
        EmbeddingProvider.Dispose();
        EnrichmentStore.Dispose();
        LibraryStore.Dispose();
    }

    public void Dispose() => Shutdown();
}
