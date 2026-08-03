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

    // --- Migrating an existing settings file --------------------------------------------------
    // A changed default is invisible to anyone who already has a settings file: their old value is
    // written out explicitly, so they keep it forever. That is precisely the population running
    // the app, so a default change without a migration reaches nobody who matters.

    [Fact]
    public void Migrate_TurnsOnTheMusicFloorForAnExistingFileThatStillHasItOff()
    {
        var old = new AppSettings { SettingsVersion = 0, DjMusicFractionFloor = 0 };

        var migrated = SettingsStore.Migrate(old);

        Assert.Equal(0.20, migrated.DjMusicFractionFloor);
        Assert.Equal(1, migrated.SettingsVersion);
    }

    [Fact]
    public void Migrate_LeavesADeliberateChoiceAlone()
    {
        // Someone who set 0.35 by hand meant it; the migration is for people who never touched it.
        var chosen = new AppSettings { SettingsVersion = 0, DjMusicFractionFloor = 0.35 };

        Assert.Equal(0.35, SettingsStore.Migrate(chosen).DjMusicFractionFloor);
    }

    [Fact]
    public void Migrate_DoesNotReapplyToAnAlreadyMigratedFile()
    {
        // Someone who has since turned the floor OFF deliberately must not have it forced back on
        // every launch.
        var already = new AppSettings { SettingsVersion = 1, DjMusicFractionFloor = 0 };

        Assert.Equal(0, SettingsStore.Migrate(already).DjMusicFractionFloor);
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var s = SettingsStore.Migrate(new AppSettings { SettingsVersion = 0, DjMusicFractionFloor = 0 });
        var twice = SettingsStore.Migrate(s);

        Assert.Equal(0.20, twice.DjMusicFractionFloor);
        Assert.Equal(1, twice.SettingsVersion);
    }
}
