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

    /// <summary>A harvester with no icy-name — only the titles it has heard identify it.</summary>
    private static DjHarvestService.StreamIdentity Anon(double minutes, params string[] titles) =>
        new(null, T0.AddMinutes(minutes), titles);

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

    // --- Streams that serve NO icy-name (GitHub #2) -------------------------------------------
    // ORF's feeds return "icy-name: (absent)". "FM4 | ORF" and "FM4 | ORF | HQ" took two of four
    // harvester slots for a whole session: different names, URLs differing only in q1a/q2a, and no
    // icy-name — so every identity key was blind. The titles they announce are the only signal.

    [Fact]
    public void MatchesOnTitlesWhenNeitherStreamServesAnIcyName()
    {
        var dupes = DjHarvestService.FindDuplicateStreams([
            Anon(0, "Ben Kidson - life's relentless", "Wolf Alice - Bloom Baby Bloom"),
            Anon(2, "Wolf Alice - Bloom Baby Bloom", "Ben Kidson - life's relentless")
        ]);

        Assert.Equal([1], dupes);   // later connection loses
    }

    /// <summary>One shared title is not proof: two pop stations can be playing the same chart
    /// single at the same moment. Agreeing on a second song as well is proof.</summary>
    [Fact]
    public void OneSharedTitleIsNotEnough()
    {
        Assert.Empty(DjHarvestService.FindDuplicateStreams([
            Anon(0, "Chappell Roan - Pink Pony Club", "Wolf Alice - Bloom Baby Bloom"),
            Anon(2, "Chappell Roan - Pink Pony Club", "Die Toten Hosen - Nur nach vorn")
        ]));
    }

    [Fact]
    public void AHarvesterWithNoTitlesYetIsNeverJudgedADuplicate()
    {
        // Just connected, or a station between songs. Silence is not evidence.
        Assert.Empty(DjHarvestService.FindDuplicateStreams([
            Anon(0),
            Anon(1, "Wolf Alice - Bloom Baby Bloom", "Ben Kidson - life's relentless")
        ]));
    }

    [Fact]
    public void MatchesOnTitlesWhenOnlyOneSideServesAnIcyName()
    {
        // A named and an unnamed feed of one broadcast can't be name-compared at all, so the
        // title pass has to cover the mixed case too.
        var dupes = DjHarvestService.FindDuplicateStreams([
            new("FM4", T0, ["A - one", "B - two"]),
            Anon(3, "B - two", "A - one")
        ]);

        Assert.Equal([1], dupes);
    }

    /// <summary>icy-name stays authoritative. Two stations that the name pass deliberately kept
    /// apart must not then be collapsed by a coincidental title overlap.</summary>
    [Fact]
    public void TitlesNeverOverrideTwoDifferentIcyNames()
    {
        Assert.Empty(DjHarvestService.FindDuplicateStreams([
            new("Hitradio Ö3", T0, ["A - one", "B - two"]),
            new("FM4", T0.AddMinutes(1), ["A - one", "B - two"])
        ]));
    }

    [Fact]
    public void TitleMatchingIsCaseInsensitive()
    {
        var dupes = DjHarvestService.FindDuplicateStreams([
            Anon(0, "Wolf Alice - Bloom Baby Bloom", "Ben Kidson - Life's Relentless"),
            Anon(2, "WOLF ALICE - BLOOM BABY BLOOM", "ben kidson - life's relentless")
        ]);

        Assert.Equal([1], dupes);
    }

    [Fact]
    public void DropsOnlyOneOfThreeFeedsOfTheSameUnnamedBroadcast()
    {
        var dupes = DjHarvestService.FindDuplicateStreams([
            Anon(5, "A - one", "B - two"),
            Anon(1, "A - one", "B - two"),
            Anon(9, "A - one", "B - two")
        ]);

        Assert.Equal([0, 2], dupes);   // index 1 connected first and survives
    }

    [Fact]
    public void LeavesTwoGenuinelyDifferentUnnamedStationsAlone()
    {
        Assert.Empty(DjHarvestService.FindDuplicateStreams([
            Anon(0, "Wolf Alice - Bloom Baby Bloom", "Mattiel - Count Your Blessings"),
            Anon(1, "Die Toten Hosen - Nur nach vorn", "Pizzera & Jaus - Klesch Koids Bier")
        ]));
    }
}
