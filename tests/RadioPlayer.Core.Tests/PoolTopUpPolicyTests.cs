using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The decisions behind mid-session pool top-up (#41). A session used to source stations once and
/// never again, so a pool could only shrink — when the reserve runs out, a retired harvester's slot
/// is lost for good. These are the three judgements that make refilling it safe rather than
/// expensive: when an attempt may run, when an offline-started session stops trying to heal, and
/// what counts as a station the session has already tried.
/// </summary>
public class PoolTopUpPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HealAfter = TimeSpan.FromMinutes(3);

    // --- the throttle ---------------------------------------------------------

    /// <summary>Each attempt costs an interpreter call and a ranker call, and harvester deaths
    /// arrive in bursts — three stations dropping in a minute must not buy three of each.</summary>
    [Fact]
    public void ASecondAttemptWaitsOutTheCooldown()
    {
        Assert.False(DjHarvestService.MayTopUp(Now, Now.AddMinutes(1), Cooldown));
        Assert.False(DjHarvestService.MayTopUp(Now, Now.AddMinutes(9.9), Cooldown));
        Assert.True(DjHarvestService.MayTopUp(Now, Now.AddMinutes(10), Cooldown));
        Assert.True(DjHarvestService.MayTopUp(Now, Now.AddMinutes(30), Cooldown));
    }

    /// <summary>A session that has never topped up shouldn't have to wait for its first attempt.</summary>
    [Fact]
    public void TheFirstAttemptIsNotThrottled()
    {
        Assert.True(DjHarvestService.MayTopUp(DateTime.MinValue, Now, Cooldown));
    }

    // --- healing an offline-started session -----------------------------------

    [Fact]
    public void AHealthySessionNeverHeals()
    {
        // Nothing is wrong, so this must not become a "is the directory back yet?" poll.
        Assert.False(DjHarvestService.ShouldHealOfflineStart(
            startedOffline: false, reachedDirectory: false, Now, Now.AddHours(3), HealAfter));
    }

    [Fact]
    public void AnOfflineStartedSessionWaitsBeforeItsFirstAttempt()
    {
        Assert.False(DjHarvestService.ShouldHealOfflineStart(
            startedOffline: true, reachedDirectory: false, Now, Now.AddMinutes(2), HealAfter));
        Assert.True(DjHarvestService.ShouldHealOfflineStart(
            startedOffline: true, reachedDirectory: false, Now, Now.AddMinutes(3), HealAfter));
    }

    /// <summary>
    /// Reaching the directory is what ends the handicap, so until it happens the session keeps
    /// trying — an outage lasting an hour shouldn't spend the one attempt in its first three
    /// minutes. The cooldown, not this, is what keeps that affordable.
    /// </summary>
    [Fact]
    public void AnOfflineStartedSessionKeepsTryingWhileTheDirectoryIsStillDown()
    {
        Assert.True(DjHarvestService.ShouldHealOfflineStart(
            startedOffline: true, reachedDirectory: false, Now, Now.AddMinutes(40), HealAfter));
    }

    [Fact]
    public void HealingStopsOnceTheDirectoryHasBeenReached()
    {
        Assert.False(DjHarvestService.ShouldHealOfflineStart(
            startedOffline: true, reachedDirectory: true, Now, Now.AddHours(2), HealAfter));
    }

    // --- what "already tried" means -------------------------------------------

    /// <summary>
    /// Exclusion and the pool's own dedupe have to agree, or a top-up hands back the stations
    /// already playing. One function, two callers — this pins what it produces.
    /// </summary>
    [Fact]
    public void AStationIsIdentifiedByBothItsUrlAndItsName()
    {
        var keys = DjHarvestService.DedupeKeys("http://example/stream", "Chill FM").ToList();

        Assert.Equal(2, keys.Count);
        Assert.Contains("url:http://example/stream", keys);
        Assert.Single(keys, k => k.StartsWith("name:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reason the name key exists: two codec variants of one station are one station. Without
    /// this they'd take two harvester slots and record every song twice — and a top-up would happily
    /// re-add the AAC twin of a station already playing on MP3.
    /// </summary>
    [Fact]
    public void CodecVariantsOfOneStationShareTheirNameKey()
    {
        var mp3 = DjHarvestService.DedupeKeys("http://example/mp3", "SomaFM Lush (128k MP3)")
            .First(k => k.StartsWith("name:", StringComparison.Ordinal));
        var aac = DjHarvestService.DedupeKeys("http://example/aac", "SomaFM Lush (128k AAC)")
            .First(k => k.StartsWith("name:", StringComparison.Ordinal));

        Assert.Equal(mp3, aac);
    }

    [Fact]
    public void DifferentStationsDoNotShareKeys()
    {
        var a = DjHarvestService.DedupeKeys("http://example/a", "Chill FM").ToList();
        var b = DjHarvestService.DedupeKeys("http://example/b", "Polka Palace").ToList();

        Assert.Empty(a.Intersect(b));
    }

    /// <summary>A url can't be mistaken for a name, however odd the station is called.</summary>
    [Fact]
    public void AUrlCannotCollideWithAName()
    {
        var keys = DjHarvestService.DedupeKeys("x", "http://example/stream").ToList();

        Assert.DoesNotContain("url:http://example/stream", keys);
    }
}
