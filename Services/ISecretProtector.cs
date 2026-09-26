namespace RadioPlayer.Services;

/// <summary>Protects the API key at rest. What "protected" means is the head's choice: DPAPI on
/// Windows; on a headless Linux box, file permissions (see <see cref="PlainSecretProtector"/>).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the value is unreadable (corrupt, or protected by someone else).</summary>
    string? Unprotect(string protectedValue);
}
