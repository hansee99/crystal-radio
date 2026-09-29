using RadioPlayer.Services;

namespace RadioPlayer.Web.Hosting;

/// <summary>
/// <c>GET /api/now</c>: what is playing and what the DJ just said, as JSON — the feed an external
/// info display reads.
///
/// <para>The shape is <see cref="NowPlayingSnapshot"/>, shared with the WPF head, which serves the
/// same path from an <see cref="InfoDisplayServer"/>. One display can then point at either head
/// without knowing which one it got, and there is one place to change when the payload grows.
/// Kestrel makes it free here, so unlike on Windows it needs no setting to turn on.</para>
/// </summary>
public static class NowPlayingEndpoint
{
    public static void MapNowPlaying(this IEndpointRouteBuilder app) =>
        app.MapGet(InfoDisplayServer.NowPath, async (PlayerHost player, HttpResponse response) =>
        {
            // Same two headers the Windows feed sends, for the same reason: the display's page is
            // usually served from somewhere else, and a cached snapshot is a stopped clock.
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers.CacheControl = "no-store";
            return Results.Json(
                await player.ReadAsync(a => NowPlayingSnapshot.From(a.ViewModel)),
                NowPlayingSnapshot.Json);
        });
}
