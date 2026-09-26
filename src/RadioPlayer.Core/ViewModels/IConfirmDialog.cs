namespace RadioPlayer.ViewModels;

/// <summary>What a confirmation asked, and what came back.</summary>
/// <param name="Title">Short question, in the title bar.</param>
/// <param name="Message">What will actually happen, in plain words. Name the cost — "this will
/// stop playback" tells someone what they are trading, where "are you sure?" does not.</param>
/// <param name="ConfirmLabel">The button that goes ahead. Says the action, never "OK": a listener
/// reading only the buttons should still know which one loses their session.</param>
/// <param name="OfferToSuppress">Show a "don't ask again" checkbox. Only for a prompt that will
/// recur often enough to become friction.</param>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string ConfirmLabel,
    string CancelLabel,
    bool OfferToSuppress = false);

/// <param name="Confirmed">True when the user chose to go ahead.</param>
/// <param name="Suppress">True when they ticked "don't ask again". Meaningless unless
/// <see cref="ConfirmRequest.OfferToSuppress"/> was set, and never acted on for a "no".</param>
public sealed record ConfirmResult(bool Confirmed, bool Suppress = false);

/// <summary>
/// Asks the user to confirm something destructive. Behind an interface so the view model can be
/// tested without a window, and so nothing in it has to know what a dialog is.
/// </summary>
public interface IConfirmDialog
{
    ConfirmResult Ask(ConfirmRequest request);
}
