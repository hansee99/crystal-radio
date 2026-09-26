using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The API key's protection off Windows (the Pi's): base64, with the settings file itself kept
/// owner-only by SettingsStore. The DPAPI protector's tests stay with the WPF head in
/// tests/RadioPlayer.Tests, since DPAPI exists only on Windows.
/// </summary>
public class PlainSecretProtectorTests
{
    private const string Key = "sk-ant-test-0123456789-ÄÖÜ-é"; // non-ASCII to catch an encoding slip

    [Fact]
    public void RoundTrips()
    {
        var p = new PlainSecretProtector();
        Assert.Equal(Key, p.Unprotect(p.Protect(Key)));
    }

    [Fact]
    public void UnreadableValue_IsNoKey()
        => Assert.Null(new PlainSecretProtector().Unprotect("not base64 at all!"));
}
