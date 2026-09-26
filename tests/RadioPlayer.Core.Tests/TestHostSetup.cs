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
    /// <para>Also gives every test thread its own <see cref="MessageLoop"/> as
    /// DispatcherContext.Current: the engines capture it in their constructors, and
    /// <see cref="TestLoop.Pump"/> runs what they queued. Created, not installed — xUnit tracks
    /// async tests through its own SynchronizationContext, which must stay in place.</para>
    /// </summary>
    [ModuleInitializer]
    public static void Init()
    {
        AppLog.Suppress();
        DispatcherContext.Fallback = MessageLoop.CreateForCurrentThread;
    }
}

/// <summary>What WPF's <c>Dispatcher.PushFrame</c> at <c>ContextIdle</c> did for these tests before
/// the threading abstraction: run everything the code under test queued on this thread.</summary>
internal static class TestLoop
{
    public static void Pump() => ((MessageLoop)DispatcherContext.Current).RunPending();
}
