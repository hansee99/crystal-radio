using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// Pattern B. Runs an Anthropic agentic loop with two tools:
///   • <c>web_search</c> — server-executed (Anthropic runs it; results return automatically),
///   • <c>search_radio_browser</c> — client-executed here, wrapping <see cref="IStationSearchService"/>.
/// The model discovers station names via the web, then MUST resolve each to a real, playable
/// station via search_radio_browser. We validate the final answer by stationuuid against the
/// candidates we actually fetched, so a hallucinated name/URL can never reach playback.
/// </summary>
public sealed class AgenticSearchService : IAgenticSearchService
{
    // Stronger model than Pattern A — this is multi-step reasoning over web + catalog.
    private const string DefaultModel = AnthropicApi.SonnetModel;
    private const int MaxIterations = 6;   // hard cap on messages.create round-trips
    private const int WebSearchMaxUses = 3;
    private const int MaxCandidatesPerCall = 20;
    private const int DefaultMaxResults = 6; // visible-search page size; DJ sourcing asks for more

    // {MAX} = max stations in the final answer — sized per caller (the visible search wants a
    // page; DJ sourcing wants enough for a harvester pool + reserve).
    private const string SystemPromptTemplate = """
        You help a user find REAL, PLAYABLE internet radio stations. You have two tools:

        1. web_search — discover station *names and descriptions* from blogs, forums, and
           "best of" lists. Use it to turn a vibe/era/scene/"the station people recommend"
           into concrete station names or genres.
        2. search_radio_browser — resolve those findings into real, playable stations from
           the Radio Browser directory. It returns candidates (each with a stationuuid)
           already filtered to streams this player can actually play.

        Rules:
        - NEVER invent or output a station's stream URL, and never pick a station straight
          from a web page. A station only counts once search_radio_browser has returned it.
        - For every candidate you want to recommend, you MUST have seen its stationuuid in a
          search_radio_browser result. Call search_radio_browser as many times as needed
          (by name, or by genre tags) to resolve your web findings.
        - Prefer well-known, higher-bitrate, popular stations that match the user's intent.

        When done, respond with ONLY this JSON object — no prose, no markdown fences:
        {
          "stations": [
            { "stationuuid": "string", "reason": "one short sentence on why it fits" }
          ]
        }
        Order best first, at most {MAX}. Use only stationuuids returned by search_radio_browser.
        If nothing suitable was found, return {"stations": []}.
        """;

    private readonly HttpClient _http;
    private readonly IStationSearchService _search;
    private readonly IEnrichmentService _enrichment;
    private readonly ApiKeySource _apiKey;
    private readonly string _model;

    public AgenticSearchService(HttpClient http, IStationSearchService search,
        IEnrichmentService enrichment, ApiKeySource? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _enrichment = enrichment ?? throw new ArgumentNullException(nameof(enrichment));
        _apiKey = apiKey ?? new ApiKeySource();
        _model = model;
    }

    public bool IsConfigured => _apiKey.IsConfigured;

    public async Task<IReadOnlyList<RankedStation>> SearchAsync(string prompt,
        int maxResults = DefaultMaxResults, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("ANTHROPIC_API_KEY is not set.");
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var systemPrompt = SystemPromptTemplate.Replace("{MAX}", Math.Max(1, maxResults).ToString());

        // Everything search_radio_browser returned, keyed by stationuuid — the trust set.
        var fetched = new Dictionary<string, StationCandidate>(StringComparer.OrdinalIgnoreCase);

        // History accumulates unparented nodes; we deep-clone into a fresh array per call.
        var messages = new List<JsonNode>
        {
            new JsonObject { ["role"] = "user", ["content"] = prompt }
        };

        var finalText = string.Empty;

        for (var i = 0; i < MaxIterations; i++)
        {
            var response = await CallApiAsync(systemPrompt, messages, ct);
            var content = response["content"];

            // Echo the assistant turn back verbatim next round — including the encrypted
            // web_search result blocks — for multi-turn continuity.
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content?.DeepClone() });

            var stop = Str(response["stop_reason"]);
            var blockTypes = (content as JsonArray)?.Select(b => Str(b?["type"])).Where(t => t is not null);
            AppLog.Debug($"[PatternB] iter {i}: stop_reason={stop}; blocks=[{string.Join(", ", blockTypes ?? [])}]");

