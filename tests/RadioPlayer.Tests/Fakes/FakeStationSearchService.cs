using RadioPlayer.Models;
using RadioPlayer.Services;

namespace RadioPlayer.Tests.Fakes;

/// <summary>
/// Stands in for the Radio Browser directory. Holds a fixed set of stations keyed by uuid and
/// answers any query with all of them — the tests using it care about which uuids the caller
/// treats as trustworthy, not about matching semantics.
/// </summary>
public sealed class FakeStationSearchService : IStationSearchService
{
    private readonly List<StationCandidate> _candidates = [];

    /// <summary>Queries that were actually executed, so a test can assert the tool ran.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>When set, every lookup throws this — the "directory is down" case.</summary>
    public Exception? Fault { get; set; }

    public FakeStationSearchService Add(string uuid, string name = "Station", string url = "http://example/s")
    {
        _candidates.Add(new StationCandidate(uuid, new Station(name, url, StreamFormat.Mp3), 128, "DE", "jazz", null));
        return this;
    }

    private IReadOnlyList<StationCandidate> Answer(string call)
    {
        Calls.Add(call);
        if (Fault is not null) throw Fault;
        return _candidates;
    }

    public Task<IReadOnlyList<StationCandidate>> SearchCandidatesAsync(
        StationSearchQuery query, int offset = 0, CancellationToken ct = default) =>
        Task.FromResult(Answer($"tags:{string.Join('|', query.Tags ?? [])} name:{query.Name}"));

    public Task<IReadOnlyList<StationCandidate>> SearchByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Answer($"byName:{name}"));

    public Task<IReadOnlyList<StationCandidate>> GetByUuidsAsync(
        IEnumerable<string> uuids, CancellationToken ct = default) =>
        Task.FromResult(Answer("byUuids"));

    public Task<IReadOnlyList<StationCandidate>> GetPopularAsync(
        int count, bool extendedInfoOnly, CancellationToken ct = default) =>
        Task.FromResult(Answer("popular"));

    public Task<IReadOnlyList<Station>> SearchAsync(StationSearchQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Station>>(Answer("search").Select(c => c.Station).ToList());
}

/// <summary>Enrichment that remembers nothing — the cold-cache case, which is the norm.</summary>
public sealed class FakeEnrichmentService : IEnrichmentService
{
    private readonly Dictionary<string, EnrichmentRecord> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FakeEnrichmentService Cache(string uuid, string description)
    {
        _cache[uuid] = new EnrichmentRecord(uuid, description, null, EnrichmentSource.TagsOnly, DateTimeOffset.UtcNow);
        return this;
    }

    public void EnrichInBackground(IEnumerable<StationCandidate> candidates) { }
    public EnrichmentRecord? GetCached(string stationUuid) => _cache.GetValueOrDefault(stationUuid);
    public void BackfillEmbeddingsInBackground() { }
}
