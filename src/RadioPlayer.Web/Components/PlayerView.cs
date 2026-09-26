using Microsoft.AspNetCore.Components;
using RadioPlayer.Services;
using RadioPlayer.Web.Hosting;

namespace RadioPlayer.Web.Components;

/// <summary>
/// Base for every component that shows player state. It reads a plain snapshot on the player
/// thread (<see cref="Read"/>), re-reads it whenever the player announces a change, and re-renders.
/// Subclasses never read the view model from render code — only from <see cref="Read"/> — and
/// act on it only through <see cref="Do"/>, which also runs on the player thread.
/// </summary>
public abstract class PlayerView<TSnapshot> : ComponentBase, IDisposable where TSnapshot : class
{
    [Inject] protected PlayerHost Player { get; set; } = default!;

    /// <summary>Null only until the first read completes.</summary>
    protected TSnapshot? Snap { get; private set; }

    private int _refreshQueued;

    /// <summary>Builds the snapshot. Runs on the player thread: copy values out, never return a
    /// live view-model object or collection.</summary>
    protected abstract TSnapshot Read(AppServices services);

    protected override async Task OnInitializedAsync()
    {
        Player.Changed += OnPlayerChanged;
        await RefreshAsync();
    }

    /// <summary>Runs <paramref name="act"/> on the player thread. The resulting change arrives
    /// through the normal change notification; no manual refresh needed.</summary>
    protected Task Do(Action<AppServices> act) => Player.DoAsync(act);

    private void OnPlayerChanged(object? sender, EventArgs e)
    {
        // At most one refresh queued per component: a slow circuit must not build a backlog.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = InvokeAsync(async () =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            await RefreshAsync();
            StateHasChanged();
        });
    }

    private async Task RefreshAsync() => Snap = await Player.ReadAsync(Read);

    public virtual void Dispose() => Player.Changed -= OnPlayerChanged;
}
