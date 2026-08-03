using System.Net;
using System.Net.Http;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// DJ mode's on-air intro line. The prose itself can't be asserted, but the machinery that shapes
/// it can: which persona reached the model, that consecutive calls ask for different sentence
/// shapes (the thing that stops every line reading as one template), and that a bad reply degrades
/// to no line rather than something odd on screen.
/// </summary>
public class DjIntroServiceTests
{
    private static DjIntroService Service(FakeHttpMessageHandler http,
        DjPersonality personality = DjPersonality.Warm) =>
        new(http.Client(), "test-key", personality);

    private static FakeHttpMessageHandler Handler(int replies = 1)
    {
        var h = new FakeHttpMessageHandler();
        for (var i = 0; i < replies; i++)
            h.RespondWithText($$"""{ "line": "line {{i}}" }""");
        return h;
    }

    [Fact]
    public async Task ReturnsTheLine()
    {
        var http = new FakeHttpMessageHandler().RespondWithText("""{ "line": "Something for the small hours." }""");

        Assert.Equal("Something for the small hours.",
            await Service(http).GetIntroAsync("Bloom Baby Bloom", "Wolf Alice", "late night indie"));
    }

    /// <summary>The core of the personality change: the same persona wording must not produce the
    /// same INSTRUCTION every time, or the model settles into one sentence shape.</summary>
    [Fact]
    public async Task AsksForADifferentSentenceShapeOnConsecutiveCalls()
    {
        var http = Handler(4);
        var service = Service(http);

        // Distinct titles so the per-session cache doesn't short-circuit the calls.
        for (var i = 0; i < 4; i++)
            await service.GetIntroAsync($"Track {i}", "Artist", "some vibe");

        var moves = http.Requests.Select(ExtractMoveLine).ToList();
        Assert.Equal(4, moves.Distinct().Count());
    }

