using System.ComponentModel;
using RadioPlayer.Services;
using RadioPlayer.Threading;

namespace RadioPlayer.Web.Hosting;

/// <summary>
/// Owns the player thread. In the WPF head the UI thread owns the view model; here a dedicated
/// thread running a <see cref="MessageLoop"/> does, for the life of the process, and every browser
/// tab is an observer. The whole object graph is built on this thread (the engines capture
/// DispatcherContext.Current in their constructors) and torn down on it.
///
/// <para>Components never touch the view model directly: they go through <see cref="ReadAsync{T}"/>
/// and <see cref="DoAsync"/>, which run on the player thread, and take a plain snapshot back.</para>
/// </summary>
public sealed class PlayerHost : IHostedService
{
    private readonly ILogger<PlayerHost> _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private MessageLoop? _loop;
    private AppServices? _services;
    private bool _changePending;

    public PlayerHost(ILogger<PlayerHost> log) => _log = log;

    /// <summary>
    /// Something observable changed. Raised on the player thread, and coalesced: a burst of
    /// property changes inside one piece of player-thread work (a station switch touches a dozen)
    /// produces one notification once that work is done, not a dozen re-renders per open tab.
    /// </summary>
    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(ThreadMain) { Name = "Player", IsBackground = true };
        _thread.Start();
        return _ready.Task;
    }

    private void ThreadMain()
    {
        try
        {
            _loop = MessageLoop.InstallOnCurrentThread();
            var services = AppServices.Build(
                new AppHooks(new PlainSecretProtector(), new NoNotificationService(),
                    WebStationDialog.Instance, new AlwaysConfirmDialog()),
                stage => _log.LogInformation("Startup: {Stage}", stage));
            _services = services;
            Watch(services.ViewModel);
            _ready.SetResult();
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "The player failed to start");
            _ready.TrySetException(ex);
            return;
        }

        _loop.Run();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_loop is null) return Task.CompletedTask;
        _loop.Post(() => _services?.Shutdown());
        _loop.Shutdown();
        _thread?.Join(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="read"/> on the player thread and returns its result. Build
    /// plain values inside it — never hand a live view-model object or collection to a component.</summary>
    public Task<T> ReadAsync<T>(Func<AppServices, T> read) =>
        Loop.InvokeAsync(() => read(_services!));

    /// <summary>Runs <paramref name="act"/> on the player thread.</summary>
    public Task DoAsync(Action<AppServices> act) =>
        Loop.InvokeAsync(() => act(_services!));

    private MessageLoop Loop =>
        _loop ?? throw new InvalidOperationException("The player has not started.");

    // --- Change tracking -------------------------------------------------------------------------

    /// <summary>
    /// Everything the pages read. The view model's own properties, plus the collections whose
    /// changes it does not re-announce, plus the rows inside them that change in place (a search
    /// result's IsAdded, a progress stage's status). A page that starts reading another collection
    /// (History, DjMix, …) adds it here.
    /// </summary>
    private void Watch(ViewModels.MainViewModel vm)
    {
        vm.PropertyChanged += (_, _) => RaiseChanged();
        WatchCollection(vm.Stations);
        WatchCollection(vm.SearchResults);
        WatchCollection(vm.History);
        WatchCollection(vm.LibrarySongs);
        WatchCollection(vm.CuratedQueue);
        vm.CurateProgress.PropertyChanged += (_, _) => RaiseChanged();
        WatchCollection(vm.CurateProgress.Stages);
        vm.SearchProgress.PropertyChanged += (_, _) => RaiseChanged();
        WatchCollection(vm.SearchProgress.Stages);
    }

    /// <summary>The collection itself, and each row's own property changes — including rows added
    /// later. Removed rows keep their handler; they are garbage once the collection drops them.</summary>
    private void WatchCollection<T>(System.Collections.ObjectModel.ObservableCollection<T> collection)
    {
        foreach (var item in collection)
            WatchRow(item);
        collection.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (var item in e.NewItems)
                    WatchRow(item);
            RaiseChanged();
        };
    }

    private void WatchRow(object? item)
    {
        if (item is INotifyPropertyChanged row)
            row.PropertyChanged += (_, _) => RaiseChanged();
    }

    private void RaiseChanged()
    {
        // Always on the player thread (every change originates there), so no lock is needed.
        if (_changePending) return;
        _changePending = true;
        Loop.Post(() =>
        {
            _changePending = false;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
