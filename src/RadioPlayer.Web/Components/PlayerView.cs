using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using RadioPlayer.Services;
using RadioPlayer.ViewModels;
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

    [Inject] protected IJSRuntime JS { get; set; } = default!;

    /// <summary>
    /// Switches the player to <paramref name="target"/> if it isn't there already, asking in the
    /// browser first when the switch would interrupt something — the same question, from the same
    /// place (<see cref="ViewModels.MainViewModel.ModeSwitchConfirmation"/>), the desktop asks in a
    /// dialog. False when the person said no. The view model's own confirmation is pre-answered
    /// yes on this head (AlwaysConfirmDialog), so it doesn't ask twice.
    /// </summary>
    protected async Task<bool> EnsureModeAsync(PlayerMode target)
    {
        var request = await Player.ReadAsync(a =>
            a.ViewModel.Mode == target ? null : a.ViewModel.ModeSwitchConfirmation(target));
        if (request is not null
            && !await JS.InvokeAsync<bool>("confirm", $"{request.Title}\n\n{request.Message}"))
            return false;

        await Do(a =>
        {
            var vm = a.ViewModel;
            if (vm.Mode == target) return;
            var command = target switch
            {
                PlayerMode.Radio => vm.SwitchToRadioCommand,
                PlayerMode.Library => vm.SwitchToLibraryCommand,
                _ => vm.SwitchToDjCommand,
            };
            command.Execute(null);
        });
        return true;
    }

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
