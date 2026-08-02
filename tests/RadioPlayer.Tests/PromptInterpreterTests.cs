using System.Net.Http;
using System.Net;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Pattern A: prompt in, Radio Browser search parameters out. The model is deliberately never
/// asked for a station or a URL, so the risk here is narrower than Pattern B's — it's about
/// returning null (so the caller contributes nothing) rather than a half-parsed query that
/// searches for the wrong thing.
/// </summary>
public class PromptInterpreterTests
{
    private static PromptInterpreter Interpreter(FakeHttpMessageHandler http) => new(http.Client(), "test-key");

    [Fact]
    public async Task TranslatesAPromptIntoSearchParameters()
    {
        var http = new FakeHttpMessageHandler().RespondWithText("""
            { "tags": ["ambient", "chillout"], "name": null, "country": "Germany",
              "language": null, "bitrateMin": 128, "order": "clickcount" }
            """);

        var query = await Interpreter(http).InterpretAsync("mellow German stations for coding");

        Assert.NotNull(query);
        Assert.Equal(["ambient", "chillout"], query!.Tags);
        Assert.Null(query.Name);
        Assert.Equal("Germany", query.Country);
        Assert.Equal(128, query.BitrateMin);
        Assert.Equal("clickcount", query.Order);
    }

    [Fact]
    public async Task ToleratesMarkdownFencesDespiteThePromptForbiddingThem()
    {
        var http = new FakeHttpMessageHandler()
            .RespondWithText("```json\n{ \"tags\": [\"jazz\"] }\n```");

        Assert.Equal(["jazz"], (await Interpreter(http).InterpretAsync("jazz"))!.Tags);
    }

    [Fact]
    public async Task FallsBackToTheSchemaDefaultsForOmittedFields()
    {
        // A partial object is still usable — order defaults to votes and tags to empty rather
        // than the query being thrown away.
        var http = new FakeHttpMessageHandler().RespondWithText("""{ "name": "BBC Radio 1" }""");

        var query = await Interpreter(http).InterpretAsync("bbc radio 1");

        Assert.Equal("BBC Radio 1", query!.Name);
        Assert.Empty(query.Tags);
        Assert.Equal("votes", query.Order);
        Assert.Equal(0, query.BitrateMin);
    }

    [Theory]
    [InlineData("I'm not sure what you mean.")]
    [InlineData("""{ "tags": "jazz" }""")]      // right key, wrong type
    public async Task ReturnsNullWhenTheReplyIsNotAUsableQuery(string reply)
    {
        var http = new FakeHttpMessageHandler().RespondWithText(reply);

        Assert.Null(await Interpreter(http).InterpretAsync("jazz"));
    }

    [Fact]
    public async Task SurfacesAnApiFailureInsteadOfLookingLikeNoMatch()
    {
        var http = new FakeHttpMessageHandler().RespondWithStatus(HttpStatusCode.Unauthorized, """
            { "error": { "message": "invalid x-api-key" } }
            """);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Interpreter(http).InterpretAsync("jazz"));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task RefusesToRunWithoutAnApiKey()
    {
        var http = new FakeHttpMessageHandler();
        var interpreter = new PromptInterpreter(http.Client(), apiKey: null);

        Assert.False(interpreter.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => interpreter.InterpretAsync("jazz"));
    }

    [Fact]
    public async Task DoesNotCallTheApiForABlankPrompt()
    {
        var http = new FakeHttpMessageHandler();

        Assert.Null(await Interpreter(http).InterpretAsync("   "));
        Assert.Equal(0, http.CallCount);
    }
}
