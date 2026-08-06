using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Splitting an ICY StreamTitle into artist and title. This matters more than it looks: a null
/// artist is how <see cref="SongHistoryFilter"/> recognises idents, so a title the parser doesn't
/// understand isn't merely displayed oddly — it's discarded as a station ident. One session on
/// German stations (2026-08-03) lost most of its songs to exactly that.
/// </summary>
public class IcyTitleParserTests
{
    // --- The regression: real titles from that session -----------------------------------------

    [Theory]
    // SWR3 and friends put the TITLE first, after a slash. Verified against the real acts:
    // Sunrise Avenue is the band, "Hollywood Hills" the song.
    [InlineData("The motto / Tiesto & Ava Max", "Tiesto & Ava Max", "The motto")]
    [InlineData("Hollywood hills / Sunrise Avenue", "Sunrise Avenue", "Hollywood hills")]
    [InlineData("Don't gimme that / The BossHoss feat. The Tijuana Wonderbrass",
        "The BossHoss feat. The Tijuana Wonderbrass", "Don't gimme that")]
    // Bayern 3: artist first, colon.
    [InlineData("Taylor Swift: Anti-Hero", "Taylor Swift", "Anti-Hero")]
    // Radio Eins: German "von" = "by", and the title arrives quoted.
    [InlineData("\"Departure\" von Robin Kester (feat. Rozi Plain)",
        "Robin Kester (feat. Rozi Plain)", "Departure")]
    public void ParsesTheConventionsThatUsedToBeDiscarded(string raw, string artist, string title)
    {
        Assert.Equal((artist, title), IcyTitleParser.Split(raw));
    }

    // --- The common case must not regress ------------------------------------------------------

    [Theory]
    [InlineData("Ace of Base - The Sign", "Ace of Base", "The Sign")]
    [InlineData("Katy Perry - I Kissed a Girl", "Katy Perry", "I Kissed a Girl")]
    [InlineData("Pizzera & Jaus - Klesch Koids Bier", "Pizzera & Jaus", "Klesch Koids Bier")]
    [InlineData("Ink, Loxy - Headz Roll (Original Mix)", "Ink, Loxy", "Headz Roll (Original Mix)")]
    public void StillParsesArtistDashTitle(string raw, string artist, string title)
    {
        Assert.Equal((artist, title), IcyTitleParser.Split(raw));
    }

    [Fact]
    public void PrefersTheDashWhenATitleAlsoContainsASlash()
    {
        // " - " is overwhelmingly the common convention, so it wins: a title containing a slash is
        // far likelier than an artist containing " - ".
        Assert.Equal(("AC/DC", "Highway to Hell"), IcyTitleParser.Split("AC/DC - Highway to Hell"));
    }

    [Theory]
    [InlineData(" – ")]  // en dash
    [InlineData(" — ")]  // em dash
    public void TreatsTypographicDashesLikeAHyphen(string dash)
    {
        Assert.Equal(("Röyksopp", "Let's Get It Right"),
            IcyTitleParser.Split($"Röyksopp{dash}Let's Get It Right"));
    }

    // --- Idents must still come back with NO artist --------------------------------------------
    // This is the half that keeps the parser honest. Inventing an artist would let a station
    // ident through SongHistoryFilter as a song.

    [Theory]
    [InlineData("SWR3 MOVE Die Feierabendshow")]
    [InlineData("radioeins ab drei mit Max Spallek")]
    [InlineData("www.radioeins.de")]
    [InlineData("Radio Knolliday")]
    [InlineData("Livestream")]
    [InlineData("Nachrichten, Wetter und Verkehr")]
    public void LeavesAnIdentWithoutAnArtist(string raw)
    {
        var (artist, title) = IcyTitleParser.Split(raw);

        Assert.Null(artist);
        Assert.Equal(raw, title);
    }

    /// <summary>The pipe is NOT a separator. Stations append show names with it — splitting there
    /// would turn every FM4 track into a fake artist and defeat the ident check.</summary>
    [Fact]
    public void DoesNotSplitOnAPipe()
    {
        Assert.Equal(("Mattiel", "Count Your Blessings | FM4 Steve Crilley bis 1"),
            IcyTitleParser.Split("Mattiel - Count Your Blessings | FM4 Steve Crilley bis 1"));

        Assert.Null(IcyTitleParser.Split("FM4 Sommerhits 3000 | fm4.orf.at").Artist);
    }

