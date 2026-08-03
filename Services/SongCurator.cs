using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// Curates a playlist from the local library (Phase D). Pipeline, mirroring the station search:
/// embed the prompt → cosine top-K over the library's stored vectors → an LLM arranges the
/// shortlist into a sequence with a mood/energy arc and a one-line reason per track. Degrades
/// gracefully: no LLM key → semantic order without reasons; no embeddings yet → recency order.
/// Knows nothing about playback.
/// </summary>
public sealed class SongCurator : ISongCurator
{
    // Sonnet, not Haiku: arranging is light, but the selection half is genre-boundary judgment
    // the listener directly hears (the "Pantera in a classic-rock set" class of mistake) —
    // upgraded under the project's quality-over-token-cost principle for DJ/curation features.
    private const string DefaultModel = AnthropicApi.SonnetModel;
    private const int RecallPoolSize = 40;                  // cosine candidates handed to the LLM

    // Safety-net floor, not the primary quality mechanism (the LLM's own judgment, given real
    // signal, is) — cosine score is included in what the model sees below, this just keeps
    // genuinely-near-zero matches from being shown at all when the library is small. Starting
    // point borrowed from MainViewModel.SemanticThreshold (station descriptions); untuned for
    // song descriptions specifically — revisit if it turns out too strict/loose in practice.
    private const double MinRelevanceScore = 0.30;

    private const string SystemPrompt = """
        You are a music curator building a playlist from a listener's personal library for a
        free-text request. You get the request and a numbered list of candidate songs (title,
        artist, a short description, and a relevance score from semantic search — higher is a
        closer match, but the score is a starting point, not a verdict: read the description and
        judge genuine fit yourself, especially near genre boundaries.

        Choose ONLY the songs that genuinely fit the request and ARRANGE them into a good
        listening sequence — a sensible mood/energy arc, not just relevance order. A short
        playlist of songs that truly fit beats a longer one padded with songs that don't —
        drop anything that doesn't genuinely belong, even if that leaves very few songs. Only
        include a clearly-off-genre or off-vibe song if there is truly nothing else remotely
        close to the request in the candidate list. Order best-opening first.

        Respond with ONLY this JSON object (no prose, no markdown fences):
        { "playlist": [ { "id": <number>, "reason": "<= 8 words, why it fits / its role" } ] }
        If none genuinely fit, return { "playlist": [] } — an empty result is correct and expected
        when nothing in the list actually matches, not a failure to fix by including weak matches.
        """;

    private readonly HttpClient _http;
    private readonly LibraryStore _store;
    private readonly IEmbeddingProvider _embeddings;
    private readonly ApiKeySource _apiKey;
    private readonly string _model;

    public SongCurator(HttpClient http, LibraryStore store, IEmbeddingProvider embeddings,
        ApiKeySource? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _apiKey = apiKey ?? new ApiKeySource();
        _model = model;
    }

    public bool IsAvailable => _embeddings.IsAvailable;

    private bool CanRank => _apiKey.IsConfigured;

    public async Task<IReadOnlyList<CuratedSong>> CurateAsync(string prompt, int max = 20,
        IReadOnlyCollection<string>? excludeKeys = null, bool requireRelevance = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        // Semantic recall over the library vectors (off the UI thread).
        var ranked = await Task.Run(() => RankByCosine(prompt), ct).ConfigureAwait(false);

        // Drop already-used songs BEFORE the floor/pool cut, so a repeat call (DJ top-up)
        // reaches deeper into the library instead of re-offering the same top matches the
        // caller will just discard.
        if (excludeKeys is { Count: > 0 })
        {
            var exclude = excludeKeys as ISet<string>
                ?? new HashSet<string>(excludeKeys, StringComparer.OrdinalIgnoreCase);
            ranked = ranked.Where(r => !exclude.Contains($"{r.Row.Artist}|{r.Row.Title}")).ToList();
        }

        // No embeddings yet → let the user play their library anyway. Except under
        // requireRelevance, where recency says nothing about fit and the caller has a better
        // answer than the wrong song.
        if (ranked.Count == 0)
            return requireRelevance ? [] : FallbackRecent(max, excludeKeys);

        // Relevance floor: prefer genuinely-matching candidates over "closest available, however
        // weak." Only fall through to the unfiltered top-K when literally nothing clears the
        // floor — "unless nothing else is available" is a real carve-out, not the common case.
        var aboveFloor = ranked.Where(r => r.Score >= MinRelevanceScore).ToList();
        if (requireRelevance && aboveFloor.Count == 0)
            return []; // nothing fits; the caller bridges live rather than playing filler

        var pool = (aboveFloor.Count > 0 ? aboveFloor : ranked).Take(RecallPoolSize).ToList();

        // Let the LLM arrange a sequence with reasons; fall back to cosine order if it can't.
        if (CanRank)
        {
            var ordered = await ArrangeAsync(prompt, pool, max, ct).ConfigureAwait(false);
            if (ordered is not null)
                return ordered;
        }

        return pool.Take(max)
            .Select(r => new CuratedSong(r.Row.Path, r.Row.Title, r.Row.Artist, null))
            .ToList();
    }

