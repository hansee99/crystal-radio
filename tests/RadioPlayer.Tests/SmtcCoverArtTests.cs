using Windows.Graphics.Imaging;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The static cover art the OS media controls show (internet radio sends none of its own, so
/// without this the Win11 flyout draws a grey music-note placeholder).
///
/// <para>Worth a test because every way this breaks is silent. The asset is referenced by a pack
/// URI — a string — so renaming or dropping <c>Assets\app-256.png</c> compiles fine and simply
/// stops producing art; and the loader itself swallows failures by design, because cover art must
/// never be the reason the media controls don't come up.</para>
/// </summary>
public class SmtcCoverArtTests
{
    static SmtcCoverArtTests()
    {
        // Same bootstrap as the other render-adjacent tests: outside a running WPF Application
        // nothing has registered the "pack" URI scheme or its WebRequest factory, so the loader's
        // own catch would turn that into a plain null and the test would blame the asset.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = System.Windows.Application.Current;
    }

    private static Windows.Storage.Streams.RandomAccessStreamReference Loaded()
    {
        var art = SmtcController.LoadThumbnail();
        Assert.NotNull(art);   // null here means the pack URI no longer resolves
        return art!;
    }

    /// <summary>
    /// Decoded rather than merely non-null: the loader copies bytes into a WinRT stream by hand,
    /// and a truncated or mis-seeked copy is still a perfectly good stream reference that the OS
    /// then refuses to draw.
    /// </summary>
    [Fact]
    public void TheArtDecodesAsASquareImageBigEnoughForTheFlyout()
    {
        using var stream = Loaded().OpenReadAsync().AsTask().GetAwaiter().GetResult();
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();

        Assert.Equal(decoder.PixelWidth, decoder.PixelHeight);
        // 256 is what the asset is; the floor is what the flyout wants (~300 nominal, and it
        // scales up cleanly from 256 — but not from a favicon).
        Assert.True(decoder.PixelWidth >= 256, $"cover art is only {decoder.PixelWidth}px");
    }

    /// <summary>
    /// The reference has to be openable more than once: the OS re-opens it every time it draws,
    /// long after the constructor returned. A stream handed over without cloning reads empty the
    /// second time — the kind of bug that shows art once and never again.
    /// </summary>
    [Fact]
    public void TheSameReferenceCanBeOpenedAgain()
    {
        var art = Loaded();

        ulong First()
        {
            using var stream = art.OpenReadAsync().AsTask().GetAwaiter().GetResult();
            return stream.Size;
        }

        var once = First();
        var twice = First();

        Assert.True(once > 0);
        Assert.Equal(once, twice);
    }
}
