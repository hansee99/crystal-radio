using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The check that makes an outside suggestion safe to accept (#31). Everything else about the
/// repair is a network lookup; this is the part that decides whether its answer is used, so it has
/// to reject a plausible-looking wrong answer as firmly as it accepts the right one.
/// </summary>
public class MetadataRepairTests
{
    private const char R = MetadataRepair.Replacement;   // U+FFFD

    [Fact]
    public void DetectsDamage()
    {
        Assert.True(MetadataRepair.NeedsRepair($"Queensr{R}che"));
        Assert.False(MetadataRepair.NeedsRepair("Queensrÿche"));
        Assert.False(MetadataRepair.NeedsRepair(""));
        Assert.False(MetadataRepair.NeedsRepair(null));
    }

    /// <summary>The two names captured off 011.fm, with the spellings LRCLIB returns.</summary>
    [Theory]
    [InlineData("Queensr�che", "Queensrÿche")]
    [InlineData("M�tley Cr�e", "Mötley Crüe")]
    [InlineData("Bj�rk", "Björk")]
    public void AcceptsTheRealName(string broken, string real)
    {
        Assert.True(MetadataRepair.IsPlausibleRepair(broken, real));
    }

    /// <summary>
    /// The 20 rows LRCLIB returns for "Breaking the Silence" include Loreena McKennitt and GET IN
    /// THE RING. Accepting one of those would replace a slightly-wrong name with a totally wrong one.
    /// </summary>
    [Theory]
    [InlineData("Loreena McKennitt")]
    [InlineData("GET IN THE RING")]
    [InlineData("Queensryche extended")]   // right prefix, wrong length
    [InlineData("Queensrche")]             // the damage simply deleted
    [InlineData("Motley Crue")]            // a different artist entirely, from another row
    public void RejectsAnythingThatDoesNotFitTheSurvivingCharacters(string candidate)
    {
        Assert.False(MetadataRepair.IsPlausibleRepair($"Queensr{R}che", candidate));
    }

    [Fact]
    public void RejectsACandidateThatIsItselfDamaged()
    {
        Assert.False(MetadataRepair.IsPlausibleRepair($"Queensr{R}che", $"Queensr{R}che"));
    }

    /// <summary>Nothing was lost, so there is nothing to replace — and a "repair" here would be a
    /// rewrite of a perfectly good name.</summary>
    [Fact]
    public void RefusesToRepairAnUndamagedName()
    {
        Assert.False(MetadataRepair.IsPlausibleRepair("Motley Crue", "Mötley Crüe"));
    }

    /// <summary>
    /// An encoder that emits one replacement per lost <i>byte</i> turns a single two-byte character
    /// into two, so a run has to be allowed to stand for fewer characters than its length.
    /// </summary>
    [Fact]
    public void ARunMayStandForFewerCharactersThanItsLength()
    {
        Assert.True(MetadataRepair.IsPlausibleRepair($"Bj{R}{R}rk", "Björk"));   // 2 marks, 1 char
        Assert.True(MetadataRepair.IsPlausibleRepair($"Bj{R}{R}rk", "Bjoerk"));  // 2 marks, 2 chars
        Assert.False(MetadataRepair.IsPlausibleRepair($"Bj{R}rk", "Bjoerk"));    // 1 mark, 2 chars
    }

    [Fact]
    public void ComparesCaseInsensitively()
    {
        Assert.True(MetadataRepair.IsPlausibleRepair($"M{R}tley Cr{R}e", "MÖTLEY CRÜE"));
    }

    /// <summary>Regex metacharacters in a title must be matched literally, not interpreted.</summary>
    [Fact]
    public void TreatsSurvivingTextAsLiteral()
    {
        Assert.True(MetadataRepair.IsPlausibleRepair($"AC/DC (Live) [1+1] {R}", "AC/DC (Live) [1+1] ü"));
        Assert.False(MetadataRepair.IsPlausibleRepair($"AC/DC (Live) [1+1] {R}", "ACxDC (Live) [1+1] ü"));
    }

    [Fact]
    public void ChoosesTheFittingCandidateFromASearchResult()
    {
        // The real ordering LRCLIB returned for "Breaking the Silence".
        string[] rows = ["GET IN THE RING", "Loreena McKennitt", "Loreena McKennitt", "Queensrÿche"];

        Assert.Equal("Queensrÿche", MetadataRepair.ChooseRepair($"Queensr{R}che", rows));
    }

    [Fact]
    public void ChoosesNothingWhenNothingFits()
    {
        string[] rows = ["GET IN THE RING", "Loreena McKennitt"];

        Assert.Null(MetadataRepair.ChooseRepair($"Queensr{R}che", rows));
        Assert.Null(MetadataRepair.ChooseRepair("Queensrÿche", rows));   // undamaged
        Assert.Null(MetadataRepair.ChooseRepair($"Queensr{R}che", []));
    }
}
