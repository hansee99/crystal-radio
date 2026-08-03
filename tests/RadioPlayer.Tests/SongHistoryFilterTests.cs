using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The "is this actually a song" gate. In DJ mode this carries far more weight than tidying a
/// history list: it is the only thing that can catch a news bulletin or ad break, because the
/// acoustic detector provably cannot. Measured on one session's own audio (2026-08-03), Ö3's
/// five-minute "Nachrichten, Wetter und Verkehr" scored 3% music and Ace of Base's "The Sign"
/// scored 3% as well — so when a station labels a segment as itself, the label is the only
/// evidence there is.
/// </summary>
public class SongHistoryFilterTests
{
    // --- The regression: Ö3 idents that reached the mix as "songs" ----------------------------
    // The station announces itself as "HITRADIO Ö3" while the directory calls it
    // "ORF Hitradio Ö3". The artist was a SUBSET of the station name, so a one-directional
    // Contains() could never fire, and 2:45 of talk plus 5:06 of news went into the mix.

    [Theory]
    [InlineData("ORF Hitradio Ö3")]
    [InlineData("ORF Hitradio Ö3 | HQ")]
    public void RejectsAnIdentWhoseArtistIsTheStationAbbreviated(string station)
    {
        Assert.False(SongHistoryFilter.IsLikelySong("Livestream", "HITRADIO Ö3", station));
        Assert.False(SongHistoryFilter.IsLikelySong("Nachrichten, Wetter und Verkehr", "HITRADIO Ö3", station));
    }

    [Fact]
    public void RejectsRegardlessOfCaseSpacingAndPunctuation()
    {
        // Stations shout, hyphenate and decorate; the comparison shouldn't care.
        Assert.False(SongHistoryFilter.IsLikelySong("Livestream", "hit-radio Ö3", "ORF Hitradio Ö3"));
        Assert.False(SongHistoryFilter.IsLikelySong("Livestream", "  FM4   ORF  ", "FM4 | ORF | HQ"));
    }

    // --- Everything the same session got RIGHT must stay right --------------------------------

    [Theory]
    [InlineData("The Sign", "Ace of Base", "ORF Hitradio Ö3")]
    [InlineData("I Kissed a Girl", "Katy Perry", "ORF Hitradio Ö3 | HQ")]
    [InlineData("Break Your Wall", "Simon Lewis", "ORF Hitradio Ö3")]
    [InlineData("Klesch Koids Bier", "Pizzera & Jaus", "ORF Hitradio Ö3")]
    [InlineData("Nur nach vorn", "Die Toten Hosen", "ORF Hitradio Ö3 | HQ")]
    [InlineData("Tiroteo | FM4 Hot", "Daniela Lalita ft. Mura Masa", "FM4 | ORF")]
    [InlineData("Count Your Blessings | FM4 Steve Crilley bis 1", "Mattiel", "FM4 | ORF | HQ")]
    [InlineData("We Were Wild Once", "Farewell Dear Ghost", "FM4 | ORF")]
    public void KeepsTheRealSongsFromThatSession(string title, string artist, string station)
    {
        Assert.True(SongHistoryFilter.IsLikelySong(title, artist, station));
    }

    /// <summary>The length floor: without it, any station whose name happens to contain a short
    /// artist name would swallow that artist's songs.</summary>
    [Fact]
    public void AShortArtistNameInsideTheStationNameIsNotAnIdent()
    {
        Assert.True(SongHistoryFilter.IsLikelySong("Side", "AINDY", "Radio Aindy Vienna"));
        Assert.True(SongHistoryFilter.IsLikelySong("Tough", "Nash", "Nashville Country Radio"));
    }

    /// <summary>Only the artist slot gets the abbreviation rule. A title sitting inside the
    /// station's name is weak evidence — "Paradise" on Radio Paradise is a plausible song.</summary>
    [Fact]
    public void ATitleInsideTheStationNameIsStillKept()
    {
        Assert.True(SongHistoryFilter.IsLikelySong("Paradise", "Coldplay", "Radio Paradise"));
    }

    // --- Pre-existing rules, pinned so the rewrite didn't quietly drop one --------------------

