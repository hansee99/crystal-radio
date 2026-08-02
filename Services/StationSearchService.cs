using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using RadioPlayer.Models;

namespace RadioPlayer.Services;

/// <summary>
/// Talks to the Radio Browser API (https://api.radio-browser.info) and maps results to
/// the engine's <see cref="Station"/> model, keeping only streams BASS can actually play.
/// </summary>
public sealed class StationSearchService : IStationSearchService
{
    // Known public mirrors, tried in order: a request that fails at the transport level
    // (connect/timeout/5xx) advances to the next mirror and stays there — every AI feature
    // depends on this one API, so a single dead hostname must not take them all down.
    private static readonly string[] DefaultMirrors =
    [
        "https://de1.api.radio-browser.info",
        "https://de2.api.radio-browser.info",
        "https://at1.api.radio-browser.info",
    ];

    private const int ResultLimit = 30;

    private readonly HttpClient _http;
    private readonly string[] _mirrors;
    private int _mirrorIndex; // sticky: advanced on failure, so later calls start at a live one

    public StationSearchService(HttpClient http, string? mirror = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _mirrors = string.IsNullOrWhiteSpace(mirror) ? DefaultMirrors : [mirror.TrimEnd('/')];

        // The maintainers ask for a descriptive User-Agent for usage stats.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RadioPlayer/1.0");
    }

    public async Task<IReadOnlyList<Station>> SearchAsync(StationSearchQuery query, CancellationToken ct = default)
    {
        var candidates = await SearchCandidatesAsync(query, 0, ct);
        return candidates.Select(c => c.Station).ToList();
    }

    /// <summary>
    /// Structured search. Radio Browser's <c>tagList</c> is an AND filter (a station must carry
    /// every tag), which quietly starves recall for multi-tag queries — "ambient,chillout" only
    /// returns stations tagged with BOTH. So for multi-tag queries this also runs one query per
    /// tag and unions the results: the precise AND matches come first, then each tag's own
    /// matches. Recall is deliberately wide here — the LLM re-ranker downstream is the strict
    /// relevance judge, so a loose union costs nothing in final quality.
    /// </summary>
    public async Task<IReadOnlyList<StationCandidate>> SearchCandidatesAsync(StationSearchQuery query, int offset = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tags = query.Tags?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [];
        if (tags.Length <= 1)
            return await QueryAsync(BuildQueryString(query, offset), ct);

        // AND query first (most precise), then a query per tag, all in parallel.
        var queries = new List<Task<IReadOnlyList<StationCandidate>>>
        {
            QueryAsync(BuildQueryString(query, offset), ct)
        };
        foreach (var tag in tags)
        {
            var single = new StationSearchQuery
            {
                Tags = [tag],
                Name = query.Name,
                Country = query.Country,
                Language = query.Language,
                BitrateMin = query.BitrateMin,
                Order = query.Order
            };
            queries.Add(QueryAsync(BuildQueryString(single, offset), ct));
        }

        var pages = await Task.WhenAll(queries);

        // Union preserving order: AND matches first, then per-tag pages; dedupe on uuid.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var union = new List<StationCandidate>();
        foreach (var page in pages)
            foreach (var c in page)
                if (seen.Add(c.StationUuid))
                    union.Add(c);
        return union;
    }

    public Task<IReadOnlyList<StationCandidate>> SearchByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult<IReadOnlyList<StationCandidate>>([]);

