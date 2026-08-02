using System.Reflection;
using System.Windows;
using RadioPlayer.Services;

namespace RadioPlayer;

/// <summary>
/// Application entry point. Enforces a single running instance: a named mutex detects an
/// already-running copy, and a named event lets the second launch ask the first to surface
/// its window instead of opening another one.
/// </summary>
public partial class App : Application
{
    // The fixed GUID keeps these names from colliding with any other app. "Local\" scopes
    // them to the current login session (one instance per user), which is what we want.
    private const string InstanceMutexName = @"Local\RadioPlayer.SingleInstance.9F2B7C14-8E3A-4D56-B1C0-2A7E5F94D3B8";
    private const string ActivateEventName = @"Local\RadioPlayer.Activate.9F2B7C14-8E3A-4D56-B1C0-2A7E5F94D3B8";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private bool _isPrimary;

    protected override void OnStartup(StartupEventArgs e)
    {
        // createdNew == false means another instance already holds the named mutex.
        _instanceMutex = new Mutex(initiallyOwned: false, InstanceMutexName, out _isPrimary);

        if (!_isPrimary)
        {
            // Ask the running instance to come to the foreground, then exit quietly.
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
            catch { /* the primary may be shutting down; nothing more we can do */ }
            Shutdown();
            return;
        }

        // We're the primary instance: listen for future launch attempts, then show the window.
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivationListener(_activateEvent);

        HookCrashLogging();
        AppLog.BeginSession(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?");

        base.OnStartup(e);
        new MainWindow().Show();
    }

    /// <summary>
    /// Records anything that escapes to the top, on all three routes an exception can take here:
    /// the UI thread, a background thread, and a fire-and-forget Task (of which this codebase has
    /// plenty — enrichment, embedding, QC). Without this, a failure in a normally-launched copy
    /// leaves nothing behind to look at.
    ///
    /// Deliberately does NOT mark the dispatcher exception handled: swallowing it would leave the
    /// app running in an unknown state. The point here is a record, not a behaviour change.
    /// </summary>
    private void HookCrashLogging()
    {
        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("[Crash] unhandled exception on the UI thread", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error($"[Crash] unhandled exception (terminating: {args.IsTerminating})",
                args.ExceptionObject as Exception);

        // Fire-and-forget Tasks don't crash the process, so these would otherwise vanish
        // completely — exactly the "it failed and said nothing" case.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("[Crash] unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Background thread that surfaces our window whenever a second launch signals us.</summary>
    private void StartActivationListener(EventWaitHandle handle)
    {
        var thread = new Thread(() =>
        {
            while (handle.WaitOne())
                Dispatcher.BeginInvoke(ActivateMainWindow);
        })
        {
            IsBackground = true,
            Name = "SingleInstanceActivation"
        };
        thread.Start();
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null) return;
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        // Bounce Topmost to force the window to the foreground (SetForegroundWindow is restricted).
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // We never acquired ownership (initiallyOwned: false), so just dispose — no ReleaseMutex.
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
