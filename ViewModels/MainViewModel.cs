using System.Collections.ObjectModel;
using RadioPlayer.Models;
using RadioPlayer.Mvvm;
using RadioPlayer.Services;

namespace RadioPlayer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly RadioEngine _engine;
    private readonly StationStore _store;
    private readonly SettingsStore _settingsStore;
    private readonly IStationDialog _stationDialog;
    private readonly IPromptInterpreter _interpreter;
    private readonly IStationSearchService _searchService;
    private readonly IAgenticSearchService _agenticSearch;
    private readonly IEnrichmentService _enrichment;
    private readonly ISemanticSearchService _semanticSearch;
    private readonly ISearchRanker _ranker;
    private readonly IQueryClassifier _classifier;

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
    private string _nowPlayingTitle = "Not playing";
    private string _nowPlayingArtist = string.Empty;
    private string _statusText = "Stopped";
    private bool _isPlaying;
    private bool _hasTrackInfo;
    private double _volume;

    // When set, changing SelectedStation won't auto-start playback. Used by the
    // add/edit/delete commands so managing the list doesn't yank what's playing.
    private bool _suppressAutoPlay;

    public MainViewModel(RadioEngine engine, StationStore store, SettingsStore settingsStore,
        IStationDialog stationDialog, IPromptInterpreter interpreter, IStationSearchService searchService,
        IAgenticSearchService agenticSearch, IEnrichmentService enrichment,
        ISemanticSearchService semanticSearch, ISearchRanker ranker, IQueryClassifier classifier)
    {
        _engine = engine;
        _store = store;
        _settingsStore = settingsStore;
        _stationDialog = stationDialog;
        _interpreter = interpreter;
        _searchService = searchService;
        _agenticSearch = agenticSearch;
        _enrichment = enrichment;
        _semanticSearch = semanticSearch;
        _ranker = ranker;
        _classifier = classifier;

        // Restore the persisted volume.
        _volume = settingsStore.Load().Volume;
        _engine.Volume = _volume;

        _engine.StateChanged += (_, state) => OnStateChanged(state);
        _engine.MetadataChanged += (_, meta) => OnMetadataChanged(meta);
        _engine.ErrorOccurred += (_, msg) => StatusText = msg;

        Stations = new ObservableCollection<Station>(_store.Load());
        _selectedStation = Stations.FirstOrDefault();

        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        StopCommand = new RelayCommand(_engine.Stop, () => _engine.State != PlaybackState.Stopped);
        NextStationCommand = new RelayCommand(NextStation, () => Stations.Count > 0);
        AddStationCommand = new RelayCommand(AddStation);
        EditStationCommand = new RelayCommand(EditStation, () => SelectedStation is not null);
        DeleteStationCommand = new RelayCommand(DeleteStation, () => SelectedStation is not null);
        SearchCommand = new RelayCommand(() => _ = RunSearchAsync(), () => !IsSearching);
        AddSearchResultCommand = new RelayCommand<SearchResultItem>(AddSearchResultToLibrary);
    }

    // ===== AI-assisted station search =====

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    public RelayCommand SearchCommand { get; }
    public RelayCommand<SearchResultItem> AddSearchResultCommand { get; }

    public string SearchPrompt
    {
        get => _searchPrompt;
        set => SetProperty(ref _searchPrompt, value);
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
                SearchCommand.RaiseCanExecuteChanged();
        }
    }

    public SearchResultItem? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set => SetProperty(ref _selectedSearchResult, value);
    }

    private async Task RunSearchAsync()
    {
        if (IsSearching || string.IsNullOrWhiteSpace(SearchPrompt))
            return;

        IsSearching = true;
        SearchResults.Clear();
        SelectedSearchResult = null;

        try
        {
            // Three paths now:
            //   literal  -> Pattern A (structured translation; needs API key)
            //   fuzzy    -> local semantic search; if weak/cold -> Pattern B (web discovery)
            // An LLM classifies which path the prompt wants; if it can't run (no key/error)
            // we fall back to the cheap keyword/length heuristic.
            SearchStatus = "Tuning in to your request…";
            var fuzzy = await _classifier.IsFuzzyAsync(SearchPrompt) ?? IsFuzzy(SearchPrompt);
            if (fuzzy)
                await RunFuzzySearchAsync(SearchPrompt);
            else
                await RunLiteralSearchAsync(SearchPrompt);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Search] failed: {ex}");
            SearchStatus = "The search hit a snag — please try again.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task RunLiteralSearchAsync(string prompt)
    {
        if (!_interpreter.IsConfigured)
        {
            SearchStatus = "Add your Anthropic API key in Options to use AI search.";
            return;
        }
        await RunStructuredSearchAsync(prompt);
    }

    /// <summary>
    /// Fuzzy path: run BOTH local semantic search and web discovery (Pattern B) concurrently,
    /// then merge and de-duplicate. Local hits carry their similarity score; web finds carry
    /// their one-line reason. The enrichment DB augments — it never replaces — web discovery,
    /// and Pattern B's finds get enriched + embedded so the local side keeps improving.
    /// </summary>
    private async Task RunFuzzySearchAsync(string prompt)
    {
        SearchStatus = "Scanning the airwaves…";

        // Kick off whichever paths are available, together. Gather a larger local pool so the
        // re-ranker has real choice (cosine alone over-favours thin/generic descriptions).
        var semanticTask = _semanticSearch.IsAvailable
            ? _semanticSearch.SearchAsync(prompt, CandidatePoolSize)
            : Task.FromResult<IReadOnlyList<SemanticResult>>([]);
        var webTask = _agenticSearch.IsConfigured
            ? _agenticSearch.SearchAsync(prompt)
            : Task.FromResult<IReadOnlyList<RankedStation>>([]);

        // Await each independently so one path failing doesn't sink the other.
        IReadOnlyList<SemanticResult> semantic = [];
        IReadOnlyList<RankedStation> web = [];
        try { semantic = await semanticTask; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Search] semantic failed: {ex.Message}"); }
        try { web = await webTask; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Search] web failed: {ex.Message}"); }

        // Build one de-duplicated candidate pool (local + web). Each carries the text the
        // ranker will judge: the stored description (local) or the model's reason (web).
        var pool = new List<SearchResultItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in semantic)
            if (seen.Add(r.Station.Url))
                pool.Add(new SearchResultItem(r.Station, r.Description));
        foreach (var r in web)
            if (seen.Add(r.Station.Url))
                pool.Add(new SearchResultItem(r.Station, r.Reason));

        if (pool.Count == 0)
        {
            SearchStatus = !_semanticSearch.IsAvailable && !_agenticSearch.IsConfigured
                ? "Add your Anthropic API key in Options to use AI search."
                : "Nothing turned up — try describing it differently.";
            return;
        }

        // LLM relevance re-rank: judge each candidate's text against the prompt so local and
        // web compete on one signal and weak matches are dropped.
        // LLM relevance re-rank produces an ordered shortlist; otherwise fall back to the
        // cosine-threshold heuristic. Either way we then validate streams before showing them.
        List<SearchResultItem> shortlist;
        if (_ranker.IsConfigured)
        {
            SearchStatus = "Finding the best matches…";
            var candidates = pool.Select((p, i) => new RankCandidate(i, p.Station.Name, p.Reason ?? "")).ToList();
            var verdicts = await _ranker.RankAsync(prompt, candidates, ResultsToValidate);
            if (verdicts is not null) // null = ranker couldn't run → fall back to heuristic
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Rank] pool={pool.Count} (local {semantic.Count}, web {web.Count}) -> kept {verdicts.Count}");
                shortlist = verdicts.Select(v => pool[v.Id]).ToList();
            }
            else
            {
                shortlist = BuildHeuristicMerge(semantic, web);
            }
        }
        else
        {
            shortlist = BuildHeuristicMerge(semantic, web);
        }

        SearchStatus = "Making sure they actually play…";
        await AddValidatedAsync(shortlist);
        SetResultStatus();
    }

    /// <summary>Fallback ordering when the LLM re-ranker isn't available: relevant local hits by score, then web.</summary>
    private List<SearchResultItem> BuildHeuristicMerge(IReadOnlyList<SemanticResult> semantic, IReadOnlyList<RankedStation> web)
    {
        var list = new List<SearchResultItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in semantic)
        {
            if (list.Count >= ResultsToValidate) break;
            if (r.Score < SemanticThreshold) continue;
            if (!seen.Add(r.Station.Url)) continue;
            list.Add(new SearchResultItem(r.Station, $"Local match · {r.Score:0.00}"));
        }
        foreach (var r in web)
        {
            if (list.Count >= ResultsToValidate) break;
            if (!seen.Add(r.Station.Url)) continue;
            list.Add(new SearchResultItem(r.Station, r.Reason));
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

    private void SetResultStatus() =>
        SearchStatus = SearchResults.Count == 0
            ? "Nothing playable came through — try describing it differently."
            : $"Found {SearchResults.Count} station{(SearchResults.Count == 1 ? "" : "s")} you can play.";

    /// <summary>Pattern A: structured-output translation, then a Radio Browser query.</summary>
    private async Task RunStructuredSearchAsync(string prompt)
    {
        SearchStatus = "Tuning in to your request…";
        var query = await _interpreter.InterpretAsync(prompt);
        if (query is null)
        {
            SearchStatus = "Didn't quite catch that — try rephrasing.";
            return;
        }

        SearchStatus = "Searching the dial…";
        var results = await _searchService.SearchCandidatesAsync(query);

        // Unhappy path: nothing matched — broaden once before giving up.
        if (results.Count == 0 && Broaden(query) is { } broadened)
        {
            SearchStatus = "Casting a wider net…";
            results = await _searchService.SearchCandidatesAsync(broadened);
        }

        // Lazily enrich what the user explored (fire-and-forget; never blocks). Enrich all
        // fetched rows even though we only show the top few.
        _enrichment.EnrichInBackground(results);

        if (results.Count == 0)
        {
            SearchStatus = "Nothing turned up — try describing it differently.";
            return;
        }

        // Validate streams before showing (over-fetch so dead ones still leave ~MaxResults).
        var shortlist = results.Take(ResultsToValidate)
            .Select(c => new SearchResultItem(c.Station, null)).ToList();
        SearchStatus = "Making sure they actually play…";
        await AddValidatedAsync(shortlist);
        SetResultStatus();
    }

    /// <summary>
    /// Fallback path picker used only when the LLM classifier can't run (no key / error).
    /// Cheap heuristic: short, plain names ("BBC") go to Pattern A; descriptive/semantic
    /// prompts ("dreamy synthwave people recommend") go to Pattern B. It misses vibe queries
    /// with no marker word (e.g. "roadtrip music") — which is exactly why the classifier leads.
    /// </summary>
    private static bool IsFuzzy(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        string[] softMarkers =
        [
            "recommend", "vibe", "like ", "similar", "dreamy", "mood", "feel", "people",
            "best ", "for ", "late", "night", "study", "studying", "coding", "workout",
            "relax", "chill", "background", "something", "kind of", "sounds like", "era",
            "scene", "drive", "drives"
        ];
        if (softMarkers.Any(lower.Contains))
            return true;
        // Long prompts are almost always descriptive, not a literal station name.
        return prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5;
    }

    /// <summary>Loosen a query that returned nothing: drop country/language, then narrow tags.</summary>
    private static StationSearchQuery? Broaden(StationSearchQuery q)
    {
        if (!string.IsNullOrWhiteSpace(q.Country) || !string.IsNullOrWhiteSpace(q.Language))
            return new StationSearchQuery { Tags = q.Tags, BitrateMin = q.BitrateMin, Order = q.Order };
        if (q.Tags.Length > 1)
            return new StationSearchQuery { Tags = [q.Tags[0]], Order = q.Order };
        return null;
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

        // Carry the search-panel blurb onto the saved station so the fixed list shows the
        // same secondary line (Reason = enriched description / web rationale, or null).
        Stations.Add(station with { Description = item.Reason });
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();
        item.IsAdded = true; // swap the row's + to a check
    }

    public ObservableCollection<Station> Stations { get; }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand NextStationCommand { get; }
    public RelayCommand AddStationCommand { get; }
    public RelayCommand EditStationCommand { get; }
    public RelayCommand DeleteStationCommand { get; }

    public Station? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (!SetProperty(ref _selectedStation, value)) return;

            EditStationCommand.RaiseCanExecuteChanged();
            DeleteStationCommand.RaiseCanExecuteChanged();
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

    /// <summary>True while a stream is connecting/reconnecting (drives the loading spinner).</summary>
    public bool IsBusy => _engine.State is PlaybackState.Buffering or PlaybackState.Reconnecting;

    /// <summary>True when there's real now-playing info worth copying.</summary>
    public bool HasTrackInfo
    {
        get => _hasTrackInfo;
        private set
        {
            if (SetProperty(ref _hasTrackInfo, value))
                OnPropertyChanged(nameof(NowPlayingClipboardText));
        }
    }

    /// <summary>"Artist - Title" (or just the title) for the clipboard, or null if nothing to copy.</summary>
    public string? NowPlayingClipboardText =>
        !HasTrackInfo ? null
        : string.IsNullOrWhiteSpace(NowPlayingArtist) ? NowPlayingTitle
        : $"{NowPlayingArtist} - {NowPlayingTitle}";

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
                OnPropertyChanged(nameof(PlayPauseLabel));
        }
    }

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
                _engine.Volume = value;
        }
    }

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

    private void AddStation()
    {
        var created = _stationDialog.Show(null);
        if (created is null) return;

        Stations.Add(created);
        SelectWithoutAutoPlay(created);
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();
    }

    private void EditStation()
    {
        var existing = SelectedStation;
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

    private void DeleteStation()
    {
        var target = SelectedStation;
        if (target is null) return;

        if (ReferenceEquals(_engine.CurrentStation, target))
            _engine.Stop();

        var index = Stations.IndexOf(target);
        Stations.Remove(target);
        _store.Save(Stations);
        NextStationCommand.RaiseCanExecuteChanged();

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
        if (_engine.State is PlaybackState.Stopped or PlaybackState.Error)
        {
            if (SelectedStation is not null)
                _engine.Play(SelectedStation);
        }
        else
        {
            _engine.TogglePause();
        }
    }

    private void OnStateChanged(PlaybackState state)
    {
        IsPlaying = state == PlaybackState.Playing;
        OnPropertyChanged(nameof(IsBusy));

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
                break;
            case PlaybackState.Buffering:
            case PlaybackState.Reconnecting:
                // No track to copy yet; show which station we're connecting to.
                NowPlayingTitle = state == PlaybackState.Reconnecting ? "Reconnecting..." : "Connecting...";
                NowPlayingArtist = _engine.CurrentStation?.Name ?? string.Empty;
                HasTrackInfo = false;
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
    }
}
