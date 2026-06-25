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
    // A single mirror is fine to start (CLAUDE.md). DNS-based discovery can come later.
    private const string DefaultMirror = "https://de1.api.radio-browser.info";
    private const int ResultLimit = 30;

    private readonly HttpClient _http;
    private readonly string _mirror;

    public StationSearchService(HttpClient http, string mirror = DefaultMirror)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _mirror = mirror.TrimEnd('/');

        // The maintainers ask for a descriptive User-Agent for usage stats.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RadioPlayer/1.0");
    }

    public async Task<IReadOnlyList<Station>> SearchAsync(StationSearchQuery query, CancellationToken ct = default)
    {
        var candidates = await SearchCandidatesAsync(query, ct);
        return candidates.Select(c => c.Station).ToList();
    }

    public Task<IReadOnlyList<StationCandidate>> SearchCandidatesAsync(StationSearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return QueryAsync(BuildQueryString(query), ct);
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
        var url = $"{_mirror}/json/stations/byuuid?uuids={Uri.EscapeDataString(string.Join(",", list))}";
        return FetchAndMapAsync(url, ct);
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

            var page = await FetchAndMapAsync($"{_mirror}/json/stations/search?{sb}", ct);
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
        FetchAndMapAsync($"{_mirror}/json/stations/search?{queryString}", ct);

    /// <summary>Fetches a Radio Browser endpoint, filters to playable streams, maps to candidates.</summary>
    private async Task<IReadOnlyList<StationCandidate>> FetchAndMapAsync(string url, CancellationToken ct)
    {
        var results = await _http.GetFromJsonAsync<List<RadioBrowserStation>>(url, ct)
                      ?? new List<RadioBrowserStation>();

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

    private static string BuildQueryString(StationSearchQuery q)
    {
        var sb = new StringBuilder();

        AppendParam(sb, "hidebroken", "true");
        AppendParam(sb, "limit", ResultLimit.ToString());

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
