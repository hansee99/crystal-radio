using System.Net.Http;
using System.Text.Json.Nodes;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The shared response-parsing helpers every LLM-backed service now funnels through. They used
/// to be duplicated per service and drifted; these pin the behaviour once so a future tweak
/// can't quietly change what seven callers see.
/// </summary>
public class AnthropicApiTests
{
    [Fact]
    public void ExtractText_JoinsEveryTextBlock()
    {
        // A reply split across blocks (common once tools are in play) must not be truncated to
        // the first one — half a JSON object parses as nothing.
        var content = JsonNode.Parse("""
            [ { "type": "text", "text": "{\"a\":" }, { "type": "text", "text": "1}" } ]
            """);

        Assert.Equal("""{"a":1}""", AnthropicApi.ExtractText(content));
    }

    [Fact]
    public void ExtractText_SkipsNonTextBlocks()
    {
        var content = JsonNode.Parse("""
            [ { "type": "server_tool_use", "name": "web_search" },
              { "type": "web_search_tool_result", "content": [] },
              { "type": "text", "text": "the answer" } ]
            """);

        Assert.Equal("the answer", AnthropicApi.ExtractText(content));
    }

    [Theory]
    [InlineData("""{ "content": [] }""")]                                  // no blocks
    [InlineData("""{ "content": [ { "type": "tool_use" } ] }""")]          // no text blocks
    [InlineData("""{ "content": "oops" }""")]                              // wrong shape
    [InlineData("""{ "error": { "type": "authentication_error" } }""")]    // an error body
    [InlineData("not json")]
    public void ExtractText_ReturnsNullWhenThereIsNoTextToRead(string body)
    {
        Assert.Null(AnthropicApi.ExtractText(body));
    }

    [Theory]
    [InlineData("""{"a":1}""", """{"a":1}""")]
    [InlineData("```json\n{\"a\":1}\n```", """{"a":1}""")]
    [InlineData("```\n{\"a\":1}\n```", """{"a":1}""")]
    [InlineData("Here you go:\n{\"a\":1}\nHope that helps!", """{"a":1}""")]
    [InlineData("  \n {\"a\":1}  ", """{"a":1}""")]
    public void StripToJsonObject_FindsTheObjectDespiteFencesAndProse(string text, string expected)
    {
        Assert.Equal(expected, AnthropicApi.StripToJsonObject(text));
    }

    [Fact]
    public void StripToJsonObject_KeepsNestedObjectsWhole()
    {
        // Taking the *last* closing brace rather than the first is what makes nesting survive.
        const string text = """{"ranked":[{"id":0},{"id":1}]}""";

        Assert.Equal(text, AnthropicApi.StripToJsonObject(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no braces here")]
    [InlineData("}{")]        // closing before opening
    [InlineData("{")]         // never closed
    public void StripToJsonObject_ReturnsNullWhenThereIsNoObject(string? text)
    {
        Assert.Null(AnthropicApi.StripToJsonObject(text));
    }

    [Fact]
    public void CreateRequest_CarriesTheAuthAndVersionHeaders()
    {
        using var request = AnthropicApi.CreateRequest("k-123", new JsonObject { ["model"] = "m" });

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(AnthropicApi.Endpoint, request.RequestUri?.ToString());
        Assert.Equal("k-123", Assert.Single(request.Headers.GetValues("x-api-key")));
        Assert.Equal(AnthropicApi.Version, Assert.Single(request.Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public void Truncate_LeavesShortStringsAlone()
    {
        Assert.Equal("short", AnthropicApi.Truncate("short"));
    }

    [Fact]
    public void Truncate_CapsLongStringsWithAnEllipsis()
    {
        var truncated = AnthropicApi.Truncate(new string('x', 500));

        Assert.Equal(301, truncated.Length);
        Assert.EndsWith("…", truncated);
    }
}
