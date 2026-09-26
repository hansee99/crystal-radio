using RadioPlayer.Models;
using RadioPlayer.Services;
using RadioPlayer.ViewModels;

namespace RadioPlayer.Web.Hosting;

/// <summary>No toasts on a headless box. The page itself shows what the DJ is playing.</summary>
public sealed class NoNotificationService : INotificationService
{
    public bool IsAvailable => false;
    public void ShowDjTrack(string title, string? artist, string? remark) { }
}

/// <summary>
/// The view model's add/edit commands ask <see cref="IStationDialog"/> for a station and then do
/// the list bookkeeping themselves. A browser can't answer a synchronous call on the player thread,
/// so the web form answers it in advance: <see cref="Supply"/> sets the station, runs the command,
/// and the command's own <c>Show()</c> call receives it. Both happen inside one
/// <see cref="PlayerHost.DoAsync"/> action, so nothing else can interleave.
/// </summary>
public sealed class WebStationDialog : IStationDialog
{
    public static WebStationDialog Instance { get; } = new();

    private Station? _next;

    private WebStationDialog() { }

    /// <summary>Makes the next <see cref="Show"/> return <paramref name="station"/> while
    /// <paramref name="run"/> executes. Call only on the player thread.</summary>
    public void Supply(Station station, Action run)
    {
        _next = station;
        try { run(); }
        finally { _next = null; }
    }

    /// <summary>Returns the supplied station once, or null (= cancelled) when nothing was supplied.</summary>
    public Station? Show(Station? existing)
    {
        var next = _next;
        _next = null;
        return next;
    }
}

/// <summary>
/// The view model asks before a mode switch throws something away (#46). The web UI asks the
/// person first, in the browser, and only then invokes the command — so by the time the view
/// model asks, the answer is yes. Blocking the player thread on a browser round-trip is not an
/// option. (doc/PI-PORT-PLAN.md, Step 7, makes IConfirmDialog async and removes this.)
/// </summary>
public sealed class AlwaysConfirmDialog : IConfirmDialog
{
    public ConfirmResult Ask(ConfirmRequest request) => new(Confirmed: true);
}
