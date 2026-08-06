using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Keeping one artist off the mix's shoulder (#51). The rule that matters most is the one that
/// says when NOT to apply it: a prompt naming a single artist should still play, and it does
/// because a repeat is only ever postponed while something else is available — never dropped, and
/// never held when there is no alternative.
/// </summary>
public class ArtistSpreadTests
{
    private sealed record Track(string Artist, string Title);

    private static List<string> Order(IEnumerable<Track> spread) => spread.Select(t => t.Title).ToList();

    private static List<Track> Spread(IReadOnlyList<Track> items, string? previous = null) =>
        ArtistSpread.Spread(items, t => t.Artist, previous);

    [Fact]
    public void SeparatesTwoSongsByTheSameArtist()
    {
        var result = Spread([
            new("Bowie", "Heroes"),
            new("Bowie", "Ashes to Ashes"),
            new("Lou Reed", "Perfect Day"),
        ]);

        Assert.Equal(["Heroes", "Perfect Day", "Ashes to Ashes"], Order(result));
    }

    /// <summary>Nothing is discarded — a song held apart is still a song in the mix.</summary>
    [Fact]
    public void KeepsEverySong()
    {
        var items = new List<Track>
        {
            new("Bowie", "Heroes"), new("Bowie", "Fame"), new("Bowie", "Changes"),
            new("Lou Reed", "Perfect Day"), new("Roxy Music", "Virginia Plain"),
        };

        var result = Spread(items);

        Assert.Equal(items.Count, result.Count);
        Assert.Equal(items.OrderBy(t => t.Title), result.OrderBy(t => t.Title));
    }

    /// <summary>
    /// The single-artist prompt. Everything is by one act, so there is nothing to alternate with
    /// and they must simply play — in their original order, not shuffled for the sake of it.
    /// </summary>
    [Fact]
    public void PlaysASingleArtistInOrderRatherThanHoldingThemBack()
    {
        var result = Spread([
            new("Bowie", "Heroes"),
            new("Bowie", "Fame"),
            new("Bowie", "Changes"),
        ]);

        Assert.Equal(["Heroes", "Fame", "Changes"], Order(result));
    }

    /// <summary>The curator ordered by fit, so this may only break ties — a list already free of
    /// neighbouring repeats has to come back untouched.</summary>
    [Fact]
    public void LeavesAnAlreadyVariedListAlone()
    {
        var result = Spread([
            new("Bowie", "Heroes"),
            new("Lou Reed", "Perfect Day"),
            new("Roxy Music", "Virginia Plain"),
        ]);

        Assert.Equal(["Heroes", "Perfect Day", "Virginia Plain"], Order(result));
    }

    /// <summary>A batch appended behind existing tracks must not repeat across the join.</summary>
    [Fact]
    public void DoesNotRepeatTheArtistAlreadyQueued()
    {
        var result = Spread([
            new("Bowie", "Heroes"),
            new("Lou Reed", "Perfect Day"),
        ], previous: "Bowie");

        Assert.Equal(["Perfect Day", "Heroes"], Order(result));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ShortListsAreReturnedUnchanged(int count)
    {
        var items = Enumerable.Range(0, count).Select(i => new Track("Bowie", $"T{i}")).ToList();

        Assert.Equal(items, Spread(items));
    }

    /// <summary>An unknown artist is not evidence of a repeat. A run of blanks is a metadata
    /// problem, and treating them as one act would hold songs back over it.</summary>
    [Fact]
    public void BlankArtistsAreNeverTreatedAsTheSameAct()
    {
        Assert.False(ArtistSpread.SameArtist(null, null));
        Assert.False(ArtistSpread.SameArtist("", ""));
        Assert.False(ArtistSpread.SameArtist("   ", "Bowie"));

        var result = Spread([new("", "One"), new("", "Two"), new("Bowie", "Heroes")]);
        Assert.Equal(["One", "Two", "Heroes"], Order(result));
    }

    [Fact]
    public void ArtistComparisonIgnoresCaseAndSurroundingSpace()
    {
        Assert.True(ArtistSpread.SameArtist("Bowie", "  bowie "));
        Assert.True(ArtistSpread.SameArtist("MÖTLEY CRÜE", "Mötley Crüe"));
        Assert.False(ArtistSpread.SameArtist("Bowie", "Lou Reed"));
    }

    /// <summary>
    /// The reported case: a station dedicated to one artist contributes a run of them among other
    /// stations' songs. No two neighbours should share an artist when the material allows it.
    /// </summary>
    [Fact]
    public void BreaksUpARunFromASingleArtistStation()
    {
        var result = Spread([
            new("Kate Bush", "Wuthering Heights"),
            new("Kate Bush", "Running Up That Hill"),
            new("Kate Bush", "Cloudbusting"),
            new("Bowie", "Heroes"),
            new("Lou Reed", "Perfect Day"),
        ]);

        for (var i = 1; i < result.Count; i++)
            Assert.False(ArtistSpread.SameArtist(result[i].Artist, result[i - 1].Artist),
                $"{result[i - 1].Title} is followed by the same artist ({result[i].Title})");
    }
}
