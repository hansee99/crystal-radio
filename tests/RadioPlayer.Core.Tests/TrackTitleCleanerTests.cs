using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

public class TrackTitleCleanerTests
{
    [Theory]
    // The artifact class from the UX audit: a templated "(Artist / Year)" with a blank field.
    [InlineData("We Gotta Get Out Of This Place (\"Stereo\") ( / 1965)",
                "We Gotta Get Out Of This Place (\"Stereo\") (1965)")]
    [InlineData("Dirty Water ( / 1966)", "Dirty Water (1966)")]
    [InlineData("Song (1970 / )", "Song (1970)")]
    [InlineData("Song ( / )", "Song")]
    [InlineData("Song [ - ]", "Song")]
    [InlineData("Song (  )", "Song")]
    // Legitimate content must be untouched.
    [InlineData("AC/DC - Back In Black", "AC/DC - Back In Black")]
    [InlineData("Song (Radio Edit)", "Song (Radio Edit)")]
    [InlineData("Song (Artist / 1965)", "Song (Artist / 1965)")]
    [InlineData("Tom Sawyer [Remastered]", "Tom Sawyer [Remastered]")]
    public void Clean_StripsEmptyFieldArtifacts(string input, string expected) =>
        Assert.Equal(expected, TrackTitleCleaner.Clean(input));

    [Fact]
    public void Clean_PassesThroughNullAndWhitespace()
    {
        Assert.Null(TrackTitleCleaner.Clean(null));
        Assert.Equal("", TrackTitleCleaner.Clean(""));
        Assert.Equal("   ", TrackTitleCleaner.Clean("   "));
    }
}
