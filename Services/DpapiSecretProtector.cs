using System.Security.Cryptography;
using System.Text;

namespace RadioPlayer.Services;

/// <summary>
/// DPAPI under the current Windows user: the encrypted blob is only readable by this user
/// on this machine, and is kept out of source control (it lives in %AppData%).
/// Windows-only; the WPF head's <see cref="ISecretProtector"/>.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string plaintext)
    {
        var blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    public string? Unprotect(string protectedValue)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Corrupt, or encrypted by a different user — treat as no key.
            return null;
        }
    }
}
