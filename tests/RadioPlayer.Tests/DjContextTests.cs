using System.Net.Http;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// What the DJ knows about "now" (#55), and that it reaches the prompt.
///
/// <para>Whether the model uses it well is a matter for listening, not assertions. What is
/// checkable is that the facts are right at the boundaries, that a source with nothing to say
/// contributes nothing, and that one which throws cannot take a DJ line down with it.</para>
/// </summary>
public class DjContextTests
{
    private static DateTimeOffset At(int month, int day, int hour) =>
        new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    // --- time of day ---------------------------------------------------------------------

    [Theory]
    [InlineData(6, "early morning")]
    [InlineData(9, "morning")]
    [InlineData(13, "the middle of the day")]
    [InlineData(15, "afternoon")]
    [InlineData(18, "early evening")]
    [InlineData(21, "evening")]
    [InlineData(23, "late night")]
    [InlineData(2, "late night")]
    [InlineData(4, "late night")]
    public void TimeOfDayNamesTheHour(int hour, string expected)
    {
        var described = new TimeOfDayContext().Describe(At(6, 15, hour))!;

        Assert.StartsWith(expected, described);
        // The clock time too — the label alone reads too loosely (06:30 became "the small hours").
        Assert.Contains($"{hour:00}:00", described);
    }

    /// <summary>Midnight and 5am are the two ends of "late night" — the wrap-around is where an
    /// hour-range table usually goes wrong.</summary>
    [Fact]
    public void LateNightWrapsAroundMidnight()
    {
        var source = new TimeOfDayContext();
        Assert.StartsWith("late night", source.Describe(At(6, 15, 0))!);
        Assert.StartsWith("late night", source.Describe(At(6, 15, 4))!);
        Assert.StartsWith("early morning", source.Describe(At(6, 15, 5))!);
    }

    // --- calendar ------------------------------------------------------------------------

    [Theory]
    [InlineData(1, "winter")]
    [InlineData(2, "winter")]
    [InlineData(4, "spring")]
    [InlineData(7, "summer")]
    [InlineData(10, "autumn")]
    [InlineData(12, "winter")]
    public void CalendarNamesTheSeason(int month, string season)
    {
        Assert.Contains(season, new CalendarContext().Describe(At(month, 15, 12)));
    }

    [Fact]
    public void CalendarNamesTheDayAndDate()
    {
        // 2026-08-07 is a Friday.
        var described = new CalendarContext().Describe(At(8, 7, 12))!;

        Assert.Contains("Friday", described);
        Assert.Contains("7 August", described);
    }

    [Theory]
    [InlineData(12, 25, "Christmas")]
    [InlineData(12, 28, "between Christmas and New Year")]
    [InlineData(12, 31, "New Year")]
    [InlineData(1, 1, "New Year")]
    [InlineData(10, 31, "Halloween")]
    public void CalendarNotesAHolidayWhenThereIsOne(int month, int day, string expected)
    {
        Assert.Contains(expected, new CalendarContext().Describe(At(month, day, 12)));
    }

    /// <summary>An ordinary day mentions no holiday at all — a DJ inventing an occasion is worse
    /// than one that says nothing.</summary>
    [Fact]
    public void AnOrdinaryDayHasNoHoliday()
    {
        var described = new CalendarContext().Describe(At(8, 7, 12))!;

        Assert.DoesNotContain("Christmas", described);
        Assert.DoesNotContain("Halloween", described);
        Assert.DoesNotContain("New Year", described);
    }

    // --- composition ---------------------------------------------------------------------

    private sealed class Fixed(string? text) : IDjContextSource
    {
        public string? Describe(DateTimeOffset now) => text;
    }

    private sealed class Throws : IDjContextSource
    {
        public string? Describe(DateTimeOffset now) => throw new InvalidOperationException("no");
    }

    [Fact]
    public void ComposeJoinsWhatEachSourceHasToSay()
    {
        var line = DjContext.Compose([new Fixed("late night"), new Fixed("Friday, winter")], At(1, 9, 23));

        Assert.Equal("late night; Friday, winter", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ASourceWithNothingToSayContributesNothing(string? nothing)
    {
        Assert.Equal("late night", DjContext.Compose([new Fixed("late night"), new Fixed(nothing)], At(1, 9, 23)));
    }

    [Fact]
    public void NoSourcesMeansNoContextRatherThanAnEmptyLine()
    {
        Assert.Null(DjContext.Compose([], At(1, 9, 23)));
        Assert.Null(DjContext.Compose(null, At(1, 9, 23)));
        Assert.Null(DjContext.Compose([new Fixed(null)], At(1, 9, 23)));
    }

    /// <summary>Context is a nicety. A future source — weather, location — failing must cost its own
    /// phrase and nothing else.</summary>
    [Fact]
    public void AFailingSourceIsSkippedRatherThanBreakingTheLine()
    {
        Assert.Equal("late night", DjContext.Compose([new Throws(), new Fixed("late night")], At(1, 9, 23)));
    }

    [Fact]
    public void TheDefaultSourcesCoverTimeAndCalendar()
    {
        var line = DjContext.Compose(DjContext.Default, At(12, 25, 23))!;

        Assert.Contains("late night", line);
        Assert.Contains("Christmas", line);
    }

    // --- it actually reaches the prompt ---------------------------------------------------

    [Fact]
    public async Task TheIntroPromptCarriesTheContext()
    {
        var handler = new FakeHttpMessageHandler().RespondWithText("""{ "line": "Here we go." }""");
        var svc = new DjIntroService(new HttpClient(handler), "test-key",
            clock: () => At(12, 25, 23));

        await svc.GetIntroAsync("Fairytale of New York", "The Pogues", "festive");

        Assert.Contains("Right now:", handler.Requests[0]);
        Assert.Contains("late night", handler.Requests[0]);
        Assert.Contains("Christmas", handler.Requests[0]);
    }

    [Fact]
    public async Task TheVibeChangeLineCarriesTheContext()
    {
        var handler = new FakeHttpMessageHandler().RespondWithText("Shifting gears.");
        var svc = new DjIntroService(new HttpClient(handler), "test-key",
            clock: () => At(8, 7, 6));

        await svc.GetVibeChangeLineAsync("chillout", "hard techno");

        Assert.Contains("early morning", handler.Requests[0]);
    }

    /// <summary>With no sources the prompt is exactly what it was before — the feature adds a line
    /// or nothing, never an empty label.</summary>
    [Fact]
    public async Task WithNoSourcesThePromptIsUnchanged()
    {
        var handler = new FakeHttpMessageHandler().RespondWithText("""{ "line": "Here we go." }""");
        var svc = new DjIntroService(new HttpClient(handler), "test-key",
            clock: () => At(12, 25, 23), contextSources: []);

        await svc.GetIntroAsync("Anything", "Someone", "a vibe");

        Assert.DoesNotContain("Right now:", handler.Requests[0]);
    }
}