            if (stop == "tool_use")
            {
                var toolResults = new JsonArray();
                foreach (var block in content as JsonArray ?? [])
                {
                    if (Str(block?["type"]) != "tool_use" || Str(block?["name"]) != "search_radio_browser")
                        continue; // web_search is server-side; nothing to execute here

                    var id = Str(block?["id"]) ?? "";
                    AppLog.Debug($"[PatternB] -> search_radio_browser({block?["input"]?.ToJsonString()})");
                    var resultJson = await ExecuteRadioBrowserAsync(block?["input"], fetched, ct);
                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = id,
                        ["content"] = resultJson
                    });
                }

                if (toolResults.Count == 0)
                    break; // tool_use with no client tool we handle — avoid a spin

                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                continue;
            }

            if (stop == "pause_turn")
                continue; // server-tool loop hit its cap; re-call to resume

            // end_turn (or any terminal stop) — the final answer is here.
            finalText = ExtractText(content);
            break;
        }

        var ranked = ParseFinalAnswer(finalText, fetched);
        AppLog.Debug($"[PatternB] fetched {fetched.Count} candidate(s); validated {ranked.Count} for playback.");
        return ranked;
    }

    private async Task<JsonObject> CallApiAsync(string systemPrompt, List<JsonNode> messages, CancellationToken ct)
    {
        var msgArray = new JsonArray();
        foreach (var m in messages)
            msgArray.Add(m.DeepClone());

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 2048,
            ["system"] = systemPrompt,
            ["tools"] = BuildTools(),
            ["messages"] = msgArray
        };

        using var request = AnthropicApi.CreateRequest(_apiKey.Current, body);
        using var response = await _http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API returned {(int)response.StatusCode}: {AnthropicApi.Truncate(responseBody)}");

        return JsonNode.Parse(responseBody) as JsonObject
               ?? throw new HttpRequestException("Anthropic API returned an unexpected response.");
    }

    private static JsonArray BuildTools() =>
    [
        new JsonObject
        {
            ["type"] = "web_search_20250305",
            ["name"] = "web_search",
            ["max_uses"] = WebSearchMaxUses
        },
        new JsonObject
        {
            ["name"] = "search_radio_browser",
            ["description"] = "Search the Radio Browser directory for real, playable internet "
                + "radio stations. Returns candidates already filtered to streams this player "
                + "can play (MP3/AAC, non-HLS), each with a stationuuid. Resolve station names "
                + "or genres you discovered into verified stations. Call repeatedly as needed.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["tags"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "genre/mood tags, e.g. [\"synthwave\"]"
                    },
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "a specific station name to resolve"
                    },
                    ["country"] = new JsonObject { ["type"] = "string" },
                    ["language"] = new JsonObject { ["type"] = "string" },
                    ["bitrateMin"] = new JsonObject { ["type"] = "integer" },
                    ["order"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "votes", "clickcount", "name" }
                    }
                }
            }
        }
    ];

    private async Task<string> ExecuteRadioBrowserAsync(JsonNode? input, Dictionary<string, StationCandidate> fetched,
        CancellationToken ct)
    {
        var name = Str(input?["name"]);
        var tags = (input?["tags"] as JsonArray)?
            .Select(t => Str(t))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToArray() ?? [];

        IReadOnlyList<StationCandidate> candidates;
        try
        {
            if (!string.IsNullOrWhiteSpace(name) && tags.Length == 0)
            {
                candidates = await _search.SearchByNameAsync(name, ct);
            }
            else
            {
                var query = new StationSearchQuery
                {
                    Tags = tags,
                    Name = name,
                    Country = Str(input?["country"]),
                    Language = Str(input?["language"]),
                    BitrateMin = Int(input?["bitrateMin"]),
                    Order = Str(input?["order"]) ?? "votes"
                };
                candidates = await _search.SearchCandidatesAsync(query, 0, ct);
            }
        }
        catch (Exception ex)
        {
            // Return the error to the model as a tool result so it can adapt, not crash the loop.
            return new JsonObject { ["error"] = ex.Message }.ToJsonString();
        }

        AppLog.Debug($"[PatternB]    <- {candidates.Count} playable candidate(s)");

        // Lazily enrich these candidates in the background — never blocks this result.
        _enrichment.EnrichInBackground(candidates);

        var arr = new JsonArray();
        foreach (var c in candidates.Take(MaxCandidatesPerCall))
        {
            fetched[c.StationUuid] = c;
            var obj = new JsonObject
            {
                ["stationuuid"] = c.StationUuid,
                ["name"] = c.Station.Name,
                ["codec"] = c.Station.Format.ToString(),
                ["bitrate"] = c.Bitrate,
                ["country"] = c.Country,
                ["tags"] = c.Tags
            };

            // If we already have a distilled description (from a prior search), give the
            // model real text to re-rank over instead of just thin tags.
            var cached = _enrichment.GetCached(c.StationUuid);
            if (cached is not null)
                obj["description"] = cached.Description;

            arr.Add(obj);
        }
        return arr.ToJsonString();
    }

    private IReadOnlyList<RankedStation> ParseFinalAnswer(string text, Dictionary<string, StationCandidate> fetched)
    {
        var json = StripToJsonObject(text);
        if (json is null)
            return [];

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return []; }

        if (node?["stations"] is not JsonArray stations)
            return [];

        var ranked = new List<RankedStation>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in stations)
        {
            var uuid = Str(s?["stationuuid"]);
            // Validation gate: only stations WE fetched, deduped.
            if (uuid is null || !fetched.TryGetValue(uuid, out var candidate) || !used.Add(uuid))
                continue;
            // The model normally supplies a reason; if it didn't, fall back so the result
            // still shows a description (cached enriched text -> tags -> generic line).
            var reason = Str(s?["reason"]);
            if (string.IsNullOrWhiteSpace(reason))
                reason = _enrichment.GetCached(uuid)?.Description ?? FormatTags(candidate.Tags) ?? "Matches your search.";
            ranked.Add(new RankedStation(candidate.Station, reason));
        }
        return ranked;
    }

    private static string ExtractText(JsonNode? content) => AnthropicApi.ExtractText(content) ?? string.Empty;

    private static string? StripToJsonObject(string text) => AnthropicApi.StripToJsonObject(text);

    private static string? Str(JsonNode? n) => AnthropicApi.Str(n);

    private static int Int(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static string? FormatTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return null;
        var parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join(" · ", parts.Take(5));
    }
}
