using System.Runtime.CompilerServices;
using RadioPlayer.Services;
using RadioPlayer.Threading;

namespace RadioPlayer.Tests;

internal static class TestHostSetup
{
    /// <summary>
    /// Keeps the test run out of the app's shared troubleshooting log. Services log freely, so a
    /// run appends hundreds of lines to the same %LocalAppData%\RadioPlayer\logs file the real app
    /// writes to — noticed while reading a live DJ session's diagnostics and finding a test run
    /// interleaved through them.
    ///
    /// <para>Also gives every test thread its own WPF Dispatcher as DispatcherContext.Current: the
    /// engines capture it in their constructors, and the PumpDispatcher helpers push frames on
    /// exactly that dispatcher — what Dispatcher.CurrentDispatcher gave them before the threading
    /// abstraction existed.</para>
    /// </summary>
    [ModuleInitializer]
    public static void Init()
    {
        AppLog.Suppress();
        DispatcherContext.Fallback = () => WpfDispatcher.ForCurrentThread();
    }
}
