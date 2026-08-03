using RadioPlayer.Controls;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The scroll arithmetic behind keeping DJ mode's playing song centred. Offsets are in ITEMS, not
/// pixels — a ListBox on a virtualizing panel scrolls by row — and the interesting part is the
/// clamping, because centring is impossible near either end of the list.
/// </summary>
public class ListCenteringTests
{
    // A 9-row viewport: 4 rows above the centre, the centre, 4 below.
    private const double Viewport = 9;

    [Fact]
    public void CentresTheTargetRowWhenThereIsRoomOnBothSides()
    {
        // Row 20 centred in a 9-row viewport → 4 rows visible above it.
        Assert.Equal(16, ListCentering.TargetOffset(20, Viewport, scrollableItems: 100));
    }

    [Fact]
    public void ClampsAtTheTopRatherThanScrollingPastIt()
    {
        // Two songs played: there is nothing above row 0 to show, so the list sits flush. Without
        // the clamp this asks for a negative offset and the row lands wherever WPF decides.
        Assert.Equal(0, ListCentering.TargetOffset(2, Viewport, scrollableItems: 100));
    }

    [Fact]
    public void ClampsAtTheBottomRatherThanScrollingPastIt()
    {
        // Playing the last song of 40: the final rows can't be pushed up past the end.
        Assert.Equal(31, ListCentering.TargetOffset(39, Viewport, scrollableItems: 31));
    }

    [Fact]
    public void DoesNothingWhenTheWholeListAlreadyFits()
    {
        // Three songs in a nine-row viewport: nothing to scroll, and asking anyway would jump the
        // list to a position it can't hold.
        Assert.Equal(0, ListCentering.TargetOffset(2, Viewport, scrollableItems: 0));
    }

    [Fact]
    public void HandlesAnExactlyCentreableFirstPosition()
    {
        // Row 4 of a 9-row viewport is already the middle row — offset 0 is correct, not a clamp
        // artefact.
        Assert.Equal(0, ListCentering.TargetOffset(4, Viewport, scrollableItems: 100));
    }

    [Theory]
    [InlineData(1)]     // a one-row viewport: the target is the only visible row
    [InlineData(2)]
    public void HandlesATinyViewport(double viewport)
    {
        var offset = ListCentering.TargetOffset(10, viewport, scrollableItems: 100);

        Assert.InRange(offset, 9, 10);
    }

    [Fact]
    public void NeverReturnsMoreThanTheScrollableRange()
    {
        // Guards the invariant the caller relies on: ScrollToVerticalOffset silently ignores an
        // out-of-range value, which would leave the row uncentred with no error anywhere.
        for (var i = 0; i < 50; i++)
            Assert.InRange(ListCentering.TargetOffset(i, Viewport, scrollableItems: 41), 0, 41);
    }
}
