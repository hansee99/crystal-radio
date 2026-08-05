using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace RadioPlayer.Services;

/// <summary>
/// Windows toast notifications for an <b>unpackaged</b> WPF app.
///
/// <para><b>Identity is the whole problem.</b> A packaged app gets one from its manifest; this one
/// has to assert it. Two things are needed and they are easy to get wrong:</para>
/// <list type="number">
/// <item><see cref="ApplyAppIdentity"/> must run before any window exists, setting the process's
/// AppUserModelID.</item>
/// <item>A Start Menu shortcut must carry the <b>same</b> ID in its
/// <c>System.AppUserModel.ID</c> property. <c>build-release.ps1</c> writes it.</item>
/// </list>
///
/// <para>Both were established by experiment, because the API gives no useful signal: with the
/// AUMID alone <c>CreateToastNotifier</c> succeeds, <c>Setting</c> reports <c>Enabled</c>,
/// <c>Show</c> does not throw, and <c>History.GetHistory</c> even returns the toast — and nothing
/// appears on screen. History echoes what this app queued under its own ID whether Windows
/// displayed it or not, so it is not a delivery check. Adding an
/// <c>HKCU\Software\Classes\AppUserModelId</c> registration changed nothing either. Only the
/// shortcut made toasts appear; removing the registry key afterwards left them working. The icon
/// comes free from the shortcut's target executable.</para>
///
/// <para>So: if toasts silently stop appearing, suspect a missing or stale Start Menu shortcut long
/// before suspecting this class.</para>
/// </summary>
public sealed class WindowsNotificationService : INotificationService
{
    /// <summary>
    /// Must match the shortcut written by <c>build-release.ps1</c> exactly — the string IS the
    /// pairing between process and shortcut, and a mismatch fails silently in both directions.
    /// </summary>
    public const string AppUserModelId = "HansSeebacher.CrystalRadio";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>
    /// Claims the app's identity for this process. Call once at startup, before any window is
    /// created — the shell reads it when windows appear, so setting it later is too late.
    /// </summary>
    public static void ApplyAppIdentity()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch (Exception ex)
        {
            // Nothing downstream depends on this succeeding; toasts simply won't show.
            AppLog.Debug($"[Toast] couldn't set AppUserModelID: {ex.Message}");
        }
    }

    private readonly Lazy<ToastNotifier?> _notifier = new(() =>
    {
        try
        {
            return ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Toast] notifier unavailable: {ex.Message}");
            return null;
        }
    });

    public bool IsAvailable
    {
        get
        {
            try
            {
                // Enabled is necessary but NOT sufficient — see the class remarks. It does at least
                // catch the case worth catching: the user switching notifications off in Windows.
                return _notifier.Value?.Setting == NotificationSetting.Enabled;
            }
            catch (Exception ex)
            {
                AppLog.Debug($"[Toast] availability check failed: {ex.Message}");
                return false;
            }
        }
    }

    public void ShowDjTrack(string title, string? artist, string? remark)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        var notifier = _notifier.Value;
        if (notifier is null) return;

        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(BuildToastXml(title, artist, remark));

            notifier.Show(new ToastNotification(xml)
            {
                // A track change is transient. Leaving these in Action Center would turn a
                // listening session into a wall of notifications to dismiss.
                ExpiresOnReboot = false,
                ExpirationTime = DateTimeOffset.Now.AddMinutes(5),
            });
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[Toast] show failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Three lines: what's playing, who by, and what the DJ said. The remark is the reason this
    /// feature exists, so it gets its own line rather than being appended to the artist.
    /// </summary>
    internal static string BuildToastXml(string title, string? artist, string? remark)
    {
        var lines = new List<string> { Escape(title) };
        if (!string.IsNullOrWhiteSpace(artist)) lines.Add(Escape(artist));
        if (!string.IsNullOrWhiteSpace(remark)) lines.Add(Escape(remark));

        var text = string.Concat(lines.Select(l => $"<text>{l}</text>"));
        return $"<toast><visual><binding template=\"ToastGeneric\">{text}</binding></visual></toast>";
    }

    /// <summary>
    /// The toast payload is XML and the content is not ours: titles come from ICY metadata a
    /// station controls, and the remark from a language model. Either can contain &amp; or &lt;,
    /// which would make LoadXml throw and lose the notification.
    /// </summary>
    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;
}
