using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// LRCLIB client (https://lrclib.net). Free, no key, no registration — which is exactly why the
/// maintainers ask clients to behave, and this one does: a descriptive User-Agent in the format
/// their docs specify, one request at a time, a minimum gap between them, and Retry-After honoured
/// on a 429.
///
/// <para>Two endpoints. <c>/api/get</c> is the precise lookup; on its 404 we fall back to
/// <c>/api/search</c>, which is fuzzier and returns up to 20 rows. See <see cref="ILyricsService"/>
/// for why a miss is an ordinary outcome here rather than an error.</para>
/// </summary>
public sealed class LyricsService : ILyricsService
{
    private const string BaseUrl = "https://lrclib.net";

    /// <summary>
    /// Their docs: "your application's name, version, and a link to its homepage or project page".
    /// Not decoration — it is how they attribute load, and the one thing they ask for in exchange
    /// for an unauthenticated API.
    ///
    /// <para>The version is read from the assembly rather than written here. It was hardcoded as
    /// 1.9.0 in anticipation of a bump, which meant the app spent a while truthfully reporting 1.8.2
    /// everywhere else while telling LRCLIB something else. A version string that can drift from the
    /// build is worse than no version string.</para>
    /// </summary>
    internal static readonly string UserAgent =
        $"CrystalRadio/{AppVersion} (https://github.com/hansee99/crystal-radio)";

    private static string AppVersion =>
        typeof(LyricsService).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    /// <summary>LRCLIB asks for 200–500 ms between requests. Enforced across the whole app, not
    /// per caller, because background enrichment and a UI click share this service.</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(300);

    private const int CacheCap = 64;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);   // sequential, as the docs request
    private DateTime _lastRequest = DateTime.MinValue;

    // Per-session only. Lyrics are deliberately never persisted: showing them transiently is a
    // different posture from warehousing the text in the library database.
    private readonly ConcurrentDictionary<string, TrackLyrics?> _cache = new();
    private readonly ConcurrentQueue<string> _cacheOrder = new();

    public LyricsService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent))
            _http.DefaultRequestHeaders.Add("Lrclib-Client", UserAgent); // their documented fallback
    }

    public async Task<TrackLyrics?> LookupAsync(string? artist, string? title,
        double? durationSeconds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return null;

        var key = CacheKey(artist, title);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        TrackLyrics? found = null;
        try
        {
            found = await FetchAsync(artist.Trim(), title.Trim(), durationSeconds, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null; // caller went away; don't poison the cache with a non-answer
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Lyrics] lookup failed for {artist} - {title}: {ex.Message}");
        }

        Remember(key, found);   // negatives cached too: a miss is stable and re-asking helps nobody
        return found;
    }

    private async Task<TrackLyrics?> FetchAsync(string artist, string title,
        double? durationSeconds, CancellationToken ct)
    {
        var exact = $"/api/get?{Query(artist, title, durationSeconds)}";
        var (status, body) = await SendAsync(exact, ct).ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
            return Parse(JsonNode.Parse(body!));

        if (status != HttpStatusCode.NotFound)
            return null;   // 429 after retry, 5xx, transport — not a miss, just no answer today

        // Fuzzier second chance. Duration is NOT forwarded: /api/get already failed the ±2s test
        // with it, so repeating the constraint would only fail the same way.
        var (searchStatus, searchBody) = await SendAsync(
            $"/api/search?{Query(artist, title, duration: null)}", ct).ConfigureAwait(false);
        if (searchStatus != HttpStatusCode.OK)
            return null;

        return BestOf(JsonNode.Parse(searchBody!) as JsonArray, durationSeconds);
    }

    /// <summary>
    /// Picks from a fuzzy search. Prefers a duration match when we have one to compare against —
    /// same ±2 s window /api/get uses — then anything with actual lyrics, then the first row.
    /// </summary>
    private static TrackLyrics? BestOf(JsonArray? rows, double? durationSeconds)
    {
        if (rows is null || rows.Count == 0) return null;

        var parsed = rows.Select(Parse).Where(r => r is not null).Select(r => r!).ToList();
        if (parsed.Count == 0) return null;

        if (durationSeconds is { } wanted)
        {
            var close = parsed.FirstOrDefault(r => Math.Abs(r.DurationSeconds - wanted) <= 2.0);
            if (close is not null) return close;
        }

        return parsed.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Lyrics)) ?? parsed[0];
    }

    internal static TrackLyrics? Parse(JsonNode? node)
    {
        if (node is null) return null;

        var title = Text(node["trackName"]) ?? Text(node["name"]);
        var artist = Text(node["artistName"]);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            return null;

        return new TrackLyrics(
            Title: title,
            Artist: artist,
            Album: Text(node["albumName"]),
            Instrumental: node["instrumental"]?.GetValueKind() == JsonValueKind.True,
            // A 200 can still carry a null here — "we know this track" is not "we have its words".
            Lyrics: Text(node["plainLyrics"]),
            DurationSeconds: Number(node["duration"]));
    }

    /// <summary>
    /// One request, serialised behind the gate with the documented minimum gap, and one retry if
    /// LRCLIB says we are going too fast. Returns the status so the caller can tell a genuine miss
    /// (404) from "no answer" — they lead to different UI.
    /// </summary>
    private async Task<(HttpStatusCode Status, string? Body)> SendAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var since = DateTime.UtcNow - _lastRequest;
                if (since < MinGap)
                    await Task.Delay(MinGap - since, ct).ConfigureAwait(false);

                using var response = await _http.GetAsync(BaseUrl + path, ct).ConfigureAwait(false);
                _lastRequest = DateTime.UtcNow;

                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    var body = response.IsSuccessStatusCode
                        ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                        : null;
                    return (response.StatusCode, body);
                }

                // "Your client must honor this header. Ignoring it may result in a temporary ban."
                var wait = response.Headers.RetryAfter?.Delta
                           ?? TimeSpan.FromSeconds(response.Headers.RetryAfter?.Date is { } d
                               ? Math.Max(1, (d - DateTimeOffset.UtcNow).TotalSeconds)
                               : 5);
                AppLog.Debug($"[Lyrics] rate limited; waiting {wait.TotalSeconds:0.#}s");
                if (attempt == 1) return (HttpStatusCode.TooManyRequests, null);
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            return (HttpStatusCode.TooManyRequests, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Query(string artist, string title, double? duration)
    {
        var q = $"artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        // Only ever sent for a full file. See ILyricsService.LookupAsync — a trimmed harvest
        // segment's length fails the ±2s match and turns a hit into a miss.
        if (duration is { } d && d >= 1 && d <= 3600)
            q += $"&duration={(int)Math.Round(d)}";
        return q;
    }

    private static string? Text(JsonNode? node)
    {
        var value = node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static double Number(JsonNode? node)
    {
        try { return node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : 0; }
        catch (InvalidOperationException) { return 0; }
    }

    private static string CacheKey(string artist, string title) =>
        $"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}";

    private void Remember(string key, TrackLyrics? value)
    {
        if (!_cache.TryAdd(key, value)) return;
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > CacheCap && _cacheOrder.TryDequeue(out var oldest))
            _cache.TryRemove(oldest, out _);
    }
}
