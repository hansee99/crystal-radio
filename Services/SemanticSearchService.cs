using System.Diagnostics;

namespace RadioPlayer.Services;

/// <summary>
/// Phase 2 semantic search. Embeds the query, scores it against the local vector index with
/// brute-force cosine similarity (dot product — vectors are L2-normalized), takes the top-K,
/// and resolves those stationuuids back to current, playable stations via Radio Browser.
/// All compute/DB work runs off the UI thread.
/// </summary>
public sealed class SemanticSearchService : ISemanticSearchService
{
    private readonly IEmbeddingProvider _embeddings;
    private readonly EnrichmentStore _store;
    private readonly IStationSearchService _stationSearch;

    public SemanticSearchService(IEmbeddingProvider embeddings, EnrichmentStore store,
        IStationSearchService stationSearch)
    {
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _stationSearch = stationSearch ?? throw new ArgumentNullException(nameof(stationSearch));
    }

    public bool IsAvailable => _embeddings.IsAvailable;

    public async Task<IReadOnlyList<SemanticResult>> SearchAsync(string query, int k = 10, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
            return [];

        // Embed + load index + cosine, all on a worker thread (never the UI thread).
        var scored = await Task.Run(() =>
        {
            var queryVec = _embeddings.Embed(query);
            if (queryVec is null)
                return new List<Scored>();

            var rows = _store.GetEmbeddedRows(_embeddings.ModelId);
            var hits = new List<Scored>(rows.Count);
            foreach (var row in rows)
            {
                if (row.Vector.Length != queryVec.Length)
                    continue; // dimension mismatch (shouldn't happen for one model) — skip
                hits.Add(new Scored(row.StationUuid, row.Description, Dot(queryVec, row.Vector)));
            }
            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (hits.Count > k)
                hits.RemoveRange(k, hits.Count - k);
            return hits;
        }, ct).ConfigureAwait(false);

        Debug.WriteLine($"[Semantic] '{query}': {scored.Count} hit(s); top={(scored.Count > 0 ? scored[0].Score.ToString("0.000") : "n/a")}");
        if (scored.Count == 0)
            return [];

        // Resolve the chosen uuids to live, playable stations (drops any now-broken/HLS ones).
        var candidates = await _stationSearch.GetByUuidsAsync(scored.Select(s => s.Uuid), ct).ConfigureAwait(false);
        var byUuid = new Dictionary<string, StationCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
            byUuid.TryAdd(c.StationUuid, c);

        var results = new List<SemanticResult>(scored.Count);
        foreach (var s in scored) // preserve score order
            if (byUuid.TryGetValue(s.Uuid, out var cand))
                results.Add(new SemanticResult(cand.Station, s.Score, s.Description));
        return results;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * (double)b[i];
        return sum;
    }

    private readonly record struct Scored(string Uuid, string Description, double Score);
}
