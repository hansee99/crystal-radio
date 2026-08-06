using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using RadioPlayer.Controls;

namespace RadioPlayer;

/// <summary>
/// Shown once, on the first run (#50). The app is about to be handed to people who have never seen
/// it, and two things are not discoverable from the UI: that the interesting half is AI-backed, and
/// that it needs a key of their own before any of it works.
///
/// <para>Deliberately honest about the split rather than "the app needs a key to function": radio,
/// the library and the station list all work without one, and telling a new user otherwise would
/// make them think the player is broken when it isn't.</para>
/// </summary>
public partial class WelcomeDialog : AppDialog
{
    public WelcomeDialog() => InitializeComponent();

    /// <summary>True when the user asked to go straight to Options and add their key.</summary>
    public bool OpenOptions { get; private set; }

    private void OnOpenOptions(object sender, RoutedEventArgs e)
    {
        OpenOptions = true;
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        // UseShellExecute so the URI goes to the default browser rather than being run as a process.
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // No browser, or a policy blocking it. The URL is on screen either way, so this is a
            // convenience failing — not a reason to interrupt someone's first thirty seconds.
            Services.AppLog.Debug($"[Welcome] couldn't open {e.Uri}: {ex.Message}");
        }
        e.Handled = true;
    }
}
