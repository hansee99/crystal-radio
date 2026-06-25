using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadioPlayer.Services;

/// <summary>
/// Pattern A interpreter: calls the Anthropic Messages API directly (raw HttpClient, no
/// wrapper SDK) and asks the model to translate a fuzzy prompt into the fixed
/// <see cref="StationSearchQuery"/> schema. The model never names stations or URLs.
/// </summary>
public sealed class PromptInterpreter : IPromptInterpreter
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    // CLAUDE.md calls for a small/fast model for query translation; Haiku is plenty.
    // (The provider-agnostic seam means this is the only place the model id lives.)
    private const string DefaultModel = "claude-haiku-4-5";

    private const string SystemPrompt = """
        You translate a user's natural-language request for internet radio into structured
        search parameters for the Radio Browser directory. You do NOT know which stations
        exist and you must NEVER invent station names or stream URLs — you only produce
        search parameters that the application will use to query the directory itself.

        Respond with ONLY a single JSON object — no prose, no explanation, no markdown code
        fences — matching exactly this shape:
        {
          "tags": ["string"],   // 1-4 lowercase genre/mood keywords, e.g. "jazz", "ambient"; [] if none
          "name": string|null,  // only if the user named a specific station, else null
          "country": string|null,   // English country name if clearly implied, else null
          "language": string|null,  // language if clearly implied, else null
          "bitrateMin": 0,      // minimum kbps the user asked for, else 0
          "order": "votes"      // one of: "votes", "clickcount", "name"
        }

        Map moods and descriptions to concise genre tags (e.g. "music for late-night coding"
        -> ["ambient","chillout"]). Prefer broad, well-known tags over niche ones.
        """;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PromptInterpreter(HttpClient http, string? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey;
        _model = model;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<StationSearchQuery?> InterpretAsync(string prompt, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var requestBody = new
        {
            model = _model,
            max_tokens = 1024,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API returned {(int)response.StatusCode}: {Truncate(body)}");

        var text = ExtractText(body);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return TryParseQuery(text);
    }

    /// <summary>Pulls the first text block out of the Messages API response.</summary>
    private static string? ExtractText(string responseBody)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<AnthropicResponse>(responseBody, JsonOptions);
            var block = parsed?.Content?.FirstOrDefault(b => b.Type == "text" && !string.IsNullOrEmpty(b.Text));
            return block?.Text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Strips optional ```json fences, isolates the JSON object, and deserializes.</summary>
    private static StationSearchQuery? TryParseQuery(string text)
    {
        var json = StripToJsonObject(text);
        if (json is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<StationSearchQuery>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StripToJsonObject(string text)
    {
        var trimmed = text.Trim();

        // Drop a leading ```json / ``` fence and its closing fence if present.
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed[(firstNewline + 1)..];
            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
                trimmed = trimmed[..closingFence];
        }

        // Isolate the outermost { ... } so stray text on either side can't break parsing.
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        return trimmed[start..(end + 1)];
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed class AnthropicResponse
    {
        [JsonPropertyName("content")] public List<ContentBlock>? Content { get; set; }
    }

    private sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
