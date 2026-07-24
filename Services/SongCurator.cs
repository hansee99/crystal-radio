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
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string DefaultModel = "claude-haiku-4-5"; // light sequencing over a small pool
    private const int RecallPoolSize = 40;                  // cosine candidates handed to the LLM

    private const string SystemPrompt = """
        You are a music curator building a playlist from a listener's personal library for a
        free-text request. You get the request and a numbered list of candidate songs (title,
        artist, and a short description), pre-filtered by relevance.

        Choose the songs that genuinely fit the request and ARRANGE them into a good listening
        sequence — a sensible mood/energy arc, not just relevance order. Drop songs that don't
        fit rather than padding. Order best-opening first.

        Respond with ONLY this JSON object (no prose, no markdown fences):
        { "playlist": [ { "id": <number>, "reason": "<= 8 words, why it fits / its role" } ] }
        If none fit, return { "playlist": [] }.
        """;

    private readonly HttpClient _http;
    private readonly LibraryStore _store;
    private readonly IEmbeddingProvider _embeddings;
    private readonly string? _apiKey;
    private readonly string _model;

    public SongCurator(HttpClient http, LibraryStore store, IEmbeddingProvider embeddings,
        string? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _apiKey = apiKey;
        _model = model;
    }

    public bool IsAvailable => _embeddings.IsAvailable;

    private bool CanRank => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<IReadOnlyList<CuratedSong>> CurateAsync(string prompt, int max = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        // Semantic recall over the library vectors (off the UI thread).
        var ranked = await Task.Run(() => RankByCosine(prompt), ct).ConfigureAwait(false);
        if (ranked.Count == 0)
            return FallbackRecent(max); // no embeddings yet → let the user play their library anyway

        var pool = ranked.Take(RecallPoolSize).ToList();

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

    private IReadOnlyList<CuratedSong> FallbackRecent(int max) =>
        _store.GetAll().Take(max)
            .Select(s => new CuratedSong(s.Path, s.Title, s.Artist, null))
            .ToList();

    private async Task<IReadOnlyList<CuratedSong>?> ArrangeAsync(
        string prompt, List<(SavedSongVector Row, double Score)> pool, int max, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Request: \"").Append(prompt).Append("\"\n\nCandidate songs:\n");
        for (var i = 0; i < pool.Count; i++)
        {
            var r = pool[i].Row;
            sb.Append('[').Append(i).Append("] ").Append(r.Artist).Append(" — ").Append(r.Title);
            if (!string.IsNullOrWhiteSpace(r.Description))
                sb.Append(" (").Append(Trim(r.Description!, 160)).Append(')');
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
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var text = ExtractText(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ParsePlaylist(text, pool);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Curate] arrange failed: {ex.Message}");
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

    private static string? ExtractText(string responseBody)
    {
        try
        {
            var node = JsonNode.Parse(responseBody);
            if (node?["content"] is not JsonArray content) return null;
            foreach (var block in content)
                if (block?["type"]?.GetValue<string>() == "text")
                    return block["text"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private static string? StripToJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = trimmed.IndexOf('\n');
            if (nl >= 0) trimmed = trimmed[(nl + 1)..];
            var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) trimmed = trimmed[..fence];
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start < 0 || end <= start ? null : trimmed[start..(end + 1)];
    }
}
