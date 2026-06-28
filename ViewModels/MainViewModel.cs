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
    private string _nowPlayingStation = string.Empty;
    private string _nowPlayingFormat = string.Empty;
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
        ISemanticSearchService semanticSearch, ISearchRanker ranker)
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
        PrevStationCommand = new RelayCommand(PrevStation, () => Stations.Count > 0);
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

        var prompt = SearchPrompt;
        IsSearching = true;
        SearchResults.Clear();
        SelectedSearchResult = null;

        try
        {
            await RunUnifiedSearchAsync(prompt);
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
    private async Task RunUnifiedSearchAsync(string prompt)
    {
        if (!_interpreter.IsConfigured && !_semanticSearch.IsAvailable && !_agenticSearch.IsConfigured)
        {
            SearchStatus = "Add your Anthropic API key in Options to use AI search.";
            return;
        }

        SearchStatus = "Scanning the airwaves…";

        // 1. Cheap recall sources, together: structured Radio Browser lookup + local semantic.
        //    Gather a larger semantic pool so the ranker has real choice.
        var structuredTask = RunStructuredAsync(prompt);
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in structured)
            if (seen.Add(c.Station.Url))
                pool.Add(new SearchResultItem(c.Station, DescriptionFor(c)));
        foreach (var r in semantic)
            if (seen.Add(r.Station.Url))
                pool.Add(new SearchResultItem(r.Station, r.Description));

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
            SearchStatus = "Nothing turned up — try describing it differently.";
            return;
        }

        // 4. Validate streams before showing (drops dead/undecodable ones).
        SearchStatus = "Making sure they actually play…";
        await AddValidatedAsync(shortlist);
        SetResultStatus();
    }

    /// <summary>
    /// Pattern A as a recall source: translate the prompt to structured Radio Browser
    /// parameters and run one query. Returns [] when not configured or the model can't parse
    /// the prompt — there is no Broaden fallback any more; the pool's other sources and the
    /// re-ranker decide relevance, so a query that matches nothing simply contributes nothing.
    /// </summary>
    private async Task<IReadOnlyList<StationCandidate>> RunStructuredAsync(string prompt)
    {
        if (!_interpreter.IsConfigured)
            return [];
        var query = await _interpreter.InterpretAsync(prompt);
        if (query is null)
            return [];
        return await _searchService.SearchCandidatesAsync(query);
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
            var candidates = pool.Select((p, i) => new RankCandidate(i, p.Station.Name, p.Reason ?? "")).ToList();
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

    private void SetResultStatus() =>
        SearchStatus = SearchResults.Count == 0
            ? "Nothing playable came through — try describing it differently."
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

        // Carry the search-panel blurb onto the saved station so the fixed list shows the
        // same secondary line (Reason = enriched description / web rationale, or null).
        Stations.Add(station with { Description = item.Reason });
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
            {
                _engine.Volume = value;
                OnPropertyChanged(nameof(VolumePercent));
            }
        }
    }

    /// <summary>Volume as a whole-number percent (0–100) for the readout next to the slider.</summary>
    public int VolumePercent => (int)Math.Round(_volume * 100);

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
