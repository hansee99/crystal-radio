using System.Net.Http;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Keeping the DJ from repeating itself (#58).
///
/// <para>Whether the lines actually stop sounding alike is a listening question. What is checkable
/// is the machinery: that the model is shown what it just said, that the memory stays short, and —
/// the one that was a real bug — that the session patter no longer carries the date.</para>
/// </summary>
public class DjRepetitionTests
{
    private static DateTimeOffset Friday => new(2026, 8, 7, 21, 0, 0, TimeSpan.Zero);

    private static (DjIntroService Svc, FakeHttpMessageHandler Http) Build(params string[] replies)
    {
        var handler = new FakeHttpMessageHandler();
        foreach (var reply in replies)
            handler.RespondWithText(reply);
        return (new DjIntroService(new HttpClient(handler), "test-key", clock: () => Friday), handler);
    }

    private static string Line(string text) => $$"""{ "line": "{{text}}" }""";

    // --- the wrong-day bug -------------------------------------------------------------------

    /// <summary>
    /// The session patter is generated ONCE and replayed for hours, so a line that names the day or
    /// the hour is wrong the moment either moves on - which is how a Friday remark came to be shown
    /// on a Saturday. Per-track lines are generated fresh and may carry it; this set may not.
    /// </summary>
    [Fact]
    public async Task TheSessionPatterDoesNotCarryTheDateOrTime()
    {
        var (svc, http) = Build("""
            { "sourcing": ["a"], "waiting": ["b"], "bridging": ["c"], "signingOff": ["d"] }
            """);

        await svc.GetSessionPatterAsync("music for working late");

        Assert.DoesNotContain("Right now:", http.Requests[0]);
        Assert.DoesNotContain("Friday", http.Requests[0]);
        Assert.DoesNotContain("21:00", http.Requests[0]);
    }

    /// <summary>The per-track line still gets it - it is generated fresh every time.</summary>
    [Fact]
    public async Task ThePerTrackLineStillCarriesTheMoment()
    {
        var (svc, http) = Build(Line("Here we go."));

        await svc.GetIntroAsync("Heroes", "Bowie", "music for working late");

        Assert.Contains("Right now:", http.Requests[0]);
    }

    // --- the memory --------------------------------------------------------------------------

    [Fact]
    public async Task TheFirstLineHasNothingToAvoid()
    {
        var (svc, http) = Build(Line("First line."));

        await svc.GetIntroAsync("Heroes", "Bowie", "morning music");

        Assert.DoesNotContain("do not echo these", http.Requests[0]);
    }

    [Fact]
    public async Task LaterLinesAreShownWhatWasJustSaid()
    {
        var (svc, http) = Build(Line("A fine morning for this."), Line("Second."));

        await svc.GetIntroAsync("Heroes", "Bowie", "morning music");
        await svc.GetIntroAsync("Ashes to Ashes", "Bowie", "morning music");

        Assert.Contains("do not echo these", http.Requests[1]);
        Assert.Contains("A fine morning for this.", http.Requests[1]);
    }

    /// <summary>Short on purpose: enough to catch what a listener notices, small enough to keep the
    /// prompt lean.</summary>
    [Fact]
    public async Task TheMemoryKeepsOnlyTheLastFew()
    {
        var replies = Enumerable.Range(1, 7).Select(i => Line($"Line {i}.")).ToArray();
        var (svc, http) = Build(replies);

        for (var i = 1; i <= 7; i++)
            await svc.GetIntroAsync($"Track {i}", "Bowie", "morning music");

        var last = http.Requests[^1];
        Assert.DoesNotContain("Line 1.", last);   // fallen out
        Assert.Contains("Line 6.", last);          // still in
    }

    /// <summary>A repeated song comes from the per-session cache without a call, so it must not
    /// push the memory along - the listener is hearing the same line, not a new one.</summary>
    [Fact]
    public async Task ACachedLineDoesNotCostAnApiCall()
    {
        var (svc, http) = Build(Line("Only generated once."));

        var first = await svc.GetIntroAsync("Heroes", "Bowie", "morning music");
        var second = await svc.GetIntroAsync("Heroes", "Bowie", "morning music");

        Assert.Equal(first, second);
        Assert.Equal(1, http.CallCount);
    }

    // --- the prompt says what it should ------------------------------------------------------

    /// <summary>The frequency instruction is the tuning knob the issue asked for, and the cliche
    /// warning is what "too many coffee remarks" actually needs.</summary>
    [Fact]
    public async Task ThePromptAsksForRestraintAndWarnsOffTheObviousImagery()
    {
        var (svc, http) = Build(Line("Here we go."));

        await svc.GetIntroAsync("Heroes", "Bowie", "morning music");

        Assert.Contains("one line in five", http.Requests[0]);
        Assert.Contains("coffee", http.Requests[0]);   // named as the cliche to avoid
    }
}
