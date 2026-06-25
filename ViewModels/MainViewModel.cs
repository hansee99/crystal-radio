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

    // Below this cosine score the local index is considered too weak; fall back to Pattern B.
    private const double SemanticThreshold = 0.30;

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
        ISemanticSearchService semanticSearch)
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
        PlaySearchResultCommand = new RelayCommand(PlaySelectedSearchResult, () => SelectedSearchResult is not null);
    }

    // ===== AI-assisted station search =====

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    public RelayCommand SearchCommand { get; }
    public RelayCommand PlaySearchResultCommand { get; }

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
        set
        {
            if (SetProperty(ref _selectedSearchResult, value))
                PlaySearchResultCommand.RaiseCanExecuteChanged();
        }
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
            if (IsFuzzy(SearchPrompt))
                await RunFuzzySearchAsync(SearchPrompt);
            else
                await RunLiteralSearchAsync(SearchPrompt);
        }
        catch (Exception ex)
        {
            SearchStatus = $"Search failed: {ex.Message}";
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
            SearchStatus = "Set the ANTHROPIC_API_KEY environment variable to use AI search.";
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
        SearchStatus = "Searching locally and on the web…";

        // Kick off whichever paths are available, together.
        var semanticTask = _semanticSearch.IsAvailable
            ? _semanticSearch.SearchAsync(prompt, 10)
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

        // Merge: relevant local hits first (by score), then web finds not already shown.
        // De-dupe on the resolved stream URL, which both paths populate.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localShown = 0;
        foreach (var r in semantic)
        {
            if (r.Score < SemanticThreshold) continue;     // keep only relevant local hits
            if (!seen.Add(r.Station.Url)) continue;
            SearchResults.Add(new SearchResultItem(r.Station, $"Local match · {r.Score:0.00}"));
            localShown++;
        }
        var webShown = 0;
        foreach (var r in web)
        {
            if (!seen.Add(r.Station.Url)) continue;        // dedupe vs local + earlier web finds
            SearchResults.Add(new SearchResultItem(r.Station, r.Reason));
            webShown++;
        }

        if (SearchResults.Count > 0)
            SearchStatus = $"Found {SearchResults.Count} station{(SearchResults.Count == 1 ? "" : "s")} "
                + $"({localShown} local + {webShown} web).";
        else if (!_semanticSearch.IsAvailable && !_agenticSearch.IsConfigured)
            SearchStatus = "Set ANTHROPIC_API_KEY (and add the embedding model) to use AI search.";
        else
            SearchStatus = "No matching stations found. Try a different prompt.";
    }

    /// <summary>Pattern A: structured-output translation, then a Radio Browser query.</summary>
    private async Task RunStructuredSearchAsync(string prompt)
    {
        SearchStatus = "Interpreting your request…";
        var query = await _interpreter.InterpretAsync(prompt);
        if (query is null)
        {
            SearchStatus = "Couldn't interpret that. Try rephrasing.";
            return;
        }

        SearchStatus = "Searching stations…";
        var results = await _searchService.SearchCandidatesAsync(query);

        // Unhappy path: nothing matched — broaden once before giving up.
        if (results.Count == 0 && Broaden(query) is { } broadened)
        {
            SearchStatus = "No exact matches — broadening the search…";
            results = await _searchService.SearchCandidatesAsync(broadened);
        }

        // Lazily enrich what the user explored (fire-and-forget; never blocks).
        _enrichment.EnrichInBackground(results);

        foreach (var candidate in results)
            SearchResults.Add(new SearchResultItem(candidate.Station, null));

        SearchStatus = results.Count == 0
            ? "No playable stations found. Try a different prompt."
            : $"Found {results.Count} station{(results.Count == 1 ? "" : "s")}.";
    }

    /// <summary>
    /// Cheap heuristic to pick the path: short, plain names ("BBC") go to Pattern A;
    /// descriptive/semantic prompts ("dreamy synthwave people recommend") go to Pattern B.
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

    /// <summary>Add the chosen result to the library (persisted) and play it.</summary>
    public void PlaySelectedSearchResult()
    {
        var station = SelectedSearchResult?.Station;
        if (station is null)
            return;

        var existing = Stations.FirstOrDefault(s =>
            string.Equals(s.Url, station.Url, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Stations.Add(station);
            _store.Save(Stations);
            NextStationCommand.RaiseCanExecuteChanged();
        }
        else
        {
            station = existing;
        }

        SelectWithoutAutoPlay(station);
        _engine.Play(station);
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
    public void SaveSettings() => _settingsStore.Save(new AppSettings { Volume = _volume });

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
