using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The toast payload. Whether Windows actually DISPLAYS a toast can't be asserted from a test — it
/// depends on the process's AppUserModelID and a matching Start Menu shortcut, and the API reports
/// success either way (see <see cref="WindowsNotificationService"/>). What can be pinned is the
/// XML, and it matters more than it looks: the content is not ours. Titles come from ICY metadata a
/// station controls and the remark from a language model, so an unescaped ampersand loses the
/// notification to a parse error.
/// </summary>
public class ToastNotificationTests
{
    [Fact]
    public void CarriesTrackArtistAndRemarkAsThreeLines()
    {
        var xml = WindowsNotificationService.BuildToastXml(
            "Silent Lucidity", "Queensrÿche", "A hair-metal ballad that snuck onto every mixtape.");

        Assert.Contains("<text>Silent Lucidity</text>", xml);
        Assert.Contains("<text>Queensrÿche</text>", xml);
        Assert.Contains("<text>A hair-metal ballad that snuck onto every mixtape.</text>", xml);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OmitsTheArtistLineRatherThanShowingAnEmptyOne(string? artist)
    {
        var xml = WindowsNotificationService.BuildToastXml("Livestream", artist, "Here we go.");

        Assert.Equal(2, CountTextElements(xml));
    }

    /// <summary>The remark is the reason this feature exists, but it arrives a beat after the
    /// track — a toast fired before it lands still has to be worth showing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OmitsTheRemarkLineWhenTheDjHasNotSpokenYet(string? remark)
    {
        var xml = WindowsNotificationService.BuildToastXml("Wasted Years", "Iron Maiden", remark);

        Assert.Equal(2, CountTextElements(xml));
        Assert.Contains("Iron Maiden", xml);
    }

    // --- Escaping: the content is untrusted ----------------------------------------------------

    [Theory]
    [InlineData("Me & You", "&amp;")]
    [InlineData("Bad <Title>", "&lt;")]
    [InlineData("Quote\" mark", "&quot;")]
    public void EscapesMarkupInTheTitle(string title, string expected)
    {
        var xml = WindowsNotificationService.BuildToastXml(title, "Artist", "Remark");

        Assert.Contains(expected, xml);
        Assert.DoesNotContain("<Title>", xml);
    }

    /// <summary>Real, and the reason this isn't theoretical: plenty of stations send titles like
    /// "Simon &amp; Garfunkel", and the payload is XML.</summary>
    [Fact]
    public void AnAmpersandInAnArtistDoesNotProduceUnparseableXml()
    {
        var xml = WindowsNotificationService.BuildToastXml(
            "The Sound of Silence", "Simon & Garfunkel", "Two voices & one guitar.");

        var doc = System.Xml.Linq.XDocument.Parse(xml);   // would throw on a raw &
        Assert.Equal(3, doc.Descendants("text").Count());
    }

    [Fact]
    public void EveryComposedPayloadIsWellFormed()
    {
        foreach (var remark in new[] { "plain", "<b>bold</b>", "a & b", "it's \"quoted\"", null })
            System.Xml.Linq.XDocument.Parse(
                WindowsNotificationService.BuildToastXml("T & <T>", "A \"A\"", remark));
    }

    // --- The setting ---------------------------------------------------------------------------

    /// <summary>On by default: the alternative is shipping a feature the listener has to go and
    /// find. One click turns it off.</summary>
    [Fact]
    public void NotificationsAreOnByDefault()
    {
        Assert.True(new AppSettings().DjNotificationsEnabled);
    }

    /// <summary>Toggling it off must survive a load-modify-save, which is how the options dialog
    /// writes every other setting.</summary>
    [Fact]
    public void TheSettingRoundTrips()
    {
        var settings = new AppSettings { DjNotificationsEnabled = false };

        Assert.False(settings.DjNotificationsEnabled);
        Assert.True(SettingsStore.Migrate(settings) is { DjNotificationsEnabled: false });
    }

    private static int CountTextElements(string xml) =>
        System.Xml.Linq.XDocument.Parse(xml).Descendants("text").Count();
}
