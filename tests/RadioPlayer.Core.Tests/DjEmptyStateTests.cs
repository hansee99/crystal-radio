using RadioPlayer.Services;
using RadioPlayer.ViewModels;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// What the DJ panel says when it has nothing to show (#60).
///
/// <para>The bug worth pinning here is not a wrong sentence, it's a right sentence shown on the
/// wrong tab: the sourcing explanation went only to the Sources tab's empty state while the Mix
/// tab — the one selected by default — fell back to its opening invitation. A start that found no
/// stations was therefore pixel-identical to never having pressed Start.</para>
///
/// <para>Both helpers are pure statics for exactly this reason. <c>MainViewModel</c> needs a whole
/// player to construct, so a branch that only differs by which string it returns is otherwise
/// unreachable from a test.</para>
/// </summary>
public class DjEmptyStateTests
{
    private const string Invitation = "describe a vibe above";

    /// <summary>The regression itself: a failed start must not read as an idle panel.</summary>
    [Fact]
    public void AFailedStartExplainsItselfInsteadOfInvitingOne()
    {
        var failure = MainViewModel.DescribeEmptySourcing(DjSourcingOutcome.NothingRelevant);

        var message = MainViewModel.DescribeEmptyMix(
            isRunning: false, sourcingFailure: failure, warmingUp: false, bridgeStation: null);

        Assert.Equal(failure, message);
        Assert.DoesNotContain(Invitation, message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>…and with nothing to explain, the invitation is still the right thing to say.</summary>
    [Fact]
    public void AnIdlePanelStillInvitesASession()
    {
        var message = MainViewModel.DescribeEmptyMix(
            isRunning: false, sourcingFailure: null, warmingUp: false, bridgeStation: null);

        Assert.Contains(Invitation, message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A running session never shows the failure, whatever is left over in it — the sourcing that
    /// produced this session succeeded by definition, and "nothing matched that vibe" under a mix
    /// that is playing would be a lie.
    /// </summary>
    [Theory]
    [InlineData(true, null, "playing live radio")]
    [InlineData(true, "Deep Focus FM", "Deep Focus FM")]
    [InlineData(false, null, "Collecting songs")]
    public void ARunningSessionDescribesItsOwnPhase(bool warmingUp, string? station, string expected)
    {
        var stale = MainViewModel.DescribeEmptySourcing(DjSourcingOutcome.OfflineNoMatch);

        var message = MainViewModel.DescribeEmptyMix(
            isRunning: true, sourcingFailure: stale, warmingUp: warmingUp, bridgeStation: station);

        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.NotEqual(stale, message);
    }

    /// <summary>
    /// Each outcome has to earn its own wording — a shared sentence would make the enum pointless.
    /// </summary>
    [Fact]
    public void EveryOutcomeSaysSomethingDifferent()
    {
        var messages = Enum.GetValues<DjSourcingOutcome>()
            .Where(o => o != DjSourcingOutcome.Ok)   // Ok never reaches an empty panel
            .Select(MainViewModel.DescribeEmptySourcing)
            .ToList();

        Assert.Equal(messages.Count, messages.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The point of the outcome added for #60. Nothing was searched — there was no catalog to
    /// search — so any hint to reword the prompt sends the listener after a fix that cannot work.
    /// The sibling outcome, where the catalog WAS searched, is allowed to suggest it.
    /// </summary>
    [Fact]
    public void NoCatalogNeverBlamesThePrompt()
    {
        var message = MainViewModel.DescribeEmptySourcing(DjSourcingOutcome.OfflineNoCatalog);

        Assert.DoesNotContain("prompt", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vibe", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("directory", message, StringComparison.OrdinalIgnoreCase);
    }
}
