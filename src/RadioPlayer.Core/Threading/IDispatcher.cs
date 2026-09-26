namespace RadioPlayer.Threading;

/// <summary>
/// The single-threaded affinity the engines and view model marshal onto. On the WPF head it
/// wraps the window's Dispatcher; elsewhere it is a <see cref="MessageLoop"/> on a dedicated
/// player thread. Members mirror System.Windows.Threading.Dispatcher so the mapping is
/// mechanical: Post == BeginInvoke, Send == Invoke, Shutdown == InvokeShutdown.
/// </summary>
public interface IDispatcher
{
    bool CheckAccess();
    bool HasShutdownStarted { get; }

    /// <summary>Queue and return immediately (BeginInvoke).</summary>
    void Post(Action action);

    /// <summary>Run inline if already on the thread, else block until it has run (Invoke).</summary>
    void Send(Action action);

    Task InvokeAsync(Action action);
    Task<T> InvokeAsync<T>(Func<T> func);

    /// <summary>Begin shutting the loop down; queued work still runs.</summary>
    void Shutdown();

    /// <summary>A timer whose Tick fires on this dispatcher's thread. Created stopped.</summary>
    IDispatcherTimer CreateTimer();
}

/// <summary>Mirror of DispatcherTimer's surface.</summary>
public interface IDispatcherTimer
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    event EventHandler? Tick;

    /// <summary>Starts the timer, or restarts the countdown if it is already running.</summary>
    void Start();
    void Stop();
}