        // byName path: a focused name lookup, ordered by popularity.
        var sb = new StringBuilder();
        AppendParam(sb, "hidebroken", "true");
        AppendParam(sb, "limit", ResultLimit.ToString());
        AppendParam(sb, "name", name);
        AppendParam(sb, "order", "votes");
        AppendParam(sb, "reverse", "true");
        return QueryAsync(sb.ToString(), ct);
    }

    public Task<IReadOnlyList<StationCandidate>> GetByUuidsAsync(IEnumerable<string> uuids, CancellationToken ct = default)
    {
        var list = uuids?.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray() ?? [];
        if (list.Length == 0)
            return Task.FromResult<IReadOnlyList<StationCandidate>>([]);

        // byuuid accepts a comma-separated list and still returns full station rows, so the
        // usual playable filter applies (re-validating codec/HLS/lastcheckok at resolve time).
        return FetchAndMapAsync($"/json/stations/byuuid?uuids={Uri.EscapeDataString(string.Join(",", list))}", ct);
    }

    public async Task<IReadOnlyList<StationCandidate>> GetPopularAsync(int count, bool extendedInfoOnly,
        CancellationToken ct = default)
    {
        const int pageSize = 100;
        // Popular stations are almost all playable; cap paging so a run of unplayable rows
        // (or the end of the directory) can't loop forever.
        var maxOffset = Math.Min(Math.Max(count * 4, 300), 5000);

        var collected = new List<StationCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var offset = 0; collected.Count < count && offset < maxOffset; offset += pageSize)
        {
            ct.ThrowIfCancellationRequested();

            var sb = new StringBuilder();
            AppendParam(sb, "hidebroken", "true");
            AppendParam(sb, "order", "clickcount");
            AppendParam(sb, "reverse", "true");
            if (extendedInfoOnly)
                AppendParam(sb, "has_extended_info", "true");
            AppendParam(sb, "limit", pageSize.ToString());
            AppendParam(sb, "offset", offset.ToString());

            var page = await FetchAndMapAsync($"/json/stations/search?{sb}", ct);
            if (page.Count == 0)
                break; // no more playable rows (likely end of the ranking)

            foreach (var c in page)
            {
                if (!seen.Add(c.StationUuid))
                    continue; // dedupe across pages
                collected.Add(c);
                if (collected.Count >= count)
                    break;
            }
        }

        return collected;
    }

    /// <summary>Runs a Radio Browser search query and maps the results.</summary>
    private Task<IReadOnlyList<StationCandidate>> QueryAsync(string queryString, CancellationToken ct) =>
        FetchAndMapAsync($"/json/stations/search?{queryString}", ct);

    /// <summary>
    /// Fetches a Radio Browser endpoint (path + query, no host), filters to playable streams,
    /// maps to candidates. On a transport-level failure it advances to the next mirror and
    /// retries once per remaining mirror; only when every mirror fails does the exception
    /// propagate to the caller's existing error handling.
    /// </summary>
    private async Task<IReadOnlyList<StationCandidate>> FetchAndMapAsync(string pathAndQuery, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var mirror = _mirrors[_mirrorIndex % _mirrors.Length];
            try
            {
                var results = await _http.GetFromJsonAsync<List<RadioBrowserStation>>(mirror + pathAndQuery, ct)
                              ?? new List<RadioBrowserStation>();
                return MapCandidates(results);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — not a mirror problem
            }
            catch (Exception ex) when (attempt < _mirrors.Length - 1)
            {
                AppLog.Debug($"[RadioBrowser] {mirror} failed ({ex.Message}) — trying next mirror");
                _mirrorIndex++; // sticky failover: later calls start at the mirror that worked
            }
        }
    }

    private static IReadOnlyList<StationCandidate> MapCandidates(List<RadioBrowserStation> results)
    {
        var candidates = new List<StationCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in results)
        {
            // Engine can only play non-HLS MP3/AAC; prefer streams the directory last
            // verified as working.
            if (r.Hls != 0 || r.LastCheckOk != 1)
                continue;
            if (!TryMapFormat(r.Codec, out var format))
                continue;

            var streamUrl = !string.IsNullOrWhiteSpace(r.UrlResolved) ? r.UrlResolved : r.Url;
            if (string.IsNullOrWhiteSpace(streamUrl) || string.IsNullOrWhiteSpace(r.Name))
                continue;
            if (string.IsNullOrWhiteSpace(r.StationUuid))
                continue;
            if (!seen.Add(streamUrl))
                continue;

            candidates.Add(new StationCandidate(
                r.StationUuid,
                new Station(r.Name.Trim(), streamUrl.Trim(), format),
                r.Bitrate,
                string.IsNullOrWhiteSpace(r.Country) ? null : r.Country,
                string.IsNullOrWhiteSpace(r.Tags) ? null : r.Tags,
                string.IsNullOrWhiteSpace(r.Homepage) ? null : r.Homepage));
        }

        return candidates;
    }

    private static void AppendParam(StringBuilder sb, string key, string value) =>
        sb.Append(sb.Length == 0 ? "" : "&").Append(key).Append('=').Append(Uri.EscapeDataString(value));

    private static string BuildQueryString(StationSearchQuery q, int offset)
    {
        var sb = new StringBuilder();

        AppendParam(sb, "hidebroken", "true");
        AppendParam(sb, "limit", ResultLimit.ToString());
        if (offset > 0)
            AppendParam(sb, "offset", offset.ToString());

        if (q.Tags is { Length: > 0 })
            AppendParam(sb, "tagList", string.Join(",", q.Tags.Where(t => !string.IsNullOrWhiteSpace(t))));
        if (!string.IsNullOrWhiteSpace(q.Name))
            AppendParam(sb, "name", q.Name);
        if (!string.IsNullOrWhiteSpace(q.Country))
            AppendParam(sb, "country", q.Country);
        if (!string.IsNullOrWhiteSpace(q.Language))
            AppendParam(sb, "language", q.Language);
        if (q.BitrateMin > 0)
            AppendParam(sb, "bitrateMin", q.BitrateMin.ToString());

        var order = q.Order?.ToLowerInvariant() switch
        {
            "clickcount" => "clickcount",
            "name" => "name",
            _ => "votes"
        };
        AppendParam(sb, "order", order);
        if (order is "votes" or "clickcount")
            AppendParam(sb, "reverse", "true"); // most popular first

        return sb.ToString();
    }

    private static bool TryMapFormat(string? codec, out StreamFormat format)
    {
        switch (codec?.Trim().ToUpperInvariant())
        {
            case "MP3":
                format = StreamFormat.Mp3;
                return true;
            case "AAC":
            case "AAC+":
            case "AACP":
                format = StreamFormat.Aac;
                return true;
            default:
                format = StreamFormat.Other;
                return false;
        }
    }

    /// <summary>Subset of the Radio Browser station JSON we care about.</summary>
    private sealed class RadioBrowserStation
    {
        [JsonPropertyName("stationuuid")] public string? StationUuid { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("url_resolved")] public string? UrlResolved { get; set; }
        [JsonPropertyName("codec")] public string? Codec { get; set; }
        [JsonPropertyName("bitrate")] public int Bitrate { get; set; }
        [JsonPropertyName("hls")] public int Hls { get; set; }
        [JsonPropertyName("lastcheckok")] public int LastCheckOk { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("tags")] public string? Tags { get; set; }
        [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    }
}