    private List<(SavedSongVector Row, double Score)> RankByCosine(string prompt)
    {
        if (!_embeddings.IsAvailable)
            return [];
        var query = _embeddings.Embed(prompt);
        if (query is null)
            return [];

        var rows = _store.GetEmbeddedRows(_embeddings.ModelId);
        var scored = new List<(SavedSongVector, double)>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Vector.Length != query.Length)
                continue;
            scored.Add((row, Dot(query, row.Vector)));
        }
        scored.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return scored;
    }

    // UserSaved only: this fallback's whole purpose is "let the user play their library anyway"
    // when there's no embeddings index yet — a harvest-heavy cold start would defeat that if it
    // surfaced DJ-mode's own ephemeral harvested songs (freshest timestamps) ahead of songs the
    // user actually chose to save.
    private IReadOnlyList<CuratedSong> FallbackRecent(int max, IReadOnlyCollection<string>? excludeKeys)
    {
        var exclude = excludeKeys is { Count: > 0 }
            ? excludeKeys as ISet<string> ?? new HashSet<string>(excludeKeys, StringComparer.OrdinalIgnoreCase)
            : null;
        return _store.GetAll(SongSource.UserSaved)
            .Where(s => exclude is null || !exclude.Contains($"{s.Artist}|{s.Title}"))
            .Take(max)
            .Select(s => new CuratedSong(s.Path, s.Title, s.Artist, null))
            .ToList();
    }

    private async Task<IReadOnlyList<CuratedSong>?> ArrangeAsync(
        string prompt, List<(SavedSongVector Row, double Score)> pool, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Request: \"").Append(prompt).Append("\"\n\nCandidate songs (with semantic-search relevance score):\n");
        for (var i = 0; i < pool.Count; i++)
        {
            var (row, score) = pool[i];
            sb.Append('[').Append(i).Append("] ").Append(row.Artist).Append(" — ").Append(row.Title)
              .Append(" [score ").Append(score.ToString("0.00")).Append(']');
            if (!string.IsNullOrWhiteSpace(row.Description))
                sb.Append(" (").Append(Trim(row.Description!, 160)).Append(')');
            sb.Append('\n');
        }
        sb.Append("\nReturn at most ").Append(max).Append(" songs, best-opening first.");

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 900,
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = sb.ToString() }
            }
        };

        try
        {
            using var request = AnthropicApi.CreateRequest(_apiKey.Current, body);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var text = ExtractText(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ParsePlaylist(text, pool);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Curate] arrange failed: {ex.Message}");
            return null;
        }
    }

    private static IReadOnlyList<CuratedSong>? ParsePlaylist(
        string? modelText, List<(SavedSongVector Row, double Score)> pool)
    {
        var json = StripToJsonObject(modelText);
        if (json is null) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }

        if (node?["playlist"] is not JsonArray list)
            return null;

        var result = new List<CuratedSong>();
        var used = new HashSet<int>();
        foreach (var item in list)
        {
            if (item?["id"] is not JsonValue idv || !idv.TryGetValue<int>(out var id))
                continue;
            if (id < 0 || id >= pool.Count || !used.Add(id))
                continue;
            var reason = item["reason"] is JsonValue rv && rv.TryGetValue<string>(out var s) ? s : null;
            var row = pool[id].Row;
            result.Add(new CuratedSong(row.Path, row.Title, row.Artist, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()));
        }
        return result;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * (double)b[i];
        return sum;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private static string? ExtractText(string responseBody) => AnthropicApi.ExtractText(responseBody);

    private static string? StripToJsonObject(string? text) => AnthropicApi.StripToJsonObject(text);
}
