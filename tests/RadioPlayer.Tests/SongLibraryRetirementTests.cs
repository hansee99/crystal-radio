using System.IO;
using System.Net.Http;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Drives the real <see cref="SongLibraryService"/> against a real (temp) library.db and a real
/// file on disk, because the interesting behaviour is a <b>deletion</b> — a parser test can't
/// show that the row and the audio actually go away, or that a user-saved song survives the same
/// verdict.
/// </summary>
public sealed class SongLibraryRetirementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crystal-radio-tests", Guid.NewGuid().ToString("n"));
    private readonly LibraryStore _store;

    public SongLibraryRetirementTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new LibraryStore(Path.Combine(_dir, "library.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    /// <summary>Embeddings are a separate concern; off here so a retirement test doesn't need a
    /// 90MB ONNX model on the machine running it.</summary>
    private sealed class NoEmbeddings : IEmbeddingProvider
    {
        public string ModelId => "none";
        public int Dimension => 0;
        public bool IsAvailable => false;
        public float[]? Embed(string text) => null;
    }

    private string WriteAudioFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[64]); // stands in for the harvested mp3
        return path;
    }

    private SongLibraryService ServiceReplying(string modelText)
    {
        var handler = new FakeHttpMessageHandler().RespondWithText(modelText);
        return new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(), apiKey: "test-key");
    }

    /// <summary>Enrichment is deliberately fire-and-forget, so there is nothing to await. Poll
    /// for the outcome rather than sleeping a fixed time, which is either flaky or slow.</summary>
    private async Task<bool> Settles(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    private SavedSong Song(string path, string title, string artist, SongSource source) =>
        new(path, title, artist, "Hard Rock Heaven", "mp3", DateTimeOffset.Now, Source: source);

    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AHarvestedRowTheModelCallsNonMusicIsDeleted()
    {
        var path = WriteAudioFile("adbreak.mp3");
        var service = ServiceReplying("""{ "is_song": false, "description": "An advertising break." }""");

        service.AddAndEnrich(Song(path, "THIS STATION WILL CONTINUE AFTER THIS BREAK", "ADWTAG_122000",
            SongSource.Harvested));

        Assert.True(await Settles(() => !File.Exists(path)), "the audio file should have been deleted");
        Assert.DoesNotContain(_store.GetAll(), s => s.Path == path);
    }

    [Fact]
    public async Task AHarvestedRowTheModelCallsMusicIsKeptAndDescribed()
    {
        var path = WriteAudioFile("song.mp3");
        var service = ServiceReplying(
            """{ "is_song": true, "description": "Melodic hard rock with twin guitars.", "genres": ["hard rock"] }""");

        service.AddAndEnrich(Song(path, "Wasted Years", "Iron Maiden", SongSource.Harvested));

        Assert.True(await Settles(() => _store.GetAll()
            .Any(s => s.Path == path && !string.IsNullOrWhiteSpace(s.Description))));
        Assert.True(File.Exists(path));
        Assert.Contains("twin guitars", _store.GetAll().Single(s => s.Path == path).Description);
    }

    /// <summary>The inversion from CLAUDE.md: a song the user deliberately saved is theirs. The
    /// model's verdict is advice about a harvest, not a licence to delete someone's library.</summary>
    [Fact]
    public async Task AUserSavedRowSurvivesTheSameVerdict()
    {
        var path = WriteAudioFile("kept.mp3");
        var service = ServiceReplying("""{ "is_song": false, "description": "Sounds like a jingle." }""");

        service.AddAndEnrich(Song(path, "Some Obscure B-Side", "A Band Nobody Knows", SongSource.UserSaved));

        Assert.True(await Settles(() => _store.GetAll()
            .Any(s => s.Path == path && !string.IsNullOrWhiteSpace(s.Description))));
        Assert.True(File.Exists(path));
    }

    /// <summary>Without an API key there is no verdict at all, so nothing may be deleted on the
    /// strength of one. The fallback description keeps the row usable for curation.</summary>
    [Fact]
    public async Task WithNoApiKeyNothingIsJudgedAndNothingIsDeleted()
    {
        var path = WriteAudioFile("nokey.mp3");
        var handler = new FakeHttpMessageHandler();
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(), apiKey: null);

        service.AddAndEnrich(Song(path, "Wasted Years", "Iron Maiden", SongSource.Harvested));

        Assert.True(await Settles(() => _store.GetAll()
            .Any(s => s.Path == path && !string.IsNullOrWhiteSpace(s.Description))));
        Assert.True(File.Exists(path));
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>
    /// The live-key path, proved through a real service rather than the holder alone: a service
    /// built at startup with no key must start working the moment one is saved, with no restart.
    /// </summary>
    [Fact]
    public async Task AKeySavedAfterStartupTakesEffectWithoutARestart()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "is_song": false, "description": "An advertising break." }""");
        var keys = new ApiKeySource();                  // launched with no key configured
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(), keys);

        keys.Current = "sk-ant-pasted-into-the-dialog";

        var path = WriteAudioFile("after-key.mp3");
        service.AddAndEnrich(Song(path, "ADBREAK_120000", "011FM", SongSource.Harvested));

        Assert.True(await Settles(() => !File.Exists(path)), "the model should have been consulted");
        Assert.Equal(1, handler.CallCount);
    }

    // --- Album name from LRCLIB (#36) ---------------------------------------------------------
    // ICY metadata carries title and artist only, so the album is information the app has no other
    // source for. Unlike the embedding index, enrichment already degrades gracefully — it has a
    // tags-only fallback — so partial coverage is the existing norm here rather than a new hazard.

    private sealed class StubLyrics : ILyricsService
    {
        public string? Album { get; init; }
        public int Calls { get; private set; }
        public double? DurationAsked { get; private set; }

        public Task<TrackLyrics?> LookupAsync(string? artist, string? title,
            double? durationSeconds = null, CancellationToken ct = default)
        {
            Calls++;
            DurationAsked = durationSeconds;
            return Task.FromResult(Album is null
                ? null
                : new TrackLyrics(title ?? "", artist ?? "", Album, false, null, 0));
        }

        /// <summary>Rows offered for a title-only search (#31 repair). Empty unless a test sets it.</summary>
        public List<TrackLyrics> TitleSearchRows { get; } = [];

        public int TitleSearches { get; private set; }

        public Task<IReadOnlyList<TrackLyrics>> SearchByTitleAsync(string? title, CancellationToken ct = default)
        {
            TitleSearches++;
            return Task.FromResult<IReadOnlyList<TrackLyrics>>(TitleSearchRows);
        }
    }

    [Fact]
    public async Task TheAlbumReachesTheDescriptionPrompt()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "is_song": true, "description": "Melodic hard rock." }""");
        var path = WriteAudioFile("with-album.mp3");
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(),
            apiKey: "test-key", lyrics: new StubLyrics { Album = "Empire" });

        service.AddAndEnrich(Song(path, "Silent Lucidity", "Queensrÿche", SongSource.Harvested));

        Assert.True(await Settles(() => handler.CallCount > 0));
        Assert.Contains("Empire", handler.Requests[0]);
    }

    // --- #31: names the station broadcast already broken -----------------------------

    /// <summary>
    /// The end-to-end shape of the repair: LRCLIB's title-only search offers several artists, the
    /// surviving characters pick the one that fits, and the corrected name lands in the row the UI
    /// reads. No model call is needed for this path.
    /// </summary>
    [Fact]
    public async Task ADamagedArtistIsRepairedFromTheTitleSearch()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "is_song": true, "description": "Progressive metal." }""");
        var path = WriteAudioFile("damaged.mp3");
        var lyrics = new StubLyrics { Album = "Empire" };
        lyrics.TitleSearchRows.AddRange(
        [
            new TrackLyrics("Breaking the Silence", "GET IN THE RING", null, false, null, 0),
            new TrackLyrics("Breaking the Silence", "Loreena McKennitt", null, false, null, 0),
            new TrackLyrics("Breaking the Silence", "Queensrÿche", "Empire", false, null, 0),
        ]);
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(),
            apiKey: "test-key", lyrics: lyrics);

        service.AddAndEnrich(Song(path, "Breaking the Silence", "Queensr�che", SongSource.Harvested));

        Assert.True(await Settles(() => _store.Get(path)?.Artist == "Queensrÿche"));
        Assert.Equal(1, lyrics.TitleSearches);
    }

    /// <summary>A clean name must not trigger a lookup, let alone be rewritten.</summary>
    [Fact]
    public async Task AnUndamagedNameIsLeftAloneAndCostsNoSearch()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "is_song": true, "description": "Progressive metal." }""");
        var path = WriteAudioFile("clean.mp3");
        var lyrics = new StubLyrics { Album = "Empire" };
        lyrics.TitleSearchRows.Add(new TrackLyrics("Silent Lucidity", "Somebody Else", null, false, null, 0));
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(),
            apiKey: "test-key", lyrics: lyrics);

        service.AddAndEnrich(Song(path, "Silent Lucidity", "Queensrÿche", SongSource.Harvested));

        Assert.True(await Settles(() => handler.CallCount > 0));
        Assert.Equal("Queensrÿche", _store.Get(path)!.Artist);
        Assert.Equal(0, lyrics.TitleSearches);
    }

    /// <summary>
    /// When nothing offered fits the surviving characters, the damaged name stays. Writing one of
    /// those rows would replace a visibly broken name with a confidently wrong one.
    /// </summary>
    [Fact]
    public async Task AnUnrepairableNameIsLeftDamagedRatherThanGuessed()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "artist": "Loreena McKennitt", "title": "Breaking the Silence" }""");
        var path = WriteAudioFile("unrepairable.mp3");
        var lyrics = new StubLyrics();
        lyrics.TitleSearchRows.Add(new TrackLyrics("Breaking the Silence", "Loreena McKennitt", null, false, null, 0));
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(),
            apiKey: "test-key", lyrics: lyrics);

        service.AddAndEnrich(Song(path, "Breaking the Silence", "Queensr�che", SongSource.Harvested));

        Assert.True(await Settles(() => handler.CallCount > 0));
        Assert.Equal("Queensr�che", _store.Get(path)!.Artist);
    }

    /// <summary>The ±2s trap: a harvested segment is edge-trimmed, so its length is not the track's
    /// and sending it would turn a hit into a miss. This path must never pass one.</summary>
    [Fact]
    public async Task NoDurationIsSentFromTheEnrichmentPath()
    {
        var stub = new StubLyrics { Album = "Empire" };
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "is_song": true, "description": "d" }""");
        var path = WriteAudioFile("no-duration.mp3");
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(),
            apiKey: "test-key", lyrics: stub);

        service.AddAndEnrich(Song(path, "Wasted Years", "Iron Maiden", SongSource.Harvested));

        Assert.True(await Settles(() => stub.Calls > 0));
        Assert.Null(stub.DurationAsked);
    }

    /// <summary>LRCLIB misses roughly two thirds of harvested tracks. Enrichment must be exactly as
    /// good as before when it does.</summary>
    [Fact]
    public async Task AMissedAlbumStillEnriches()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "is_song": true, "description": "Still described fine." }""");
        var path = WriteAudioFile("no-album.mp3");
        var service = new SongLibraryService(new HttpClient(handler), _store, new NoEmbeddings(),
            apiKey: "test-key", lyrics: new StubLyrics { Album = null });

        service.AddAndEnrich(Song(path, "Nectarine", "Linkwood", SongSource.Harvested));

        Assert.True(await Settles(() => _store.GetAll()
            .Any(s => s.Path == path && !string.IsNullOrWhiteSpace(s.Description))));
        Assert.Contains("Still described fine.", _store.GetAll().Single(s => s.Path == path).Description);
    }

    /// <summary>A 500, a timeout or a reply we can't parse means we learned nothing — which must
    /// not be confused with "not music".</summary>
    [Fact]
    public async Task AFailedOrUnparseableReplyDeletesNothing()
    {
        var path = WriteAudioFile("garbled.mp3");
        var service = ServiceReplying("I'm sorry, I can't help with that.");

        service.AddAndEnrich(Song(path, "Wasted Years", "Iron Maiden", SongSource.Harvested));

        Assert.True(await Settles(() => _store.GetAll()
            .Any(s => s.Path == path && !string.IsNullOrWhiteSpace(s.Description))));
        Assert.True(File.Exists(path));
    }

    /// <summary>The model may decide it's an ad break and not bother describing it. The verdict
    /// still has to land.</summary>
    [Fact]
    public async Task AVerdictWithNoDescriptionStillRetires()
    {
        var path = WriteAudioFile("verdict-only.mp3");
        var service = ServiceReplying("""{ "is_song": false }""");

        service.AddAndEnrich(Song(path, "ADBREAK_120000", "011FM", SongSource.Harvested));

        Assert.True(await Settles(() => !File.Exists(path)));
        Assert.DoesNotContain(_store.GetAll(), s => s.Path == path);
    }
}
