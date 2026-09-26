using System.Windows.Threading;

namespace RadioPlayer.Threading;

/// <summary>IDispatcher over the real WPF Dispatcher. The UI thread's dispatcher on the WPF head.</summary>
public sealed class WpfDispatcher : IDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public static WpfDispatcher ForCurrentThread() => new(Dispatcher.CurrentDispatcher);

    public bool CheckAccess() => _dispatcher.CheckAccess();
    public bool HasShutdownStarted => _dispatcher.HasShutdownStarted;
    public void Post(Action action) => _dispatcher.BeginInvoke(action);
    public void Send(Action action) => _dispatcher.Invoke(action);
    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
    public Task<T> InvokeAsync<T>(Func<T> func) => _dispatcher.InvokeAsync(func).Task;
    public void Shutdown() => _dispatcher.InvokeShutdown();
    public IDispatcherTimer CreateTimer() => new WpfTimer(_dispatcher);

    private sealed class WpfTimer : IDispatcherTimer
    {
        private readonly DispatcherTimer _timer;

        public WpfTimer(Dispatcher dispatcher)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher);
            _timer.Tick += (_, e) => Tick?.Invoke(this, e);
        }

        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
        public bool IsEnabled => _timer.IsEnabled;
        public event EventHandler? Tick;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}
