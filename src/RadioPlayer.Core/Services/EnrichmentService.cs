using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RadioPlayer.Services;

/// <summary>
/// Phase 1 enrichment pipeline: fetch a station's homepage (untrusted I/O — tight timeout,
/// size cap, content-type check), extract readable text, distill it with a cheap model into
/// a short description + facets, and cache it via <see cref="EnrichmentStore"/>. Falls back
/// to a name+tags description when the homepage is dead/JS-only/junk. Best-effort: every
/// failure degrades silently. Never touches RadioEngine or the search loop.
/// </summary>
public sealed partial class EnrichmentService : IEnrichmentService
{
    private const string DefaultModel = AnthropicApi.HaikuModel; // summarization, not reasoning

    private const int MaxHomepageBytes = 200_000;   // size cap for untrusted fetch
    private const int MaxExtractedChars = 4_000;    // cap text sent to the model
    private const int MinUsefulChars = 150;         // below this, treat homepage as junk
    private const int MaxConcurrentEnrichments = 4;

    private readonly HttpClient _llmHttp;
    private readonly HttpClient _homepageHttp;
    private readonly EnrichmentStore _store;
    private readonly ApiKeySource _apiKey;
    private readonly string _model;

    private readonly SemaphoreSlim _gate = new(MaxConcurrentEnrichments);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IEmbeddingProvider _embeddings;

    public EnrichmentService(HttpClient llmHttp, HttpClient homepageHttp, EnrichmentStore store,
        IEmbeddingProvider embeddings, ApiKeySource? apiKey, string model = DefaultModel)
    {
        _llmHttp = llmHttp ?? throw new ArgumentNullException(nameof(llmHttp));
        _homepageHttp = homepageHttp ?? throw new ArgumentNullException(nameof(homepageHttp));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _apiKey = apiKey ?? new ApiKeySource();
        _model = model;

        try { _homepageHttp.Timeout = TimeSpan.FromSeconds(5); } catch { /* already used */ }
    }

    private bool CanDistill => _apiKey.IsConfigured;

    public EnrichmentRecord? GetCached(string stationUuid) => _store.Get(stationUuid);

