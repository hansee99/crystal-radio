using RadioPlayer.Services;

namespace RadioPlayer.ViewModels;

/// <summary>
/// The type and card dimensions for each <see cref="DjRemarkSize"/> (#61).
///
/// <para>One place for the numbers so the three that have to move together cannot drift apart:
/// growing the font without the card re-wraps the same sentence into more lines in the same width,
/// which makes the block taller without making it readable from further away — the opposite of
/// what the setting is for.</para>
///
/// <para><b>Why a container width appears here at all.</b> The remark card sits inside the Now
/// Playing block, which caps itself at 440px, so the card's own MaxWidth cannot take it past that
/// on its own. <see cref="ContainerMaxWidth"/> raises the cap just enough for the chosen size and
/// never lowers it below the 440 the rest of that block was laid out around.</para>
///
/// <para>Widths stay inside what the right-hand column actually offers: at the 1000px default
/// window that is 638px of column minus 88px of margin, so 550. They are caps rather than demands,
/// so a narrower window clamps them instead of overflowing.</para>
/// </summary>
internal static class DjRemarkMetrics
{
    /// <summary>The width the Now Playing block was designed at; no size may shrink it.</summary>
    private const double BaseContainerWidth = 440;

    /// <summary>15px was what shipped, and #61 is the report that it was too small to read from a
    /// side monitor — so it survives as Small rather than as the default.</summary>
    public static double FontSize(DjRemarkSize size) => size switch
    {
        DjRemarkSize.Small => 15,
        DjRemarkSize.Large => 19.5,
        _ => 17,
    };

    /// <summary>Leading grows a little faster than the type: at a distance the gaps between lines
    /// are what keeps a wrapped sentence from reading as a block.</summary>
    public static double LineHeight(DjRemarkSize size) => size switch
    {
        DjRemarkSize.Small => 22,
        DjRemarkSize.Large => 28,
        _ => 25,
    };

    public static double CardMaxWidth(DjRemarkSize size) => size switch
    {
        DjRemarkSize.Small => 430,
        DjRemarkSize.Large => 530,
        _ => 480,
    };

    public static double ContainerMaxWidth(DjRemarkSize size) =>
        Math.Max(BaseContainerWidth, CardMaxWidth(size));
}
