using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RadioPlayer.Controls;

/// <summary>
/// Keeps one row of a list vertically centred — used by DJ mode's Mix list so the playing song
/// stays put and the songs either side of it stay visible without anyone scrolling.
///
/// <para><b>Offsets are in ITEMS, not pixels.</b> That's the whole trick. A ListBox on a
/// virtualizing panel defaults to <c>ScrollUnit="Item"</c>, so <c>VerticalOffset</c>,
/// <c>ViewportHeight</c> and <c>ScrollableHeight</c> all count rows; the usual recipe of
/// measuring the target container with <c>TransformToAncestor</c> and converting to a pixel offset
/// mixes the two unit systems and lands in the wrong place. It also needs the container to exist,
/// which under virtualization it doesn't for a row that's currently off-screen. Counting rows
/// avoids realisation, transforms and a layout round-trip entirely — and works because these rows
/// are a fixed height. Set <c>VirtualizingPanel.ScrollUnit="Item"</c> explicitly on the list so
/// this doesn't depend on a default.</para>
/// </summary>
public static class ListCentering
{
    /// <summary>
    /// Index of the row to keep centred. Bind it to whatever the view model calls "playing now";
    /// -1 parks the behaviour (nothing playing).
    /// </summary>
    public static readonly DependencyProperty CurrentIndexProperty =
        DependencyProperty.RegisterAttached(
            "CurrentIndex", typeof(int), typeof(ListCentering),
            new PropertyMetadata(-1, OnCurrentIndexChanged));

    public static void SetCurrentIndex(DependencyObject element, int value) =>
        element.SetValue(CurrentIndexProperty, value);

    public static int GetCurrentIndex(DependencyObject element) =>
        (int)element.GetValue(CurrentIndexProperty);

    private static void OnCurrentIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || e.NewValue is not int index)
            return;

        // The row may not exist yet: the queue grows and the index is raised in the same beat, so
        // a fresh append can arrive before the list has measured. Loaded priority runs after
        // layout, by which point Items and the scroll extent are both current.
        list.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => Centre(list, index)));
    }

    private static void Centre(ListBox list, int index)
    {
        if (index < 0 || index >= list.Items.Count)
            return;
        if (FindScrollViewer(list) is not { } scroller)
            return;

        scroller.ScrollToVerticalOffset(
            TargetOffset(index, scroller.ViewportHeight, scroller.ScrollableHeight));
    }

    /// <summary>
    /// Where to scroll to put <paramref name="index"/> in the middle, in item units.
    ///
    /// Pure so the interesting part — the clamping — can be tested. Centring is impossible near
    /// either end: with three songs played you cannot scroll above the first row, so the playing
    /// one physically can't sit in the middle. Clamping to the scrollable range means the list
    /// centres when it can and sits flush at the ends when it can't, rather than fighting a limit
    /// it can't reach.
    /// </summary>
    internal static double TargetOffset(int index, double viewportItems, double scrollableItems)
    {
        if (scrollableItems <= 0)
            return 0; // everything already fits — nothing to centre

        // (viewport - 1) / 2 rows above the target puts the target itself in the middle row.
        var target = index - (viewportItems - 1) / 2.0;
        return Math.Clamp(target, 0, scrollableItems);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found)
            return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } child)
                return child;
        return null;
    }
}
