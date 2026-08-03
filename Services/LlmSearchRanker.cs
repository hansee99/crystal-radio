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
    private const string DefaultModel = AnthropicApi.HaikuModel; // relevance judging is cheap
    private const int MaxTextChars = 320;                        // cap each description's length

    // The system prompt tells the model to only include candidates scoring >= 0.5, but that is
    // an instruction, not a guarantee — this enforces the same floor client-side so a loose
    // match (or an unparseable score, which reads as 0.0) can never slip through on a day the
    // model ignores the strictness rule. Keep the two values in sync with the prompt.
    private const double MinScore = 0.5;

    private const string SystemPrompt = """
        You rank internet radio stations by how well they match a user's request. You get the
        request and a numbered list of candidates, each with a name, a short description, and
        (when known) an origin country.

        Score each candidate 0.0–1.0 for how well it matches the REQUEST specifically — its
        genre, mood, era, activity, language, etc. — NOT merely whether it is a radio station.
        Be strict: a generic, unrelated, or only-loosely-related station scores low. Only
        include candidates that genuinely match (score >= 0.5).

        Use the origin country to honour the request's geographic intent:
        - If the request names or clearly implies a specific country, region, or language,
          prefer stations that fit it and penalise ones that don't.
        - If the request implies international or broad scope (e.g. "international", "world",
          "global", or no geographic hint at all), prefer VARIETY across countries and avoid
          returning a set dominated by a single country.

        Respond with ONLY this JSON object (no prose, no markdown fences):
        { "ranked": [ { "id": <number>, "score": <0..1> } ] }
        Order best match first. If none genuinely match, return { "ranked": [] }.
        """;

    private readonly HttpClient _http;
    private readonly ApiKeySource _apiKey;
    private readonly string _model;

    public LlmSearchRanker(HttpClient http, ApiKeySource? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey ?? new ApiKeySource();
        _model = model;
    }

    public bool IsConfigured => _apiKey.IsConfigured;

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
            sb.Append('[').Append(c.Id).Append("] ").Append(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Country))
                sb.Append(" (").Append(c.Country).Append(')');
            sb.Append(" — ").Append(text).Append('\n');
        }
        sb.Append("\nReturn at most ").Append(topK).Append(" best matches.");

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
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

            var text = AnthropicApi.ExtractText(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return ParseRanked(text, candidates.Count, topK);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancelled — propagate rather than degrade to the heuristic
        }
        catch (Exception)
        {
            return null; // couldn't run — caller falls back
        }
    }

    private static IReadOnlyList<RankVerdict>? ParseRanked(string? modelText, int candidateCount, int topK)
    {
        var json = AnthropicApi.StripToJsonObject(modelText);
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
            if (score < MinScore)
                continue; // enforce the prompt's own floor client-side
            verdicts.Add(new RankVerdict(id, score));
            if (verdicts.Count >= topK)
                break;
        }
        return verdicts; // may be empty (ran, but nothing matched)
    }
}
