using System.Net;
using System.Net.Http;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The LRCLIB client. Response shapes below are the real ones, taken from live calls and from the
/// API docs — including the two that bite: a <b>200 can carry null lyrics</b> ("we know this track"
/// is not "we have its words"), and <c>instrumental: true</c> is a legitimate answer that must not
/// read as a miss.
///
/// <para>Coverage is 37% against this app's own library, so a miss is the common case and has to be
/// completely unremarkable — never an exception, never a stuck UI.</para>
/// </summary>
public class LyricsServiceTests
{
    private const string Hit = """
        { "id": 537434, "name": "Silent Lucidity", "trackName": "Silent Lucidity",
          "artistName": "Queensrÿche", "albumName": "Empire", "duration": 348.0,
          "instrumental": false, "plainLyrics": "Hush now, don't you cry\nWipe away the teardrop",
          "syncedLyrics": "[00:17.12] Hush now" }
        """;

    private static LyricsService Service(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler));

    // --- The happy path, and the metadata that comes with it ----------------------------------

    [Fact]
    public async Task ReturnsLyricsAndTheMetadataThatRidesAlong()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson(Hit);

        var result = await Service(handler).LookupAsync("Queensrÿche", "Silent Lucidity");

        Assert.NotNull(result);
        Assert.Equal("Silent Lucidity", result!.Title);
        Assert.Equal("Queensrÿche", result.Artist);
        Assert.Equal("Empire", result.Album);      // the app has no other source for this
        Assert.False(result.Instrumental);
        Assert.Contains("Hush now", result.Lyrics);
        Assert.Equal(348.0, result.DurationSeconds);
    }

    /// <summary>Instrumental is an ANSWER, not a miss — "this song has no words" and "we couldn't
    /// find this song" are different things and the UI says so differently.</summary>
    [Fact]
    public async Task AnInstrumentalIsAResultWithNoLyrics()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson("""
            { "trackName": "Oxygene Part 4", "artistName": "Jean-Michel Jarre",
              "albumName": "Oxygene", "duration": 236.0, "instrumental": true,
              "plainLyrics": null, "syncedLyrics": null }
            """);

        var result = await Service(handler).LookupAsync("Jean-Michel Jarre", "Oxygene Part 4");

        Assert.NotNull(result);
        Assert.True(result!.Instrumental);
        Assert.Null(result.Lyrics);
    }

    /// <summary>Seen live: a 200 whose plainLyrics and syncedLyrics are both null while
    /// instrumental is false. Neither lyrics nor an instrumental — still a match, still has an
    /// album, and must not be mistaken for either of the other two states.</summary>
    [Fact]
    public async Task AMatchCanArriveWithNoLyricsAndNotBeInstrumental()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson("""
            { "trackName": "Adagio for Strings", "artistName": "Tiësto", "albumName": "Just Be",
              "duration": 450.0, "instrumental": false, "plainLyrics": null, "syncedLyrics": null }
            """);

        var result = await Service(handler).LookupAsync("Tiësto", "Adagio for Strings");

        Assert.NotNull(result);
        Assert.Null(result!.Lyrics);
        Assert.False(result.Instrumental);
        Assert.Equal("Just Be", result.Album);
    }

    // --- Misses and failures are ordinary ------------------------------------------------------

    [Fact]
    public async Task A404FallsBackToSearch()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithStatus(HttpStatusCode.NotFound, """{"code":404,"name":"TrackNotFound"}""")
            .RespondWithJson($"[{Hit}]");

        var result = await Service(handler).LookupAsync("Queensrÿche", "Silent Lucidity");

        Assert.NotNull(result);
        Assert.Equal("Empire", result!.Album);
        Assert.Equal(2, handler.CallCount);   // /api/get then /api/search
    }

    [Fact]
    public async Task A404WithNothingInSearchEitherIsJustNull()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithStatus(HttpStatusCode.NotFound, """{"code":404,"name":"TrackNotFound"}""")
            .RespondWithJson("[]");

        Assert.Null(await Service(handler).LookupAsync("Nobody", "Nothing At All"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AServerErrorIsNotASearchFallback(HttpStatusCode status)
    {
        // Only a 404 means "not in the database". A 5xx means we learned nothing, and hammering
        // /api/search after it just doubles the load on a service that is already struggling.
        var handler = new FakeHttpMessageHandler().RespondWithStatus(status);

        Assert.Null(await Service(handler).LookupAsync("Artist", "Title"));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GarbageJsonIsAMissRatherThanAThrow()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson("not json at all {");

        Assert.Null(await Service(handler).LookupAsync("Artist", "Title"));
    }

    [Theory]
    [InlineData(null, "Title")]
    [InlineData("Artist", null)]
    [InlineData("", "Title")]
    [InlineData("   ", "   ")]
    public async Task BlankInputNeverReachesTheNetwork(string? artist, string? title)
    {
        var handler = new FakeHttpMessageHandler();

        Assert.Null(await Service(handler).LookupAsync(artist, title));
        Assert.Equal(0, handler.CallCount);
    }

    // --- Behaving as LRCLIB asks ---------------------------------------------------------------

    /// <summary>Their one condition for an unauthenticated API: identify yourself, with name,
    /// version and a link. Free service, cheap courtesy, easy to drop by accident.</summary>
    [Fact]
    public async Task IdentifiesItselfWithTheDocumentedUserAgentFormat()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson(Hit);
        var client = new HttpClient(handler);

        await new LyricsService(client).LookupAsync("Queensrÿche", "Silent Lucidity");

        var ua = client.DefaultRequestHeaders.UserAgent.ToString();
        Assert.Contains("CrystalRadio/", ua);
        Assert.Contains("github.com", ua);   // "a link to its homepage or project page"

        // The version must come from the BUILD, not a literal. It was hardcoded to 1.9.0 while the
        // assembly said 1.8.2, so LRCLIB was told a different version from the one running.
        var version = typeof(LyricsService).Assembly.GetName().Version!;
        Assert.Contains($"CrystalRadio/{version.Major}.{version.Minor}.{version.Build}", ua);
    }

    [Fact]
    public async Task DurationIsSentWhenKnownBecauseItSharpensTheMatch()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson(Hit);

        await Service(handler).LookupAsync("Queensrÿche", "Silent Lucidity", durationSeconds: 348);

        Assert.Contains("duration=348", handler.RequestUris[0].Query);
    }

    /// <summary>The trap in the docs: LRCLIB only matches within ±2 s. A harvested segment has been
    /// edge-trimmed, so its length is NOT the track's — sending it converts hits into misses. The
    /// harvest path therefore passes nothing and this must stay optional.</summary>
    [Fact]
    public async Task NoDurationIsSentWhenTheCallerDoesNotKnowIt()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson(Hit);

        await Service(handler).LookupAsync("Queensrÿche", "Silent Lucidity");

        Assert.DoesNotContain("duration=", handler.RequestUris[0].Query);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(4000)]
    public async Task AnOutOfRangeDurationIsOmittedRatherThanSent(double duration)
    {
        // The docs bound it to 1..3600; sending outside that is a request we know will be rejected.
        var handler = new FakeHttpMessageHandler().RespondWithJson(Hit);

        await Service(handler).LookupAsync("Artist", "Title", duration);

        Assert.DoesNotContain("duration=", handler.RequestUris[0].Query);
    }

    [Fact]
    public async Task ARepeatedLookupIsServedFromCache()
    {
        var handler = new FakeHttpMessageHandler().RespondWithJson(Hit);
        var sut = Service(handler);

        await sut.LookupAsync("Queensrÿche", "Silent Lucidity");
        await sut.LookupAsync("queensrÿche", "  Silent Lucidity  ");   // same track, sloppier

        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>Misses are cached too. 63% of lookups miss, and re-asking LRCLIB for a track it has
    /// already said it doesn't have is exactly the inconsiderate behaviour their docs ask us to
    /// avoid.</summary>
    [Fact]
    public async Task AMissIsCachedSoItIsNotAskedTwice()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithStatus(HttpStatusCode.NotFound, "")
            .RespondWithJson("[]");
        var sut = Service(handler);

        await sut.LookupAsync("Nobody", "Nothing");
        await sut.LookupAsync("Nobody", "Nothing");

        Assert.Equal(2, handler.CallCount);   // the first lookup's get+search, and nothing more
    }

    // --- The excerpt used for DJ remarks -------------------------------------------------------

    private const string ThreeLines =
        "Hush now, don't you cry\nWipe away the teardrop from your eye\nYou're lying safe in bed";

    [Fact]
    public void ExcerptPrefersALineBreak()
    {
        // Stopping at the end of a line makes an excerpt read as a quote, not a truncation.
        var excerpt = new TrackLyrics("T", "A", null, false, ThreeLines, 348).Excerpt(maxChars: 70);

        Assert.EndsWith("eye", excerpt);
        Assert.DoesNotContain("lying safe", excerpt);
    }

    /// <summary>When the only line break inside the limit is too early to be worth using, the cut
    /// falls back to a WORD boundary. The first version hard-cut at maxChars here and produced
    /// "...Wipe away the teardro" — the exact mid-word cut the method promises not to make.</summary>
    [Fact]
    public void ExcerptFallsBackToAWordBoundaryNotAHardCut()
    {
        var excerpt = new TrackLyrics("T", "A", null, false, ThreeLines, 348).Excerpt(maxChars: 50);

        Assert.NotNull(excerpt);
        Assert.EndsWith("teardrop", excerpt);
    }

    [Fact]
    public void ExcerptNeverExceedsItsLimit()
    {
        foreach (var limit in new[] { 20, 30, 50, 70, 200 })
            Assert.True(
                new TrackLyrics("T", "A", null, false, ThreeLines, 1).Excerpt(limit)!.Length <= limit,
                $"excerpt exceeded {limit}");
    }

    [Fact]
    public void ShortLyricsAreReturnedWhole()
    {
        var track = new TrackLyrics("T", "A", null, false, "La la la", 100);

        Assert.Equal("La la la", track.Excerpt());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ThereIsNoExcerptWithoutLyrics(string? lyrics)
    {
        Assert.Null(new TrackLyrics("T", "A", null, false, lyrics, 100).Excerpt());
    }
}
