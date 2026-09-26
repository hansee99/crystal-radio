using System.Net;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The ranker decides which stations a user is offered and which stations DJ mode harvests from,
/// entirely on a model's say-so. These cover what it does with replies that are malformed,
/// out of range, or simply don't follow the instructions — the cases that decide whether a bad
/// reply degrades gracefully or quietly corrupts the results.
/// </summary>
public class LlmSearchRankerTests
{
    private static readonly IReadOnlyList<RankCandidate> ThreeCandidates =
    [
        new(0, "Jazz FM", "smooth jazz"),
        new(1, "Rock Radio", "classic rock"),
        new(2, "Talk Nation", "phone-ins")
    ];

    private static LlmSearchRanker Ranker(FakeHttpMessageHandler handler) =>
        new(handler.Client(), "test-key");

    [Fact]
    public async Task ReturnsRankedCandidatesBestFirst()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "ranked": [ {"id": 1, "score": 0.9}, {"id": 0, "score": 0.7} ] }""");

        var verdicts = await Ranker(handler).RankAsync("rock", ThreeCandidates, topK: 5);

        Assert.NotNull(verdicts);
        Assert.Equal([1, 0], verdicts!.Select(v => v.Id));
    }

    /// <summary>The system prompt asks for >= 0.5 only, but a prompt is a request, not a
    /// guarantee. The floor is enforced client-side so one lax reply can't put a loosely-related
    /// station into a harvest pool for a whole session.</summary>
    [Fact]
    public async Task DropsCandidatesBelowTheRelevanceFloor()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""
                { "ranked": [ {"id": 0, "score": 0.9}, {"id": 1, "score": 0.31}, {"id": 2, "score": 0.0} ] }
                """);

        var verdicts = await Ranker(handler).RankAsync("jazz", ThreeCandidates, topK: 5);

        Assert.Equal([0], verdicts!.Select(v => v.Id));
    }

    /// <summary>A missing or unparseable score reads as 0.0, which must not sneak past the floor.</summary>
    [Fact]
    public async Task TreatsAMissingScoreAsNoMatch()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""{ "ranked": [ {"id": 0}, {"id": 1, "score": "high"} ] }""");

        Assert.Empty((await Ranker(handler).RankAsync("jazz", ThreeCandidates, 5))!);
    }

    [Fact]
    public async Task IgnoresIdsThatAreNotInTheCandidateList()
    {
        // An index outside the pool would throw on the caller's pool[v.Id] lookup.
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""
                { "ranked": [ {"id": 99, "score": 0.9}, {"id": -1, "score": 0.9}, {"id": 2, "score": 0.8} ] }
                """);

        var verdicts = await Ranker(handler).RankAsync("talk", ThreeCandidates, 5);

        Assert.Equal([2], verdicts!.Select(v => v.Id));
    }

    [Fact]
    public async Task IgnoresARepeatedId()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""
                { "ranked": [ {"id": 0, "score": 0.9}, {"id": 0, "score": 0.8} ] }
                """);

        Assert.Single((await Ranker(handler).RankAsync("jazz", ThreeCandidates, 5))!);
    }

    [Fact]
    public async Task HonoursTopK()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""
                { "ranked": [ {"id": 0, "score": 0.9}, {"id": 1, "score": 0.8}, {"id": 2, "score": 0.7} ] }
                """);

        Assert.Equal(2, (await Ranker(handler).RankAsync("music", ThreeCandidates, topK: 2))!.Count);
    }

    [Fact]
    public async Task ToleratesMarkdownFencesAroundTheJson()
    {
        var handler = new FakeHttpMessageHandler()
            .RespondWithText("""
                Here you go:
                ```json
                { "ranked": [ {"id": 0, "score": 0.9} ] }
                ```
                """);

        Assert.Equal([0], (await Ranker(handler).RankAsync("jazz", ThreeCandidates, 5))!.Select(v => v.Id));
    }

    /// <summary>An empty list means "ran, nothing matched" — meaningfully different from null,
    /// which means "couldn't run, fall back to the heuristic".</summary>
    [Fact]
    public async Task AnEmptyRankingIsAResult_NotAFailure()
    {
        var handler = new FakeHttpMessageHandler().RespondWithText("""{ "ranked": [] }""");

        var verdicts = await Ranker(handler).RankAsync("nothing like this", ThreeCandidates, 5);

        Assert.NotNull(verdicts);
        Assert.Empty(verdicts!);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "something": "else" }""")]
    [InlineData("""{ "ranked": "not an array" }""")]
    public async Task ReturnsNullSoTheCallerCanFallBack_WhenTheReplyIsUnusable(string reply)
    {
        var handler = new FakeHttpMessageHandler().RespondWithText(reply);

        Assert.Null(await Ranker(handler).RankAsync("jazz", ThreeCandidates, 5));
    }

    [Fact]
    public async Task ReturnsNullOnAnApiError()
    {
        var handler = new FakeHttpMessageHandler().RespondWithStatus(HttpStatusCode.TooManyRequests);

        Assert.Null(await Ranker(handler).RankAsync("jazz", ThreeCandidates, 5));
    }

    /// <summary>Cancellation must propagate rather than being swallowed into "couldn't run" —
    /// otherwise an abandoned search silently degrades to the heuristic merge instead of stopping.</summary>
    [Fact]
    public async Task PropagatesCancellationRatherThanDegrading()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeHttpMessageHandler().RespondWithText("""{ "ranked": [] }""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Ranker(handler).RankAsync("jazz", ThreeCandidates, 5, cts.Token));
    }

    [Fact]
    public async Task DoesNotCallTheApiWithoutAKey()
    {
        var handler = new FakeHttpMessageHandler();
        var ranker = new LlmSearchRanker(handler.Client(), apiKey: null);

        Assert.False(ranker.IsConfigured);
        Assert.Null(await ranker.RankAsync("jazz", ThreeCandidates, 5));
        Assert.Equal(0, handler.CallCount);
    }
}
