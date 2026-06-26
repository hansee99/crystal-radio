using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// LLM query router: a cheap one-shot Haiku call that decides whether a search prompt is a
/// literal directory lookup (Pattern A) or a fuzzy/semantic "vibe" query (semantic + web).
/// Replaces the brittle keyword/length heuristic with the model's judgement; on any failure
/// the caller falls back to that heuristic. Raw HttpClient, no wrapper SDK. No playback knowledge.
/// </summary>
public sealed class LlmQueryClassifier : IQueryClassifier
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string DefaultModel = "claude-haiku-4-5"; // classification is cheap

    private const string SystemPrompt = """
        You route internet-radio search queries to one of two engines:

        - "literal": the user named a specific station or gave concrete directory attributes a
          tag/name/country lookup can satisfy directly — e.g. "BBC Radio 1", "German news radio",
          "Jazz FM", "a classical station from France".
        - "fuzzy": the user described a vibe, mood, activity, era, or scene rather than a concrete
          station or attribute — e.g. "roadtrip music", "something dreamy for late-night coding",
          "stations like the ones in GTA Vice City".

        When unsure, prefer "fuzzy".

        Respond with ONLY this JSON object, no prose and no markdown fences:
        { "fuzzy": true }
        (use false for a literal lookup)
        """;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    public LlmQueryClassifier(HttpClient http, string? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey;
        _model = model;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<bool?> IsFuzzyAsync(string prompt, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(prompt))
            return null;

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 16,
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
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
            return ParseFuzzy(text);
        }
        catch (Exception)
        {
            return null; // couldn't run — caller falls back to its heuristic
        }
    }

    private static bool? ParseFuzzy(string? modelText)
    {
        var json = StripToJsonObject(modelText);
        if (json is null)
            return null;

        try
        {
            if (JsonNode.Parse(json)?["fuzzy"] is JsonValue v && v.TryGetValue<bool>(out var fuzzy))
                return fuzzy;
        }
        catch (System.Text.Json.JsonException) { }
        return null;
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
