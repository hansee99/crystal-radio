using RadioPlayer.Services;
using RadioPlayer.ViewModels;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// What the DJ panel claims it is counting. A vibe change truncates the mix queue, so every song
/// counted up to that point is removed from the mix — but the harvest counters kept running, and
/// the session card went on reporting songs that had been deliberately discarded.
///
/// <para>Two halves to that: the counters now reset (see
/// <see cref="DjHarvestService.ResetSessionTotals"/>), and the wording no longer claims something
/// the number can't deliver. "Kept" means "harvested", which is not the same as "queued" — saying
/// "in the mix" let the number contradict the list beside it.</para>
/// </summary>
public class DjSessionCountingTests
{
    // --- The reset ----------------------------------------------------------------------------

    /// <summary>
    /// One method for both call sites (StartAsync and ChangeVibeAsync), so a counter added later
    /// cannot be reset in one place and forgotten in the other — which is exactly the shape of the
    /// bug being fixed. Nulls for the dependencies are safe here and only here: the reset touches
    /// three fields and none of the injected services.
    /// </summary>
    [Fact]
    public void ResetSessionTotalsClearsEverythingTheCardCounts()
    {
        var sut = Harvest();
        sut.RecordOutcome("Hard Rock Heaven", kept: true);
        sut.RecordOutcome("Hard Rock Heaven", kept: true);
        sut.RecordOutcome("011.fm", kept: true);
        sut.RecordOutcome("011.fm", kept: false);
        Assert.Equal((3, 1, 2), sut.SessionTotals);   // counting works, so the reset is provable

        sut.ResetSessionTotals();

        // Per-station tallies too: the pool is fully replaced on a vibe change, and tallies are
        // keyed by station label, so a re-sourced station would otherwise inherit an earlier count.
        Assert.Equal((0, 0, 0), sut.SessionTotals);
    }

    [Fact]
    public void CountingIsPerStationAsWellAsPerSession()
    {
        var sut = Harvest();

        sut.RecordOutcome("A", kept: true);
        sut.RecordOutcome("B", kept: false);
        sut.RecordOutcome("B", kept: false);

        Assert.Equal((1, 2, 2), sut.SessionTotals);
    }

    /// <summary>Nulls for the dependencies are safe in this file and only here: the counting and
    /// reset paths touch three fields and none of the injected services.</summary>
    private static DjHarvestService Harvest() =>
        new(null!, null!, null!, null!, null!, null!, harvestDir: "");

    // --- The wording --------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 0, "3 songs collected")]
    [InlineData(1, 0, "1 song collected")]
    [InlineData(4, 2, "4 songs collected · 2 skipped")]
    public void AProducingStationReportsWhatItCollected(int kept, int rejected, string expected)
    {
        // "collected", not "in the mix" — a song stays collected after a vibe change empties the
        // queue, so this number can no longer disagree with the list next to it.
        Assert.Equal(expected, new DjHarvesterItem("Some Station", TitlesSeen: 9, kept, rejected).StatusText);
    }

    [Theory]
    [InlineData(0, 0, "Listening…")]
    [InlineData(0, 5, "Nothing kept yet · 5 skipped")]
    public void AStationWithNothingKeptSaysSo(int kept, int rejected, string expected)
    {
        Assert.Equal(expected, new DjHarvesterItem("Some Station", TitlesSeen: 12, kept, rejected).StatusText);
    }

    /// <summary>TitlesSeen counts ICY metadata changes, roughly two per completed segment. Reporting
    /// those as songs overstated every station by about double, and a row claiming four songs while
    /// the session had produced one was the clearest way to make the panel untrustworthy.</summary>
    [Fact]
    public void TheCountIsNeverTheTitleCount()
    {
        var row = new DjHarvesterItem("Some Station", TitlesSeen: 20, Kept: 2, Rejected: 1);

        Assert.Equal("2 songs collected · 1 skipped", row.StatusText);
        Assert.DoesNotContain("20", row.StatusText);
    }
}
