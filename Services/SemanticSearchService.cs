using System.Diagnostics;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// Phase 2 semantic search. Embeds the query, scores it against the local vector index with
/// brute-force cosine similarity (dot product — vectors are L2-normalized), takes the top-K,
/// and resolves those stationuuids back to current, playable stations via Radio Browser.
/// All compute/DB work runs off the UI thread.
/// </summary>
public sealed class SemanticSearchService : ISemanticSearchService
{
    /// <summary>
    /// Cosine floor for the offline path: below this a row is dropped, not demoted.
    /// <para>
    /// Measured, not picked. #26 said to start from MainViewModel's SemanticThreshold (0.30) and
    /// tune against real queries, so the eight prompts below were scored against 60 real enriched
    /// stations. At 0.30 the false positives are exactly the failure mode #26 forbids — "hard rock
    /// and metal" admitted Adroit Jazz Underground at 0.319, "classical piano" admitted a smooth
    /// jazz station at 0.311, "polka" admitted five stations topping out at 0.324 in a catalog with
    /// no polka in it. Every genuine match scored well clear: Rock Antenne 0.390 for the metal
    /// prompt, SomaFM Groove Salad 0.478 for "ambient music for late-night coding", German
    /// stations 0.637+ for "german news radio". 0.35 sits in that gap with margin, and takes the
    /// clear mismatches to zero on all three prompts above.
    /// </para>
    /// Raising it further would start costing real matches (the lowest true positive seen was
    /// 0.390); lowering it re-admits the jazz-for-metal case. The LLM ranker still judges whatever
    /// clears this — the floor's job is not to hand it nonsense in the first place.
    /// </summary>
    public const double DefaultOfflineFloor = 0.35;

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

        AppLog.Debug($"[Semantic] '{query}': {scored.Count} hit(s); top={(scored.Count > 0 ? scored[0].Score.ToString("0.000") : "n/a")}");
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
                results.Add(new SemanticResult(cand.Station, s.Score, s.Description, cand.Country));
        return results;
    }

    public async Task<IReadOnlyList<SemanticResult>> SearchOfflineAsync(string query, int k = 10,
        double minScore = DefaultOfflineFloor, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
            return [];

        var results = await Task.Run(() =>
        {
            var queryVec = _embeddings.Embed(query);
            if (queryVec is null)
                return new List<SemanticResult>();

            // Rows carrying BOTH a vector and a stream url — the rest of the catalog can't help
            // here, however good its descriptions are.
            var rows = _store.GetPlayableRows(_embeddings.ModelId);
            var hits = new List<(SemanticResult Result, double Score)>();
            foreach (var row in rows)
            {
                if (row.Vector.Length != queryVec.Length)
                    continue;
                var score = Dot(queryVec, row.Vector);
                if (score < minScore)
                    continue; // dropped, not demoted — the point of the floor
                if (!TryBuildStation(row, out var station))
                    continue;
                hits.Add((new SemanticResult(station, score, row.Description, row.Country), score));
            }

            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (hits.Count > k)
                hits.RemoveRange(k, hits.Count - k);
            return hits.Select(h => h.Result).ToList();
        }, ct).ConfigureAwait(false);

        AppLog.Info($"[Semantic] offline '{query}': {results.Count} hit(s) at or above {minScore:0.00}"
                    + $"; top={(results.Count > 0 ? results[0].Score.ToString("0.000") : "n/a")}");
        return results;
    }

    /// <summary>
    /// Rebuild a playable <see cref="Station"/> from cached columns. The codec column holds the
    /// <see cref="StreamFormat"/> name the engine needs (it was written as such precisely so this
    /// needs no codec-string mapping), and anything that no longer parses to a format BASS can
    /// open is dropped rather than guessed at.
    /// </summary>
    private static bool TryBuildStation(PlayableStationRow row, out Station station)
    {
        station = null!;
        if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Url))
            return false;
        if (!Enum.TryParse<StreamFormat>(row.Codec, ignoreCase: true, out var format) ||
            format == StreamFormat.Other)
            return false;

        station = new Station(row.Name, row.Url, format, row.Description);
        return true;
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
