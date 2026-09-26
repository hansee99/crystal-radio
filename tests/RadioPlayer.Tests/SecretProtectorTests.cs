using System.Security.Cryptography;
using System.Text;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The API key's protection moved out of SettingsStore into ISecretProtector. What must not change
/// is the stored format: an existing settings.json holds a key written by the old code, and a
/// protector that can't read it back would silently log the user out of every AI feature.
/// </summary>
public class SecretProtectorTests
{
    private const string Key = "sk-ant-test-0123456789-ÄÖÜ-é"; // non-ASCII to catch an encoding slip

    [Fact]
    public void Dpapi_RoundTrips()
    {
        var p = new DpapiSecretProtector();
        Assert.Equal(Key, p.Unprotect(p.Protect(Key)));
    }

    [Fact]
    public void Dpapi_ReadsAKeyStoredByTheOldSettingsStore()
    {
        // Byte-for-byte what SettingsStore.Protect did before the extraction.
        var legacy = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(Key), null, DataProtectionScope.CurrentUser));

        Assert.Equal(Key, new DpapiSecretProtector().Unprotect(legacy));
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")] // valid base64, not a DPAPI blob
    public void Dpapi_UnreadableValue_IsNoKey(string stored)
        => Assert.Null(new DpapiSecretProtector().Unprotect(stored));

    [Fact]
    public void Plain_RoundTrips()
    {
        var p = new PlainSecretProtector();
        Assert.Equal(Key, p.Unprotect(p.Protect(Key)));
    }

    [Fact]
    public void Plain_UnreadableValue_IsNoKey()
        => Assert.Null(new PlainSecretProtector().Unprotect("not base64 at all!"));
}
