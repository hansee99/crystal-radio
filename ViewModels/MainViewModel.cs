using System.Collections.ObjectModel;
using System.IO;
using RadioPlayer.Models;
using RadioPlayer.Mvvm;
using RadioPlayer.Services;

namespace RadioPlayer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly RadioEngine _engine;
    private readonly StationStore _store;
    private readonly SettingsStore _settingsStore;
    private readonly SongHistoryStore _historyStore;
    private readonly StreamRecorder _recorder;
    private readonly long _cacheCapBytes;
    private readonly string _libraryFolder;
    private readonly IStationDialog _stationDialog;
    private readonly IPromptInterpreter _interpreter;
    private readonly IStationSearchService _searchService;
    private readonly IAgenticSearchService _agenticSearch;
    private readonly IEnrichmentService _enrichment;
    private readonly ISemanticSearchService _semanticSearch;
    private readonly ISearchRanker _ranker;
    private readonly ITrackInfoService _trackInfoService;
    private readonly ISongLibraryService _songLibrary;
    private readonly LocalPlaybackEngine _local;
    private readonly ISongCurator _curator;
    private readonly DjHarvestService _djHarvest;
    private readonly DjQueueService _djQueue;
    private readonly IDjIntroService _djIntro;
    // DjHarvestService's events fire on a background thread (unlike every other engine this
    // view model handles, which already marshal to the UI thread before raising anything) — this
    // is the one dispatcher MainViewModel captures itself, purely to marshal those.
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    // Below this cosine score the local index is considered too weak (heuristic fallback only).
    private const double SemanticThreshold = 0.30;

    // Max search results shown (top matches).
    private const int MaxResults = 6;

    // Validate a few extra so dropping dead streams still tends to leave MaxResults working.
    private const int ResultsToValidate = MaxResults + 3;

    // How many local candidates to gather for the re-ranker to choose from.
    private const int CandidatePoolSize = 12;

    private Station? _selectedStation;
    private SearchResultItem? _selectedSearchResult;
    private string _searchPrompt = string.Empty;
    private string _searchStatus = string.Empty;
    private bool _isSearching;

    // Cancels the in-flight search pipeline (see RunSearchAsync) when the user switches away
    // from the Radio panel mid-search, or hits Cancel in the progress panel.
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _curateCts;
    private CancellationTokenSource? _djStartCts;

    // "Show different" (Regenerate) search: remember what's already been shown for the current
    // prompt so a re-run surfaces fresh stations, and page deeper into the directory each time.
    private readonly HashSet<string> _shownStationUrls = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSearchPrompt = string.Empty;
    private int _searchPage;

    // Radio Browser rows fetched per structured page — must match StationSearchService's limit
    // so paging on regenerate advances past the previous page's directory rows.
    private const int StructuredPageSize = 30;
    private string _nowPlayingTitle = "Nothing playing"; // == IdleTitle (const not usable here)
    private string _nowPlayingArtist = string.Empty;
    private string _nowPlayingStation = string.Empty;
    private string _nowPlayingFormat = string.Empty;
    private string _statusText = "Stopped";
    private bool _isPlaying;
    private bool _hasTrackInfo;
    private double _volume;

    // "About this track" reading-view state.
    private AboutViewState _aboutState = AboutViewState.Home;
    private TrackInfo? _trackInfo;
    private string _aboutError = string.Empty;
    private CancellationTokenSource? _aboutCts;
    // The track a briefing is ABOUT, frozen when it opens. The reading view shows these (not the
    // live now-playing fields) so a new song can start underneath without disturbing what the
    // user is reading — the view stays put until they hit Back. Since the subject can also come
    // from a history row, the originating station is frozen alongside (for Regenerate).
    private string _aboutSubjectTitle = string.Empty;
    private string _aboutSubjectArtist = string.Empty;
    private string? _aboutSubjectStation;

    // When set, changing SelectedStation won't auto-start playback. Used by the
    // add/edit/delete commands so managing the list doesn't yank what's playing.
    private bool _suppressAutoPlay;

    public MainViewModel(RadioEngine engine, StationStore store, SettingsStore settingsStore,
        SongHistoryStore historyStore, StreamRecorder recorder,
        IStationDialog stationDialog, IPromptInterpreter interpreter, IStationSearchService searchService,
        IAgenticSearchService agenticSearch, IEnrichmentService enrichment,
        ISemanticSearchService semanticSearch, ISearchRanker ranker, ITrackInfoService trackInfoService,
        ISongLibraryService songLibrary, LocalPlaybackEngine local, ISongCurator curator,
        DjHarvestService djHarvest, IDjIntroService djIntro)
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _engine = engine;
        _store = store;
        _settingsStore = settingsStore;
        _historyStore = historyStore;
        _recorder = recorder;
        _stationDialog = stationDialog;
        _interpreter = interpreter;
        _searchService = searchService;
        _agenticSearch = agenticSearch;
        _enrichment = enrichment;
        _semanticSearch = semanticSearch;
        _ranker = ranker;
        _trackInfoService = trackInfoService;
        _songLibrary = songLibrary;
        _local = local;
        _curator = curator;
        _djHarvest = djHarvest;
        _djIntro = djIntro;
        _djQueue = new DjQueueService(local, curator, djHarvest,
            maxSeed: 20, lowWatermark: settingsStore.Load().DjQueueLowWatermark);

        // DjHarvestService's events fire on a background thread (see its own doc comment) —
        // marshal before touching any UI-bound property.
        _djHarvest.StatusChanged += (_, status) => _dispatcher.BeginInvoke(() => OnDjStatusChanged(status));

        // Restore the persisted volume onto both engines (either/or, but volume is shared).
        _volume = settingsStore.Load().Volume;
        _engine.Volume = _volume;
        _local.Volume = _volume;

        _engine.StateChanged += (_, state) => OnStateChanged(state);
        _engine.MetadataChanged += (_, meta) => OnMetadataChanged(meta);
        _engine.ErrorOccurred += (_, msg) => StatusText = msg;

        // Local (library/DJ) engine — its handlers no-op unless one of those modes is active.
        _local.StateChanged += (_, state) => OnLocalStateChanged(state);
        _local.TrackChanged += (_, e) => OnLocalTrackChanged(e.Track);
        _local.PositionChanged += (_, e) => OnLocalPosition(e.Position, e.Duration);
        _local.ErrorOccurred += (_, msg) => LibraryStatus = msg;

        // Rolling-cache settings resolved once at startup (Options changes apply on restart,
        // consistent with the other settings).
        var settings = settingsStore.Load();
        _cacheCapBytes = settings.CacheCapMb * 1024L * 1024L;
        _libraryFolder = settings.ResolveLibraryFolder();

        Stations = new ObservableCollection<Station>(_store.Load());
        _selectedStation = Stations.FirstOrDefault();
        History = new ObservableCollection<SongHistoryEntry>(_historyStore.Load());

        // Reconcile history with the cache on disk: drop references to segments that no longer
        // exist, then delete cache files nothing references (crash leftovers).
        foreach (var entry in History)
        {
            if (entry.SegmentFile is not null && !File.Exists(_recorder.PathFor(entry.SegmentFile)))
            {
                entry.SegmentFile = null;
                entry.SegmentBytes = 0;
            }
        }
        StreamRecorder.SweepOrphans(History.Where(e => e.SegmentFile is not null).Select(e => e.SegmentFile!));

        _recorder.SegmentCompleted += (_, seg) => OnSegmentCompleted(seg);

        // Reconcile the song library against disk and finish any pending enrichment/embeddings.
        _songLibrary.BackfillInBackground();
        LibrarySongs = new ObservableCollection<LibrarySongItem>(
            _songLibrary.GetAll(SongSource.UserSaved).Select(s => new LibrarySongItem(s)));

        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(() => ActiveEngine.Stop(), () => ActiveEngine.State != PlaybackState.Stopped);
        NextStationCommand = new RelayCommand(Next, CanGoNext);
        PrevStationCommand = new RelayCommand(Prev, CanGoPrev);
        SwitchToRadioCommand = new RelayCommand(() => SetMode(PlayerMode.Radio));
        SwitchToLibraryCommand = new RelayCommand(() => SetMode(PlayerMode.Library));
        SwitchToDjCommand = new RelayCommand(() => SetMode(PlayerMode.Dj));
        CurateCommand = new RelayCommand(() => _ = RunCurateAsync(), () => !IsCurating);
        StartDjCommand = new RelayCommand(() => _ = StartDjAsync(),
            () => !IsDjRunning && !string.IsNullOrWhiteSpace(DjPrompt));
        StopDjCommand = new RelayCommand(StopDj, () => IsDjRunning);

        // Staged progress for the three long AI waits, each with its own Cancel (UX audit).
        SearchProgress = new StagedProgress { CancelCommand = new RelayCommand(() => _searchCts?.Cancel()) };
        CurateProgress = new StagedProgress { CancelCommand = new RelayCommand(() => _curateCts?.Cancel()) };
        DjProgress = new StagedProgress { CancelCommand = new RelayCommand(() => _djStartCts?.Cancel()) };
        PlayQueueItemCommand = new RelayCommand<CuratedQueueItem>(PlayQueueItem);
        PlayLibrarySongCommand = new RelayCommand<LibrarySongItem>(PlayLibrarySong);
        AddStationCommand = new RelayCommand(AddStation);
        EditStationCommand = new RelayCommand<Station>(EditStation, s => s is not null);
        DeleteStationCommand = new RelayCommand<Station>(DeleteStation, s => s is not null);
        SearchCommand = new RelayCommand(() => _ = RunSearchAsync(regenerate: false), () => !IsSearching);
        RegenerateSearchCommand = new RelayCommand(() => _ = RunSearchAsync(regenerate: true),
            () => CanRegenerateSearch);
        AddSearchResultCommand = new RelayCommand<SearchResultItem>(AddSearchResultToLibrary);

        OpenAboutCommand = new RelayCommand(
            () => _ = GenerateAboutAsync(NowPlayingTitle, NowPlayingArtist, NowPlayingStation, forceRefresh: false),
            () => CanShowAbout);
        RegenerateAboutCommand = new RelayCommand(
            () => _ = GenerateAboutAsync(AboutSubjectTitle, AboutSubjectArtist, _aboutSubjectStation, forceRefresh: true),
            () => CanRegenerateAbout);
        OpenAboutForEntryCommand = new RelayCommand<SongHistoryEntry>(
            e => { if (e is not null) _ = GenerateAboutAsync(e.Title, e.Artist, e.Station, forceRefresh: false); });
        BackToNowPlayingCommand = new RelayCommand(BackToNowPlaying);
        SaveSongCommand = new RelayCommand<SongHistoryEntry>(SaveSong, e => e?.CanSave == true);
        MarkForSaveCommand = new RelayCommand(ToggleMarkForSave, () => CanMarkForSave && !IsCurrentSongSaved);

        // Every list panel's state is derived from its collection's count, so each collection
        // drives its own panel's change notification (UX audit: PanelState).
        WatchPanel(Stations, nameof(StationsPanelState));
        WatchPanel(SearchResults, nameof(SearchPanelState));
        WatchPanel(History, nameof(HistoryPanelState));
        WatchPanel(CuratedQueue, nameof(CuratePanelState));
        WatchPanel(LibrarySongs, nameof(LibrarySongsPanelState));
        WatchPanel(DjHarvesters, nameof(DjSourcesPanelState));
        WatchPanel(DjMix, nameof(DjMixPanelState));

        // The Mix list is a view of the engine's queue, so it follows both "the queue changed"
        // and "we moved within it".
        _local.QueueChanged += (_, _) => SyncDjMix();

        // Show the selected station's preview from the very first frame instead of a bare
        // "Not playing" (Shared Framework Spec §4a) — SelectedStation was set on the backing
        // field above, bypassing the setter's own preview refresh.
        ApplyStoppedPreview();
        RefreshRecentOnStation();
    }

    /// <summary>Re-raise <paramref name="statePropertyName"/> whenever the backing collection
    /// gains or loses rows, so the panel's derived state follows its content.</summary>
    private void WatchPanel(System.Collections.Specialized.INotifyCollectionChanged collection,
        string statePropertyName) =>
        collection.CollectionChanged += (_, _) => OnPropertyChanged(statePropertyName);

    // ===== AI-assisted station search =====

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    public RelayCommand SearchCommand { get; }
    public RelayCommand RegenerateSearchCommand { get; }
    public RelayCommand<SearchResultItem> AddSearchResultCommand { get; }

    // "About this track" — on-demand AI briefing about a song (now playing or from history).
    public RelayCommand OpenAboutCommand { get; }
    public RelayCommand RegenerateAboutCommand { get; }
    public RelayCommand<SongHistoryEntry> OpenAboutForEntryCommand { get; }
    public RelayCommand BackToNowPlayingCommand { get; }

    // ===== Song history + rolling cache (Phases A/B) =====

    /// <summary>Songs heard on streams, newest first, persisted across sessions.</summary>
    public ObservableCollection<SongHistoryEntry> History { get; }

    /// <summary>Save a completed song's cached audio into the library folder.</summary>
    public RelayCommand<SongHistoryEntry> SaveSongCommand { get; }

    /// <summary>Mark/unmark the currently-playing song to be saved when its segment completes.</summary>
    public RelayCommand MarkForSaveCommand { get; }

    /// <summary>Cap on stored history rows (metadata is tiny; this is a UI/file sanity bound).</summary>
    private const int HistoryCap = 100;

    /// <summary>The per-row About affordance shows only when the service has an API key.</summary>
    public bool IsAboutAvailable => _trackInfoService.IsConfigured;

    /// <summary>Songs previously heard on the station currently shown in Now Playing (playing, or
    /// merely selected-but-not-started — see <see cref="ApplyStoppedPreview"/>), newest first.
    /// Fills the space Radio has no scrubber to put in (Shared Framework Spec §4a, pin 2).</summary>
    public ObservableCollection<SongHistoryEntry> RecentOnStation { get; } = new();

    private const int RecentOnStationCap = 4;

    /// <summary>Radio-only: the list shows once there's at least one prior play for this station.</summary>
    public bool ShowRecentOnStation => IsRadioMode && RecentOnStation.Count > 0;

    private void RefreshRecentOnStation()
    {
        RecentOnStation.Clear();
        if (!string.IsNullOrWhiteSpace(NowPlayingStation))
        {
            foreach (var e in History
                .Where(h => string.Equals(h.Station, NowPlayingStation, StringComparison.OrdinalIgnoreCase))
                .Take(RecentOnStationCap))
                RecentOnStation.Add(e);
        }
        OnPropertyChanged(nameof(ShowRecentOnStation));
    }

    /// <summary>
    /// Record a title change in the history: filter out ads/jingles/idents, skip consecutive
    /// duplicates (reconnects re-announce the same song), cap, persist.
    /// </summary>
    private void RecordHistory(TrackMetadata meta)
    {
        if (!SongHistoryFilter.IsLikelySong(meta.Title, meta.Artist, meta.StationName))
            return;
        if (History.Count > 0
            && string.Equals(History[0].Title, meta.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(History[0].Artist, meta.Artist, StringComparison.OrdinalIgnoreCase))
            return;

        var entry = new SongHistoryEntry
        {
            Title = meta.Title,
            Artist = meta.Artist!,
            Station = meta.StationName ?? string.Empty,
            PlayedAt = DateTime.Now
        };
        History.Insert(0, entry);
        while (History.Count > HistoryCap)
            RemoveHistoryAt(History.Count - 1);
        _historyStore.Save(History);
        RefreshRecentOnStation(); // the new entry (or an evicted one) may affect this station's list

        // This is now the current song; the mark-for-save toggle targets it (fresh → unmarked).
        SetCurrentSong(entry);
    }

    /// <summary>Removes a history row AND its cached segment file (never orphan audio).</summary>
    private void RemoveHistoryAt(int index)
    {
        var entry = History[index];
        if (entry.SegmentFile is not null)
            StreamRecorder.TryDelete(_recorder.PathFor(entry.SegmentFile));
        History.RemoveAt(index);
    }

    /// <summary>
    /// A song finished capturing completely: attach the segment to its history row (making it
    /// saveable), then enforce the cache size cap. Raised on the UI thread right after the
    /// next song's history row was recorded, so the finished song sits just below it.
    /// </summary>
    private void OnSegmentCompleted(CompletedSegment seg)
    {
        var entry = History.FirstOrDefault(e =>
            e.SegmentFile is null
            && string.Equals(e.Title, seg.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Artist, seg.Artist ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            // No matching row (filtered or trimmed in the meantime) — don't keep orphan audio.
            StreamRecorder.TryDelete(_recorder.PathFor(seg.FileName));
            return;
        }

        entry.SegmentFile = seg.FileName;
        entry.SegmentBytes = seg.Bytes;

        // Marked while playing → save it now that its audio is complete. If it was never
        // completed (stopped mid-song, or a mid-song head segment), we simply never get here.
        if (entry.MarkedForSave && entry.CanSave)
            SaveSong(entry);

        PruneCacheToCap();
        _historyStore.Save(History);
        SaveSongCommand.RaiseCanExecuteChanged();
    }

    // --- Mark the currently-playing song to be saved when its segment completes ---

    private SongHistoryEntry? _currentSong;

    /// <summary>The mark toggle is available while a radio song is playing (the library plays
    /// already-saved files).</summary>
    public bool CanMarkForSave => IsRadioMode && _currentSong is not null;

    /// <summary>Whether the current song is marked (drives the toggle button's state).</summary>
    public bool IsCurrentSongMarked => _currentSong?.MarkedForSave == true;

    /// <summary>Whether the current song has already been saved (button becomes a non-interactive
    /// check, matching the download→check pair used everywhere else Save appears).</summary>
    public bool IsCurrentSongSaved => _currentSong?.IsSaved == true;

    private void SetCurrentSong(SongHistoryEntry? entry)
    {
        _currentSong = entry;
        OnPropertyChanged(nameof(CanMarkForSave));
        OnPropertyChanged(nameof(IsCurrentSongMarked));
        OnPropertyChanged(nameof(IsCurrentSongSaved));
        MarkForSaveCommand.RaiseCanExecuteChanged();
    }

    private void ToggleMarkForSave()
    {
        if (_currentSong is null || _currentSong.IsSaved) return;
        _currentSong.MarkedForSave = !_currentSong.MarkedForSave;
        OnPropertyChanged(nameof(IsCurrentSongMarked));

        // If it's already saveable (segment complete) and just got marked, save immediately.
        if (_currentSong.MarkedForSave && _currentSong.CanSave)
            SaveSong(_currentSong);
    }

    /// <summary>Evict the oldest cached segments until the cache fits the configured cap.
    /// The rows stay in the history — they just lose their Save affordance.</summary>
    private void PruneCacheToCap()
    {
        var total = History.Where(e => e.SegmentFile is not null).Sum(e => e.SegmentBytes);
        for (var i = History.Count - 1; i >= 0 && total > _cacheCapBytes; i--)
        {
            var entry = History[i];
            if (entry.SegmentFile is null)
                continue;
            StreamRecorder.TryDelete(_recorder.PathFor(entry.SegmentFile));
            total -= entry.SegmentBytes;
            entry.SegmentFile = null;
            entry.SegmentBytes = 0;
        }
    }

    /// <summary>
    /// Copy a completed segment into the library folder as "Artist - Title.ext" (personal use —
    /// the raw stream bytes, no re-encode). Best-effort: failure surfaces in the status text.
    /// </summary>
    private void SaveSong(SongHistoryEntry? entry)
    {
        if (entry is not { SegmentFile: not null } || entry.IsSaved)
            return;
        try
        {
            Directory.CreateDirectory(_libraryFolder);

            var ext = Path.GetExtension(entry.SegmentFile);
            var baseName = SanitizeFileName($"{entry.Artist} - {entry.Title}");
            var dest = Path.Combine(_libraryFolder, baseName + ext);
            for (var n = 2; File.Exists(dest); n++)
                dest = Path.Combine(_libraryFolder, $"{baseName} ({n}){ext}");

            File.Copy(_recorder.PathFor(entry.SegmentFile), dest);
            entry.SavedPath = dest;
            _historyStore.Save(History);
            SaveSongCommand.RaiseCanExecuteChanged();

            // Index the saved song (metadata now; AI description + embedding fill in async).
            _songLibrary.AddAndEnrich(new SavedSong(
                Path: dest,
                Title: entry.Title,
                Artist: entry.Artist,
                Station: string.IsNullOrWhiteSpace(entry.Station) ? null : entry.Station,
                Codec: ext.TrimStart('.').ToUpperInvariant(),
                SavedAt: DateTimeOffset.Now));
            RefreshLibrarySongs(); // so the Songs tab shows it immediately (description fills in later)

            if (ReferenceEquals(entry, _currentSong))
            {
                OnPropertyChanged(nameof(IsCurrentSongSaved));
                MarkForSaveCommand.RaiseCanExecuteChanged();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("[Save] failed", ex);
            StatusText = "Couldn't save the song — check the library folder in Options.";
        }
    }

    /// <summary>Makes "Artist - Title" safe as a file name (invalid chars → '_', capped length).</summary>
    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length > 120 ? name[..120] : name;
    }

    public string SearchPrompt
    {
        get => _searchPrompt;
        set
        {
            if (SetProperty(ref _searchPrompt, value))
                RaiseRegenerateCanExecute();
        }
    }

    /// <summary>"Show different" is offered once there are results for a prompt and we're idle.</summary>
    public bool CanRegenerateSearch =>
        !IsSearching && SearchResults.Count > 0 && !string.IsNullOrWhiteSpace(SearchPrompt);

    private void RaiseRegenerateCanExecute()
    {
        OnPropertyChanged(nameof(CanRegenerateSearch));
        RegenerateSearchCommand.RaiseCanExecuteChanged();
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                SearchCommand.RaiseCanExecuteChanged();
                RaiseRegenerateCanExecute();
                OnPropertyChanged(nameof(SearchPanelState));
            }
        }
    }

    public SearchResultItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set => SetProperty(ref _selectedSearchResult, value);
    }

    // ===== Panel states (UX audit) =====
    //
    // Each list panel publishes exactly one PanelState. Derived, never assigned, so Empty can
    // never coexist with Loading (the class of bug the audit found in Library-Curate and DJ) —
    // the precedence is encoded once, here, instead of in per-element visibility triggers.
    // Change notification comes from the collections themselves (hooked in the constructor)
    // plus the in-flight flags and error properties below.

    public PanelState StationsPanelState =>
        Stations.Count == 0 ? PanelState.Empty : PanelState.Content;

    public PanelState SearchPanelState =>
        IsSearching ? PanelState.Loading
        : SearchError is not null ? PanelState.Error
        : SearchResults.Count == 0 ? PanelState.Empty
        : PanelState.Content;

    public PanelState HistoryPanelState =>
        History.Count == 0 ? PanelState.Empty : PanelState.Content;

    public PanelState CuratePanelState =>
        IsCurating ? PanelState.Loading
        : CurateError is not null ? PanelState.Error
        : CuratedQueue.Count == 0 ? PanelState.Empty
        : PanelState.Content;

    public PanelState LibrarySongsPanelState =>
        LibrarySongs.Count == 0 ? PanelState.Empty : PanelState.Content;

    /// <summary>Sources tab: a running session with no harvesters connected yet is still
    /// starting up — that's Loading, not Empty.</summary>
    public PanelState DjSourcesPanelState =>
        DjError is not null ? PanelState.Error
        : !IsDjRunning ? PanelState.Empty
        : DjHarvesters.Count == 0 ? PanelState.Loading
        : PanelState.Content;

    /// <summary>Mix tab: the warm-up checklist lives here (it's the space that used to say
    /// "nothing yet" while a session was demonstrably starting), so a running session with an
    /// empty queue is Loading, not Empty.</summary>
    public PanelState DjMixPanelState =>
        DjMix.Count > 0 ? PanelState.Content
        : DjError is not null ? PanelState.Error
        : IsDjRunning ? PanelState.Loading
        : PanelState.Empty;

    private string? _searchError;
    /// <summary>Non-null puts the Search panel in its Error state. Cleared when a search starts.</summary>
    public string? SearchError
    {
        get => _searchError;
        private set
        {
            if (SetProperty(ref _searchError, value))
                OnPropertyChanged(nameof(SearchPanelState));
        }
    }

    private string? _curateError;
    public string? CurateError
    {
        get => _curateError;
        private set
        {
            if (SetProperty(ref _curateError, value))
                OnPropertyChanged(nameof(CuratePanelState));
        }
    }

    private string? _djError;
    public string? DjError
    {
        get => _djError;
        private set
        {
            if (SetProperty(ref _djError, value))
                OnPropertyChanged(nameof(DjSourcesPanelState));
        }
    }

    private string _searchEmptyMessage = DefaultSearchEmptyMessage;
    private const string DefaultSearchEmptyMessage =
        "Describe a vibe above and we'll find stations that actually play it.";

    /// <summary>What the Search panel says when it has nothing to show — the opening invitation
    /// before the first search, or why the last one came back empty.</summary>
    public string SearchEmptyMessage
    {
        get => _searchEmptyMessage;
        private set => SetProperty(ref _searchEmptyMessage, value);
    }

    private string _curateEmptyMessage = DefaultCurateEmptyMessage;
    private const string DefaultCurateEmptyMessage =
        "Describe a mood or vibe above and we'll build a set from your saved songs.";

    public string CurateEmptyMessage
    {
        get => _curateEmptyMessage;
        private set => SetProperty(ref _curateEmptyMessage, value);
    }

    private string _djSourcesEmptyMessage = DefaultDjSourcesEmptyMessage;
    private const string DefaultDjSourcesEmptyMessage =
        "No session yet — describe a vibe above to start one.";

    public string DjSourcesEmptyMessage
    {
        get => _djSourcesEmptyMessage;
        private set => SetProperty(ref _djSourcesEmptyMessage, value);
    }

    // ===== Staged progress for the long AI waits (UX audit) =====
    //
    // Each panel's Loading slot binds one of these instead of a bare spinner + sentence: named
    // steps that tick off and stay on screen, so a 40-second search reads as progress rather
    // than a possible hang, plus a Cancel for anything that can outstay its welcome.
    // Stage labels are the user-facing copy; the pipeline calls Step() with the same strings.

    private const string SearchStageDirectory = "Searching the station directory";
    private const string SearchStageRank = "Picking the ones that fit";
    private const string SearchStageWeb = "Casting a wider net on the web";
    private const string SearchStageValidate = "Checking they actually play";

    private const string DjStageFind = "Finding stations for your vibe";
    private const string DjStageConnect = "Tuning in to them";
    private const string DjStageRecord = "Recording the first songs";

    public StagedProgress SearchProgress { get; }
    public StagedProgress CurateProgress { get; }
    public StagedProgress DjProgress { get; }

    private async Task RunSearchAsync(bool regenerate)
    {
        if (IsSearching || string.IsNullOrWhiteSpace(SearchPrompt))
            return;

        var prompt = SearchPrompt;

        // A fresh search (or a regenerate after the prompt was edited) starts over: forget what
        // was shown and reset paging. A regenerate of the same prompt pages deeper and keeps the
        // "already shown" set so it surfaces different stations.
        if (!regenerate || !string.Equals(prompt, _lastSearchPrompt, StringComparison.OrdinalIgnoreCase))
        {
            _shownStationUrls.Clear();
            _searchPage = 0;
            _lastSearchPrompt = prompt;
        }
        else
        {
            _searchPage++;
        }

        SearchError = null; // a new attempt clears the previous failure
        SearchEmptyMessage = DefaultSearchEmptyMessage;
        // The web stage is inserted by the pipeline only on runs that actually escalate.
        SearchProgress.Begin(SearchStageDirectory, SearchStageRank, SearchStageValidate);
        IsSearching = true;
        SearchResults.Clear();
        SelectedSearchResult = null;

        // One CTS per search so switching away from the Radio panel can abandon a slow
        // pipeline (especially a web escalation) instead of letting it run to completion
        // against a panel nobody is looking at.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();

        try
        {
            await RunUnifiedSearchAsync(prompt, _searchPage, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Abandoned by Cancel or a mode switch — not an error; the panel falls back to its
            // empty invitation rather than accusing the user of a failure.
            SearchStatus = string.Empty;
            SearchEmptyMessage = DefaultSearchEmptyMessage;
        }
        catch (Exception ex)
        {
            AppLog.Error("[Search] failed", ex);
            SearchStatus = string.Empty;                      // the panel says it now
            SearchError = "The search hit a snag — please try again.";
        }
        finally
        {
            SearchProgress.Reset();
            IsSearching = false;
            RaiseRegenerateCanExecute();
        }
    }

    /// <summary>
    /// One retrieval pipeline for every prompt — no literal-vs-fuzzy routing. Several cheap
    /// recall sources feed one ranker; web search is the only escalation, fired by result
    /// quality rather than a guess about intent:
    ///
    ///   1. Run the cheap sources in parallel — Pattern A (structured Radio Browser lookup)
    ///      and local semantic search — and pool their candidates.
    ///   2. Re-rank the pool (the strict LLM ranker drops loose matches and replaces the old
    ///      Broaden fallback).
    ///   3. If that can't fill a page of genuine matches, escalate to web discovery
    ///      (Pattern B) — the one source with real cost — and re-rank the combined pool.
    ///   4. Validate streams and show.
    ///
    /// "BBC Radio 1" is filled by the cheap sources and never pays for web; a niche
    /// genre/region/qualifier prompt comes back thin and escalates automatically.
    /// </summary>
    private async Task RunUnifiedSearchAsync(string prompt, int page, CancellationToken ct)
    {
        if (!_interpreter.IsConfigured && !_semanticSearch.IsAvailable && !_agenticSearch.IsConfigured)
        {
            SearchStatus = string.Empty;
            SearchError = "Add your Anthropic API key in Options to use AI search.";
            return;
        }

        var isRegenerate = page > 0;
        // Progress now lives in the panel's staged checklist (SearchProgress), not this line —
        // SearchStatus is left for the result summary once the search finishes.

        // 1. Cheap recall sources, together: structured Radio Browser lookup + local semantic.
        //    Gather a larger semantic pool so the ranker has real choice. On regenerate, page
        //    deeper into the directory so Pattern A brings back rows we haven't shown yet.
        var structuredTask = RunStructuredAsync(prompt, page * StructuredPageSize, ct);
        var semanticTask = _semanticSearch.IsAvailable
            ? _semanticSearch.SearchAsync(prompt, CandidatePoolSize, ct)
            : Task.FromResult<IReadOnlyList<SemanticResult>>([]);

        // Await each independently so one source failing doesn't sink the other.
        IReadOnlyList<StationCandidate> structured = [];
        IReadOnlyList<SemanticResult> semantic = [];
        try { structured = await structuredTask; }
        catch (Exception ex) { AppLog.Debug($"[Search] structured failed: {ex.Message}"); }
        try { semantic = await semanticTask; }
        catch (Exception ex) { AppLog.Debug($"[Search] semantic failed: {ex.Message}"); }

        // Lazily enrich the structured finds (fire-and-forget) so the local side keeps growing.
        if (structured.Count > 0)
            _enrichment.EnrichInBackground(structured);

        // 2. One de-duplicated pool (dedupe on stream URL). Each candidate carries the text the
        //    ranker judges: an enriched description / tags (structured) or the stored
        //    description (semantic).
        var pool = new List<SearchResultItem>();
        // Seed "seen" with stations already shown for this prompt so a regenerate excludes them
        // (fresh searches start with an empty set — see RunSearchAsync).
        var seen = new HashSet<string>(_shownStationUrls, StringComparer.OrdinalIgnoreCase);
        foreach (var c in structured)
            if (seen.Add(c.Station.Url))
                pool.Add(new SearchResultItem(c.Station, DescriptionFor(c), c.Country));
        foreach (var r in semantic)
            if (seen.Add(r.Station.Url))
                pool.Add(new SearchResultItem(r.Station, r.Description, r.Country));

        var shortlist = await RankOrMerge(prompt, pool, semantic, ct);

        // 3. Escalate to web discovery only when the cheap sources came back thin.
        if (NeedsWebEscalation(shortlist) && _agenticSearch.IsConfigured)
        {
            SearchProgress.Step(SearchStageWeb); // inserted into the checklist only when it runs
            IReadOnlyList<RankedStation> web = [];
            try { web = await _agenticSearch.SearchAsync(prompt, MaxResults, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppLog.Debug($"[Search] web failed: {ex.Message}"); }

            foreach (var r in web)
                if (seen.Add(r.Station.Url))
                    pool.Add(new SearchResultItem(r.Station, r.Reason));

            // Re-rank the combined pool so web finds compete with the cheap ones on one signal.
            shortlist = await RankOrMerge(prompt, pool, semantic, ct);
        }

        if (shortlist.Count == 0)
        {
            // "Nothing found" is Empty, not Error — the panel says it, so the status line
            // doesn't repeat it two inches above.
            SearchEmptyMessage = isRegenerate
                ? "That's everything I could find for this — try a new description."
                : "Nothing turned up — try describing it differently.";
            SearchStatus = string.Empty;
            return;
        }

        // 4. Validate streams before showing (drops dead/undecodable ones).
        ct.ThrowIfCancellationRequested();
        SearchProgress.Step(SearchStageValidate);
        await AddValidatedAsync(shortlist);

        // Remember what we've shown so the next "Show different" surfaces new stations.
        foreach (var item in SearchResults)
            _shownStationUrls.Add(item.Station.Url);

        SetResultStatus(isRegenerate);
    }

    /// <summary>
    /// Pattern A as a recall source: translate the prompt to structured Radio Browser
    /// parameters and run one query. Returns [] when not configured or the model can't parse
    /// the prompt — there is no Broaden fallback any more; the pool's other sources and the
    /// re-ranker decide relevance, so a query that matches nothing simply contributes nothing.
    /// </summary>
    private async Task<IReadOnlyList<StationCandidate>> RunStructuredAsync(string prompt, int offset, CancellationToken ct)
    {
        if (!_interpreter.IsConfigured)
            return [];
        var query = await _interpreter.InterpretAsync(prompt, ct);
        if (query is null)
            return [];
        return await _searchService.SearchCandidatesAsync(query, offset, ct);
    }

    /// <summary>
    /// Order the pool with the LLM re-ranker (drops non-matches, best match first); fall back
    /// to the cosine-threshold heuristic when the ranker can't run (no key / transient error).
    /// </summary>
    private async Task<List<SearchResultItem>> RankOrMerge(
        string prompt, List<SearchResultItem> pool, IReadOnlyList<SemanticResult> semantic,
        CancellationToken ct)
    {
        if (pool.Count == 0)
            return [];

        if (_ranker.IsConfigured)
        {
            SearchProgress.Step(SearchStageRank);
            var candidates = pool.Select((p, i) => new RankCandidate(i, p.Station.Name, p.Reason ?? "", p.Country)).ToList();
            var verdicts = await _ranker.RankAsync(prompt, candidates, ResultsToValidate, ct);
            if (verdicts is not null) // null = ranker couldn't run → fall back to heuristic
            {
                AppLog.Debug($"[Rank] pool={pool.Count} -> kept {verdicts.Count}");
                return verdicts.Select(v => pool[v.Id]).ToList();
            }
        }
        return BuildHeuristicMerge(pool, semantic);
    }

    /// <summary>
    /// Escalate to web search when the cheap sources can't fill a page of genuine matches.
    /// Because the re-ranker is strict (it drops loosely-related stations), a thin shortlist
    /// means the directory tags + local catalog genuinely don't cover this prompt — a niche
    /// genre, a multi-country region, a stylistic qualifier — which is exactly when web
    /// discovery earns its cost. A rich prompt fills the page from the cheap sources and pays
    /// nothing for the web.
    /// </summary>
    private static bool NeedsWebEscalation(IReadOnlyList<SearchResultItem> shortlist)
        => shortlist.Count < MaxResults;

    /// <summary>
    /// Fallback ordering when the LLM re-ranker can't run: semantic hits that clear the cosine
    /// floor first (best score first — they carry a real relevance signal), then the rest of
    /// the pool in arrival order (structured, then any web finds, then weak semantic hits).
    /// </summary>
    private static List<SearchResultItem> BuildHeuristicMerge(
        IReadOnlyList<SearchResultItem> pool, IReadOnlyList<SemanticResult> semantic)
    {
        var score = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in semantic)
            score[s.Station.Url] = s.Score;

        var list = new List<SearchResultItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Strong semantic hits first, best score first.
        foreach (var item in pool
            .Where(p => score.TryGetValue(p.Station.Url, out var sc) && sc >= SemanticThreshold)
            .OrderByDescending(p => score[p.Station.Url]))
        {
            if (list.Count >= ResultsToValidate) break;
            if (seen.Add(item.Station.Url)) list.Add(item);
        }
        // Then the rest of the pool in arrival order.
        foreach (var item in pool)
        {
            if (list.Count >= ResultsToValidate) break;
            if (seen.Add(item.Station.Url)) list.Add(item);
        }
        return list;
    }

    /// <summary>
    /// Probe each candidate's stream (off the UI thread) and add only the ones that actually
    /// open, in order, up to MaxResults. Dead/undecodable streams are silently dropped.
    /// </summary>
    private async Task AddValidatedAsync(IReadOnlyList<SearchResultItem> items)
    {
        if (items.Count == 0)
            return;

        // Probe concurrently; Task.WhenAll preserves order so ranking is kept.
        var checks = await Task.WhenAll(
            items.Select(async it => (item: it, ok: await _engine.TestStreamAsync(it.Station))));

        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, ok) in checks)
        {
            if (SearchResults.Count >= MaxResults) break;
            if (!ok) continue; // dead / undecodable stream

            // Drop exact-URL dupes and near-dupes that differ only by a "[2]"-style
            // disambiguator (the same station registered more than once in the directory).
            if (!seenUrls.Add(item.Station.Url) || !seenNames.Add(NormalizeStationName(item.Station.Name)))
                continue;

            // Pre-check the add affordance for results already in the library.
            item.IsAdded = Stations.Any(s =>
                string.Equals(s.Url, item.Station.Url, StringComparison.OrdinalIgnoreCase));
            SearchResults.Add(item);
        }
    }

    /// <summary>
    /// Normalizes a station name for near-duplicate detection: strips a trailing duplicate
    /// disambiguator like " [2]" or "(3)" that Radio Browser users add to re-registered
    /// stations, collapses whitespace, and lower-cases. Deliberately conservative — it does
    /// NOT strip trailing bare numbers (so "Radio 1"/"Radio 2" stay distinct) or codec/bitrate
    /// suffixes (so "… | 320k AAC" and "… | 64k MP3" remain separate, playable choices).
    /// </summary>
    private static string NormalizeStationName(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s*[\[(]\d+[\])]\s*$", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Best available short description for a candidate so every result row shows something:
    /// a cached enriched description if we have one, else its Radio Browser tags, else null.
    /// </summary>
    private string? DescriptionFor(StationCandidate candidate)
    {
        var cached = _enrichment.GetCached(candidate.StationUuid)?.Description;
        return !string.IsNullOrWhiteSpace(cached) ? cached : FormatTags(candidate.Tags);
    }

    /// <summary>Formats a comma-separated Radio Browser tag string as a short " · " list (or null).</summary>
    private static string? FormatTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;
        var parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join(" · ", parts.Take(5));
    }

    private void SetResultStatus(bool isRegenerate)
    {
        // Nothing survived validation → Empty state carries the message; otherwise the status
        // line reports the win and the panel shows the rows.
        if (SearchResults.Count == 0)
        {
            SearchEmptyMessage = isRegenerate
                ? "No more new stations for this — try a new description."
                : "Nothing playable came through — try describing it differently.";
            SearchStatus = string.Empty;
            return;
        }
        SearchStatus = $"Found {SearchResults.Count} station{(SearchResults.Count == 1 ? "" : "s")} you can play.";
    }


    /// <summary>
    /// Play a search result WITHOUT adding it to the fixed list (adding is explicit, via the
    /// per-row Add button). If it's already in the library, play that instance so it stays
    /// in sync with next-station cycling.
    /// </summary>
    public void PlaySelectedSearchResult()
    {
        var station = SelectedSearchResult?.Station;
        if (station is null)
            return;

        var existing = Stations.FirstOrDefault(s =>
            string.Equals(s.Url, station.Url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectWithoutAutoPlay(existing);
            station = existing;
        }

        _engine.Play(station);
    }

    /// <summary>Add a search result to the fixed, persisted station list (no playback).</summary>
    private void AddSearchResultToLibrary(SearchResultItem? item)
    {
        if (item?.Station is not { } station)
            return;
        if (Stations.Any(s => string.Equals(s.Url, station.Url, StringComparison.OrdinalIgnoreCase)))
        {
            item.IsAdded = true; // already in the library — reflect it on the row
            return;
        }

        // Save the cleaned display name (technical noise stripped) and carry the search-panel
        // blurb onto the saved station so the fixed list shows the same secondary line
        // (Reason = enriched description / web rationale, or null). Dedup here is by URL, so
        // renaming is safe.
        Stations.Add(station with { Name = StationNameFormatter.Clean(station.Name), Description = item.Reason });
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();
        PrevStationCommand.RaiseCanExecuteChanged();
        item.IsAdded = true; // swap the row's + to a check
    }

    public ObservableCollection<Station> Stations { get; }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextStationCommand { get; }
    public RelayCommand PrevStationCommand { get; }
    public RelayCommand AddStationCommand { get; }
    public RelayCommand<Station> EditStationCommand { get; }
    public RelayCommand<Station> DeleteStationCommand { get; }

    // Phase D: mode switch + local library curation/playback.
    public RelayCommand SwitchToRadioCommand { get; }
    public RelayCommand SwitchToLibraryCommand { get; }
    public RelayCommand SwitchToDjCommand { get; }
    public RelayCommand CurateCommand { get; }
    public RelayCommand StartDjCommand { get; }
    public RelayCommand StopDjCommand { get; }
    public RelayCommand<CuratedQueueItem> PlayQueueItemCommand { get; }
    public RelayCommand<LibrarySongItem> PlayLibrarySongCommand { get; }

    public Station? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (!SetProperty(ref _selectedStation, value)) return;

            // Stopped: browsing the list updates the preview live instead of waiting for Play.
            if (_engine.State == PlaybackState.Stopped)
            {
                ApplyStoppedPreview();
                RefreshRecentOnStation();
            }
            if (value is null) return;

            // Switching station while already on-air restarts playback immediately.
            if (!_suppressAutoPlay
                && _engine.State is PlaybackState.Playing or PlaybackState.Paused
                    or PlaybackState.Buffering or PlaybackState.Reconnecting
                && !ReferenceEquals(value, _engine.CurrentStation))
            {
                _engine.Play(value);
            }
        }
    }

    /// <summary>The idle placeholder title. UX audit: idle must read quieter than a song title
    /// (the view styles it down when <see cref="IsNowPlayingIdle"/>) and offer a next step
    /// (<see cref="IdleHint"/>) instead of shouting an absence at 44px.</summary>
    public const string IdleTitle = "Nothing playing";

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            if (SetProperty(ref _nowPlayingTitle, value))
            {
                OnPropertyChanged(nameof(NowPlayingClipboardText));
                OnPropertyChanged(nameof(IsNowPlayingIdle));
            }
        }
    }

    /// <summary>True while the title slot holds the idle placeholder — derived from the title
    /// itself so it can never disagree with what's on screen.</summary>
    public bool IsNowPlayingIdle => _nowPlayingTitle == IdleTitle;

    /// <summary>Mode-specific next step shown under the idle title (UX audit).</summary>
    public string IdleHint => _mode switch
    {
        PlayerMode.Library => "Play a saved song, or curate a set.",
        PlayerMode.Dj => "Describe a vibe to start a session.",
        _ => "Pick a station on the left."
    };

    public string NowPlayingArtist
    {
        get => _nowPlayingArtist;
        private set
        {
            if (SetProperty(ref _nowPlayingArtist, value))
                OnPropertyChanged(nameof(NowPlayingClipboardText));
        }
    }

    /// <summary>URL of the station actively playing, null when paused or stopped.
    /// Used by the per-row equalizer indicator in the Stations list.</summary>
    public string? NowPlayingUrl
    {
        get => _nowPlayingUrl;
        private set => SetProperty(ref _nowPlayingUrl, value);
    }
    private string? _nowPlayingUrl;

    /// <summary>Friendly name of the station currently playing — the source of truth for
    /// "what's playing", independent of which row is selected in either list.</summary>
    public string NowPlayingStation
    {
        get => _nowPlayingStation;
        private set
        {
            if (SetProperty(ref _nowPlayingStation, value))
                OnPropertyChanged(nameof(HasStation));
        }
    }

    public bool HasStation => !string.IsNullOrWhiteSpace(NowPlayingStation);

    /// <summary>Codec of the playing station ("AAC"/"MP3"), for the now-playing status line.</summary>
    public string NowPlayingFormat
    {
        get => _nowPlayingFormat;
        private set => SetProperty(ref _nowPlayingFormat, value);
    }

    /// <summary>True while a stream is connecting/reconnecting (drives the loading spinner) —
    /// genuine Radio mode, or DJ mode's live warm-up window (see <see cref="UsesRadioEngine"/>).</summary>
    public bool IsBusy => UsesRadioEngine && _engine.State is PlaybackState.Buffering or PlaybackState.Reconnecting;

    /// <summary>The LIVE badge applies whenever RadioEngine is actually the one playing — genuine
    /// Radio mode, or DJ mode's live warm-up (local tracks show a seek timeline instead).</summary>
    public bool ShowLiveBadge => IsPlaying && UsesRadioEngine;

    /// <summary>True when there's real now-playing info worth copying.</summary>
    public bool HasTrackInfo
    {
        get => _hasTrackInfo;
        private set
        {
            if (SetProperty(ref _hasTrackInfo, value))
            {
                OnPropertyChanged(nameof(NowPlayingClipboardText));
                OnPropertyChanged(nameof(CanShowAbout));
                OpenAboutCommand.RaiseCanExecuteChanged();
                RegenerateAboutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>"Artist - Title" (or just the title) for the clipboard, or null if nothing to copy.</summary>
    public string? NowPlayingClipboardText =>
        !HasTrackInfo ? null
        : string.IsNullOrWhiteSpace(NowPlayingArtist) ? NowPlayingTitle
        : $"{NowPlayingArtist} - {NowPlayingTitle}";

    // ===== "About this track" reading view =====

    /// <summary>State of the right pane's upper region (Now Playing ⟷ About reading view).</summary>
    public AboutViewState AboutState
    {
        get => _aboutState;
        private set
        {
            if (SetProperty(ref _aboutState, value))
            {
                OnPropertyChanged(nameof(ShowNowPlaying));
                OnPropertyChanged(nameof(ShowAbout));
                OnPropertyChanged(nameof(IsAboutLoading));
                OnPropertyChanged(nameof(IsAboutResult));
                OnPropertyChanged(nameof(IsAboutError));
                OnPropertyChanged(nameof(CanRegenerateAbout));
                RegenerateAboutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowNowPlaying => AboutState == AboutViewState.Home;
    public bool ShowAbout => AboutState != AboutViewState.Home;
    public bool IsAboutLoading => AboutState == AboutViewState.Loading;
    public bool IsAboutResult => AboutState == AboutViewState.Result;
    public bool IsAboutError => AboutState == AboutViewState.Error;

    /// <summary>The generated briefing (song/artist/notable), set in the Result state.</summary>
    public TrackInfo? TrackInfo
    {
        get => _trackInfo;
        private set => SetProperty(ref _trackInfo, value);
    }

    /// <summary>User-facing message shown in the Error state.</summary>
    public string AboutError
    {
        get => _aboutError;
        private set => SetProperty(ref _aboutError, value);
    }

    /// <summary>Title of the track the open briefing is about (frozen — see the fields above).</summary>
    public string AboutSubjectTitle
    {
        get => _aboutSubjectTitle;
        private set => SetProperty(ref _aboutSubjectTitle, value);
    }

    /// <summary>Artist of the track the open briefing is about (frozen).</summary>
    public string AboutSubjectArtist
    {
        get => _aboutSubjectArtist;
        private set => SetProperty(ref _aboutSubjectArtist, value);
    }

    /// <summary>The "About this track" trigger shows only with a playing track and an API key.</summary>
    public bool CanShowAbout => HasTrackInfo && _trackInfoService.IsConfigured;

    /// <summary>Regenerate needs an open reading view (a frozen subject) — not a playing track,
    /// since briefings can be opened from history rows while stopped.</summary>
    public bool CanRegenerateAbout => ShowAbout && _trackInfoService.IsConfigured;

    /// <summary>
    /// Generate a briefing for an explicit subject (the now-playing track, a history row, or —
    /// on Regenerate — the subject already frozen on screen): freeze it, switch to Loading, call
    /// the service (cancellable so Back aborts it), then land on Result or Error.
    /// <paramref name="forceRefresh"/> bypasses the per-session cache.
    /// </summary>
    private async Task GenerateAboutAsync(string title, string? artist, string? station, bool forceRefresh)
    {
        if (!_trackInfoService.IsConfigured || string.IsNullOrWhiteSpace(title))
            return;

        // Cancel any in-flight generation and start a fresh token for this run.
        _aboutCts?.Cancel();
        _aboutCts?.Dispose();
        var cts = _aboutCts = new CancellationTokenSource();

        // Freeze the subject: the reading view binds to these, independent of live now-playing.
        AboutSubjectTitle = title;
        AboutSubjectArtist = artist ?? string.Empty;
        _aboutSubjectStation = station;

        AboutState = AboutViewState.Loading;
        try
        {
            var info = await _trackInfoService.GetTrackInfoAsync(title, artist, station, forceRefresh, cts.Token);
            if (cts.IsCancellationRequested)
                return; // Back was pressed (or a new track reset us) — discard this result.

            if (info is null)
            {
                AboutError = "Couldn't find reliable notes on this song.";
                AboutState = AboutViewState.Error;
            }
            else
            {
                TrackInfo = info;
                AboutState = AboutViewState.Result;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by Back / track change — leave the state as whoever cancelled us set it.
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[TrackInfo] generation failed: {ex.Message}");
            if (!cts.IsCancellationRequested)
            {
                AboutError = "Couldn't generate notes right now.";
                AboutState = AboutViewState.Error;
            }
        }
    }

    private void BackToNowPlaying()
    {
        _aboutCts?.Cancel();
        AboutState = AboutViewState.Home;
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseLabel));
                OnPropertyChanged(nameof(ShowLiveBadge));
            }
        }
    }

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                _engine.Volume = value;
                _local.Volume = value; // volume is shared across modes
                OnPropertyChanged(nameof(VolumePercent));
            }
        }
    }

    /// <summary>Volume as a whole-number percent (0–100) for the readout next to the slider.</summary>
    public int VolumePercent => (int)Math.Round(_volume * 100);

    // Transport prev/next route to the active mode: stations (wrapping) for radio, the local
    // queue for library/DJ (both share LocalPlaybackEngine's queue).
    private void Next()
    {
        if (UsesLocalEngine) _local.Next();
        else NextStation();
    }

    private void Prev()
    {
        if (UsesLocalEngine) _local.Previous();
        else PrevStation();
    }

    private bool CanGoNext() => UsesLocalEngine ? _local.HasQueue : Stations.Count > 0;
    private bool CanGoPrev() => UsesLocalEngine ? _local.HasQueue : Stations.Count > 0;

    private void NextStation()
    {
        if (Stations.Count == 0) return;

        // Cycle from whatever is currently playing (or just selected), wrapping around.
        var reference = _engine.CurrentStation ?? SelectedStation;
        var index = reference is null ? -1 : Stations.IndexOf(reference);
        var next = Stations[(index + 1) % Stations.Count];

        SelectWithoutAutoPlay(next); // update selection without double-triggering play
        _engine.Play(next);
    }

    private void PrevStation()
    {
        if (Stations.Count == 0) return;

        var reference = _engine.CurrentStation ?? SelectedStation;
        var index = reference is null ? 0 : Stations.IndexOf(reference);
        var prev = Stations[(index - 1 + Stations.Count) % Stations.Count];

        SelectWithoutAutoPlay(prev);
        _engine.Play(prev);
    }

    private void AddStation()
    {
        var created = _stationDialog.Show(null);
        if (created is null) return;

        Stations.Add(created);
        SelectWithoutAutoPlay(created);
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();
        PrevStationCommand.RaiseCanExecuteChanged();
    }

    private void EditStation(Station? station)
    {
        var existing = station ?? SelectedStation;
        if (existing is null) return;

        var edited = _stationDialog.Show(existing);
        if (edited is null) return;

        var index = Stations.IndexOf(existing);
        var wasPlayingThis = ReferenceEquals(_engine.CurrentStation, existing);

        Stations[index] = edited;
        SelectWithoutAutoPlay(edited);
        _store.Save(Stations);

        // If we edited the station that's currently on-air, restart it (the URL or
        // format may have changed).
        if (wasPlayingThis)
            _engine.Play(edited);
    }

    private void DeleteStation(Station? station)
    {
        var target = station ?? SelectedStation;
        if (target is null) return;

        if (ReferenceEquals(_engine.CurrentStation, target))
            _engine.Stop();

        var index = Stations.IndexOf(target);
        Stations.Remove(target);
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();
        PrevStationCommand.RaiseCanExecuteChanged();

        // Move selection to a sensible neighbour without auto-starting it.
        SelectWithoutAutoPlay(Stations.Count == 0
            ? null
            : Stations[Math.Min(index, Stations.Count - 1)]);
    }

    /// <summary>Persist user settings (volume). Called when the app is closing.</summary>
    /// <remarks>Load-modify-save so it preserves other persisted fields (e.g. the API key).</remarks>
    public void SaveSettings()
    {
        var settings = _settingsStore.Load();
        settings.Volume = _volume;
        _settingsStore.Save(settings);
    }

    /// <summary>
    /// Explicitly play the selected station — used by double-click in the stations list.
    /// Starts playback even when stopped, where merely selecting a row doesn't auto-start it.
    /// </summary>
    public void PlaySelectedStation()
    {
        if (SelectedStation is { } station)
            _engine.Play(station);
    }

    private void SelectWithoutAutoPlay(Station? station)
    {
        _suppressAutoPlay = true;
        SelectedStation = station;
        _suppressAutoPlay = false;
    }

    private void TogglePlayPause()
    {
        if (UsesLocalEngine)
        {
            // Stopped → (re)start the queue; otherwise pause/resume.
            if (_local.State is PlaybackState.Stopped && _local.HasQueue)
                _local.PlayAt(_local.CurrentIndex >= 0 ? _local.CurrentIndex : 0);
            else
                _local.TogglePause();
            return;
        }

        switch (_engine.State)
        {
            case PlaybackState.Playing:
                // "Pause" a live stream: keep the Paused state (station stays on screen) but…
                _engine.Pause();
                break;
            case PlaybackState.Paused:
                // …resume by RECONNECTING fresh rather than un-pausing a stale buffer — otherwise
                // playback briefly resumes the buffered audio, then jumps ahead to the live point.
                if (_engine.CurrentStation is { } paused)
                    _engine.Play(paused);
                break;
            default: // Stopped / Error (and any transient) → start the selected station
                if (SelectedStation is not null)
                    _engine.Play(SelectedStation);
                break;
        }
    }

    private void OnStateChanged(PlaybackState state)
    {
        if (!IsRadioMode) return; // radio engine drives now-playing only in radio mode

        IsPlaying = state == PlaybackState.Playing;
        OnPropertyChanged(nameof(IsBusy));

        // NowPlayingUrl drives the per-row equalizer: set only while actively playing so the
        // equalizer animation matches the main status-row equalizer (both hidden when paused).
        NowPlayingUrl = state == PlaybackState.Playing
            ? _engine.CurrentStation?.Url
            : null;

        // The playing station name is the source of truth for "what's playing" while something IS
        // playing/connecting. On Stopped it's handled below by ApplyStoppedPreview instead of
        // being cleared, so the panel can preview the selected station rather than going bare.
        if (state != PlaybackState.Stopped)
        {
            NowPlayingStation = _engine.CurrentStation?.Name ?? string.Empty;
            NowPlayingFormat = _engine.CurrentStation?.Format switch
            {
                StreamFormat.Aac => "AAC",
                StreamFormat.Mp3 => "MP3",
                _ => string.Empty
            };
        }

        StatusText = state switch
        {
            PlaybackState.Stopped => "Stopped",
            PlaybackState.Buffering => "Buffering...",
            PlaybackState.Playing => "Playing",
            PlaybackState.Paused => "Paused",
            PlaybackState.Reconnecting => "Reconnecting...",
            PlaybackState.Error => StatusText, // keep the error message
            _ => StatusText
        };

        switch (state)
        {
            case PlaybackState.Stopped:
                // Preview the selected station instead of going bare (Shared Framework Spec
                // §4a) — only the true first-run state (no station ever selected) stays empty.
                ApplyStoppedPreview();
                HasTrackInfo = false;
                SetCurrentSong(null); // nothing playing → nothing to mark for saving
                // An open briefing stays open even across Stop — its subject is frozen and may
                // have come from a history row; only Back closes the reading view.
                break;
            case PlaybackState.Buffering:
            case PlaybackState.Reconnecting:
                // No track to copy yet; show which station we're connecting to.
                NowPlayingTitle = state == PlaybackState.Reconnecting ? "Reconnecting..." : "Connecting...";
                NowPlayingArtist = _engine.CurrentStation?.Name ?? string.Empty;
                HasTrackInfo = false;
                break;
            case PlaybackState.Playing:
                // Recovering from a stall goes straight to Playing WITHOUT firing fresh
                // metadata, so a lingering "Connecting…/Reconnecting…" placeholder would
                // otherwise stay until the next song's ICY title arrives. Restore the station
                // name in that case; leave a real track title (set via metadata) untouched.
                if (NowPlayingTitle is "Connecting..." or "Reconnecting..." or IdleTitle)
                {
                    NowPlayingTitle = _engine.CurrentStation?.Name ?? "Live stream";
                    NowPlayingArtist = string.Empty;
                    HasTrackInfo = !string.IsNullOrWhiteSpace(NowPlayingTitle);
                }
                break;
        }

        RefreshRecentOnStation(); // NowPlayingStation may have just changed (any branch above)
        StopCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Stopped-state preview (Shared Framework Spec §4a): show the selected station's name and
    /// description instead of a bare "Not playing", so the right panel only ever looks truly
    /// empty when no station has been selected at all (e.g. an empty Stations list).
    /// </summary>
    private void ApplyStoppedPreview()
    {
        var sel = SelectedStation;
        NowPlayingStation = sel?.Name ?? string.Empty;
        NowPlayingTitle = sel?.Name ?? IdleTitle;
        NowPlayingArtist = sel?.Description ?? string.Empty;
        NowPlayingFormat = sel?.Format switch
        {
            StreamFormat.Aac => "AAC",
            StreamFormat.Mp3 => "MP3",
            _ => string.Empty
        };
    }

    private void OnMetadataChanged(TrackMetadata meta)
    {
        NowPlayingTitle = string.IsNullOrWhiteSpace(meta.Title)
            ? (meta.StationName ?? "Live stream")
            : meta.Title;
        NowPlayingArtist = meta.Artist ?? meta.StationName ?? string.Empty;
        HasTrackInfo = !string.IsNullOrWhiteSpace(NowPlayingTitle);
        // An open briefing is deliberately left in place when a new track starts: it's frozen on
        // its subject (AboutSubjectTitle/Artist), so the user can finish reading. Only Back
        // returns to Now Playing.

        _nowPlayingChanged?.Invoke(NowPlayingTitle, NowPlayingArtist); // mirror to OS media controls
        RecordHistory(meta);
    }

    // ===== Phase D: mode switch + local library player =====

    private PlayerMode _mode = PlayerMode.Radio;

    // True during DJ mode's cold-start warm-up window: RadioEngine plays the top-ranked harvested
    // station live (talk/ads acceptable — it's genuinely live radio) until the curated queue has
    // its first song ready, at which point EndDjWarmupIfActive() hands over to LocalPlaybackEngine.
    // See BeginDjWarmupLivePlayback/EndDjWarmupIfActive further down.
    private bool _djWarmingUp;

    /// <summary>The engine driving playback right now.</summary>
    private IPlaybackEngine ActiveEngine => UsesRadioEngine ? _engine : _local;

    public PlayerMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsRadioMode));
            OnPropertyChanged(nameof(IsLibraryMode));
            OnPropertyChanged(nameof(IsDjMode));
            OnPropertyChanged(nameof(UsesLocalEngine));
            OnPropertyChanged(nameof(UsesRadioEngine));
            OnPropertyChanged(nameof(ShowRadioPanel));
            OnPropertyChanged(nameof(ShowLibraryPanel));
            OnPropertyChanged(nameof(ShowDjPanel));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ShowLiveBadge));
            OnPropertyChanged(nameof(HasDuration));
            OnPropertyChanged(nameof(ShowRecentOnStation));
            OnPropertyChanged(nameof(ShowDjIntro));
            OnPropertyChanged(nameof(IdleHint));
        }
    }

    public bool IsRadioMode => _mode == PlayerMode.Radio;
    public bool IsLibraryMode => _mode == PlayerMode.Library;
    public bool IsDjMode => _mode == PlayerMode.Dj;

    /// <summary>True whenever LocalPlaybackEngine is the one actually producing audio right now —
    /// genuine Library mode, or DJ mode once it's past its live warm-up window.</summary>
    public bool UsesLocalEngine => IsLibraryMode || (IsDjMode && !_djWarmingUp);

    /// <summary>True whenever RadioEngine is the one actually producing audio right now —
    /// genuine Radio mode, or DJ mode's live warm-up window before the curated queue takes over.</summary>
    public bool UsesRadioEngine => IsRadioMode || (IsDjMode && _djWarmingUp);

    public bool ShowRadioPanel => IsRadioMode;
    public bool ShowLibraryPanel => IsLibraryMode;
    public bool ShowDjPanel => IsDjMode;

    /// <summary>Flips <see cref="_djWarmingUp"/> and raises everything that depends on it —
    /// centralized so BeginDjWarmupLivePlayback/EndDjWarmupIfActive/StopDj never forget a
    /// cascade the Mode setter above already lists for the Mode-level equivalent.</summary>
    private void SetDjWarmingUp(bool value)
    {
        if (_djWarmingUp == value) return;
        _djWarmingUp = value;
        OnPropertyChanged(nameof(UsesLocalEngine));
        OnPropertyChanged(nameof(UsesRadioEngine));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowLiveBadge));
        OnPropertyChanged(nameof(HasDuration));
        OnPropertyChanged(nameof(IsDjWarmingUp));
        RaiseDjIndicatorChanged();
        RefreshDjSessionMeta();
    }

    /// <summary>Switch player modes. Either/or: the now-inactive engine is stopped and the
    /// now-playing view reset, so only one thing ever plays. Leaving DJ mode also stops its
    /// background harvest/queue session — it has no visible presence once you've navigated away,
    /// so it shouldn't keep running unseen.</summary>
    private void SetMode(PlayerMode mode)
    {
        if (_mode == mode) return;

        _searchCts?.Cancel(); // abandon any in-flight station search — its panel is going away
        if (_mode == PlayerMode.Dj) StopDj();

        Mode = mode; // set first so the stopped engine's handler no-ops (guards on mode)
        if (mode == PlayerMode.Radio) _local.Stop(); else _engine.Stop();

        BackToNowPlaying();  // close any open About reading view
        ResetNowPlaying();
        RaiseTransportCanExecute();
        ActiveEngineChanged?.Invoke(ActiveEngine); // let the host repoint OS media controls
    }

    /// <summary>Raised when the active playback engine changes (mode switch), so the host can
    /// repoint the OS media transport controls at it.</summary>
    public event Action<IPlaybackEngine>? ActiveEngineChanged;

    private void RaiseTransportCanExecute()
    {
        StopCommand.RaiseCanExecuteChanged();
        NextStationCommand.RaiseCanExecuteChanged();
        PrevStationCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Clear the now-playing view to its idle state (used on mode switch).</summary>
    private void ResetNowPlaying()
    {
        IsPlaying = false;
        NowPlayingTitle = IdleTitle;
        NowPlayingArtist = string.Empty;
        NowPlayingStation = string.Empty;
        NowPlayingFormat = string.Empty;
        NowPlayingUrl = null;
        HasTrackInfo = false;
        PositionSeconds = 0;
        DurationSeconds = 0;
        SetCurrentSong(null);
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowLiveBadge));
    }

    // --- Local library: curation + queue ---

    public ObservableCollection<CuratedQueueItem> CuratedQueue { get; } = new();

    /// <summary>Every song in the local library — the "Songs" tab, browsable independent of any
    /// curated playlist. Populated at startup and refreshed whenever a new song is saved.</summary>
    public ObservableCollection<LibrarySongItem> LibrarySongs { get; }

    private void RefreshLibrarySongs()
    {
        LibrarySongs.Clear();
        // UserSaved only — DJ mode's harvested songs are indexed in the same library (so
        // SongCurator's recall benefits from them) but shouldn't clutter the user's own list.
        foreach (var s in _songLibrary.GetAll(SongSource.UserSaved))
            LibrarySongs.Add(new LibrarySongItem(s));
    }

    private void PlayLibrarySong(LibrarySongItem? item)
    {
        if (item is null) return;
        var index = LibrarySongs.IndexOf(item);
        if (index < 0) return;
        _local.SetQueue(LibrarySongs.Select(ToLocalTrack).ToList(), index);
    }

    private static LocalTrack ToLocalTrack(LibrarySongItem item)
    {
        var format = item.Song.Path.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
            ? StreamFormat.Aac
            : StreamFormat.Mp3;
        return new LocalTrack(item.Song.Path, item.Title, item.Artist, format);
    }

    /// <summary>Title of the last successfully curated playlist (title-cased prompt), shown on
    /// the Curate tab's context card. Empty until the first successful curation.</summary>
    public string CuratedPlaylistTitle { get; private set; } = string.Empty;

    /// <summary>The Curate tab's context card shows only once a playlist has been curated.</summary>
    public bool HasCuratedPlaylist => CuratedQueue.Count > 0;

    private string _libraryPrompt = string.Empty;
    public string LibraryPrompt
    {
        get => _libraryPrompt;
        set => SetProperty(ref _libraryPrompt, value);
    }

    private string _libraryStatus = string.Empty;
    public string LibraryStatus
    {
        get => _libraryStatus;
        private set => SetProperty(ref _libraryStatus, value);
    }

    private bool _isCurating;
    public bool IsCurating
    {
        get => _isCurating;
        private set
        {
            if (SetProperty(ref _isCurating, value))
            {
                CurateCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CuratePanelState));
            }
        }
    }

    /// <summary>Curate a playlist from the local library for the prompt, then start playing it.</summary>
    private async Task RunCurateAsync()
    {
        if (IsCurating || string.IsNullOrWhiteSpace(LibraryPrompt))
            return;
        if (!_curator.IsAvailable)
        {
            LibraryStatus = string.Empty;
            CurateEmptyMessage = "Save some songs first — there's nothing to curate from yet.";
            return;
        }

        var prompt = LibraryPrompt;
        CurateError = null; // a new attempt clears the previous failure
        CurateEmptyMessage = DefaultCurateEmptyMessage;
        CurateProgress.Begin("Building a set from your library");
        IsCurating = true;
        LibraryStatus = string.Empty; // the panel narrates while this runs
        _curateCts?.Cancel();
        _curateCts?.Dispose();
        var curateCts = _curateCts = new CancellationTokenSource();
        try
        {
            var songs = await _curator.CurateAsync(prompt, 20, null, curateCts.Token);
            CuratedQueue.Clear();
            foreach (var s in songs)
                CuratedQueue.Add(new CuratedQueueItem(s));

            if (CuratedQueue.Count == 0)
            {
                // Empty, not Error — the library simply had no match for this vibe.
                CurateEmptyMessage = "Nothing in your library matched — try a different vibe.";
                LibraryStatus = string.Empty;
                OnPropertyChanged(nameof(HasCuratedPlaylist));
                return;
            }

            // The context card's title, so "the thing I asked for" reads distinctly from the
            // resulting track list (Shared Framework Spec §3). Verbatim in quotes, NOT
            // title-cased (UX audit): rewriting the user's own words — typos included — reads
            // as a bug, while quoting them reads as "here's what you asked for."
            CuratedPlaylistTitle = $"“{prompt.Trim()}”";
            OnPropertyChanged(nameof(CuratedPlaylistTitle));
            OnPropertyChanged(nameof(HasCuratedPlaylist));

            LibraryStatus = $"Playing {CuratedQueue.Count} song{(CuratedQueue.Count == 1 ? "" : "s")}.";
            _local.SetQueue(CuratedQueue.Select(ToLocalTrack).ToList(), 0);
            RaiseTransportCanExecute();
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the progress panel — back to the invitation, not an error.
            LibraryStatus = string.Empty;
            CurateEmptyMessage = DefaultCurateEmptyMessage;
        }
        catch (Exception ex)
        {
            AppLog.Error("[Curate] failed", ex);
            LibraryStatus = string.Empty;
            CurateError = "Curation hit a snag — please try again.";
        }
        finally
        {
            CurateProgress.Reset();
            IsCurating = false;
        }
    }

    // ===== DJ mode: harvest + self-refilling queue =====

    /// <summary>Currently-connected harvesters (Harvesting tab) — rebuilt wholesale each time
    /// DjHarvestService.StatusChanged fires; dead harvesters are never in it (see DjHarvesterItem).</summary>
    public ObservableCollection<DjHarvesterItem> DjHarvesters { get; } = new();

    /// <summary>
    /// The session's mix (Mix tab): the playback queue in order — played, playing, upcoming.
    /// A direct view of <see cref="ILocalQueuePlayer.Queue"/>; the queue never drops played
    /// tracks, so it doubles as the session record and there's no separate history list.
    /// </summary>
    public ObservableCollection<DjMixItem> DjMix { get; } = new();

    /// <summary>The track after the current one, for the "Up next" line, or null at the end.</summary>
    public string? UpNext
    {
        get
        {
            var next = _local.CurrentIndex + 1;
            if (next <= 0 || next >= _local.Queue.Count) return null;
            var track = _local.Queue[next];
            return string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Title} — {track.Artist}";
        }
    }

    public bool HasUpNext => IsDjMode && UpNext is not null;

    /// <summary>
    /// Rebuilds <see cref="DjMix"/> from the engine's queue. Appends in place when the existing
    /// rows are still a prefix of the queue (the common case — a harvested song landing), so the
    /// list doesn't reset its scroll position every time the mix grows.
    /// </summary>
    private void SyncDjMix()
    {
        // Library mode drives the same engine queue (curated playlists), so without this guard a
        // curated set would show up as the DJ's "mix" the next time you opened the panel.
        if (!IsDjMode) return;

        var queue = _local.Queue;
        if (DjMix.Count > queue.Count)
            DjMix.Clear();

        for (var i = 0; i < DjMix.Count; i++)
        {
            if (DjMix[i].Title == queue[i].Title && DjMix[i].Artist == queue[i].Artist)
                continue;
            DjMix.Clear(); // diverged — the queue was replaced, not appended to
            break;
        }

        for (var i = DjMix.Count; i < queue.Count; i++)
            DjMix.Add(new DjMixItem(queue[i].Title, queue[i].Artist));

        MarkDjMixPosition();
    }

    /// <summary>Flags which mix row is playing and which are behind it.</summary>
    private void MarkDjMixPosition()
    {
        var current = _local.CurrentIndex;
        for (var i = 0; i < DjMix.Count; i++)
        {
            DjMix[i].IsCurrent = i == current;
            DjMix[i].HasPlayed = current >= 0 && i < current;
        }
        OnPropertyChanged(nameof(UpNext));
        OnPropertyChanged(nameof(HasUpNext));
    }

    private string _djPrompt = string.Empty;
    public string DjPrompt
    {
        get => _djPrompt;
        set
        {
            if (SetProperty(ref _djPrompt, value))
                StartDjCommand.RaiseCanExecuteChanged();
        }
    }

    private string _djStatus = string.Empty;
    public string DjStatus
    {
        get => _djStatus;
        private set => SetProperty(ref _djStatus, value);
    }

    private bool _isDjRunning;
    public bool IsDjRunning
    {
        get => _isDjRunning;
        private set
        {
            if (SetProperty(ref _isDjRunning, value))
            {
                StartDjCommand.RaiseCanExecuteChanged();
                StopDjCommand.RaiseCanExecuteChanged();
                RaiseDjIndicatorChanged();
                OnPropertyChanged(nameof(DjSourcesPanelState));
            }
        }
    }

    /// <summary>True while a live station is bridging until the mix fills. Public so the session
    /// card and the Now Playing badge can distinguish "bridging" from "live".</summary>
    public bool IsDjWarmingUp => _djWarmingUp;

    /// <summary>Session running but nothing connected yet — the Sources panel shows the staged
    /// checklist for this phase, so the status row stays out of its way.</summary>
    private bool IsDjStartingUp => IsDjRunning && DjHarvesters.Count == 0;

    /// <summary>Spinner only while genuinely waiting on something the status row owns: the
    /// warm-up window, bridging live radio until the mix fills (UX audit: a spinner that never
    /// stops means nothing).</summary>
    public bool ShowDjSpinner => IsDjRunning && !IsDjStartingUp && _djWarmingUp;

    /// <summary>Steady state gets the pulsing live dot instead — the session is healthy, not
    /// loading.</summary>
    public bool ShowDjLiveDot => IsDjRunning && !IsDjStartingUp && !_djWarmingUp;

    private void RaiseDjIndicatorChanged()
    {
        OnPropertyChanged(nameof(ShowDjSpinner));
        OnPropertyChanged(nameof(ShowDjLiveDot));
    }

    // The prompt/vibe the current DJ session was started with — frozen at session start (not
    // read live from DjPrompt) and given to the intro service as context for every track. Also
    // what the session card shows, so the card records what was actually asked for.
    private string? _djSessionVibe;

    public string? DjSessionVibe
    {
        get => _djSessionVibe;
        private set => SetProperty(ref _djSessionVibe, value);
    }

    private string? _djIntroLine;
    /// <summary>Short "why this song" DJ patter for the current DJ Mode track, shown in Now
    /// Playing. Null until generation completes (or if it fails/isn't configured) — the UI simply
    /// omits the line rather than showing a placeholder.</summary>
    public string? DjIntroLine
    {
        get => _djIntroLine;
        private set
        {
            if (SetProperty(ref _djIntroLine, value))
                OnPropertyChanged(nameof(ShowDjIntro));
        }
    }

    public bool ShowDjIntro => IsDjMode && !string.IsNullOrWhiteSpace(DjIntroLine);

    private CancellationTokenSource? _djIntroCts;

    /// <summary>Starts a DJ session for the prompt: sources + connects the harvest pool, warm-
    /// starts the queue from the existing library, and lets freshly harvested songs blend in as
    /// they arrive. On a cold library (nothing to warm-start with), bridges the gap by playing
    /// the top-ranked harvested station live via RadioEngine (talk/ads acceptable — it's genuinely
    /// live radio) until the first harvested song is ready — see BeginDjWarmupLivePlayback.</summary>
    private async Task StartDjAsync()
    {
        if (IsDjRunning || string.IsNullOrWhiteSpace(DjPrompt))
            return;

        var prompt = DjPrompt;
        DjSessionVibe = prompt;
        DjHarvesters.Clear();
        DjMix.Clear();
        DjIntroLine = null;
        DjError = null; // a new attempt clears the previous failure
        DjSourcesEmptyMessage = DefaultDjSourcesEmptyMessage;
        DjProgress.Begin(DjStageFind, DjStageConnect, DjStageRecord);
        _djLastStatus = null;

        // Per-session diagnostics: what the pool actually produced vs what playback consumed.
        // Best-effort and optional — the harvest path doesn't depend on it.
        var settings = _settingsStore.Load();
        _djSessionLog = DjSessionLog.Start(prompt, settings.DjHarvesterCount,
            settings.DjHarvestReserveCount, settings.DjMaxHarvestCacheMb * 1024L * 1024L);
        _djHarvest.SessionLog = _djSessionLog;

        IsDjRunning = true;
        StartDjSessionClock();
        DjStatus = "Warming up the decks…";

        _djStartCts?.Cancel();
        _djStartCts?.Dispose();
        var startCts = _djStartCts = new CancellationTokenSource();

        // The service reports its own phases; map them to the checklist's copy here so the
        // wording stays in the view model.
        var stages = new Progress<HarvestStartStage>(stage => DjProgress.Step(stage switch
        {
            HarvestStartStage.Connecting => DjStageConnect,
            _ => DjStageFind
        }));

        try
        {
            await _djHarvest.StartAsync(prompt, stages, startCts.Token).ConfigureAwait(true);
            if (!_djHarvest.IsRunning)
            {
                // No matching stations is Empty, not Error — the panel invites another try.
                DjSourcesEmptyMessage = "Nothing out there matched that vibe — try a different prompt.";
                DjStatus = string.Empty;
                EndDjSessionLog(); // never started harvesting, so the service won't close it
                IsDjRunning = false;
                return;
            }
            DjProgress.Step(DjStageRecord);
            var seeded = await _djQueue.StartAsync(prompt, startCts.Token).ConfigureAwait(true);
            AppLog.Info($"[Dj] session started · warm-start {(seeded ? "seeded the queue" : "was empty, bridging live")}");
            if (!seeded)
                BeginDjWarmupLivePlayback();
            RaiseTransportCanExecute();
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the progress panel — tear the half-started session back down.
            _djQueue.Stop();
            _djHarvest.Stop();
            DjStatus = string.Empty;
            DjSourcesEmptyMessage = DefaultDjSourcesEmptyMessage;
            EndDjSessionLog();
            IsDjRunning = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("[Dj] start failed", ex);
            DjStatus = string.Empty;
            DjError = "DJ mode hit a snag starting up — please try again.";
            _djHarvest.Stop();
            EndDjSessionLog();
            IsDjRunning = false;
        }
        finally
        {
            DjProgress.Reset();
        }
    }

    private void StopDj()
    {
        if (!IsDjRunning) return;
        _djQueue.Stop();
        _djHarvest.Stop();
        EndDjWarmupIfActive();
        _djIntroCts?.Cancel();
        DjIntroLine = null;
        DjSessionVibe = null;
        _djSessionTimer?.Stop();
        _djLastStatus = null;
        DjMix.Clear(); // the session's record ends with the session

        EndDjSessionLog();
        IsDjRunning = false;
        DjStatus = string.Empty;
    }

    /// <summary>Closes the session log (idempotent — DjHarvestService.Stop may already have) and
    /// unhooks it, so a stale log can't collect events from the next session.</summary>
    private void EndDjSessionLog()
    {
        _djSessionLog?.Finish();
        _djSessionLog = null;
        _djHarvest.SessionLog = null;
    }

    /// <summary>Generates the "why this song" intro line for the track that just started, if the
    /// service is configured. Cancels any still-running generation for the previous track first —
    /// a fast track change (e.g. skip) shouldn't leave a stale line from an earlier song landing
    /// after the new one started. Best-effort: DjIntroLine simply stays null on any failure.
    /// <paramref name="curatorNote"/> is the curator's own reason when the track came from a
    /// curated playlist — it grounds the intro in the real selection logic.</summary>
    private async Task GenerateDjIntroAsync(string title, string? artist, string? curatorNote)
    {
        _djIntroCts?.Cancel();
        _djIntroCts?.Dispose();
        var cts = _djIntroCts = new CancellationTokenSource();

        DjIntroLine = null;
        if (!_djIntro.IsConfigured || string.IsNullOrWhiteSpace(title))
            return;

        try
        {
            var line = await _djIntro.GetIntroAsync(title, artist, _djSessionVibe, curatorNote, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
                DjIntroLine = line;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer track or the session stopped — leave DjIntroLine as-is.
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[DjIntro] generation failed: {ex.Message}");
        }
    }

    private void OnDjStatusChanged(HarvestStatus status)
    {
        if (!IsDjRunning) return;
        _djLastStatus = status;
        RefreshDjSessionMeta();

        DjHarvesters.Clear();
        foreach (var h in status.Harvesters)
            DjHarvesters.Add(new DjHarvesterItem(h.Label, h.TitlesSeen));
        RaiseDjIndicatorChanged(); // harvester count feeds ShowDjSpinner/ShowDjLiveDot
    }

    private HarvestStatus? _djLastStatus;
    private DateTime _djSessionStarted;
    private System.Windows.Threading.DispatcherTimer? _djSessionTimer;
    private DjSessionLog? _djSessionLog;

    /// <summary>
    /// The session card's one-line summary. Rebuilt both when the harvest pool reports in and on
    /// a slow timer — otherwise the elapsed figure would only move when a song happened to land,
    /// which is exactly when it looks stale.
    /// </summary>
    private void RefreshDjSessionMeta()
    {
        if (!IsDjRunning)
        {
            DjStatus = string.Empty;
            return;
        }

        if (_djWarmingUp)
        {
            var station = _djHarvest.TopStation?.Name;
            DjStatus = station is null
                ? "Bridging a live station until the mix fills"
                : $"Bridging {station} until the mix fills";
            return;
        }

        if (_djLastStatus is not { } status)
        {
            DjStatus = "Warming up the decks…";
            return;
        }

        var stations = $"{status.ActiveHarvesters} station{(status.ActiveHarvesters == 1 ? "" : "s")}";
        var songs = $"{status.Kept} song{(status.Kept == 1 ? "" : "s")} in the mix";
        DjStatus = $"Tuned into {stations} · {songs} · {DescribeElapsed()}";
    }

    private string DescribeElapsed()
    {
        var minutes = (int)(DateTime.Now - _djSessionStarted).TotalMinutes;
        return minutes < 1 ? "just started" : $"{minutes} min";
    }

    private void StartDjSessionClock()
    {
        _djSessionStarted = DateTime.Now;
        _djSessionTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _djSessionTimer.Tick -= OnDjSessionTick;
        _djSessionTimer.Tick += OnDjSessionTick;
        _djSessionTimer.Start();
    }

    private void OnDjSessionTick(object? sender, EventArgs e) => RefreshDjSessionMeta();

    /// <summary>
    /// Cold-start bridge: plays DjHarvestService's top-ranked station live via RadioEngine — a
    /// SEPARATE connection from that station's own headless harvester, which keeps harvesting
    /// regardless — while ActiveEngine temporarily reports _engine even though Mode == Dj.
    /// EndDjWarmupIfActive() (hooked into OnLocalTrackChanged) hands over the instant the queue
    /// actually starts playing for real.
    /// </summary>
    private void BeginDjWarmupLivePlayback()
    {
        var topStation = _djHarvest.TopStation;
        if (topStation is null)
            return; // nothing rankable to play live — fall back to the existing silent warm-up

        SetDjWarmingUp(true);
        NowPlayingTitle = "Connecting...";
        NowPlayingArtist = topStation.Name;
        NowPlayingStation = topStation.Name;
        HasTrackInfo = false;
        _engine.StateChanged += OnDjWarmupStateChanged;
        _engine.MetadataChanged += OnDjWarmupMetadataChanged;
        _engine.Play(topStation);
        ActiveEngineChanged?.Invoke(ActiveEngine); // repoints SMTC at _engine for this window
    }

    /// <summary>No-ops if warm-up isn't active. Called both on a genuine handover (the queue's
    /// first track landing) and when the DJ session is stopped mid-warm-up.</summary>
    private void EndDjWarmupIfActive()
    {
        if (!_djWarmingUp) return;
        _engine.StateChanged -= OnDjWarmupStateChanged;
        _engine.MetadataChanged -= OnDjWarmupMetadataChanged;
        _engine.Stop();
        SetDjWarmingUp(false);
        ActiveEngineChanged?.Invoke(ActiveEngine); // repoints SMTC back at _local
    }

    private void OnDjWarmupStateChanged(object? sender, PlaybackState state)
    {
        if (!_djWarmingUp) return;

        IsPlaying = state == PlaybackState.Playing;
        OnPropertyChanged(nameof(IsBusy));
        NowPlayingUrl = state == PlaybackState.Playing ? _engine.CurrentStation?.Url : null;
        StatusText = state switch
        {
            PlaybackState.Buffering => "Buffering...",
            PlaybackState.Playing => "Playing",
            PlaybackState.Reconnecting => "Reconnecting...",
            _ => StatusText
        };
    }

    private void OnDjWarmupMetadataChanged(object? sender, TrackMetadata meta)
    {
        if (!_djWarmingUp) return;

        NowPlayingTitle = string.IsNullOrWhiteSpace(meta.Title) ? (meta.StationName ?? "Live stream") : meta.Title;
        NowPlayingArtist = meta.Artist ?? string.Empty;
        NowPlayingStation = meta.StationName ?? string.Empty;
        HasTrackInfo = !string.IsNullOrWhiteSpace(NowPlayingTitle);
    }

    private void PlayQueueItem(CuratedQueueItem? item)
    {
        if (item is null) return;
        var index = CuratedQueue.IndexOf(item);
        if (index < 0) return;

        // Ensure the engine's queue matches what's shown, then jump to the clicked track.
        if (!_local.HasQueue)
            _local.SetQueue(CuratedQueue.Select(ToLocalTrack).ToList(), index);
        else
            _local.PlayAt(index);
    }

    private static LocalTrack ToLocalTrack(CuratedQueueItem item)
    {
        var format = item.Path.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
            ? StreamFormat.Aac
            : StreamFormat.Mp3;
        return new LocalTrack(item.Path, item.Title, item.Artist, format);
    }

    // --- Local engine event handlers (only act in Library mode) ---

    private void OnLocalStateChanged(PlaybackState state)
    {
        if (!UsesLocalEngine) return;

        IsPlaying = state == PlaybackState.Playing;
        OnPropertyChanged(nameof(IsBusy));

        if (state == PlaybackState.Stopped)
        {
            // Queue finished (or stopped): clear the now-playing header and row highlight.
            foreach (var item in CuratedQueue) item.IsCurrent = false;
            foreach (var item in LibrarySongs) item.IsCurrent = false;
            NowPlayingTitle = IdleTitle;
            NowPlayingArtist = string.Empty;
            HasTrackInfo = false;
            PositionSeconds = 0;
            DurationSeconds = 0;
            _djIntroCts?.Cancel();
            DjIntroLine = null;
        }

        RaiseTransportCanExecute();
    }

    private void OnLocalTrackChanged(LocalTrack track)
    {
        // The queue just started playing for real (cold-start's first harvested/seeded song) —
        // if DJ warm-up's live bridge was running, hand over now. Must run BEFORE the
        // UsesLocalEngine check below: while warm-up is still active that property is false
        // (RadioEngine is "the" engine until this call flips it), so ending warm-up first is what
        // makes the rest of this method correctly apply to the track that just started.
        EndDjWarmupIfActive();
        if (!UsesLocalEngine) return;

        NowPlayingTitle = track.Title;
        NowPlayingArtist = track.Artist;
        NowPlayingStation = string.Empty; // no station chip for library tracks
        NowPlayingFormat = string.Empty;
        NowPlayingUrl = null;
        HasTrackInfo = !string.IsNullOrWhiteSpace(track.Title);

        if (IsDjMode)
        {
            MarkDjMixPosition(); // move the equalizer down the mix and refresh "Up next"
            _djSessionLog?.SongPlayed(track.Path, track.Title, track.Artist);
            _ = GenerateDjIntroAsync(track.Title, track.Artist, track.Reason);
        }

        // Highlight the playing row wherever it appears (Curate results and/or the full Songs
        // list) — matched by title+artist rather than the given index, since that index is only
        // meaningful within whichever list the engine's queue was actually built from.
        foreach (var item in CuratedQueue)
            item.IsCurrent = Matches(item.Title, item.Artist, track);
        foreach (var item in LibrarySongs)
            item.IsCurrent = Matches(item.Title, item.Artist, track);

        _nowPlayingChanged?.Invoke(track.Title, track.Artist);

        static bool Matches(string title, string artist, LocalTrack track) =>
            string.Equals(title, track.Title, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(artist, track.Artist, StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocalPosition(double position, double duration)
    {
        if (!UsesLocalEngine) return;
        if (_suspendPositionUpdates) return;

        _applyingPosition = true;
        PositionSeconds = position;
        _applyingPosition = false;
        DurationSeconds = duration;
    }

    // --- Position / seek (library) ---

    private double _positionSeconds;
    private double _durationSeconds;
    private bool _applyingPosition;    // true while the timer sets position (so the setter doesn't seek)
    private bool _suspendPositionUpdates; // set by the view while the user drags the seek slider

    /// <summary>Playback position in seconds. User-driven changes seek; timer updates don't.</summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            if (!SetProperty(ref _positionSeconds, value)) return;
            OnPropertyChanged(nameof(PositionText));
            if (!_applyingPosition && UsesLocalEngine)
                _local.Seek(value);
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (SetProperty(ref _durationSeconds, value))
            {
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(HasDuration));
            }
        }
    }

    /// <summary>The seek timeline shows only in library/DJ mode with a track loaded.</summary>
    public bool HasDuration => UsesLocalEngine && _durationSeconds > 0;

    public string PositionText => FormatTime(_positionSeconds);
    public string DurationText => FormatTime(_durationSeconds);

    private static string FormatTime(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds)) return "0:00";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>The view calls these around a seek-slider drag so timer ticks don't fight the drag.</summary>
    public void BeginSeekDrag() => _suspendPositionUpdates = true;
    public void EndSeekDrag() => _suspendPositionUpdates = false;

    // Optional hook so the host can mirror now-playing text to the OS media controls in both modes.
    private Action<string, string>? _nowPlayingChanged;
    public void SetNowPlayingSink(Action<string, string> sink) => _nowPlayingChanged = sink;
}