    [Fact]
    public void RejectsAFieldCarryingTheFullStationName()
    {
        Assert.False(SongHistoryFilter.IsLikelySong("Radio Paradise - commercial free", "Ident", "Radio Paradise"));
        Assert.False(SongHistoryFilter.IsLikelySong("Some Song", "Radio Paradise Studio", "Radio Paradise"));
    }

    [Theory]
    [InlineData(null, "Artist")]
    [InlineData("", "Artist")]
    [InlineData("   ", "Artist")]
    [InlineData("Title", null)]
    [InlineData("Title", "")]
    [InlineData("Title", "   ")]
    public void RejectsAnythingMissingATitleOrAnArtist(string? title, string? artist)
    {
        // Real songs arrive as "Artist - Title"; an ident usually has no artist part at all, and
        // the per-connect station-info publish has a null artist by construction.
        Assert.False(SongHistoryFilter.IsLikelySong(title, artist, "Some Station"));
    }

    [Fact]
    public void RejectsAUrlPushedAsATitle()
    {
        Assert.False(SongHistoryFilter.IsLikelySong("https://example.fm", "Station", "Some Station"));
    }

    [Theory]
    [InlineData("Werbung")]
    [InlineData("Jingle")]
    [InlineData("Sponsored message")]
    [InlineData("ADVERT")]
    public void RejectsAdMarkersInEitherField(string marker)
    {
        Assert.False(SongHistoryFilter.IsLikelySong(marker, "Whoever", "Some Station"));
        Assert.False(SongHistoryFilter.IsLikelySong("Whatever", marker, "Some Station"));
    }

    // --- Ad breaks the station labels as such (real session, 2026-08-03) ----------------------
    // Two of these reached a mix. The detector had called both TALK (5.8% and 11.3% music); they
    // survived because the post-trim duration gate is the primary check and the trim left them
    // just over the minimum. The metadata says plainly what they are.

    [Theory]
    [InlineData("ADBREAK_120000", "011FM")]
    [InlineData("THIS STATION WILL CONTINUE AFTER THIS BREAK", "ADWTAG_122000")]
    [InlineData("PROMO_143000", "Some Station")]
    [InlineData("We'll be right back after these messages", "Station")]
    public void RejectsAnAdBreakTheStationNamed(string title, string artist)
    {
        Assert.False(SongHistoryFilter.IsLikelySong(title, artist, "Hard Rock Heaven"));
    }

    /// <summary>The cart pattern is deliberately narrow — UPPERCASE and four or more digits.
    /// A song can plausibly be titled "Extended_2024"; nothing is plausibly "PROMO_143000".
    /// A false positive here throws away a real song, so precision beats recall.</summary>
    [Theory]
    [InlineData("Extended_2024", "Some Artist")]      // lowercase letters — a plausible title
    [InlineData("Blink_182", "Blink-182")]            // too few digits
    [InlineData("MP3_12", "Artist")]
    public void DoesNotMistakeAPlausibleTitleForAPlayoutCart(string title, string artist)
    {
        Assert.True(SongHistoryFilter.IsLikelySong(title, artist, "Some Station"));
    }

    /// <summary>Self-titled tracks are real and common in exactly the genres being harvested, so
    /// artist == title is NOT treated as an ident however much it looks like one.</summary>
    [Theory]
    [InlineData("Black Sabbath")]
    [InlineData("Bad Company")]
    [InlineData("Iron Maiden")]
    public void KeepsASelfTitledTrack(string name)
    {
        Assert.True(SongHistoryFilter.IsLikelySong(name, name, "Hard Rock Heaven"));
    }

    [Fact]
    public void KeepsASongWhenTheStationIsUnknown()
    {
        // No station name to compare against is not a reason to throw a song away.
        Assert.True(SongHistoryFilter.IsLikelySong("The Sign", "Ace of Base", null));
        Assert.True(SongHistoryFilter.IsLikelySong("The Sign", "Ace of Base", "  "));
    }

    /// <summary>Codec/bitrate decoration on the directory's name must not defeat the match — it's
    /// exactly the kind of thing that differs between a directory entry and the stream's own
    /// ident, and duplicate codec variants of one station are common in a harvest pool.</summary>
    [Fact]
    public void IgnoresCodecAndBitrateDecorationOnTheStationName()
    {
        Assert.False(SongHistoryFilter.IsLikelySong("Livestream", "HITRADIO Ö3", "ORF Hitradio Ö3 (128k MP3)"));
    }
}
