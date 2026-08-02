using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The policy that decides whether a station keeps its harvester slot. Worth pinning down in both
/// directions: retiring too eagerly churns the pool and throws away stations mid-track, retiring
/// too late leaves dead weight burning bandwidth for the whole session.
/// </summary>
public class HarvesterRetirementTests
{
    private static readonly DateTime Now = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MetadataGrace = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);

    private static bool Retire(int titlesSeen, double connectedMinutesAgo,
        double? lastSegmentMinutesAgo, out string reason) =>
        DjHarvestService.ShouldRetire(
            titlesSeen,
            Now.AddMinutes(-connectedMinutesAgo),
            lastSegmentMinutesAgo is { } m ? Now.AddMinutes(-m) : default,
            Now, MetadataGrace, IdleLimit, out reason);

    [Fact]
    public void AStationServingNoMetadata_IsDroppedAfterTheGrace()
    {
        Assert.True(Retire(titlesSeen: 0, connectedMinutesAgo: 7, lastSegmentMinutesAgo: null, out var reason));
        Assert.Equal("no metadata", reason);
    }

    [Fact]
    public void AStationServingNoMetadata_IsGivenTheFullGraceFirst()
    {
        // It may have connected mid-song and only announce on change.
        Assert.False(Retire(titlesSeen: 0, connectedMinutesAgo: 5, lastSegmentMinutesAgo: null, out _));
    }

    [Fact]
    public void ALongMixProducingNoSegments_IsDroppedOnTheIdleLimit()
    {
        // Announced one title and has been playing it ever since — the DJ-set case. It has
        // metadata, so the first rule never fires; only the idle rule catches it.
        Assert.True(Retire(titlesSeen: 1, connectedMinutesAgo: 20, lastSegmentMinutesAgo: null, out var reason));
        Assert.Equal("no songs", reason);
    }

    [Fact]
    public void AStationThatStoppedProducing_IsDroppedEvenThoughItDeliveredEarlier()
    {
        // Stalled stream that never raised an error.
        Assert.True(Retire(titlesSeen: 12, connectedMinutesAgo: 60, lastSegmentMinutesAgo: 16, out var reason));
        Assert.Equal("no songs", reason);
    }

    [Fact]
    public void AProductiveStationIsLeftAlone()
    {
        Assert.False(Retire(titlesSeen: 12, connectedMinutesAgo: 60, lastSegmentMinutesAgo: 4, out _));
    }

    [Fact]
    public void AStationPartwayThroughItsFirstLongTrackIsLeftAlone()
    {
        // 10:04 was the longest real track measured; nothing completed yet is normal at this point.
        Assert.False(Retire(titlesSeen: 1, connectedMinutesAgo: 11, lastSegmentMinutesAgo: null, out _));
    }

    [Fact]
    public void TheIdleClockStartsAtConnectionWhenNothingHasEverCompleted()
    {
        // Guards the default(DateTime) case: measuring from DateTime.MinValue would retire every
        // harvester on the watchdog's first tick.
        Assert.False(Retire(titlesSeen: 3, connectedMinutesAgo: 1, lastSegmentMinutesAgo: null, out _));
    }
}
