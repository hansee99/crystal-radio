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

    // The move directive is the line beginning "This time:" in the system prompt.
    private static string ExtractMoveLine(string requestBody)
    {
        var i = requestBody.IndexOf("This time:", StringComparison.Ordinal);
        Assert.True(i >= 0, "no move directive was sent");
        return requestBody[i..Math.Min(i + 60, requestBody.Length)];
    }
}
