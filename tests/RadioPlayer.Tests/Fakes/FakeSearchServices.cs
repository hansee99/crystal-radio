using RadioPlayer.Models;
using RadioPlayer.Services;

namespace RadioPlayer.Tests.Fakes;

/// <summary>Passes the prompt through as a single tag, or isn't configured at all.</summary>
public sealed class FakePromptInterpreter : IPromptInterpreter
{
    public bool IsConfigured { get; set; } = true;

    public Task<StationSearchQuery?> InterpretAsync(string prompt, CancellationToken ct = default) =>
        Task.FromResult<StationSearchQuery?>(new StationSearchQuery { Tags = [prompt] });
}

/// <summary>Pattern B, off by default — the tests here are about the cheap sources.</summary>
public sealed class FakeAgenticSearchService : IAgenticSearchService
{
    public bool IsConfigured { get; set; }

    /// <summary>Incremented per call, so a test can assert web discovery never ran.</summary>
    public int Calls { get; private set; }

    public List<RankedStation> Results { get; } = [];

    public Task<IReadOnlyList<RankedStation>> SearchAsync(string prompt, int maxResults = 6,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<RankedStation>>(Results);
    }
}

/// <summary>
/// The strict relevance judge. Three configurable behaviours, matching the three real ones:
/// configured and answering, configured but the call fails (<see cref="ReturnsNull"/>), and
/// no API key at all.
/// </summary>
public sealed class FakeSearchRanker : ISearchRanker
{
    public bool IsConfigured { get; set; } = true;

    /// <summary>Mimics a ranker call that couldn't complete — the real service returns null.</summary>
    public bool ReturnsNull { get; set; }

    /// <summary>Ids to keep, in order. Null means "keep everything, in the order given".</summary>
    public int[]? Keep { get; set; }

    /// <summary>What the ranker was asked to judge, for asserting on the pool it received.</summary>
    public List<IReadOnlyList<RankCandidate>> Seen { get; } = [];

    public Task<IReadOnlyList<RankVerdict>?> RankAsync(string prompt,
        IReadOnlyList<RankCandidate> candidates, int topK, CancellationToken ct = default)
    {
        Seen.Add(candidates);
        if (ReturnsNull)
            return Task.FromResult<IReadOnlyList<RankVerdict>?>(null);

        // Respects topK like the real ranker does — a caller asking for 4 stations gets 4, which is
        // what leaves later candidates available to a subsequent pass.
        var ids = Keep ?? candidates.Select(c => c.Id).Take(topK).ToArray();
        // Descending score in the given order, so OrderByDescending preserves the caller's intent.
        var verdicts = ids.Select((id, i) => new RankVerdict(id, 1.0 - i * 0.01)).ToList();
        return Task.FromResult<IReadOnlyList<RankVerdict>?>(verdicts);
    }
}

/// <summary>Local semantic search with a canned catalog. Online and offline answer separately,
/// so a test can make the online path silent (as an outage does) while the offline one still
/// holds stations.</summary>
public sealed class FakeSemanticSearchService : ISemanticSearchService
{
    public bool IsAvailable { get; set; } = true;

    public List<SemanticResult> Online { get; } = [];
    public List<SemanticResult> Offline { get; } = [];

    /// <summary>Offline calls made, so a test can assert the fallback did (or didn't) fire.</summary>
    public int OfflineCalls { get; private set; }

    /// <summary>The floor the caller asked for, to check it isn't silently bypassed.</summary>
    public double? LastMinScore { get; private set; }

    /// <summary>
    /// The url defaults to one derived from the name, not a fixed string: the pools these feed
    /// dedupe on stream url, so a shared default would quietly collapse several stations into one.
    /// </summary>
    public FakeSemanticSearchService AddOffline(string name, double score, string? description = "a description",
        string? url = null, StreamFormat format = StreamFormat.Mp3)
    {
        url ??= "http://example/" + name.Replace(' ', '-').ToLowerInvariant();
        Offline.Add(new SemanticResult(new Station(name, url, format), score, description, "DE"));
        return this;
    }

    public Task<IReadOnlyList<SemanticResult>> SearchAsync(string query, int k = 10, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SemanticResult>>(Online.Take(k).ToList());

    public Task<IReadOnlyList<SemanticResult>> SearchOfflineAsync(string query, int k = 10,
        double minScore = SemanticSearchService.DefaultOfflineFloor, CancellationToken ct = default)
    {
        OfflineCalls++;
        LastMinScore = minScore;
        return Task.FromResult<IReadOnlyList<SemanticResult>>(Offline.Take(k).ToList());
    }
}

/// <summary>Library that accepts everything and remembers nothing.</summary>
public sealed class FakeSongLibraryService : ISongLibraryService
{
    public List<SavedSong> Added { get; } = [];

    public void AddAndEnrich(SavedSong song) => Added.Add(song);
    public IReadOnlyList<SavedSong> GetAll(SongSource? source = null) => [];
    public void Remove(string path) { }
    public void BackfillInBackground() { }
}
