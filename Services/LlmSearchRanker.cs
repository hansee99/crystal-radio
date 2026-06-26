using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// LLM relevance re-ranker (Pattern A/B's optional re-rank step). Sends the prompt plus each
/// candidate's name + description to a cheap model and asks it to score true relevance and
/// drop non-matches — making local semantic hits and web finds comparable. Raw HttpClient,
/// no wrapper SDK. Knows nothing about playback.
/// </summary>
public sealed class LlmSearchRanker : ISearchRanker
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string DefaultModel = "claude-haiku-4-5"; // relevance judging is cheap
    private const int MaxTextChars = 320;                   // cap each description's length

    private const string SystemPrompt = """
        You rank internet radio stations by how well they match a user's request. You get the
        request and a numbered list of candidates, each with a name and a short description.

        Score each candidate 0.0–1.0 for how well it matches the REQUEST specifically — its
        genre, mood, era, activity, language, etc. — NOT merely whether it is a radio station.
        Be strict: a generic, unrelated, or only-loosely-related station scores low. Only
        include candidates that genuinely match (score >= 0.5).

        Respond with ONLY this JSON object (no prose, no markdown fences):
        { "ranked": [ { "id": <number>, "score": <0..1> } ] }
        Order best match first. If none genuinely match, return { "ranked": [] }.
        """;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    public LlmSearchRanker(HttpClient http, string? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey;
        _model = model;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<IReadOnlyList<RankVerdict>?> RankAsync(string prompt,
        IReadOnlyList<RankCandidate> candidates, int topK, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(prompt) || candidates is null || candidates.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append("Request: \"").Append(prompt).Append("\"\n\nCandidates:\n");
        foreach (var c in candidates)
        {
            var text = c.Text ?? "";
            if (text.Length > MaxTextChars)
                text = text[..MaxTextChars];
            sb.Append('[').Append(c.Id).Append("] ").Append(c.Name).Append(" — ").Append(text).Append('\n');
        }
        sb.Append("\nReturn at most ").Append(topK).Append(" best matches.");

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 512,
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
            return ParseRanked(text, candidates.Count, topK);
        }
        catch (Exception)
        {
            return null; // couldn't run — caller falls back
        }
    }

    private static IReadOnlyList<RankVerdict>? ParseRanked(string? modelText, int candidateCount, int topK)
    {
        var json = StripToJsonObject(modelText);
        if (json is null)
            return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        if (node?["ranked"] is not JsonArray ranked)
            return null;

        var verdicts = new List<RankVerdict>();
        var used = new HashSet<int>();
        foreach (var item in ranked)
        {
            if (item?["id"] is not JsonValue idv || !idv.TryGetValue<int>(out var id))
                continue;
            if (id < 0 || id >= candidateCount || !used.Add(id))
                continue;
            var score = item["score"] is JsonValue sv && sv.TryGetValue<double>(out var s) ? s : 0.0;
            verdicts.Add(new RankVerdict(id, score));
            if (verdicts.Count >= topK)
                break;
        }
        return verdicts; // may be empty (ran, but nothing matched)
    }

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
        catch (JsonException) { }
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
