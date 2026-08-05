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
    public void CarriesTrackArtistAndRemark()
    {
        var xml = WindowsNotificationService.BuildToastXml(
            "Silent Lucidity", "Queensrÿche", "A hair-metal ballad that snuck onto every mixtape.");

        Assert.Contains("<text>Silent Lucidity</text>", xml);
        Assert.Contains("<text>Queensrÿche</text>", xml);
        Assert.Contains("A hair-metal ballad that snuck onto every mixtape.", xml);
    }

    /// <summary>
    /// The remark has to sit inside a <c>subgroup</c>, not as a third plain line. Three plain texts
    /// run together with no separation, and <c>hint-style</c> on a top-level <c>text</c> is silently
    /// ignored — grouping is the only way to make the DJ's voice look different from the metadata
    /// above it. Established by firing the layouts side by side.
    /// </summary>
    [Fact]
    public void TheRemarkIsStyledInsideASubgroupNotAPlainThirdLine()
    {
        var xml = System.Xml.Linq.XDocument.Parse(WindowsNotificationService.BuildToastXml(
            "Spirale", "Magnetic Rust", "Easy and bright."));

        var styled = xml.Descendants("subgroup").Single().Elements("text").SingleOrDefault();
        Assert.NotNull(styled);
        Assert.Equal("Easy and bright.", styled!.Value);
        Assert.Equal("captionSubtle", styled.Attribute("hint-style")?.Value);
        Assert.Equal("true", styled.Attribute("hint-wrap")?.Value);
    }

    [Fact]
    public void AttributesTheRemarkToTheDj()
    {
        var xml = System.Xml.Linq.XDocument.Parse(WindowsNotificationService.BuildToastXml(
            "Spirale", "Magnetic Rust", "Easy and bright."));

        Assert.Equal("Your DJ", xml.Descendants("text")
            .Single(t => t.Attribute("placement")?.Value == "attribution").Value);
    }

    /// <summary>A chime every few minutes through a listening session is intrusive; the toast is
    /// glanceable information, not a demand for attention.</summary>
    [Fact]
    public void IsSilent()
    {
        var xml = System.Xml.Linq.XDocument.Parse(
            WindowsNotificationService.BuildToastXml("T", "A", "R"));

        Assert.Equal("true", xml.Descendants("audio").Single().Attribute("silent")?.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OmitsTheArtistLineRatherThanShowingAnEmptyOne(string? artist)
    {
        var xml = System.Xml.Linq.XDocument.Parse(
            WindowsNotificationService.BuildToastXml("Livestream", artist, "Here we go."));

        // Title only, at the top level — the remark lives in the subgroup below it.
        Assert.Equal(["Livestream"],
            xml.Descendants("binding").Single().Elements("text")
               .Where(t => t.Attribute("placement") is null).Select(t => t.Value));
    }

    /// <summary>The remark is the reason this feature exists, but it arrives a beat after the
    /// track — a toast fired before it lands still has to be worth showing, and without a remark
    /// there is nothing to attribute either.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OmitsTheRemarkBlockEntirelyWhenTheDjHasNotSpokenYet(string? remark)
    {
        var xml = System.Xml.Linq.XDocument.Parse(
            WindowsNotificationService.BuildToastXml("Wasted Years", "Iron Maiden", remark));

        Assert.DoesNotContain(xml.Descendants(), e => e.Name == "subgroup");
        Assert.DoesNotContain(xml.Descendants("text"), t => t.Attribute("placement") is not null);
        Assert.Contains("Iron Maiden", xml.ToString());
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

        // Round-tripped, not just parseable: an over-eager escape would leave "Simon &amp;amp;".
        Assert.Contains(doc.Descendants("text"), t => t.Value == "Simon & Garfunkel");
        Assert.Contains(doc.Descendants("text"), t => t.Value == "Two voices & one guitar.");
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
