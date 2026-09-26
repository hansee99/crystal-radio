using RadioPlayer.Services;
using RadioPlayer.ViewModels;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The two first-run defaults (#46, #50), and the shape of a confirmation request.
///
/// <para>Only <see cref="AppSettings"/> is exercised, never <see cref="SettingsStore"/> — its file
/// path is a static pointing at the real user profile, so a round-trip test here would rewrite the
/// settings of whoever ran it. Making that injectable is #12's job.</para>
///
/// <para>The prompt itself is a dialog and the decision lives in
/// <c>MainViewModel.ConfirmLeavingMode</c>, which needs a whole player to construct.</para>
/// </summary>
public class ModeSwitchConfirmTests
{
    /// <summary>The guard protects someone who has not yet learned that the pills are a power
    /// switch, so it has to be on before anybody has chosen anything.</summary>
    [Fact]
    public void ConfirmationIsOnByDefault()
    {
        Assert.True(new AppSettings().ConfirmModeSwitch);
    }

    /// <summary>An introduction is for someone who has never seen the app, so it defaults to
    /// unseen — and the flag is a bool, not a version, so an upgrade doesn't re-introduce the app
    /// to someone who already knows it.</summary>
    [Fact]
    public void TheWelcomeHasNotBeenSeenOnAFreshInstall()
    {
        Assert.False(new AppSettings().HasSeenWelcome);
    }

    /// <summary>
    /// A declined confirmation carries no suppression. Acting on one would let a listener disable
    /// the guard by refusing it — the opposite of what backing out means.
    /// </summary>
    [Fact]
    public void ADeclinedConfirmationCarriesNoSuppression()
    {
        var result = new ConfirmResult(Confirmed: false);

        Assert.False(result.Confirmed);
        Assert.False(result.Suppress);
    }

    /// <summary>The request carries its own wording, and the button says the action rather than
    /// "OK": someone reading only the buttons should still know which one costs them the session.</summary>
    [Fact]
    public void AConfirmRequestCarriesItsOwnWording()
    {
        var request = new ConfirmRequest("End the DJ session?", "The mix stops.",
            "End session", "Keep listening", OfferToSuppress: true);

        Assert.Equal("End session", request.ConfirmLabel);
        Assert.Equal("Keep listening", request.CancelLabel);
        Assert.True(request.OfferToSuppress);
    }

    /// <summary>Suppression is opt-in per prompt: a one-off destructive confirmation should not
    /// offer to silence itself.</summary>
    [Fact]
    public void SuppressionIsNotOfferedUnlessAskedFor()
    {
        var request = new ConfirmRequest("Delete?", "This cannot be undone.", "Delete", "Cancel");

        Assert.False(request.OfferToSuppress);
    }
}
