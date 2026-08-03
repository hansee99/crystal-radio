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

    // --- Stations whose metadata is promotional text, not track titles ------------------------
    // SWR3 and Radio Eins cycle a show name, a phone number and a slogan through StreamTitle every
    // 18-21 seconds while music plays. Every rotation looks like a track boundary, so the song
    // underneath is chopped into fragments that are all discarded as idents. The audio is fine;
    // the metadata simply never names the track, and nothing downstream can recover a title that
    // was never sent — so the slot is better spent elsewhere.

    [Theory]
    [InlineData(22, 3)]   // SWR3, measured across two sessions
    [InlineData(18, 1)]   // Radio Eins, same
    public void AStationCyclingPromosThroughStreamTitleIsDropped(int idents, int segments)
    {
        Assert.True(DjHarvestService.IsMetadataCarousel(idents, segments));
    }

    /// <summary>Adverts legitimately produce ident boundaries, so a station that plays them and
    /// still delivers songs has to survive. This is what the ratio protects.</summary>
    [Theory]
    [InlineData(8, 3)]    // an ad-heavy hour on an otherwise fine station
    [InlineData(12, 5)]
    [InlineData(30, 12)]
    public void AStationThatAlsoPlaysAdvertsIsKept(int idents, int segments)
    {
        Assert.False(DjHarvestService.IsMetadataCarousel(idents, segments));
    }

    /// <summary>Judging early would drop a good station for a single commercial break. A carousel
    /// reaches the minimum in under three minutes, so nothing is lost by waiting.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(7, 0)]    // one short of the minimum, and producing nothing — still too early
    public void NoVerdictUntilThereAreEnoughBoundariesToJudge(int idents, int segments)
    {
        Assert.False(DjHarvestService.IsMetadataCarousel(idents, segments));
    }

    [Fact]
    public void AStationProducingNothingButIdentsIsDroppedOnceThereIsEnoughEvidence()
    {
        Assert.False(DjHarvestService.IsMetadataCarousel(7, 0)); // not yet
        Assert.True(DjHarvestService.IsMetadataCarousel(8, 0));  // now
    }

    [Fact]
    public void TheIdleClockStartsAtConnectionWhenNothingHasEverCompleted()
    {
        // Guards the default(DateTime) case: measuring from DateTime.MinValue would retire every
        // harvester on the watchdog's first tick.
        Assert.False(Retire(titlesSeen: 3, connectedMinutesAgo: 1, lastSegmentMinutesAgo: null, out _));
    }
}
