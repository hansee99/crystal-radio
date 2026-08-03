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

        Some entries are not music at all. They are recorded off live radio, and stations leak
        non-music into the track metadata: adverts, station idents, news bulletins, traffic and
        weather, competition promos, and playout-system cart IDs such as ADBREAK_120000 or
        ADWTAG_122000. Say so when the title and artist plainly describe one of those.

        Be conservative. Set "is_song" to false ONLY when it is obvious. An unfamiliar, obscure,
        non-English or oddly punctuated title is still a song — a wrong "false" deletes real
        music, while a wrong "true" only leaves one bad track in a mix.

        Respond with ONLY this JSON object — no prose, no code fences:
        {
          "is_song": true,     // false ONLY for plainly non-music: advert, ident, news, cart ID
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
        EnrichInBackground(song);
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
                    // 1b. Re-judge harvested rows against the CURRENT song filter. Rows were
                    // admitted by whatever version of it was in force when they were harvested,
                    // so tightening the filter has to reach backwards: Ö3 news bulletins and
                    // livestream idents that slipped through stayed in the index and remained
                    // eligible for every later warm-start, which is the DJ mix serving five
                    // minutes of speech on a fresh session with no obvious cause.
                    if (song.Source == SongSource.Harvested)
                    {
                        if (RetireIfNoLongerSongLike(song))
                            continue;
                    }
                    // 2. Enrich rows still missing a description.
                    if (string.IsNullOrWhiteSpace(song.Description))
                        EnrichInBackground(song);
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
                AppLog.Debug($"[Library] backfill error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Drops a harvested row (and its audio file) if the current
    /// <see cref="SongHistoryFilter"/> would no longer accept it. Harvested audio is ephemeral by
    /// design — it lives in a size-capped folder and is evicted anyway — so deleting a
    /// now-rejected one costs nothing. Returns true if it was removed.
    ///
    /// Deliberately scoped to <see cref="SongSource.Harvested"/>: a song the user deliberately
    /// saved is theirs, and no tightening of a heuristic gets to delete it.
    /// </summary>
    private bool RetireIfNoLongerSongLike(SavedSong song)
    {
        if (SongHistoryFilter.IsLikelySong(song.Title, song.Artist, song.Station))
            return false;

        RetireHarvested(song, "the song filter no longer accepts it");
        return true;
    }

    /// <summary>
    /// Drops a harvested row and its audio file. Harvested audio is ephemeral by design — it
    /// lives in a size-capped folder and gets evicted anyway — so discarding one costs nothing,
    /// and the playback engine already skips a file that has gone missing under a queued track.
    ///
    /// Only ever called for <see cref="SongSource.Harvested"/>: a song the user deliberately
    /// saved is theirs, and no heuristic of ours gets to delete it.
    /// </summary>
    private void RetireHarvested(SavedSong song, string reason)
    {
        AppLog.Info($"[Library] retiring harvested \"{song.Artist} - {song.Title}\" "
                    + $"({song.Station}) — {reason}");
        _store.Remove(song.Path);
        try { File.Delete(song.Path); } catch { /* the cache cap would have taken it anyway */ }
    }

    /// <summary>
    /// Describes a row in the background, and — for harvested rows only — drops it if the model
    /// says it isn't music.
    ///
    /// <para>This call already happens for every harvested segment, so the verdict rides along
    /// for the price of one extra field. It is worth having because it is the only gate in the
    /// pipeline with general world knowledge: <see cref="SongHistoryFilter"/> can only catch
    /// shapes we anticipated, whereas a model that knows music can tell that ADWTAG_122000 is
    /// not an artist and that Blink_182 is, without either being written down anywhere.</para>
    ///
    /// <para>It is not, strictly, a gate before playback — the harvest raises SegmentIndexed the
    /// moment the row lands, so the song joins the queue while this is still in flight. But the
    /// queue appends to the tail and this is a one-shot Haiku call, so in practice the verdict
    /// arrives minutes before the song could reach the front. Worst case it is a cleanup.</para>
    /// </summary>
    private void EnrichInBackground(SavedSong song)
    {
        var (path, title, artist) = (song.Path, song.Title, song.Artist);
        if (!_inFlight.TryAdd(path, 0))
            return; // already working on this one

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var distilled = CanDistill ? await DistillAsync(title, artist).ConfigureAwait(false) : null;

                if (distilled is { IsSong: false })
                {
                    if (song.Source == SongSource.Harvested)
                    {
                        RetireHarvested(song, "the description model says it isn't music");
                        return; // nothing left to describe or embed
                    }
                    // The user saved this one deliberately, so the verdict is advice, not a
                    // licence to delete. Describe it like anything else.
                    AppLog.Debug($"[Library] model doubts \"{artist} - {title}\" is music — "
                                 + "keeping it, the user saved it");
                }

                var description = distilled?.Description;
                if (string.IsNullOrWhiteSpace(description))
                    description = BuildFallbackDescription(title, artist); // no key / model failed

                _store.SetEnrichment(path, description, distilled?.FacetsJson);
                EmbedAndStore(path, description);
                AppLog.Debug($"[Library] enriched {title} — {artist}");
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[Library] enrich failed for {path}: {ex.Message}");
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
            AppLog.Debug($"[Library] embed failed for {path}: {ex.Message}");
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

    internal static DistillResult? ParseDistill(string? modelText)
    {
        var json = StripToJsonObject(modelText);
        if (json is null) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }

        var isSong = ReadIsSong(node);

        var description = node?["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description))
            return isSong ? null : new DistillResult("", null, IsSong: false);

        var facets = new JsonObject
        {
            ["genres"] = node?["genres"]?.DeepClone() ?? new JsonArray(),
            ["moods"] = node?["moods"]?.DeepClone() ?? new JsonArray(),
            ["era"] = node?["era"]?.DeepClone()
        };
        return new DistillResult(description.Trim(), facets.ToJsonString(), isSong);
    }

    /// <summary>
    /// Reads the model's "is this music at all" verdict. Everything unclear reads as true: a
    /// missing field, a null, a number, a word we don't recognise. This decides whether a file
    /// gets deleted, so silence must never be taken for a "no".
    /// </summary>
    private static bool ReadIsSong(JsonNode? node)
    {
        var value = node?["is_song"];
        if (value is null) return true;

        // Every other shape — a number, an array, a word we don't know — falls to the default.
        return value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String =>
                !string.Equals(value.GetValue<string>(), "false", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static string? StripToJsonObject(string? text) => AnthropicApi.StripToJsonObject(text);

    internal sealed record DistillResult(string Description, string? FacetsJson, bool IsSong = true);
}
