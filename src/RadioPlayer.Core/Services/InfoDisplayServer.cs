using System.Net;
using System.Text;
using System.Text.Json;

namespace RadioPlayer.Services;

/// <summary>Where the info display server got to, in terms the options dialog can print.</summary>
/// <param name="Running">A socket is open and answering.</param>
/// <param name="Port">The port this is the outcome FOR — set even when the start failed, so the
/// dialog can tell "it tried this port and couldn't" from "you have typed a different port and not
/// saved yet". Those read identically otherwise, and only one of them is the app's fault.</param>
/// <param name="Url">The address to point a display at, or null when nothing is being served.</param>
/// <param name="ReachableFromNetwork">False when it fell back to loopback — the feature still
/// works, but only for a browser on this PC, which is not what anyone turned it on for.</param>
/// <param name="Message">One sentence for the user. The failure cases are the interesting ones:
/// a port already in use and a missing url reservation look identical from the outside otherwise.</param>
public sealed record InfoDisplayState(
    bool Running, int Port, string? Url, bool ReachableFromNetwork, string Message)
{
    public static readonly InfoDisplayState Off =
        new(false, 0, null, false, "Off — nothing is being published.");
}

/// <summary>
/// A small read-only HTTP server so an external info display can show what the player is doing —
/// above all the DJ's remark. One endpoint, <c>GET /api/now</c>, returning a
/// <see cref="NowPlayingSnapshot"/>; a display polls it.
///
/// <para><b>Why HttpListener and not Kestrel.</b> This head ships framework-dependent against the
/// .NET Desktop runtime. Referencing ASP.NET Core would add a second runtime every user has to
/// install, for one JSON endpoint — a bigger change to how the app is delivered than to what it
/// does. HttpListener is in the BCL and costs nothing at the door.</para>
///
/// <para><b>Two things come with that choice</b>, both handled below or in the installer. A
/// non-elevated process may not register <c>http://+:port/</c> without a url reservation, so the
/// start falls back to loopback and says so rather than failing. And http.sys owns the socket, so
/// Windows Firewall attributes it to System and never raises the usual "allow this app?" prompt —
/// an unopened port is silently unreachable, which is why the installer adds a port rule instead
/// of waiting for a prompt that will not come.</para>
///
/// <para><b>No authentication</b>, by design and in the same spirit as the web head: this is a LAN
/// appliance, not a service. It is off by default, and read-only — nothing reachable here can
/// change what is playing. Don't forward the port.</para>
///
/// <para>Knows nothing about playback: it is handed a reader and serves whatever that returns. The
/// reader is async because on the WPF head it hops to the UI thread, and a request must never block
/// a threadpool thread waiting on a window.</para>
/// </summary>
public sealed class InfoDisplayServer : IDisposable
{
    /// <summary>Deliberately not 8080 or 8000, which every other dev tool on a machine already
    /// wants. Nothing depends on the number — it is a setting.</summary>
    public const int DefaultPort = 8723;

    public const string NowPath = "/api/now";

    private readonly Func<Task<NowPlayingSnapshot>> _read;
    private readonly object _gate = new();

    private HttpListener? _listener;
    private int _boundPort;

