namespace RadioPlayer.Threading;

/// <summary>
/// The dispatcher for the current thread — the replacement for Dispatcher.CurrentDispatcher,
/// which every engine captures in its constructor. A thread gets one either explicitly
/// (<see cref="MessageLoop.InstallOnCurrentThread"/>) or lazily through <see cref="Fallback"/>,
/// which the WPF head sets at startup so the UI thread — and any test thread — behaves exactly
/// as before.
/// </summary>
public static class DispatcherContext
{
    [ThreadStatic] private static IDispatcher? _current;

    /// <summary>Process-wide factory used when a thread has no dispatcher installed. Set once,
    /// at startup, by the head; a head with a single player thread leaves it null so a
    /// construction on the wrong thread fails loudly instead of silently getting its own loop.</summary>
    public static Func<IDispatcher>? Fallback { get; set; }

    public static bool HasCurrent => _current is not null;

    public static IDispatcher Current =>
        _current ??= Fallback?.Invoke()
            ?? throw new InvalidOperationException(
                "No dispatcher on this thread. Construct on the player thread (MessageLoop.InstallOnCurrentThread) " +
                "or set DispatcherContext.Fallback at startup.");

    public static void Install(IDispatcher dispatcher) => _current = dispatcher;
}