    [Theory]
    [InlineData("- Title")]        // nothing to the left
    [InlineData(" - ")]
    [InlineData("Artist - ")]      // nothing to the right
    [InlineData(": Title")]
    public void RefusesAMalformedSplit(string raw)
    {
        Assert.Null(IcyTitleParser.Split(raw).Artist);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HandlesNothingGracefully(string? raw)
    {
        var (artist, _) = IcyTitleParser.Split(raw!);
        Assert.Null(artist);
    }

    // --- Quoting -------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"Departure\" von Robin Kester", "Departure")]
    [InlineData("„Departure“ von Robin Kester", "Departure")]   // German quotes
    [InlineData("“Departure” von Robin Kester", "Departure")]   // curly
    public void StripsQuotesAroundATitle(string raw, string title)
    {
        Assert.Equal(title, IcyTitleParser.Split(raw).Title);
    }

    [Fact]
    public void LeavesAnUnbalancedQuoteAlone()
    {
        // A stray quote is part of the text, not decoration to strip.
        Assert.Equal("\"Departure", IcyTitleParser.Split("\"Departure von Robin Kester").Title);
    }

    /// <summary>"von" is a whole word, not a substring — "Ludwig van Beethoven" must not split
    /// inside a name.</summary>
    [Fact]
    public void DoesNotSplitInsideAWordContainingVon()
    {
        Assert.Null(IcyTitleParser.Split("Beethoven Symphony No. 5").Artist);
    }

    // --- Tilde-delimited playout records ------------------------------------------------------
    //
    // Virgin Radio Rockstar sends a whole record where a title belongs. Two of its fields — a
    // "now" timestamp and elapsed seconds — change on every metadata push, so treating the payload
    // as the title made every push look like a new song: 39 cuts in ten minutes on 2026-08-06, 34
    // of them rejected as too short, and not one completed song.

    private const string Record =
        "Not Now John~Pink Floyd~~1983~~287~2026-05-08T08:35:40~2026-05-08T08:35:43~United Music Pink Floyd~3.26~dcbd2283-b76d-4e6a-ab4c-c4cc92affafc";

    private const string RecordLater =
        "Not Now John~Pink Floyd~~1983~~287~2026-05-08T08:35:40~2026-05-08T08:36:13~United Music Pink Floyd~33.14~dcbd2283-b76d-4e6a-ab4c-c4cc92affafc";

    [Fact]
    public void ReadsTitleAndArtistFromATildeDelimitedRecord()
    {
        var (artist, title) = IcyTitleParser.Split(Record);

        Assert.Equal("Pink Floyd", artist);
        Assert.Equal("Not Now John", title);
    }

    /// <summary>
    /// The whole point: the same song announced 30 seconds later must parse identically, or
    /// boundary detection cuts a new segment every push.
    /// </summary>
    [Fact]
    public void TheSameSongParsesIdenticallyAsItsVolatileFieldsChange()
    {
        Assert.NotEqual(Record, RecordLater);   // the payloads really do differ
        Assert.Equal(IcyTitleParser.Split(Record), IcyTitleParser.Split(RecordLater));
    }

    /// <summary>A record whose second field is empty still yields a usable title and no invented
    /// artist — inventing one is how an ident gets through as a song.</summary>
    [Fact]
    public void ARecordWithNoArtistFieldYieldsNoArtist()
    {
        var (artist, title) = IcyTitleParser.Split("Some Ident~~~~~~");

        Assert.Null(artist);
        Assert.Equal("Some Ident", title);
    }

    /// <summary>An ordinary title carrying a tilde or two is not a record, and must still split on
    /// its real separator.</summary>
    [Theory]
    [InlineData("Alice Cooper - Wish You Were Here ~ live", "Alice Cooper", "Wish You Were Here ~ live")]
    [InlineData("Some Artist - A ~ B ~ C", "Some Artist", "A ~ B ~ C")]
    public void DoesNotTreatAnOccasionalTildeAsARecord(string raw, string artist, string title)
    {
        Assert.Equal((artist, title), IcyTitleParser.Split(raw));
    }
}
