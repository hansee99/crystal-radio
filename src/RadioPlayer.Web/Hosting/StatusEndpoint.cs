using System.Reflection;
using RadioPlayer.ViewModels;

namespace RadioPlayer.Web.Hosting;

/// <summary>
/// <c>GET /api/status</c>: what the player is doing, as JSON. Read-only and, like the rest of the
/// web head, unauthenticated and meant for the LAN. Its first user is the Pi's nightly updater
/// (deploy/pi), which must not restart the service under a listener.
/// </summary>
public static class StatusEndpoint
{
    /// <param name="Playing">Audio is playing or about to be: also true while a stream buffers or
    /// reconnects, which is a listener waiting, not silence.</param>
    /// <param name="Mode">radio, library or dj.</param>
    /// <param name="DjRunning">A DJ session is on, even while paused: it harvests in the
    /// background, so a restart costs it more than the current song.</param>
    /// <param name="Version">The product version, e.g. 1.10.0.</param>
    /// <param name="Commit">The commit this build was made from, when the build recorded one.</param>
    public sealed record Status(bool Playing, string Mode, bool DjRunning, string Version, string? Commit);

    private static readonly (string Version, string? Commit) Build = ReadBuild();

    public static void MapStatus(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/status", (PlayerHost player) => player.ReadAsync(a => Read(a.ViewModel)));

    private static Status Read(MainViewModel vm) => new(
        Playing: vm.IsPlaying || vm.IsBusy,
        Mode: vm.Mode.ToString().ToLowerInvariant(),
        DjRunning: vm.IsDjRunning,
        Version: Build.Version,
        Commit: Build.Commit);

    /// <summary>The SDK stamps "1.10.0+&lt;commit sha&gt;" into the informational version when it
    /// builds inside a git checkout, which CI and publish-pi.ps1 both do.</summary>
    private static (string, string?) ReadBuild()
    {
        var info = typeof(StatusEndpoint).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        return plus < 0 ? (info, null) : (info[..plus], info[(plus + 1)..]);
    }
}
