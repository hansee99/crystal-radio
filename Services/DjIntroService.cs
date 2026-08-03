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
    // Sonnet, not Haiku. This is short creative writing the listener actually reads every time a
    // track changes — the one place in the app where prose quality is the product. CLAUDE.md's
    // own tier rule reserves Sonnet for "listener-audible judgment", which this is; it was
    // originally filed under one-shot description by shape rather than by what it's for. One small
    // call per song, so the cost delta is negligible.
    private const string DefaultModel = AnthropicApi.SonnetModel;
    private const int CacheCap = 48; // per-session, soft FIFO bound

    private const string SystemPromptTemplate = """
        You are a radio DJ introducing the next song, on air, to one listener who told you the kind
        of thing they wanted to hear. Write ONE line — 1-2 sentences, never more.

        {PERSONA}

        You get the track title, the artist (may be blank), and the vibe the listener asked for.
        Sometimes you also get a curator's note explaining why the track was picked. Treat that
        note as background for YOU, not as the subject of the line: it tells you why the track
        belongs, which you may draw on, but a line whose whole job is explaining the match gets
        dull fast. Say something about the music, the artist, or the moment first; reach for how it
        fits the request only when there's genuinely nothing better to say, and then lightly.

        {MOVE}

        Never invent biographical or factual claims. If you don't confidently recognize the track
        or artist, say nothing specific about them — work with the mood or the moment instead. Do
        not use the listener's words back at them verbatim, and don't start with "Here's".

        Respond with ONLY this JSON object — no prose, no markdown fences:
        { "line": "string" }
        """;

    /// <summary>
    /// One of these is injected per call, cycling. This — not the tone wording — is what stops the
    /// lines reading as a template: left to itself the model settles into one sentence shape
    /// ("Here's X, which fits your Y because Z") and every intro sounds like the last one however
    /// the persona is described. Varying what the line *does* is what a real DJ varies.
    /// </summary>
    private static readonly string[] Moves =
    [
        "This time: pick out one concrete detail of the track or artist and hang the line on it.",
        "This time: set a scene — where or when this music belongs — and let the track arrive in it.",
        "This time: just announce it, cleanly and with a little style. No justification at all.",
        "This time: speak as if handing off from whatever was playing before, mid-flow.",
        "This time: an aside — a small, human, slightly offhand remark, then the track.",
        "This time: lead with the feeling the first few seconds will give the listener.",
    ];

    private static readonly Dictionary<DjPersonality, string> Personas = new()
    {
        [DjPersonality.Warm] =
            "Your voice is warm and a little playful — an unhurried late-evening presenter who "
            + "likes this music. Never corny, never over-the-top.",
        [DjPersonality.Upbeat] =
            "Your voice is bright and energetic — you're genuinely glad this track is next and it "
            + "shows. Momentum and warmth, not shouting; no exclamation marks stacked up.",
        [DjPersonality.LateNight] =
            "Your voice is low and unhurried — the small hours, lights down, talking quietly to "
            + "someone still awake. Spare, atmospheric, comfortable with saying little.",
        [DjPersonality.Wry] =
            "Your voice is dry and lightly sardonic — an arched eyebrow, the occasional deflating "
            + "aside. Never mean about the music, and never sneering at the listener.",
        [DjPersonality.Professional] =
            "Your voice is clean and understated — a seasoned presenter who trusts the music to do "
            + "the work. Precise, unfussy, no whimsy.",
    };

    private const string PatterSystemPromptTemplate = """
        You are a radio DJ. A listener has asked for a particular kind of music and you are about to
        build them a mix from live radio. Write the short things you'd say at four moments that
        aren't a track introduction.

        {PERSONA}

        The four moments:
        - "sourcing": you're still finding stations. Nothing is playing yet.
        - "waiting": you're listening to stations and haven't captured a song worth playing yet.
        - "bridging": the mix has run out, so live radio is covering while you gather more.
        - "signingOff": the session is ending.

        Give THREE alternatives for each, so the same moment twice doesn't repeat itself. Each is
        ONE short sentence. Shape them around what the listener asked for without quoting their
        words back at them. Be honest about what's happening — these describe a real state, so
        don't promise music is playing when it isn't. Never invent facts about stations or tracks.

        Respond with ONLY this JSON object — no prose, no markdown fences:
        {
          "sourcing": ["string", "string", "string"],
          "waiting": ["string", "string", "string"],
          "bridging": ["string", "string", "string"],
          "signingOff": ["string", "string", "string"]
        }
        """;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;
    private readonly string _systemPromptBase;
    private readonly string _patterSystemPrompt;

    // Which move to use next. Advanced per generated line, so consecutive intros differ in shape.
    private int _moveIndex = -1;

    // Per-session cache keyed by normalized "artist|title". ConcurrentDictionary because a
    // background-thread continuation may read/write while the UI thread queues another request.
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public DjIntroService(HttpClient http, string? apiKey,
        DjPersonality personality = DjPersonality.Warm, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiKey = apiKey;
        _model = model;
        var persona = Personas.TryGetValue(personality, out var p) ? p : Personas[DjPersonality.Warm];
        _systemPromptBase = SystemPromptTemplate.Replace("{PERSONA}", persona);
        _patterSystemPrompt = PatterSystemPromptTemplate.Replace("{PERSONA}", persona);
    }

    public async Task<DjPatter?> GetSessionPatterAsync(string? vibe, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 900,
            ["system"] = _patterSystemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = string.IsNullOrWhiteSpace(vibe)
                        ? "The listener didn't say what they wanted — keep it open."
                        : $"The listener asked for: \"{vibe}\""
                }
            }
        };

        using var request = AnthropicApi.CreateRequest(_apiKey, body);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Debug($"[DjIntro] patter API returned {(int)response.StatusCode}: "
                             + AnthropicApi.Truncate(responseBody));
                return null;
            }
            return ParsePatter(AnthropicApi.ExtractText(responseBody));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[DjIntro] patter generation failed: {ex.Message}");
            return null;
        }
    }

    private static DjPatter? ParsePatter(string? text)
    {
        var json = AnthropicApi.StripToJsonObject(text);
        if (json is null)
            return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        var sourcing = Lines(node, "sourcing");
        var waiting = Lines(node, "waiting");
        var bridging = Lines(node, "bridging");
        var signingOff = Lines(node, "signingOff");

        // All four or nothing: a half-filled set would leave some moments voiced and others in the
        // app's own wording, which reads worse than using the fallbacks throughout.
        if (sourcing.Count == 0 || waiting.Count == 0 || bridging.Count == 0 || signingOff.Count == 0)
            return null;

        return new DjPatter(sourcing, waiting, bridging, signingOff);
    }

    private static List<string> Lines(JsonNode? node, string key)
    {
        var lines = new List<string>();
        if (node?[key] is not JsonArray arr)
            return lines;
        foreach (var item in arr)
        {
            var line = Clean(AnthropicApi.Str(item));
            if (line.Length > 0)
                lines.Add(line);
        }
        return lines;
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

        // Cycle the move. Interlocked because two tracks can land close together and the counter
        // is the only thing keeping consecutive lines from sharing a shape.
        var move = Moves[(int)((uint)Interlocked.Increment(ref _moveIndex) % Moves.Length)];

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 200,
            ["system"] = _systemPromptBase.Replace("{MOVE}", move),
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
                AppLog.Debug($"[DjIntro] API returned {(int)response.StatusCode}: {AnthropicApi.Truncate(responseBody)}");
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
            AppLog.Debug($"[DjIntro] generation failed: {ex.Message}");
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
