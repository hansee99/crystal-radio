
using System.Net.Http;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// What reaches the panel after the model has spoken (#52). The vibe-change line asks for plain
/// prose rather than JSON, which removes the fence-stripping that parsing a JSON reply gave the
/// other calls for free — and a model answering plainly still wraps it sometimes. Observed on
/// 2026-08-06: a line arrived as <c>``` Alright... ```</c> and would have been shown that way.
/// </summary>
public class DjLineCleaningTests
{
    private static DjIntroService Build(string replyText) =>
        new(new HttpClient(new FakeHttpMessageHandler().RespondWithText(replyText)), "test-key");

    [Theory]
    [InlineData("``` Alright, shifting gears then. ```", "Alright, shifting gears then.")]
    [InlineData("```\nAlright, shifting gears then.\n```", "Alright, shifting gears then.")]
    [InlineData("```text\nAlright, shifting gears then.\n```", "Alright, shifting gears then.")]
    public async Task StripsMarkdownFences(string reply, string expected)
    {
        Assert.Equal(expected, await Build(reply).GetVibeChangeLineAsync("chillout", "hard techno"));
    }

    /// <summary>A quoted line reads as the DJ quoting somebody rather than speaking.</summary>
    [Fact]
    public async Task StripsWholeLineQuotes()
    {
        Assert.Equal("Right, let's get subterranean.",
            await Build("\"Right, let's get subterranean.\"").GetVibeChangeLineAsync("pop", "art rock"));
    }

    /// <summary>Quotes INSIDE the line are the DJ's own and must survive.</summary>
    [Fact]
    public async Task KeepsQuotesWithinTheLine()
    {
        const string line = "You said \"something heavier\" — so here we go.";

        Assert.Equal(line, await Build(line).GetVibeChangeLineAsync("pop", "metal"));
    }

    [Fact]
    public async Task CollapsesWhitespaceAndTrims()
    {
        Assert.Equal("Shifting gears then.",
            await Build("  Shifting   gears\n\nthen.  ").GetVibeChangeLineAsync("pop", "metal"));
    }

    /// <summary>No key, no call — and no line rather than an exception.</summary>
    [Fact]
    public async Task ReturnsNothingWithoutAKey()
    {
        var svc = new DjIntroService(new HttpClient(new FakeHttpMessageHandler()), apiKey: null);

        Assert.Null(await svc.GetVibeChangeLineAsync("chillout", "hard techno"));
    }

    [Fact]
    public async Task ReturnsNothingWithoutANewVibe()
    {
        Assert.Null(await Build("anything").GetVibeChangeLineAsync("chillout", "   "));
    }
}
