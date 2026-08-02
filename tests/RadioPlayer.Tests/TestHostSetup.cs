using System.Runtime.CompilerServices;
using RadioPlayer.Services;

namespace RadioPlayer.Tests;

internal static class TestHostSetup
{
    /// <summary>
    /// Keeps the test run out of the app's shared troubleshooting log. Services log freely, so a
    /// run appends hundreds of lines to the same %LocalAppData%\RadioPlayer\logs file the real app
    /// writes to — noticed while reading a live DJ session's diagnostics and finding a test run
    /// interleaved through them.
    /// </summary>
    [ModuleInitializer]
    public static void Init() => AppLog.Suppress();
}
