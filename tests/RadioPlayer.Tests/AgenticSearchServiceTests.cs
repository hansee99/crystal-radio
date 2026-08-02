using System.Net.Http;
using System.Net;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Pattern B's safety property, stated in CLAUDE.md as "never play a web- or model-supplied URL":
/// a station reaches playback only if its stationuuid came back from a search_radio_browser call
/// this service actually made. Everything else here — the tool loop, pause_turn, tool errors —
/// exists to make sure that gate still holds on the awkward paths, not just the happy one.
/// </summary>
public class AgenticSearchServiceTests
{
    private const string RealUuid = "11111111-1111-1111-1111-111111111111";
    private const string OtherUuid = "22222222-2222-2222-2222-222222222222";

    private static string ToolUse(string id = "tu_1", string tags = "\"jazz\"") => $$"""
        { "stop_reason": "tool_use",
          "content": [ { "type": "tool_use", "id": "{{id}}", "name": "search_radio_browser",
                         "input": { "tags": [{{tags}}] } } ] }
        """;

    private static string FinalAnswer(params string[] uuids) =>
        $$"""{ "stations": [ {{string.Join(", ", uuids.Select(u => $"{{\"stationuuid\": \"{u}\", \"reason\": \"fits\"}}"))}} ] }""";

    private static AgenticSearchService Service(
        FakeHttpMessageHandler http, FakeStationSearchService search, FakeEnrichmentService? enrichment = null) =>
        new(http.Client(), search, enrichment ?? new FakeEnrichmentService(), "test-key");

    [Fact]
    public async Task PlaysAStationTheToolActuallyReturned()
    {
        var search = new FakeStationSearchService().Add(RealUuid, "Jazz FM");
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText(FinalAnswer(RealUuid));

        var ranked = await Service(http, search).SearchAsync("jazz");

        Assert.Equal("Jazz FM", Assert.Single(ranked).Station.Name);
        Assert.Equal("fits", ranked[0].Reason);
    }

    /// <summary>The core gate. The model names a plausible uuid it was never given; it must be
    /// dropped, because the only station object we could pair it with would be one we invented.</summary>
    [Fact]
    public async Task DiscardsAStationUuidTheToolNeverReturned()
    {
        var search = new FakeStationSearchService().Add(RealUuid, "Jazz FM");
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText(FinalAnswer(OtherUuid, RealUuid));

        var ranked = await Service(http, search).SearchAsync("jazz");

        Assert.Equal("Jazz FM", Assert.Single(ranked).Station.Name);
    }

    /// <summary>The failure mode the gate exists for: the model skips the directory entirely and
    /// answers straight from what it read on the web. Nothing was fetched, so nothing is playable.</summary>
    [Fact]
    public async Task PlaysNothingWhenTheModelAnswersWithoutEverCallingTheDirectory()
    {
        var http = new FakeHttpMessageHandler().RespondWithText(FinalAnswer(RealUuid));

        Assert.Empty(await Service(http, new FakeStationSearchService().Add(RealUuid)).SearchAsync("jazz"));
    }

    [Fact]
    public async Task IgnoresARepeatedStationUuid()
    {
        var search = new FakeStationSearchService().Add(RealUuid, "Jazz FM");
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText(FinalAnswer(RealUuid, RealUuid));

        Assert.Single(await Service(http, search).SearchAsync("jazz"));
    }

    [Fact]
    public async Task ResumesAfterAPauseTurn()
    {
        // pause_turn means the server-side web_search hit its own cap mid-turn; re-calling
        // resumes it. Treating it as terminal would silently return nothing on long searches.
        var search = new FakeStationSearchService().Add(RealUuid, "Jazz FM");
        var http = new FakeHttpMessageHandler()
            .RespondWithText("", stopReason: "pause_turn")
            .RespondWithJson(ToolUse())
            .RespondWithText(FinalAnswer(RealUuid));

        Assert.Single(await Service(http, search).SearchAsync("jazz"));
        Assert.Equal(3, http.CallCount);
    }

    [Fact]
    public async Task KeepsGoingWhenTheDirectoryLookupFails()
    {
        // The tool error is handed back as a tool_result so the model can adapt; a directory
        // outage must not throw out of the search.
        var search = new FakeStationSearchService { Fault = new HttpRequestException("directory down") };
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText("""{ "stations": [] }""");

        Assert.Empty(await Service(http, search).SearchAsync("jazz"));
        Assert.Equal(2, http.CallCount);
    }

    [Fact]
    public async Task StopsAtTheIterationCapRatherThanLoopingForever()
    {
        // A model that only ever calls tools would otherwise spin up unbounded cost and latency.
        var search = new FakeStationSearchService().Add(RealUuid);
        var http = new FakeHttpMessageHandler();
        for (var i = 0; i < 10; i++) http.RespondWithJson(ToolUse($"tu_{i}"));

        Assert.Empty(await Service(http, search).SearchAsync("jazz"));
        Assert.Equal(6, http.CallCount);   // MaxIterations
    }