    /// <param name="read">Produces the snapshot to serve, on whichever thread owns the view model.</param>
    public InfoDisplayServer(Func<Task<NowPlayingSnapshot>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    public InfoDisplayState State { get; private set; } = InfoDisplayState.Off;

    /// <summary>
    /// Brings the server in line with the saved settings, and is the only method the app calls:
    /// start, stop and move-to-another-port are all "make it match this". Idempotent — re-applying
    /// the binding it already has leaves the socket alone, so saving an unrelated option doesn't
    /// drop a display's connection.
    /// </summary>
    public InfoDisplayState Apply(bool enabled, int port)
    {
        lock (_gate)
        {
            if (!enabled)
            {
                StopCore();
                return State = InfoDisplayState.Off;
            }

            if (_listener is not null && _boundPort == port)
                return State;

            StopCore();
            return State = Start(port);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
            State = InfoDisplayState.Off;
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The prefixes to try, best first. <c>+</c> is every interface — what an external display
    /// needs — and requires either elevation or a url reservation (the installer adds one for
    /// <see cref="DefaultPort"/>). Loopback is the consolation prize: always permitted, and enough
    /// to prove the feature works before going hunting for why the tablet cannot see it.
    /// </summary>
    internal static string[] PrefixesFor(int port) =>
        [$"http://+:{port}/", $"http://localhost:{port}/"];

    private InfoDisplayState Start(int port)
    {
        if (port is < 1 or > 65535)
            return new InfoDisplayState(false, port, null, false, $"Port {port} isn't a usable port number.");

        Exception? failure = null;

        foreach (var prefix in PrefixesFor(port))
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                // Keep the FIRST failure: it is the one about the address the user actually wants.
                // If loopback fails too, that same reason (a port in use) explains both; if it
                // succeeds, the first failure is still what the message has to be about.
                failure ??= ex;
                listener.Close();
                continue;
            }

            _listener = listener;
            _boundPort = port;
            var wide = prefix.StartsWith("http://+", StringComparison.Ordinal);
            _ = Task.Run(() => AcceptLoopAsync(listener));

            AppLog.Info($"[InfoDisplay] listening on {prefix}");
            var host = wide ? Environment.MachineName.ToLowerInvariant() : "localhost";
            var url = $"http://{host}:{port}{NowPath}";
            return new InfoDisplayState(true, port, url, wide,
                wide ? $"Serving {url}"
                     : $"Only this PC can reach it — {url}. {Reservation(port)}");
        }

        AppLog.Warn($"[InfoDisplay] could not listen on port {port}: {failure?.Message}");
        return new InfoDisplayState(false, port, null, false,
            $"Couldn't start on port {port}: {failure?.Message ?? "unknown error"}");
    }

    /// <summary>The command that grants this user the wide binding, for when the app was not put
    /// here by the installer — an xcopy build, or a port other than the default.</summary>
    internal static string Reservation(int port) =>
        "To reach it from another device, run this once as administrator: " +
        $"netsh http add urlacl url=http://+:{port}/ user=\"{Environment.UserDomainName}\\{Environment.UserName}\"";

    private void StopCore()
    {
        var listener = _listener;
        _listener = null;
        _boundPort = 0;
        if (listener is null) return;

        // Close, not Stop: Stop leaves the object alive and the accept loop parked on a call that
        // never completes. Close ends it, which the loop reads as "we were shut down".
        try { listener.Close(); }
        catch (Exception ex) { AppLog.Debug($"[InfoDisplay] stop failed: {ex.Message}"); }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (!listener.IsListening)
            {
                return;   // stopped, or the app is closing — the expected way out of this loop
            }
            catch (Exception ex)
            {
                AppLog.Warn($"[InfoDisplay] accept failed: {ex.Message}");
                return;
            }

            // Not awaited: one slow reader must not hold up the next request. A display that stops
            // reading mid-response costs its own task and nothing else.
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            // A display is usually a browser page served from somewhere else entirely — a file on
            // an SD card, a kiosk, the Pi head. Without this it can reach the socket and still be
            // refused the body by its own browser.
            response.AddHeader("Access-Control-Allow-Origin", "*");
            response.AddHeader("Cache-Control", "no-store");

            var head = string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            if (!head && !string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteAsync(response, 405, Error("Read-only: use GET."), head).ConfigureAwait(false);
                return;
            }

            switch (RouteFor(request.Url?.AbsolutePath))
            {
                case Route.Now:
                    var snapshot = await _read().ConfigureAwait(false);
                    await WriteAsync(response, 200, snapshot.ToJson(), head).ConfigureAwait(false);
                    break;

                case Route.Index:
                    await WriteAsync(response, 200, Index(), head).ConfigureAwait(false);
                    break;

                default:
                    await WriteAsync(response, 404, Error($"Nothing here. Try {NowPath}."), head)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug($"[InfoDisplay] request failed: {ex.Message}");
            try
            {
                await WriteAsync(context.Response, 503, Error("The player couldn't answer."), false)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The client is gone, or the response is already on the wire — nothing to salvage.
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* already closed by the write above */ }
        }
    }

    internal enum Route { Index, Now, NotFound }

    /// <summary>One endpoint and a signpost. The signpost earns its place: someone pointing a
    /// browser at the port to check it is alive should see the answer, not a 404 that reads exactly
    /// like the feature being switched off.</summary>
    internal static Route RouteFor(string? absolutePath)
    {
        var path = (absolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0) return Route.Index;
        return string.Equals(path, NowPath, StringComparison.OrdinalIgnoreCase)
            ? Route.Now
            : Route.NotFound;
    }

    private static string Index() =>
        JsonSerializer.Serialize(new { app = "Crystal Radio", endpoints = new[] { NowPath } },
            NowPlayingSnapshot.Json);

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message }, NowPlayingSnapshot.Json);

    private static async Task WriteAsync(HttpListenerResponse response, int status, string json, bool head)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        if (head) return;   // headers only, and the length above still describes the real body
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }
}
