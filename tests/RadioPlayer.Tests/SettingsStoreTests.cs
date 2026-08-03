using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The DjPersonality setting is stored as TEXT on purpose. An enum property would throw during
/// deserialization on an unrecognised value, and SettingsStore.Load catches that by returning
/// all-defaults — so one typo in a hand-edited file would silently reset volume, library folder,
/// API key and every DJ knob. Text plus a lenient parse costs one setting instead.
/// </summary>
public class SettingsStoreTests
{
    [Theory]
    [InlineData("Warm", DjPersonality.Warm)]
    [InlineData("upbeat", DjPersonality.Upbeat)]
    [InlineData("LATENIGHT", DjPersonality.LateNight)]
    [InlineData("Wry", DjPersonality.Wry)]
    [InlineData("Professional", DjPersonality.Professional)]
    public void ResolveDjPersonality_ParsesCaseInsensitively(string stored, DjPersonality expected)
    {
        Assert.Equal(expected, new AppSettings { DjPersonality = stored }.ResolveDjPersonality());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sarcastic")]      // plausible but not one of ours
    [InlineData("Late Night")]     // the space is not optional — it's LateNight
    [InlineData("3")]              // a stray number must not index into the enum
    public void ResolveDjPersonality_FallsBackToWarmOnAnythingUnrecognised(string stored)
    {
        Assert.Equal(DjPersonality.Warm, new AppSettings { DjPersonality = stored }.ResolveDjPersonality());
    }

    [Fact]
    public void ResolveDjPersonality_ToleratesSurroundingWhitespace()
    {
        // Enum.TryParse trims, so a hand-edited file with a stray space still works.
        Assert.Equal(DjPersonality.Wry, new AppSettings { DjPersonality = " Wry " }.ResolveDjPersonality());
    }

    [Fact]
    public void DefaultsToWarm()
    {
        Assert.Equal(DjPersonality.Warm, new AppSettings().ResolveDjPersonality());
    }
}
