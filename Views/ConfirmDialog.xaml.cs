using System.Windows;
using RadioPlayer.Controls;
using RadioPlayer.ViewModels;

namespace RadioPlayer;

/// <summary>
/// The app's own confirmation prompt, so a destructive action doesn't hand the listener a system
/// message box in the middle of a dark UI (the same reason <see cref="AppDialog"/> exists at all).
/// </summary>
public partial class ConfirmDialog : AppDialog
{
    public ConfirmDialog(ConfirmRequest request)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(request);

        Title = request.Title;
        MessageText.Text = request.Message;
        ConfirmButton.Content = request.ConfirmLabel;
        CancelButton.Content = request.CancelLabel;
        SuppressCheck.Visibility = request.OfferToSuppress ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Only meaningful after a confirm — see <see cref="ConfirmResult.Suppress"/>.</summary>
    public bool Suppress => SuppressCheck.IsChecked == true;

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>View-layer implementation of <see cref="IConfirmDialog"/>.</summary>
public sealed class ConfirmDialogService : IConfirmDialog
{
    private readonly Window _owner;

    public ConfirmDialogService(Window owner) => _owner = owner;

    public ConfirmResult Ask(ConfirmRequest request)
    {
        var dialog = new ConfirmDialog(request) { Owner = _owner };
        // Cancel on anything unexpected: a prompt that fails open would go ahead and stop the
        // session, which is precisely the outcome it exists to prevent.
        return dialog.ShowDialog() == true
            ? new ConfirmResult(true, dialog.Suppress)
            : new ConfirmResult(false);
    }
}
