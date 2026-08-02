using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// The one place the Anthropic Messages API plumbing lives: endpoint/version constants, the
/// model catalog every service draws from, request construction, and the response-parsing
/// helpers (first-text-block extraction, markdown-fence/JSON-object stripping) that were
/// previously duplicated per service. Each service keeps its own system prompt, body shape,
/// and error policy — this class only removes the mechanical drift between them.
/// </summary>
public static class AnthropicApi
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string Version = "2023-06-01";

    // Model catalog — the only place model ids live. Per CLAUDE.md, verify these against
    // https://docs.claude.com/en/api/overview when bumping; don't trust memory.
    //
    // Haiku: one-shot translation/description/judging — PromptInterpreter, LlmSearchRanker,
    //   EnrichmentService, SongLibraryService, DjIntroService.
    // Sonnet: multi-step reasoning or judgment users directly hear the results of —
    //   AgenticSearchService (web+catalog agent loop), TrackInfoService (web synthesis),
    //   SongCurator (genre-boundary playlist judgment; upgraded from Haiku under the project's
    //   quality-over-token-cost principle for DJ/curation features).
    public const string HaikuModel = "claude-haiku-4-5";
    public const string SonnetModel = "claude-sonnet-5";

    /// <summary>Builds a POST request for the Messages API with the auth/version headers set.
    /// The caller owns (disposes) the returned request.</summary>
    public static HttpRequestMessage CreateRequest(string? apiKey, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", Version);
        return request;
    }

    /// <summary>Concatenates all text blocks from a Messages API response body (the raw JSON
    /// string). Returns null when the body isn't parseable or has no text blocks.</summary>
    public static string? ExtractText(string responseBody)
    {
        try
        {
            return ExtractText(JsonNode.Parse(responseBody)?["content"]);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Concatenates all text blocks from an already-parsed <c>content</c> array.
    /// Returns null when there are none.</summary>
    public static string? ExtractText(JsonNode? content)
    {
        if (content is not JsonArray arr)
            return null;
        var sb = new StringBuilder();
        foreach (var block in arr)
            if (Str(block?["type"]) == "text")
                sb.Append(Str(block?["text"]));
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Isolates the JSON object in model output: strips an optional ```json fence, then takes
    /// the outermost { ... } so stray prose on either side can't break parsing. Returns null
    /// when no object is present.
    /// </summary>
    public static string? StripToJsonObject(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];
            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
                trimmed = trimmed[..closingFence];
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start < 0 || end <= start ? null : trimmed[start..(end + 1)];
    }

    /// <summary>Reads a string value from a JsonNode, or null.</summary>
    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Truncates a (typically error-body) string for logging.</summary>
    public static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
