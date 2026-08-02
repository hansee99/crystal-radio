using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Spotting two harvesters on one stream. The directory lists the same stream more than once
/// under different names and different URLs, so pre-connect deduplication can't see it — only
/// the stream's own icy-name can. Observed live on 2026-08-02: "Liquid DnB" and "DnB Liquified"
/// held two of four slots on one stream for a whole session.
/// </summary>
public class DuplicateStreamTests
{
    private static readonly DateTime T0 = new(2026, 8, 2, 10, 42, 0, DateTimeKind.Utc);

    private static DjHarvestService.StreamIdentity At(string? name, double minutes) =>
        new(name, T0.AddMinutes(minutes));

    [Fact]
    public void DropsTheLaterOfTwoHarvestersOnTheSameStream()
    {
        var dupes = DjHarvestService.FindDuplicateStreams([
            At("Liquid DnB", 0),      // connected first — keeps its slot
            At("DnBRadio.com", 0),
            At("Liquid DnB", 2)       // same stream, joined later
        ]);

        Assert.Equal([2], dupes);
    }

    [Fact]
    public void KeepsTheEarliestConnectionRegardlessOfListOrder()
    {
        // The one with the most history is the more useful slot to keep, and the active list
        // isn't ordered by connection time once retirements have shuffled it.
        var dupes = DjHarvestService.FindDuplicateStreams([
            At("Liquid DnB", 5),
            At("Liquid DnB", 1)
        ]);

        Assert.Equal([0], dupes);
    }

    [Fact]
    public void DropsAllButOneWhenThreeShareAStream()
    {
        var dupes = DjHarvestService.FindDuplicateStreams([
            At("Liquid DnB", 3),
            At("Liquid DnB", 1),
            At("Liquid DnB", 7)
        ]);

        Assert.Equal([0, 2], dupes);
    }

    [Fact]
    public void MatchesRegardlessOfCaseAndSurroundingSpace()
    {
        var dupes = DjHarvestService.FindDuplicateStreams([
            At("Liquid DnB", 0),
            At("  liquid dnb ", 1)
        ]);

        Assert.Equal([1], dupes);
    }

    /// <summary>A station serving no icy-name has no identity to compare, so it can never be
    /// judged a duplicate — several of them must not collapse into one.</summary>
    [Fact]
    public void NeverTreatsAMissingStreamNameAsAMatch()
    {
        Assert.Empty(DjHarvestService.FindDuplicateStreams([
            At(null, 0), At(null, 1), At("   ", 2), At("", 3)
        ]));
    }

    [Fact]
    public void LeavesADistinctPoolAlone()
    {
        Assert.Empty(DjHarvestService.FindDuplicateStreams([
            At("Liquid DnB", 0), At("Liqui Radio", 0), At("DnBRadio.com", 1)
        ]));
    }

    [Fact]
    public void ReturnsIndicesInAscendingOrder()
    {
        // The caller indexes into the live list to pick victims, so out-of-order results would
        // be a trap even though it currently retires by reference.
        var dupes = DjHarvestService.FindDuplicateStreams([
            At("A", 9), At("B", 9), At("A", 1), At("B", 1)
        ]);

        Assert.Equal([0, 1], dupes);
    }

    [Fact]
    public void HandlesAnEmptyPool()
    {
        Assert.Empty(DjHarvestService.FindDuplicateStreams([]));
    }
}
