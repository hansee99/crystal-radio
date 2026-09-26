using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// Generates an "About this track" briefing via the Anthropic Messages API with the server-side
/// <c>web_search</c> tool (Anthropic runs the searches; results return automatically). The model
/// gathers facts from varied sources and returns a small, fixed JSON shape that maps to
/// <see cref="TrackInfo"/>. Raw HttpClient, no wrapper SDK. Knows nothing about playback.
///
/// Server-side web search means there is no client tool to execute — we only re-call on
/// <c>pause_turn</c> (the search loop paused at its cap) until the model emits its final answer.
/// </summary>
public sealed class TrackInfoService : ITrackInfoService
{
    // Sonnet: this is synthesis over multiple web sources, not a one-shot translation.
    private const string DefaultModel = AnthropicApi.SonnetModel;
    private const int MaxIterations = 5;       // hard cap on messages.create round-trips
    private const int WebSearchMaxUses = 4;
    private const int CacheCap = 48;           // per-session, soft FIFO bound

    private const string SystemPrompt = """
        You write a short, friendly briefing about a piece of music currently playing on internet
        radio. You are given the track title, the artist (may be blank), and the station name.

        Use the web_search tool to gather facts from a VARIETY of reputable sources — music
        databases, magazines, interviews, label/artist pages, reviews — not Wikipedia alone.
        Cross-check where you can.

        Produce THREE parts, all concise — the UI panel is small, so do not pad:
        - song: 2-4 sentences about THIS track: year/album, who wrote or produced it, its style,
          and what it is about or why it was made.
        - artist: 2-3 sentences about the artist: origin, era, genre/musical direction, what they
          are known for.
        - notable: 2-4 short bullets of genuinely interesting facts or trivia — the story behind
          the song, chart/cultural moments, surprising details. One sentence each.

        Be accurate; if unsure of a fact, leave it out. If you cannot confidently identify the
        track at all, return {"song":"","artist":"","notable":[]}.

        Write PLAIN TEXT only — no citation markers, footnotes, reference numbers, or HTML/XML
        tags (e.g. no <cite> tags) anywhere in the values.

        Respond with ONLY this JSON object — no prose, no markdown fences:
        { "song": "string", "artist": "string", "notable": ["string"] }
        """;

    private readonly HttpClient _http;
    private readonly ApiKeySource _apiKey;
    private readonly string _model;

    // Per-session cache keyed by normalized "artist|title". ConcurrentDictionary because a
    // background-thread continuation may read/write while the UI thread queues another request.
    private readonly ConcurrentDictionary<string, TrackInfo> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public TrackInfoService(HttpClient http, ApiKeySource? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey ?? new ApiKeySource();
        _model = model;
    }

    public bool IsConfigured => _apiKey.IsConfigured;

    public async Task<TrackInfo?> GetTrackInfoAsync(string title, string? artist, string? station,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            return null;

        var key = CacheKey(title, artist);
        if (!forceRefresh && _cache.TryGetValue(key, out var cached))
            return cached;

        var info = await GenerateAsync(title, artist, station, ct).ConfigureAwait(false);
        if (info is not null)
            StoreInCache(key, info);
        return info;
    }

    private async Task<TrackInfo?> GenerateAsync(string title, string? artist, string? station,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Now playing: \"").Append(title).Append('"');
        if (!string.IsNullOrWhiteSpace(artist))
            sb.Append(" by ").Append(artist);
        if (!string.IsNullOrWhiteSpace(station))
            sb.Append(" (on the station \"").Append(station).Append("\")");
        sb.Append('.');

        var messages = new List<JsonNode>
        {
            new JsonObject { ["role"] = "user", ["content"] = sb.ToString() }
        };

        var finalText = string.Empty;
        for (var i = 0; i < MaxIterations; i++)
        {
            var response = await CallApiAsync(messages, ct).ConfigureAwait(false);
            var content = response["content"];

            // Echo the assistant turn back verbatim next round — including the encrypted
            // web_search result blocks — for multi-turn continuity.
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content?.DeepClone() });

            var stop = Str(response["stop_reason"]);
            AppLog.Debug($"[TrackInfo] iter {i}: stop_reason={stop}");

            if (stop == "pause_turn")
                continue; // server-tool loop hit its cap; re-call to resume

            // end_turn (or any terminal stop) — the final answer is here.
            finalText = ExtractText(content);
            break;
        }

        return ParseTrackInfo(finalText);
    }

    private async Task<JsonObject> CallApiAsync(List<JsonNode> messages, CancellationToken ct)
    {
        var msgArray = new JsonArray();
        foreach (var m in messages)
            msgArray.Add(m.DeepClone());

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["system"] = SystemPrompt,
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = WebSearchMaxUses
                }
            },
            ["messages"] = msgArray
        };

        using var request = AnthropicApi.CreateRequest(_apiKey.Current, body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Anthropic API returned {(int)response.StatusCode}: {AnthropicApi.Truncate(responseBody)}");

        return JsonNode.Parse(responseBody) as JsonObject
               ?? throw new HttpRequestException("Anthropic API returned an unexpected response.");
    }

    private static TrackInfo? ParseTrackInfo(string text)
    {
        var json = StripToJsonObject(text);
        if (json is null)
            return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        var song = Clean(Str(node?["song"]));
        var artist = Clean(Str(node?["artist"]));
        var notable = (node?["notable"] as JsonArray)?
            .Select(n => Clean(Str(n)))
            .Where(s => s.Length > 0)
            .ToList() ?? [];

        // The model signals "couldn't identify" with all-empty fields — treat as no result.
        if (song.Length == 0 && artist.Length == 0 && notable.Count == 0)
            return null;

        return new TrackInfo(song, artist, notable);
    }

    private void StoreInCache(string key, TrackInfo info)
    {
        if (_cache.TryAdd(key, info))
            _cacheOrder.Enqueue(key);
        else
            _cache[key] = info; // refresh overwrites in place; order entry already exists

        // Soft FIFO bound so a long session can't grow the cache without limit.
        while (_cache.Count > CacheCap && _cacheOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }

    private static string CacheKey(string title, string? artist) =>
        $"{artist?.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";

    private static string ExtractText(JsonNode? content) => AnthropicApi.ExtractText(content) ?? string.Empty;

    private static string? StripToJsonObject(string text) => AnthropicApi.StripToJsonObject(text);

    /// <summary>
    /// Normalize a model-produced string for display: strip any markup the web-search model
    /// leaks in (e.g. &lt;cite index="…"&gt;…&lt;/cite&gt; citation tags), decode HTML entities,
    /// and collapse the whitespace those removals leave behind.
    /// </summary>
    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        var text = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

    private static string? Str(JsonNode? n) => AnthropicApi.Str(n);
}