    public void EnrichInBackground(IEnumerable<StationCandidate> candidates)
    {
        if (candidates is null) return;

        var seen = candidates.Where(c => !string.IsNullOrWhiteSpace(c.StationUuid)).ToList();
        if (seen.Count == 0) return;

        // Free, so it runs for every sighting rather than only for stale rows: the stream url and
        // friends came in the same directory response the search already paid for (#26).
        TopUpPlayableFieldsInBackground(seen);

        foreach (var candidate in seen)
        {
            var uuid = candidate.StationUuid;

            // Enrich each station once until stale; never re-summarize on every search.
            if (!_store.IsStale(_store.Get(uuid)))
                continue;
            if (!_inFlight.TryAdd(uuid, 0))
                continue; // already enriching this one

            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await EnrichOneAsync(candidate).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"[Enrich] {uuid} failed: {ex.Message}");
                }
                finally
                {
                    _gate.Release();
                    _inFlight.TryRemove(uuid, out _);
                }
            });
        }
    }

    /// <summary>
    /// Enrich + embed many candidates and AWAIT completion (the seed tool uses this).
    /// Bounded by the same concurrency gate, idempotent (skips fresh rows), best-effort
    /// per station, and reports progress as each finishes.
    /// </summary>
    public async Task EnrichManyAsync(IReadOnlyList<StationCandidate> candidates,
        IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        if (candidates is null || candidates.Count == 0)
            return;

        var total = candidates.Count;
        var done = 0;

        var tasks = candidates.Select(async candidate =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Idempotent/resumable: only (re)enrich missing or stale rows.
                if (!string.IsNullOrWhiteSpace(candidate.StationUuid))
                {
                    if (_store.IsStale(_store.Get(candidate.StationUuid)))
                        await EnrichOneAsync(candidate).ConfigureAwait(false);
                    else
                        // Fresh description, but it may predate #26 and carry no url. Topping up
                        // is free, so a re-run of the seed tool repairs an existing catalog.
                        _store.TopUpPlayableFields(candidate.StationUuid, candidate.Station.Name,
                            candidate.Station.Url, candidate.Station.Format.ToString(),
                            candidate.Bitrate, candidate.Country);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[Enrich] {candidate.StationUuid} failed: {ex.Message}");
            }
            finally
            {
                _gate.Release();
                progress?.Report((Interlocked.Increment(ref done), total));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Write the directory-supplied playable fields for candidates whose row already exists.
    /// Off the caller's thread because it touches SQLite and the search paths call this from the
    /// UI thread; failures are swallowed, since this only ever improves an offline fallback.
    /// </summary>
    private void TopUpPlayableFieldsInBackground(IReadOnlyList<StationCandidate> candidates) =>
        _ = Task.Run(() =>
        {
            foreach (var c in candidates)
            {
                try
                {
                    _store.TopUpPlayableFields(c.StationUuid, c.Station.Name, c.Station.Url,
                        c.Station.Format.ToString(), c.Bitrate, c.Country);
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"[Enrich] top-up {c.StationUuid} failed: {ex.Message}");
                }
            }
        });

    private async Task EnrichOneAsync(StationCandidate candidate)
    {
        var record = await BuildRecordAsync(candidate).ConfigureAwait(false);
        _store.Upsert(record);
        AppLog.Debug($"[Enrich] cached {candidate.Station.Name} (source={record.Source})");

        // Phase 2: embed the fresh description right away (same path that backfill uses).
        EmbedAndStore(candidate.StationUuid, record.Description);
    }

    private void EmbedAndStore(string uuid, string description)
    {
        if (!_embeddings.IsAvailable || string.IsNullOrWhiteSpace(description))
            return;
        try
        {
            var vector = _embeddings.Embed(description);
            if (vector is not null)
            {
                _store.SetEmbedding(uuid, vector, _embeddings.ModelId);
                AppLog.Debug($"[Embed] stored vector for {uuid} (model={_embeddings.ModelId})");
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Embed] {uuid} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One-time/background pass: embed any already-enriched rows that lack a current-model
    /// vector (e.g. rows from before Phase 2, or after a model change). Fire-and-forget.
    /// </summary>
    public void BackfillEmbeddingsInBackground()
    {
        if (!_embeddings.IsAvailable)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var rows = _store.GetRowsNeedingEmbedding(_embeddings.ModelId);
                AppLog.Debug($"[Embed] backfill: {rows.Count} row(s) need an embedding");
                foreach (var row in rows)
                    EmbedAndStore(row.StationUuid, row.Description);
                AppLog.Debug("[Embed] backfill complete");
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[Embed] backfill error: {ex.Message}");
            }
        });
    }

    private async Task<EnrichmentRecord> BuildRecordAsync(StationCandidate c)
    {
        // Preferred path: distil the homepage with the cheap model.
        if (CanDistill && !string.IsNullOrWhiteSpace(c.Homepage))
        {
            try
            {
                var text = await FetchAndExtractAsync(c.Homepage!).ConfigureAwait(false);
                if (text.Length >= MinUsefulChars)
                {
                    var distilled = await DistillAsync(c.Station.Name, c.Tags, text).ConfigureAwait(false);
                    if (distilled is not null && !string.IsNullOrWhiteSpace(distilled.Description))
                        return new EnrichmentRecord(c.StationUuid, distilled.Description.Trim(),
                            distilled.FacetsJson, EnrichmentSource.Homepage, DateTimeOffset.UtcNow,
                            Name: c.Station.Name, Url: c.Station.Url,
                            Codec: c.Station.Format.ToString(), Bitrate: c.Bitrate,
                            Country: c.Country);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[Enrich] homepage path failed for {c.Station.Name}: {ex.Message}");
            }
        }

        // Fallback: a description from name + Radio Browser tags (cached so we don't refetch).
        // Still carries the playable fields — a tags-only description is a perfectly usable offline
        // catalog entry, and 899 of one real machine's 2,448 rows came through this path.
        return new EnrichmentRecord(c.StationUuid, BuildTagsDescription(c), TagsFacets(c.Tags),
            EnrichmentSource.TagsOnly, DateTimeOffset.UtcNow,
            Name: c.Station.Name, Url: c.Station.Url,
            Codec: c.Station.Format.ToString(), Bitrate: c.Bitrate,
            Country: c.Country);
    }

    // --- Homepage fetch + extraction (untrusted I/O) --------------------------

    private async Task<string> FetchAndExtractAsync(string homepage)
    {
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return string.Empty;

        using var response = await _homepageHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return string.Empty;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase))
            return string.Empty; // not a readable page

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[MaxHomepageBytes];
        var total = 0;
        int read;
        while (total < buffer.Length &&
               (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total)).ConfigureAwait(false)) > 0)
            total += read;

        return ExtractReadableText(Encoding.UTF8.GetString(buffer, 0, total));
    }

    private static string ExtractReadableText(string html)
    {
        html = ScriptRegex().Replace(html, " ");
        html = StyleRegex().Replace(html, " ");
        html = CommentRegex().Replace(html, " ");
        html = TagRegex().Replace(html, " ");
        html = WebUtility.HtmlDecode(html);
        html = WhitespaceRegex().Replace(html, " ").Trim();
        return html.Length > MaxExtractedChars ? html[..MaxExtractedChars] : html;
    }

    // --- LLM distillation (cheap model) ---------------------------------------

    private async Task<DistillResult?> DistillAsync(string name, string? tags, string text)
    {
        const string system = """
            You write a concise, factual description of an internet radio station from its
            homepage text. Respond with ONLY a JSON object — no prose, no code fences:
            {
              "description": "1-2 sentences: what the station plays and its character. Concrete, no marketing fluff.",
              "genres": ["..."],   // [] if unknown
              "moods": ["..."],    // e.g. "relaxing", "energetic"; [] if unknown
              "era": "string|null" // e.g. "80s", "modern", or null
            }
            If the text is unusable, set "description" to "".
            """;

        var userContent = $"Station name: {name}\nTags: {tags ?? "(none)"}\n\nHomepage text:\n{text}";

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 400,
            ["system"] = system,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userContent }
            }
        };

        using var request = AnthropicApi.CreateRequest(_apiKey.Current, body);
        using var response = await _llmHttp.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var responseText = AnthropicApi.ExtractText(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return ParseDistill(responseText);
    }

    private static DistillResult? ParseDistill(string? modelText)
    {
        var json = StripToJsonObject(modelText);
        if (json is null) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        var description = node?["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description))
            return null;

        // Keep only the facets in a compact JSON object.
        var facets = new JsonObject
        {
            ["genres"] = node?["genres"]?.DeepClone() ?? new JsonArray(),
            ["moods"] = node?["moods"]?.DeepClone() ?? new JsonArray(),
            ["era"] = node?["era"]?.DeepClone()
        };
        return new DistillResult(description, facets.ToJsonString());
    }

    // --- Tags-only fallback ---------------------------------------------------

    private static string BuildTagsDescription(StationCandidate c)
    {
        var sb = new StringBuilder($"{c.Station.Name} is an internet radio station");
        if (!string.IsNullOrWhiteSpace(c.Tags))
        {
            var tags = string.Join(", ", c.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (tags.Length > 0)
                sb.Append($" ({tags})");
        }
        if (!string.IsNullOrWhiteSpace(c.Country))
            sb.Append($" from {c.Country}");
        sb.Append('.');
        return sb.ToString();
    }

    private static string? TagsFacets(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return null;
        var genres = new JsonArray();
        foreach (var t in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            genres.Add(t);
        return new JsonObject { ["genres"] = genres, ["moods"] = new JsonArray() }.ToJsonString();
    }

    private static string? StripToJsonObject(string? text) => AnthropicApi.StripToJsonObject(text);

    private sealed record DistillResult(string Description, string? FacetsJson);

    [GeneratedRegex("<script.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();
    [GeneratedRegex("<style.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleRegex();
    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