    [Theory]
    [InlineData(DjPersonality.Warm, "warm")]
    [InlineData(DjPersonality.Upbeat, "energetic")]
    [InlineData(DjPersonality.LateNight, "small hours")]
    [InlineData(DjPersonality.Wry, "sardonic")]
    [InlineData(DjPersonality.Professional, "understated")]
    public async Task SendsTheConfiguredPersona(DjPersonality personality, string marker)
    {
        var http = Handler();

        await Service(http, personality).GetIntroAsync("Track", "Artist", "vibe");

        Assert.Contains(marker, Assert.Single(http.Requests), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The voice used to be baked into a prompt string in the constructor, so changing it
    /// in the options dialog did nothing until relaunch.</summary>
    [Fact]
    public async Task ChangingThePersonaAffectsTheVeryNextLine()
    {
        var http = Handler();
        var sut = Service(http, DjPersonality.Professional);

        await sut.GetIntroAsync("Track", "Artist", "vibe");
        sut.Personality = DjPersonality.Wry;
        await sut.GetIntroAsync("Another Track", "Another Artist", "vibe");

        Assert.DoesNotContain("sardonic", http.Requests[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sardonic", http.Requests[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotSendAnotherPersonasWording()
    {
        var http = Handler();

        await Service(http, DjPersonality.Professional).GetIntroAsync("Track", "Artist", "vibe");

        Assert.DoesNotContain("sardonic", http.Requests[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The curator's note is background, not the subject — the prompt has to say so, since
    /// the previous wording called it "the real why this song" and every line explained the match.</summary>
    [Fact]
    public async Task TellsTheModelTheCuratorNoteIsNotTheSubject()
    {
        var http = Handler();

        await Service(http).GetIntroAsync("Track", "Artist", "vibe", curatorNote: "deep and hypnotic");

        var sent = http.Requests[0];
        Assert.Contains("deep and hypnotic", sent);                 // still provided
        Assert.Contains("background for YOU", sent);                // but demoted
    }

    [Fact]
    public async Task CachesPerTrackSoAReplayKeepsItsLine()
    {
        var http = Handler(1);   // a second call would throw for want of a scripted reply
        var service = Service(http);

        var first = await service.GetIntroAsync("Maniac", "Michael Sembello", "80s");
        var second = await service.GetIntroAsync("Maniac", "Michael Sembello", "80s");

        Assert.Equal(first, second);
        Assert.Equal(1, http.CallCount);
    }

    [Theory]
    [InlineData("""{ "line": "" }""")]
    [InlineData("""{ "line": "   " }""")]
    [InlineData("""{ "something": "else" }""")]
    [InlineData("not json")]
    public async Task ReturnsNullRatherThanShowingSomethingOdd(string reply)
    {
        var http = new FakeHttpMessageHandler().RespondWithText(reply);

        Assert.Null(await Service(http).GetIntroAsync("Track", "Artist", "vibe"));
    }

    [Fact]
    public async Task StripsMarkupAndCollapsesWhitespace()
    {
        var http = new FakeHttpMessageHandler()
            .RespondWithText("""{ "line": "<em>Lights   down</em> &amp; volume up." }""");

        Assert.Equal("Lights down & volume up.",
            await Service(http).GetIntroAsync("Track", "Artist", "vibe"));
    }

    [Fact]
    public async Task ReturnsNullOnAnApiError()
    {
        var http = new FakeHttpMessageHandler().RespondWithStatus(HttpStatusCode.TooManyRequests);

        Assert.Null(await Service(http).GetIntroAsync("Track", "Artist", "vibe"));
    }

    [Fact]
    public async Task DoesNotCallTheApiWithoutAKeyOrATitle()
    {
        var http = new FakeHttpMessageHandler();
        var unconfigured = new DjIntroService(http.Client(), apiKey: null);

        Assert.False(unconfigured.IsConfigured);
        Assert.Null(await unconfigured.GetIntroAsync("Track", "Artist", "vibe"));
        Assert.Null(await Service(http).GetIntroAsync("  ", "Artist", "vibe"));
        Assert.Equal(0, http.CallCount);
    }

    // --- Session patter: the lines for moments that aren't a track (#5) -----------------------

    private const string PatterReply = """
        { "sourcing": ["s1","s2","s3"], "waiting": ["w1","w2","w3"],
          "bridging": ["b1","b2","b3"], "signingOff": ["o1","o2","o3"] }
        """;

    [Fact]
    public async Task ReturnsAllFourMomentsOfPatter()
    {
        var http = new FakeHttpMessageHandler().RespondWithText(PatterReply);

        var patter = await Service(http).GetSessionPatterAsync("deep house for coding");

        Assert.NotNull(patter);
        Assert.Equal(["s1", "s2", "s3"], patter!.For(DjMoment.Sourcing));
        Assert.Equal(["w1", "w2", "w3"], patter.For(DjMoment.Waiting));
        Assert.Equal(["b1", "b2", "b3"], patter.For(DjMoment.Bridging));
        Assert.Equal(["o1", "o2", "o3"], patter.For(DjMoment.SigningOff));
    }

    [Fact]
    public async Task SendsTheVibeAndThePersonaWithThePatterRequest()
    {
        var http = new FakeHttpMessageHandler().RespondWithText(PatterReply);

        await Service(http, DjPersonality.LateNight).GetSessionPatterAsync("deep house for coding");

        var sent = http.Requests[0];
        Assert.Contains("deep house for coding", sent);
        Assert.Contains("small hours", sent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlesAMissingVibeWithoutSendingAnEmptyQuote()
    {
        var http = new FakeHttpMessageHandler().RespondWithText(PatterReply);

        Assert.NotNull(await Service(http).GetSessionPatterAsync(null));
        // Deliberately an apostrophe-free substring. System.Text.Json's default encoder escapes
        // apostrophes to their numeric form on the wire, so asserting on a phrase containing one
        // would be testing the encoder rather than the prompt.
        Assert.Contains("say what they wanted", http.Requests[0]);
    }

    /// <summary>All four moments or none. A half-filled set would leave some moments in the DJ's
    /// voice and others in the app's own wording, which reads worse than using the fallbacks
    /// throughout.</summary>
    [Theory]
    [InlineData("""{ "sourcing": ["s"], "waiting": ["w"], "bridging": ["b"] }""")]
    [InlineData("""{ "sourcing": ["s"], "waiting": [], "bridging": ["b"], "signingOff": ["o"] }""")]
    [InlineData("""{ "sourcing": ["s"], "waiting": ["  "], "bridging": ["b"], "signingOff": ["o"] }""")]
    [InlineData("""{ "sourcing": "not an array", "waiting": ["w"], "bridging": ["b"], "signingOff": ["o"] }""")]
    [InlineData("sorry, I can't do that")]
    public async Task ReturnsNullWhenAnyMomentIsMissing(string reply)
    {
        var http = new FakeHttpMessageHandler().RespondWithText(reply);

        Assert.Null(await Service(http).GetSessionPatterAsync("vibe"));
    }

    [Fact]
    public async Task AcceptsFewerThanThreeLinesPerMomentAsLongAsEachHasOne()
    {
        // The prompt asks for three; one is still usable, it just repeats sooner.
        var http = new FakeHttpMessageHandler().RespondWithText(
            """{ "sourcing": ["s"], "waiting": ["w"], "bridging": ["b"], "signingOff": ["o"] }""");

        var patter = await Service(http).GetSessionPatterAsync("vibe");

        Assert.Equal(["s"], patter!.For(DjMoment.Sourcing));
    }

    [Fact]
    public async Task ReturnsNullPatterWithoutAKeyAndNeverCallsTheApi()
    {
        var http = new FakeHttpMessageHandler();

        Assert.Null(await new DjIntroService(http.Client(), apiKey: null).GetSessionPatterAsync("vibe"));
        Assert.Equal(0, http.CallCount);
    }

    [Fact]
    public async Task ReturnsNullPatterOnAnApiError()
    {
        var http = new FakeHttpMessageHandler().RespondWithStatus(HttpStatusCode.InternalServerError);

        Assert.Null(await Service(http).GetSessionPatterAsync("vibe"));
    }

    [Fact]
    public async Task PropagatesPatterCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var http = new FakeHttpMessageHandler().RespondWithText(PatterReply);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(http).GetSessionPatterAsync("vibe", cts.Token));
    }

    // The move directive is the line beginning "This time:" in the system prompt.
    private static string ExtractMoveLine(string requestBody)
    {
        var i = requestBody.IndexOf("This time:", StringComparison.Ordinal);
        Assert.True(i >= 0, "no move directive was sent");
        return requestBody[i..Math.Min(i + 60, requestBody.Length)];
    }
}
