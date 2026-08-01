using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// Generates DJ Mode's "why this song" intro line via a single plain Anthropic Messages API call
/// — no tools, since this is short creative writing, not fact lookup. Raw HttpClient, no wrapper
/// SDK, mirroring the other LLM services in this codebase. Knows nothing about playback.
/// </summary>
public sealed class DjIntroService : IDjIntroService
{
    private const string DefaultModel = AnthropicApi.HaikuModel; // short creative line, not deep reasoning
    private const int CacheCap = 48; // per-session, soft FIFO bound

    private const string SystemPrompt = """
        You are a radio DJ giving a short, on-air introduction just before playing a song for a
        listener who described a specific vibe they wanted. Write ONE short intro line — 1-2
        sentences, no more — in a professional-but-slightly-quirky tone: warm and a little
        playful, never corny or over-the-top.

        You're given the track title, the artist (may be blank), and the vibe/prompt the listener
        originally asked for. Sometimes you also get the curator's note — the actual reason this
        track was picked for the playlist; when present, let it shape the line (it's the real
        "why this song"). Reference the vibe or the track naturally if it fits; don't force it
        if there's nothing good to say.

        If you don't confidently recognize the track or artist, keep the line generic but still
        natural-sounding — lean on the vibe instead. Never invent biographical or factual claims
        you're not confident of.

        Respond with ONLY this JSON object — no prose, no markdown fences:
        { "line": "string" }
        """;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    // Per-session cache keyed by normalized "artist|title". ConcurrentDictionary because a
    // background-thread continuation may read/write while the UI thread queues another request.
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public DjIntroService(HttpClient http, string? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey;
        _model = model;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<string?> GetIntroAsync(string title, string? artist, string? vibe,
        string? curatorNote = null, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title))
            return null;

        var key = CacheKey(title, artist);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var line = await GenerateAsync(title, artist, vibe, curatorNote, ct).ConfigureAwait(false);
        if (line is not null)
            StoreInCache(key, line);
        return line;
    }

    private async Task<string?> GenerateAsync(string title, string? artist, string? vibe,
        string? curatorNote, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Track: \"").Append(title).Append('"');
        if (!string.IsNullOrWhiteSpace(artist))
            sb.Append(" by ").Append(artist);
        if (!string.IsNullOrWhiteSpace(vibe))
            sb.Append(".\nListener's original request: \"").Append(vibe).Append('"');
        if (!string.IsNullOrWhiteSpace(curatorNote))
            sb.Append(".\nCurator's note (why this track was picked): \"").Append(curatorNote).Append('"');
        sb.Append('.');

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 200,
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = sb.ToString() }
            }
        };

        using var request = AnthropicApi.CreateRequest(_apiKey, body);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[DjIntro] API returned {(int)response.StatusCode}: {AnthropicApi.Truncate(responseBody)}");
                return null;
            }

            return ParseLine(AnthropicApi.ExtractText(responseBody));
        }
        catch (OperationCanceledException)
        {
            throw; // let the caller's cancellation propagate untouched
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DjIntro] generation failed: {ex.Message}");
            return null;
        }
    }

    private static string? ParseLine(string? text)
    {
        var json = AnthropicApi.StripToJsonObject(text);
        if (json is null)
            return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        var line = Clean(AnthropicApi.Str(node?["line"]));
        return line.Length == 0 ? null : line;
    }

    private void StoreInCache(string key, string line)
    {
        if (_cache.TryAdd(key, line))
            _cacheOrder.Enqueue(key);

        while (_cache.Count > CacheCap && _cacheOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }

    private static string CacheKey(string title, string? artist) =>
        $"{artist?.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        var text = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
        return text.Trim();
    }

}