    [Fact]
    public async Task StopsWhenTheOnlyToolCallIsOneItCannotExecute()
    {
        // web_search is server-executed; a tool_use turn containing nothing client-side would
        // otherwise be answered with an empty tool_result array and spin.
        var http = new FakeHttpMessageHandler().RespondWithJson("""
            { "stop_reason": "tool_use",
              "content": [ { "type": "server_tool_use", "id": "s1", "name": "web_search",
                             "input": { "query": "jazz" } } ] }
            """);

        Assert.Empty(await Service(http, new FakeStationSearchService()).SearchAsync("jazz"));
        Assert.Equal(1, http.CallCount);
    }

    [Fact]
    public async Task FallsBackToTheCachedDescriptionWhenTheModelGivesNoReason()
    {
        var search = new FakeStationSearchService().Add(RealUuid, "Jazz FM");
        var enrichment = new FakeEnrichmentService().Cache(RealUuid, "Late-night jazz from Cologne.");
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText($$"""{ "stations": [ { "stationuuid": "{{RealUuid}}" } ] }""");

        var ranked = await Service(http, search, enrichment).SearchAsync("jazz");

        Assert.Equal("Late-night jazz from Cologne.", Assert.Single(ranked).Reason);
    }

    [Fact]
    public async Task FallsBackToTagsWhenThereIsNeitherAReasonNorACachedDescription()
    {
        var search = new FakeStationSearchService().Add(RealUuid, "Jazz FM");
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText($$"""{ "stations": [ { "stationuuid": "{{RealUuid}}", "reason": "  " } ] }""");

        Assert.Equal("jazz", Assert.Single(await Service(http, search).SearchAsync("jazz")).Reason);
    }

    [Theory]
    [InlineData("sorry, I couldn't find anything")]
    [InlineData("""{ "stations": "none" }""")]
    [InlineData("")]
    public async Task ReturnsNothingWhenTheFinalAnswerIsNotUsable(string finalText)
    {
        var search = new FakeStationSearchService().Add(RealUuid);
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText(finalText);

        Assert.Empty(await Service(http, search).SearchAsync("jazz"));
    }

    [Fact]
    public async Task ToleratesMarkdownFencesAroundTheFinalAnswer()
    {
        var search = new FakeStationSearchService().Add(RealUuid, "Jazz FM");
        var http = new FakeHttpMessageHandler()
            .RespondWithJson(ToolUse())
            .RespondWithText($"```json\n{FinalAnswer(RealUuid)}\n```");

        Assert.Single(await Service(http, search).SearchAsync("jazz"));
    }

    [Fact]
    public async Task SurfacesAnApiFailureRatherThanReturningAnEmptyPage()
    {
        // Distinct from "found nothing": the caller escalates/reports differently, and silently
        // swallowing a 401 would look like a genuinely empty result.
        var http = new FakeHttpMessageHandler().RespondWithStatus(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Service(http, new FakeStationSearchService()).SearchAsync("jazz"));
    }

    [Fact]
    public async Task RefusesToRunWithoutAnApiKey()
    {
        var http = new FakeHttpMessageHandler();
        var service = new AgenticSearchService(
            http.Client(), new FakeStationSearchService(), new FakeEnrichmentService(), apiKey: null);

        Assert.False(service.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync("jazz"));
    }

    [Fact]
    public async Task DoesNotCallTheApiForABlankPrompt()
    {
        var http = new FakeHttpMessageHandler();

        Assert.Empty(await Service(http, new FakeStationSearchService()).SearchAsync("   "));
        Assert.Equal(0, http.CallCount);
    }

    /// <summary>A name-only tool call goes to the by-name lookup; adding tags switches it to the
    /// tag search. Getting this backwards makes "resolve this station name" behave as a genre
    /// query, which is exactly how a wrong station gets recommended confidently.</summary>
    [Fact]
    public async Task RoutesANameOnlyToolCallToTheByNameLookup()
    {
        var search = new FakeStationSearchService().Add(RealUuid);
        var http = new FakeHttpMessageHandler()
            .RespondWithJson("""
                { "stop_reason": "tool_use",
                  "content": [ { "type": "tool_use", "id": "tu_1", "name": "search_radio_browser",
                                 "input": { "name": "BBC Radio 1" } } ] }
                """)
            .RespondWithText("""{ "stations": [] }""");

        await Service(http, search).SearchAsync("bbc");

        Assert.Equal("byName:BBC Radio 1", Assert.Single(search.Calls));
    }
}
