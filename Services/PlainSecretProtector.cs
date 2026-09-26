using System.Text;

namespace RadioPlayer.Services;

/// <summary>
/// Base64 only — NOT encryption. For platforms with no per-user secret store: there the settings
/// file itself is owner-only (mode 0600, see <see cref="SettingsStore.Save"/>), which is how most
/// command-line tools keep a token on Linux.
/// </summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));

    public string? Unprotect(string protectedValue)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue)); }
        catch (FormatException) { return null; }
    }
}
