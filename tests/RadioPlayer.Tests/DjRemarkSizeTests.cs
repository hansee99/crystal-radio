using System.Reflection;
using RadioPlayer.Services;
using RadioPlayer.ViewModels;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The user-settable size of the DJ's remark (#61) — the stored setting, the numbers it maps to,
/// and the property names the view binds by string.
/// </summary>
public class DjRemarkSizeTests
{
    private static readonly DjRemarkSize[] Ascending =
        [DjRemarkSize.Small, DjRemarkSize.Medium, DjRemarkSize.Large];

    // --- the setting -------------------------------------------------------------------------

    /// <summary>#61 is the report that what shipped was too small, so the default has to be bigger
    /// than it — a default of Small would ship the bug again under a new name.</summary>
    [Fact]
    public void TheDefaultIsBiggerThanWhatShipped()
    {
        Assert.Equal(DjRemarkSize.Medium, new AppSettings().ResolveDjRemarkSize());
        Assert.True(DjRemarkMetrics.FontSize(DjRemarkSize.Medium)
                    > DjRemarkMetrics.FontSize(DjRemarkSize.Small));
    }

    [Theory]
    [InlineData("Small", DjRemarkSize.Small)]
    [InlineData("large", DjRemarkSize.Large)]      // case is not the user's problem
    [InlineData("  Medium  ", DjRemarkSize.Medium)]
    public void AStoredNameRoundTrips(string stored, DjRemarkSize expected)
    {
        Assert.Equal(expected, new AppSettings { DjRemarkSize = stored }.ResolveDjRemarkSize());
    }

    /// <summary>
    /// The reason this is name-matched rather than Enum.TryParse: that also accepts numeric
    /// strings, so a stray number in a hand-edited file would silently select a member by ordinal
    /// instead of falling back. Same trap already documented on ResolveDjPersonality.
    /// </summary>
    [Theory]
    [InlineData("2")]
    [InlineData("Huge")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnrecognisedValueFallsBackInsteadOfPickingOne(string? stored)
    {
        Assert.Equal(DjRemarkSize.Medium, new AppSettings { DjRemarkSize = stored! }.ResolveDjRemarkSize());
    }

    // --- the numbers -------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the setting: every dimension grows together. Type alone would re-wrap the
    /// same sentence into more lines in the same box — taller, not more readable from a distance.
    /// </summary>
    [Fact]
    public void EveryDimensionGrowsWithTheSize()
    {
        for (var i = 1; i < Ascending.Length; i++)
        {
            var (smaller, bigger) = (Ascending[i - 1], Ascending[i]);
            Assert.True(DjRemarkMetrics.FontSize(bigger) > DjRemarkMetrics.FontSize(smaller));
            Assert.True(DjRemarkMetrics.LineHeight(bigger) > DjRemarkMetrics.LineHeight(smaller));
            Assert.True(DjRemarkMetrics.CardMaxWidth(bigger) > DjRemarkMetrics.CardMaxWidth(smaller));
        }
    }

    /// <summary>Leading has to keep up with the type or the lines close up as it grows.</summary>
    [Theory]
    [InlineData(DjRemarkSize.Small)]
    [InlineData(DjRemarkSize.Medium)]
    [InlineData(DjRemarkSize.Large)]
    public void LineHeightStaysClearOfTheFontSize(DjRemarkSize size)
    {
        Assert.True(DjRemarkMetrics.LineHeight(size) >= DjRemarkMetrics.FontSize(size) * 1.4);
    }

    /// <summary>
    /// The card lives inside the Now Playing block, so the block has to make room for it — a
    /// child's MaxWidth cannot take it past its parent. And it must never shrink below the 440 the
    /// rest of that block was laid out around.
    /// </summary>
    [Theory]
    [InlineData(DjRemarkSize.Small)]
    [InlineData(DjRemarkSize.Medium)]
    [InlineData(DjRemarkSize.Large)]
    public void TheContainerMakesRoomForTheCardAndNeverShrinks(DjRemarkSize size)
    {
        Assert.True(DjRemarkMetrics.ContainerMaxWidth(size) >= DjRemarkMetrics.CardMaxWidth(size));
        Assert.True(DjRemarkMetrics.ContainerMaxWidth(size) >= 440);
    }

    /// <summary>
    /// Nothing may exceed what the right-hand column actually offers at the default window size:
    /// 1000px window - 362px left column - 88px of margin. Wider would be clamped at runtime, which
    /// silently makes two sizes render identically.
    /// </summary>
    [Fact]
    public void TheLargestSizeStillFitsTheColumn()
    {
        Assert.True(DjRemarkMetrics.ContainerMaxWidth(DjRemarkSize.Large) <= 1000 - 362 - 88);
    }

    /// <summary>What shipped survives as Small — someone who liked it can still have it.</summary>
    [Fact]
    public void SmallIsExactlyWhatShipped()
    {
        Assert.Equal(15, DjRemarkMetrics.FontSize(DjRemarkSize.Small));
        Assert.Equal(22, DjRemarkMetrics.LineHeight(DjRemarkSize.Small));
        Assert.Equal(430, DjRemarkMetrics.CardMaxWidth(DjRemarkSize.Small));
    }

    // --- the names the view binds by string --------------------------------------------------

    /// <summary>
    /// XAML binding paths are strings and a broken one fails silently — the control just keeps its
    /// default. These four are what MainWindow.xaml asks for; renaming a property without updating
    /// the markup would otherwise leave the setting quietly doing nothing.
    /// </summary>
    [Theory]
    [InlineData("DjRemarkFontSize", typeof(double))]
    [InlineData("DjRemarkLineHeight", typeof(double))]
    [InlineData("DjRemarkMaxWidth", typeof(double))]
    [InlineData("NowPlayingMaxWidth", typeof(double))]
    [InlineData("DjRemarkSize", typeof(DjRemarkSize))]
    public void TheViewModelExposesWhatTheViewBinds(string name, Type type)
    {
        var property = typeof(MainViewModel).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.Equal(type, property!.PropertyType);
        Assert.True(property.CanRead);
    }
}
