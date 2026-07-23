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

    // "Show different" (Regenerate) search: remember what's already been shown for the current
    // prompt so a re-run surfaces fresh stations, and page deeper into the directory each time.
    private readonly HashSet<string> _shownStationUrls = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSearchPrompt = string.Empty;
    private int _searchPage;

    // Radio Browser rows fetched per structured page — must match StationSearchService's limit
    // so paging on regenerate advances past the previous page's directory rows.
    private const int StructuredPageSize = 30;
    private string _nowPlayingTitle = "Not playing";
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
        ISongLibraryService songLibrary, LocalPlaybackEngine local, ISongCurator curator)
    {
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

        // Restore the persisted volume onto both engines (either/or, but volume is shared).
        _volume = settingsStore.Load().Volume;
        _engine.Volume = _volume;
        _local.Volume = _volume;

        _engine.StateChanged += (_, state) => OnStateChanged(state);
        _engine.MetadataChanged += (_, meta) => OnMetadataChanged(meta);
        _engine.ErrorOccurred += (_, msg) => StatusText = msg;

        // Local (library) engine — its handlers no-op unless Library mode is active.
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
            if (entry.SegmentFile is not null && !File.Exists(StreamRecorder.PathFor(entry.SegmentFile)))
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
            _songLibrary.GetAll().Select(s => new LibrarySongItem(s)));

        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(() => ActiveEngine.Stop(), () => ActiveEngine.State != PlaybackState.Stopped);
        NextStationCommand = new RelayCommand(Next, CanGoNext);
        PrevStationCommand = new RelayCommand(Prev, CanGoPrev);
        SwitchToRadioCommand = new RelayCommand(() => SetMode(PlayerMode.Radio));
        SwitchToLibraryCommand = new RelayCommand(() => SetMode(PlayerMode.Library));
        CurateCommand = new RelayCommand(() => _ = RunCurateAsync(), () => !IsCurating);
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
    }

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

        // This is now the current song; the mark-for-save toggle targets it (fresh → unmarked).
        SetCurrentSong(entry);
    }

    /// <summary>Removes a history row AND its cached segment file (never orphan audio).</summary>
    private void RemoveHistoryAt(int index)
    {
        var entry = History[index];
        if (entry.SegmentFile is not null)
            StreamRecorder.TryDelete(StreamRecorder.PathFor(entry.SegmentFile));
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
            StreamRecorder.TryDelete(StreamRecorder.PathFor(seg.FileName));
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
            StreamRecorder.TryDelete(StreamRecorder.PathFor(entry.SegmentFile));
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

            File.Copy(StreamRecorder.PathFor(entry.SegmentFile), dest);
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
            System.Diagnostics.Debug.WriteLine($"[Save] failed: {ex.Message}");
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
            }
        }
    }

    public SearchResultItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set => SetProperty(ref _selectedSearchResult, value);
    }

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

        IsSearching = true;
        SearchResults.Clear();
        SelectedSearchResult = null;

        try
        {
            await RunUnifiedSearchAsync(prompt, _searchPage);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Search] failed: {ex}");
            SearchStatus = "The search hit a snag — please try again.";
        }
        finally
        {
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
    private async Task RunUnifiedSearchAsync(string prompt, int page)
    {
        if (!_interpreter.IsConfigured && !_semanticSearch.IsAvailable && !_agenticSearch.IsConfigured)
        {
            SearchStatus = "Add your Anthropic API key in Options to use AI search.";
            return;
        }

        var isRegenerate = page > 0;
        SearchStatus = isRegenerate ? "Looking for something different…" : "Scanning the airwaves…";

        // 1. Cheap recall sources, together: structured Radio Browser lookup + local semantic.
        //    Gather a larger semantic pool so the ranker has real choice. On regenerate, page
        //    deeper into the directory so Pattern A brings back rows we haven't shown yet.
        var structuredTask = RunStructuredAsync(prompt, page * StructuredPageSize);
        var semanticTask = _semanticSearch.IsAvailable
            ? _semanticSearch.SearchAsync(prompt, CandidatePoolSize)
            : Task.FromResult<IReadOnlyList<SemanticResult>>([]);

        // Await each independently so one source failing doesn't sink the other.
        IReadOnlyList<StationCandidate> structured = [];
        IReadOnlyList<SemanticResult> semantic = [];
        try { structured = await structuredTask; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Search] structured failed: {ex.Message}"); }
        try { semantic = await semanticTask; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Search] semantic failed: {ex.Message}"); }

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

        var shortlist = await RankOrMerge(prompt, pool, semantic);

        // 3. Escalate to web discovery only when the cheap sources came back thin.
        if (NeedsWebEscalation(shortlist) && _agenticSearch.IsConfigured)
        {
            SearchStatus = "Casting a wider net on the web…";
            IReadOnlyList<RankedStation> web = [];
            try { web = await _agenticSearch.SearchAsync(prompt); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Search] web failed: {ex.Message}"); }

            foreach (var r in web)
                if (seen.Add(r.Station.Url))
                    pool.Add(new SearchResultItem(r.Station, r.Reason));

            // Re-rank the combined pool so web finds compete with the cheap ones on one signal.
            shortlist = await RankOrMerge(prompt, pool, semantic);
        }

        if (shortlist.Count == 0)
        {
            SearchStatus = isRegenerate
                ? "That's everything I could find for this — try a new description."
                : "Nothing turned up — try describing it differently.";
            return;
        }

        // 4. Validate streams before showing (drops dead/undecodable ones).
        SearchStatus = "Making sure they actually play…";
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
    private async Task<IReadOnlyList<StationCandidate>> RunStructuredAsync(string prompt, int offset)
    {
        if (!_interpreter.IsConfigured)
            return [];
        var query = await _interpreter.InterpretAsync(prompt);
        if (query is null)
            return [];
        return await _searchService.SearchCandidatesAsync(query, offset);
    }

    /// <summary>
    /// Order the pool with the LLM re-ranker (drops non-matches, best match first); fall back
    /// to the cosine-threshold heuristic when the ranker can't run (no key / transient error).
    /// </summary>
    private async Task<List<SearchResultItem>> RankOrMerge(
        string prompt, List<SearchResultItem> pool, IReadOnlyList<SemanticResult> semantic)
    {
        if (pool.Count == 0)
            return [];

        if (_ranker.IsConfigured)
        {
            SearchStatus = "Finding the best matches…";
            var candidates = pool.Select((p, i) => new RankCandidate(i, p.Station.Name, p.Reason ?? "", p.Country)).ToList();
            var verdicts = await _ranker.RankAsync(prompt, candidates, ResultsToValidate);
            if (verdicts is not null) // null = ranker couldn't run → fall back to heuristic
            {
                System.Diagnostics.Debug.WriteLine($"[Rank] pool={pool.Count} -> kept {verdicts.Count}");
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

    private void SetResultStatus(bool isRegenerate) =>
        SearchStatus = SearchResults.Count == 0
            ? (isRegenerate
                ? "No more new stations for this — try a new description."
                : "Nothing playable came through — try describing it differently.")
            : $"Found {SearchResults.Count} station{(SearchResults.Count == 1 ? "" : "s")} you can play.";


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
    public RelayCommand CurateCommand { get; }
    public RelayCommand<CuratedQueueItem> PlayQueueItemCommand { get; }
    public RelayCommand<LibrarySongItem> PlayLibrarySongCommand { get; }

    public Station? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (!SetProperty(ref _selectedStation, value)) return;
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

    public string NowPlayingTitle
    {
        get => _nowPlayingTitle;
        private set
        {
            if (SetProperty(ref _nowPlayingTitle, value))
                OnPropertyChanged(nameof(NowPlayingClipboardText));
        }
    }

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

    /// <summary>True while a stream is connecting/reconnecting (drives the loading spinner).</summary>
    public bool IsBusy => IsRadioMode && _engine.State is PlaybackState.Buffering or PlaybackState.Reconnecting;

    /// <summary>The LIVE badge is a radio-only affordance (local tracks show a seek timeline instead).</summary>
    public bool ShowLiveBadge => IsPlaying && IsRadioMode;

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
                AboutError = "Couldn't find reliable notes on this track.";
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
            System.Diagnostics.Debug.WriteLine($"[TrackInfo] generation failed: {ex.Message}");
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

    // Transport prev/next route to the active mode: stations (wrapping) for radio, the curated
    // queue for the library.
    private void Next()
    {
        if (IsLibraryMode) _local.Next();
        else NextStation();
    }

    private void Prev()
    {
        if (IsLibraryMode) _local.Previous();
        else PrevStation();
    }

    private bool CanGoNext() => IsLibraryMode ? _local.HasQueue : Stations.Count > 0;
    private bool CanGoPrev() => IsLibraryMode ? _local.HasQueue : Stations.Count > 0;

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
        if (IsLibraryMode)
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

        // The playing station name is the source of truth for "what's playing" (cleared when
        // stopped), so a selected-but-not-playing row in either list isn't mistaken for it.
        NowPlayingStation = state == PlaybackState.Stopped
            ? string.Empty
            : _engine.CurrentStation?.Name ?? string.Empty;
        NowPlayingFormat = state == PlaybackState.Stopped
            ? string.Empty
            : _engine.CurrentStation?.Format switch
            {
                StreamFormat.Aac => "AAC",
                StreamFormat.Mp3 => "MP3",
                _ => string.Empty
            };

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
                NowPlayingTitle = "Not playing";
                NowPlayingArtist = string.Empty;
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
                if (NowPlayingTitle is "Connecting..." or "Reconnecting..." or "Not playing")
                {
                    NowPlayingTitle = _engine.CurrentStation?.Name ?? "Live stream";
                    NowPlayingArtist = string.Empty;
                    HasTrackInfo = !string.IsNullOrWhiteSpace(NowPlayingTitle);
                }
                break;
        }

        StopCommand.RaiseCanExecuteChanged();
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

    /// <summary>The engine driving playback in the current mode.</summary>
    private IPlaybackEngine ActiveEngine => IsLibraryMode ? _local : _engine;

    public PlayerMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsRadioMode));
            OnPropertyChanged(nameof(IsLibraryMode));
            OnPropertyChanged(nameof(ShowRadioPanel));
            OnPropertyChanged(nameof(ShowLibraryPanel));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ShowLiveBadge));
            OnPropertyChanged(nameof(HasDuration));
        }
    }

    public bool IsRadioMode => _mode == PlayerMode.Radio;
    public bool IsLibraryMode => _mode == PlayerMode.Library;
    public bool ShowRadioPanel => IsRadioMode;
    public bool ShowLibraryPanel => IsLibraryMode;

    /// <summary>Switch player modes. Either/or: the now-inactive engine is stopped and the
    /// now-playing view reset, so only one thing ever plays.</summary>
    private void SetMode(PlayerMode mode)
    {
        if (_mode == mode) return;

        Mode = mode; // set first so the stopped engine's handler no-ops (guards on mode)
        if (mode == PlayerMode.Library) _engine.Stop(); else _local.Stop();

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
        NowPlayingTitle = "Not playing";
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
        foreach (var s in _songLibrary.GetAll())
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
                CurateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Curate a playlist from the local library for the prompt, then start playing it.</summary>
    private async Task RunCurateAsync()
    {
        if (IsCurating || string.IsNullOrWhiteSpace(LibraryPrompt))
            return;
        if (!_curator.IsAvailable)
        {
            LibraryStatus = "Save some songs first — the library is empty.";
            return;
        }

        var prompt = LibraryPrompt;
        IsCurating = true;
        LibraryStatus = "Curating a playlist…";
        try
        {
            var songs = await _curator.CurateAsync(prompt, 20);
            CuratedQueue.Clear();
            foreach (var s in songs)
                CuratedQueue.Add(new CuratedQueueItem(s));

            if (CuratedQueue.Count == 0)
            {
                LibraryStatus = "Nothing in your library matched — try a different vibe.";
                OnPropertyChanged(nameof(HasCuratedPlaylist));
                return;
            }

            // The context card's title, so "the thing I asked for" reads distinctly from the
            // resulting track list (Shared Framework Spec §3).
            CuratedPlaylistTitle = System.Globalization.CultureInfo.InvariantCulture.TextInfo
                .ToTitleCase(prompt.ToLowerInvariant());
            OnPropertyChanged(nameof(CuratedPlaylistTitle));
            OnPropertyChanged(nameof(HasCuratedPlaylist));

            LibraryStatus = $"Playing {CuratedQueue.Count} song{(CuratedQueue.Count == 1 ? "" : "s")}.";
            _local.SetQueue(CuratedQueue.Select(ToLocalTrack).ToList(), 0);
            RaiseTransportCanExecute();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Curate] failed: {ex}");
            LibraryStatus = "Curation hit a snag — please try again.";
        }
        finally
        {
            IsCurating = false;
        }
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
        if (!IsLibraryMode) return;

        IsPlaying = state == PlaybackState.Playing;
        OnPropertyChanged(nameof(IsBusy));

        if (state == PlaybackState.Stopped)
        {
            // Queue finished (or stopped): clear the now-playing header and row highlight.
            foreach (var item in CuratedQueue) item.IsCurrent = false;
            foreach (var item in LibrarySongs) item.IsCurrent = false;
            NowPlayingTitle = "Not playing";
            NowPlayingArtist = string.Empty;
            HasTrackInfo = false;
            PositionSeconds = 0;
            DurationSeconds = 0;
        }

        RaiseTransportCanExecute();
    }

    private void OnLocalTrackChanged(LocalTrack track)
    {
        if (!IsLibraryMode) return;

        NowPlayingTitle = track.Title;
        NowPlayingArtist = track.Artist;
        NowPlayingStation = string.Empty; // no station chip for library tracks
        NowPlayingFormat = string.Empty;
        NowPlayingUrl = null;
        HasTrackInfo = !string.IsNullOrWhiteSpace(track.Title);

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
        if (!IsLibraryMode) return;
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
            if (!_applyingPosition && IsLibraryMode)
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

    /// <summary>The seek timeline shows only in library mode with a track loaded.</summary>
    public bool HasDuration => IsLibraryMode && _durationSeconds > 0;

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
