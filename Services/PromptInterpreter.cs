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
    // CLAUDE.md calls for a small/fast model for query translation; Haiku is plenty.
    private const string DefaultModel = AnthropicApi.HaikuModel;

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

        var requestBody = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["system"] = SystemPrompt,
            ["messages"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = prompt }
            }
        };

        using var request = AnthropicApi.CreateRequest(_apiKey, requestBody);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API returned {(int)response.StatusCode}: {AnthropicApi.Truncate(body)}");

        var text = ExtractText(body);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return TryParseQuery(text);
    }

    /// <summary>Pulls the text out of the Messages API response.</summary>
    private static string? ExtractText(string responseBody) => AnthropicApi.ExtractText(responseBody);

    /// <summary>Strips optional ```json fences, isolates the JSON object, and deserializes.</summary>
    private static StationSearchQuery? TryParseQuery(string text)
    {
        var json = AnthropicApi.StripToJsonObject(text);
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
}
