using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace RadioPlayer.Services;

/// <summary>
/// Builds and maintains the local song-library index (Phase C). When a song is saved it is
/// persisted immediately, then — in the background — described by a cheap model from the
/// title/artist and embedded locally for semantic curation later.
///
/// Note on grounding: unlike station search (where "the LLM is not the database" and all real
/// data comes from Radio Browser), here the model is only DESCRIBING a track we already hold as
/// a file — genre, mood, era, a one-line profile. It never supplies audio or URLs, so using the
/// model's general music knowledge is appropriate. Best-effort throughout: any failure leaves
/// the row with its core metadata and moves on.
/// </summary>
public sealed class SongLibraryService : ISongLibraryService
{
    private const string DefaultModel = AnthropicApi.HaikuModel; // describing, not reasoning
    private const int MaxConcurrent = 3;

    private const string SystemPrompt = """
        You describe a piece of music for a personal library index, from its title and artist.
        Use your general music knowledge. If you are not confident which track/artist this is,
        still give a best-effort description from the title's style — never invent specific facts
        (labels, years, chart positions) you are unsure of.

        Respond with ONLY this JSON object — no prose, no code fences:
        {
          "description": "1-2 sentences: the song's style, mood, and era — concrete, no fluff. Written so it can be matched against a listener's free-text request.",
          "genres": ["..."],   // [] if unsure
          "moods": ["..."],    // e.g. "melancholic", "energetic"; [] if unsure
          "era": "string|null" // e.g. "80s", "2010s", "modern", or null
        }
        """;

    private readonly HttpClient _http;
    private readonly LibraryStore _store;
    private readonly IEmbeddingProvider _embeddings;
    private readonly string? _apiKey;
    private readonly string _model;

    private readonly SemaphoreSlim _gate = new(MaxConcurrent);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public SongLibraryService(HttpClient http, LibraryStore store, IEmbeddingProvider embeddings,
        string? apiKey, string model = DefaultModel)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _apiKey = apiKey;
        _model = model;
    }

    private bool CanDistill => !string.IsNullOrWhiteSpace(_apiKey);

    public IReadOnlyList<SavedSong> GetAll(SongSource? source = null) => _store.GetAll(source);

    public void Remove(string path) => _store.Remove(path);

    public void AddAndEnrich(SavedSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        _store.Upsert(song);               // persist core metadata immediately
        EnrichInBackground(song.Path, song.Title, song.Artist);
    }

    public void BackfillInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                // 1. Reconcile: drop rows whose file the user has since deleted.
                foreach (var song in _store.GetAll())
                {
                    if (!File.Exists(song.Path))
                    {
                        _store.Remove(song.Path);
                        continue;
                    }
                    // 2. Enrich rows still missing a description.
                    if (string.IsNullOrWhiteSpace(song.Description))
                        EnrichInBackground(song.Path, song.Title, song.Artist);
                }

                // 3. Embed rows that have a description but no current-model vector.
                if (_embeddings.IsAvailable)
                {
                    foreach (var (path, description) in _store.GetRowsNeedingEmbedding(_embeddings.ModelId))
                        EmbedAndStore(path, description);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Library] backfill error: {ex.Message}");
            }
        });
    }

    private void EnrichInBackground(string path, string title, string artist)
    {
        if (!_inFlight.TryAdd(path, 0))
            return; // already working on this one

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var distilled = CanDistill ? await DistillAsync(title, artist).ConfigureAwait(false) : null;
                var description = distilled?.Description;
                if (string.IsNullOrWhiteSpace(description))
                    description = BuildFallbackDescription(title, artist); // no key / model failed

                _store.SetEnrichment(path, description, distilled?.FacetsJson);
                EmbedAndStore(path, description);
                Debug.WriteLine($"[Library] enriched {title} — {artist}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Library] enrich failed for {path}: {ex.Message}");
            }
            finally
            {
                _gate.Release();
                _inFlight.TryRemove(path, out _);
            }
        });
    }

    private void EmbedAndStore(string path, string description)
    {
        if (!_embeddings.IsAvailable || string.IsNullOrWhiteSpace(description))
            return;
        try
        {
            var vector = _embeddings.Embed(description);
            if (vector is not null)
                _store.SetEmbedding(path, vector, _embeddings.ModelId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Library] embed failed for {path}: {ex.Message}");
        }
    }

    private static string BuildFallbackDescription(string title, string artist) =>
        string.IsNullOrWhiteSpace(artist) ? title : $"{title} by {artist}";

    // --- LLM distillation (cheap model, general music knowledge) ---------------

    private async Task<DistillResult?> DistillAsync(string title, string artist)
    {
        var userContent = $"Title: {title}\nArtist: {(string.IsNullOrWhiteSpace(artist) ? "(unknown)" : artist)}";

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 300,
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = userContent }
            }
        };

        using var request = AnthropicApi.CreateRequest(_apiKey, body);
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var text = AnthropicApi.ExtractText(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return ParseDistill(text);
    }

    private static DistillResult? ParseDistill(string? modelText)
    {
        var json = StripToJsonObject(modelText);
        if (json is null) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }

        var description = node?["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var facets = new JsonObject
        {
            ["genres"] = node?["genres"]?.DeepClone() ?? new JsonArray(),
            ["moods"] = node?["moods"]?.DeepClone() ?? new JsonArray(),
            ["era"] = node?["era"]?.DeepClone()
        };
        return new DistillResult(description.Trim(), facets.ToJsonString());
    }

    private static string? StripToJsonObject(string? text) => AnthropicApi.StripToJsonObject(text);

    private sealed record DistillResult(string Description, string? FacetsJson);
}
