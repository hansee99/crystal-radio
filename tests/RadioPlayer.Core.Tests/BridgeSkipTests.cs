using RadioPlayer.Models;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Skipping during a live DJ bridge (#49) walks the harvest pool, not the user's saved stations.
///
/// <para>The reported symptom was the transport jumping to Radio Paradise — the first entry in the
/// listener's own station list — because the bridge runs on the radio engine, so
/// <c>UsesLocalEngine</c> is false and "next" fell through to the ordinary station cycle. The
/// routing fix lives in the view model; what is worth pinning here is the walk itself, where the
/// wrap-around and the retired-station case live.</para>
///
/// <para>Pure, following <c>ShouldRetire</c>: a real pool only exists while harvesters hold live
/// connections, so a test that built one would be testing the network.</para>
/// </summary>
public class BridgeSkipTests
{
    private static Station S(string name) =>
        new(name, $"http://example/{name.ToLowerInvariant()}", StreamFormat.Mp3);

    private static readonly Station One = S("One");
    private static readonly Station Two = S("Two");
    private static readonly Station Three = S("Three");

    [Fact]
    public void AnEmptyPoolOffersNothingToSkipTo()
    {
        Assert.Null(DjHarvestService.NextInPool([], null));
        Assert.Null(DjHarvestService.NextInPool([], One));
    }

    [Fact]
    public void WithNothingPlayingYetItStartsAtTheTopOfThePool()
    {
        Assert.Equal(One, DjHarvestService.NextInPool([One, Two, Three], null));
    }

    [Fact]
    public void SkippingWalksThePoolInOrder()
    {
        Assert.Equal(Two, DjHarvestService.NextInPool([One, Two, Three], One));
        Assert.Equal(Three, DjHarvestService.NextInPool([One, Two, Three], Two));
    }

    [Fact]
    public void SkippingPastTheEndWrapsAround()
    {
        Assert.Equal(One, DjHarvestService.NextInPool([One, Two, Three], Three));
    }

    /// <summary>
    /// The caller relies on getting the same station back rather than null, so it can leave
    /// playback alone instead of tearing down and rebuilding a bridge to where it already was.
    /// </summary>
    [Fact]
    public void ASingleStationPoolSkipsBackToItself()
    {
        Assert.Equal(One, DjHarvestService.NextInPool([One], One));
    }

    /// <summary>
    /// A station can be retired out from under the bridge at any moment — the idle rule runs on a
    /// 30-second watchdog. "The station you were on is gone" is not a reason to refuse to move.
    /// </summary>
    [Fact]
    public void SkippingFromAStationNoLongerInThePoolStartsOver()
    {
        Assert.Equal(One, DjHarvestService.NextInPool([One, Two], S("Retired")));
    }

    /// <summary>Position is by stream url, not by name: the pool can hold two entries the directory
    /// names differently, and the name is not what the engine is playing.</summary>
    [Fact]
    public void PositionIsFoundByUrlNotName()
    {
        var renamed = new Station("A Different Name", Two.Url, StreamFormat.Aac);

        Assert.Equal(Three, DjHarvestService.NextInPool([One, Two, Three], renamed));
    }

    [Fact]
    public void UrlComparisonIgnoresCase()
    {
        var shouted = new Station("Two", Two.Url.ToUpperInvariant(), StreamFormat.Mp3);

        Assert.Equal(Three, DjHarvestService.NextInPool([One, Two, Three], shouted));
    }
}
