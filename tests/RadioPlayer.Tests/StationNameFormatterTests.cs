using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// This started as display-only cleanup, but DJ harvesting now uses it as an identity key: two
/// entries that clean to the same name are treated as one station, so a codec variant can't take
/// a second slot in a four-harvester pool. That makes both over-matching (merging genuinely
/// different stations) and under-matching (letting duplicates through) worth pinning down.
/// </summary>
public class StationNameFormatterTests
{
    [Theory]
    // The case that cost half a harvest pool: same station, two codecs.
    [InlineData("SomaFM Beat Blender (128k MP3)", "SomaFM Beat Blender")]
    [InlineData("SomaFM Beat Blender (128k AAC)", "SomaFM Beat Blender")]
    [InlineData("Jazz FM (128K MP3)", "Jazz FM")]
    [InlineData("Deep House Lounge | 320 kbps", "Deep House Lounge")]
    [InlineData("Liquid DnB - 96kbit AAC+", "Liquid DnB")]
    [InlineData("Some Station [44.1kHz]", "Some Station")]
    public void Clean_StripsCodecAndBitrateNoise(string raw, string expected) =>
        Assert.Equal(expected, StationNameFormatter.Clean(raw));

    [Theory]
    // Bare trailing numbers are part of the name, not a bitrate — these must stay distinct.
    [InlineData("Radio 1")]
    [InlineData("Radio 2")]
    [InlineData("Kanal 5")]
    [InlineData("Studio Brussel")]
    [InlineData("Bassdrive")]
    public void Clean_LeavesRealNamesAlone(string name) =>
        Assert.Equal(name, StationNameFormatter.Clean(name));

    [Fact]
    public void Clean_KeepsDifferentStationsDistinct()
    {
        // Over-matching would be worse than the bug it fixes: merging these would silently drop
        // a station from the pool.
        Assert.NotEqual(StationNameFormatter.Clean("Radio 1 (128k MP3)"),
                        StationNameFormatter.Clean("Radio 2 (128k MP3)"));
        Assert.NotEqual(StationNameFormatter.Clean("SomaFM Groove Salad (128k MP3)"),
                        StationNameFormatter.Clean("SomaFM Beat Blender (128k MP3)"));
    }

    [Fact]
    public void Clean_FallsBackToTheOriginalWhenCleaningWouldEmptyIt()
    {
        // A station literally named after its codec still needs an identity.
        Assert.Equal("MP3", StationNameFormatter.Clean("MP3"));
        Assert.Equal("", StationNameFormatter.Clean(""));
    }
}
