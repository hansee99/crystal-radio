using System.Collections.Concurrent;
using RadioPlayer.Services;

namespace RadioPlayer.Threading;

/// <summary>
/// A single-threaded message pump with Dispatcher semantics and no WPF: the thread that calls
/// <see cref="Run"/> executes every posted action in order until <see cref="Shutdown"/>.
/// An action that throws is logged and the loop carries on — the player must never go silent
/// because one callback misbehaved (CLAUDE.md, "it's a player, not a stream ripper").
///
/// <para>Like a WPF Dispatcher, it installs a SynchronizationContext on its thread, so an
/// <c>await</c> without ConfigureAwait(false) resumes back on the loop. The view model relies on
/// that: its async commands touch observable state after every await.</para>
/// </summary>
public sealed class MessageLoop : IDispatcher
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly int _threadId;
    private volatile bool _shutdown;

    private MessageLoop(int threadId) => _threadId = threadId;

    /// <summary>Creates a loop bound to the calling thread and makes it that thread's
    /// <see cref="DispatcherContext.Current"/> and SynchronizationContext. Call this before
    /// constructing anything that captures the current dispatcher, then <see cref="Run"/> once
    /// setup is done.</summary>
    public static MessageLoop InstallOnCurrentThread()
    {
        var loop = CreateForCurrentThread();
        DispatcherContext.Install(loop);
        SynchronizationContext.SetSynchronizationContext(new LoopSynchronizationContext(loop));
        return loop;
    }

    /// <summary>A loop bound to the calling thread, installed nowhere: not as
    /// <see cref="DispatcherContext.Current"/>, and not as the SynchronizationContext. For a
    /// <see cref="DispatcherContext.Fallback"/> on threads that already have a context of their
    /// own to keep — a test framework's, for instance.</summary>
    public static MessageLoop CreateForCurrentThread() => new(Environment.CurrentManagedThreadId);

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;
    public bool HasShutdownStarted => _shutdown;

    public void Post(Action action) => TryPost(action);

    // False once the loop has stopped accepting work — a Dispatcher drops such calls too.
    private bool TryPost(Action action)
    {
        if (_queue.IsAddingCompleted) return false;
        try { _queue.Add(action); return true; }
        catch (InvalidOperationException) { return false; } // completed between check and add
    }

    public void Send(Action action)
    {
        if (CheckAccess()) { action(); return; }
        try { InvokeAsync(action).GetAwaiter().GetResult(); }
        catch (TaskCanceledException) when (_queue.IsAddingCompleted) { /* shut down: dropped, like Invoke */ }
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = TryPost(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!posted) tcs.TrySetCanceled();
        return tcs.Task;
    }

    public void Shutdown()
    {
        _shutdown = true;
        TryPost(() => _queue.CompleteAdding()); // queued work ahead of this still runs
    }

    /// <summary>Pumps until <see cref="Shutdown"/> has been processed. Must be called on the
    /// thread that installed the loop.</summary>
    public void Run()
    {
        if (!CheckAccess())
            throw new InvalidOperationException("MessageLoop.Run must be called on the thread it was installed on.");
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { AppLog.Error("[MessageLoop] unhandled exception in posted action", ex); }
        }
    }

    /// <summary>
    /// Runs everything queued so far — including work queued while it runs — then returns: what
    /// WPF's <c>Dispatcher.PushFrame</c> at <c>ContextIdle</c> did. For a thread that can't hand
    /// itself to <see cref="Run"/> because it has more to do in between — the tests, which step
    /// an engine, flush its callbacks, and assert. Returns how many actions ran.
    /// </summary>
    internal int RunPending()
    {
        if (!CheckAccess())
            throw new InvalidOperationException("MessageLoop.RunPending must be called on the loop's thread.");
        var ran = 0;
        while (_queue.TryTake(out var action))
        {
            ran++;
            try { action(); }
            catch (Exception ex) { AppLog.Error("[MessageLoop] unhandled exception in posted action", ex); }
        }
        return ran;
    }

    public IDispatcherTimer CreateTimer() => new LoopTimer(this);

    private sealed class LoopSynchronizationContext(MessageLoop loop) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => loop.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state) => loop.Send(() => d(state));
        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>System.Threading.Timer that delivers Tick through the loop. A tick still queued
    /// when the next one is due is not queued twice, and a tick queued before a Stop or restart
    /// is dropped — DispatcherTimer behaves the same way.</summary>
    private sealed class LoopTimer : IDispatcherTimer
    {
        private readonly MessageLoop _loop;
        private readonly object _gate = new();
        private Timer? _timer;
        private TimeSpan _interval = TimeSpan.Zero;
        private int _generation;
        private int _pending;

        public LoopTimer(MessageLoop loop) => _loop = loop;

        public TimeSpan Interval
        {
            get => _interval;
            set
            {
                _interval = value;
                if (IsEnabled) Start(); // DispatcherTimer restarts its countdown on a new interval
            }
        }

        public bool IsEnabled { get { lock (_gate) return _timer is not null; } }
        public event EventHandler? Tick;

        public void Start()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                var generation = ++_generation;
                Interlocked.Exchange(ref _pending, 0);
                var period = _interval > TimeSpan.Zero ? _interval : TimeSpan.FromMilliseconds(1);
                _timer = new Timer(_ => OnElapsed(generation), null, period, period);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _generation++;
            }
        }

        private void OnElapsed(int generation)
        {
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            _loop.Post(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                bool current;
                lock (_gate) current = _timer is not null && _generation == generation;
                if (current) Tick?.Invoke(this, EventArgs.Empty);
            });
        }
    }
}
