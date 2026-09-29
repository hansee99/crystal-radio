using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The read-only feed an external info display reads.
///
/// <para>The contract under test is not "it returns 200" — it is the JSON key names and the
/// binding fallback. A display binds <c>remark</c> by string from another device entirely, so a
/// renamed key and a feed that quietly bound loopback-only both look the same from here: the panel
/// stays blank and nothing anywhere says why.</para>
/// </summary>
public class InfoDisplayServerTests
{
    private static NowPlayingSnapshot Sample(string? remark = "Something warm to end on.") =>
        new(Playing: true, Buffering: false, Mode: "dj", Station: "Deep Focus FM", Format: "MP3",
            Title: "Nightdrive", Artist: "Kavinsky", DjRunning: true, Vibe: "late-night coding",
            Remark: remark, RemarkChangedAt: DateTimeOffset.UtcNow, UpNext: "Rampage — Lifelike",
            ServedAt: DateTimeOffset.UtcNow);

    // --- routing -------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/now")]
    [InlineData("/api/now/")]      // a display's url-joining leaves trailing slashes everywhere
    [InlineData("/API/Now")]
    public void TheFeedAnswersOnItsPathHoweverItIsWritten(string path)
    {
        Assert.Equal(InfoDisplayServer.Route.Now, InfoDisplayServer.RouteFor(path));
    }

    /// <summary>Pointing a browser at the port is how anyone checks the feature is on. A 404 there
    /// reads exactly like it being off, which is the one answer that isn't true.</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData(null)]
    public void TheRootIsASignpostRatherThanA404(string? path)
    {
        Assert.Equal(InfoDisplayServer.Route.Index, InfoDisplayServer.RouteFor(path));
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/nowhere")]
    [InlineData("/index.html")]
    public void NothingElseIsServed(string path)
    {
        Assert.Equal(InfoDisplayServer.Route.NotFound, InfoDisplayServer.RouteFor(path));
    }

    // --- the binding ---------------------------------------------------------------------------

    /// <summary>
    /// Wide first, loopback second, and never the other way round: loopback always succeeds, so
    /// trying it first would mean the feed silently never leaves the machine even on the installs
    /// where the url reservation is in place.
    /// </summary>
    [Fact]
    public void TheWideBindingIsTriedBeforeLoopback()
    {
        var prefixes = InfoDisplayServer.PrefixesFor(9999);

        Assert.Equal(["http://+:9999/", "http://localhost:9999/"], prefixes);
    }

    /// <summary>The fallback is a degraded success, not a failure — the feature works, for one
    /// machine — so the state has to carry both halves or the dialog can't tell the user which
    /// they got.</summary>
    [Fact]
    public void AStartedFeedReportsItsAddressAndHowFarItReaches()
    {
        var port = FreePort();
        using var server = new InfoDisplayServer(() => Task.FromResult(Sample()));

        var state = server.Apply(enabled: true, port);

        Assert.True(state.Running);
        Assert.Equal(port, state.Port);
        Assert.Contains($":{port}{InfoDisplayServer.NowPath}", state.Url);
        // Which one it got depends on rights this test does not control (a wide binding needs a
        // reservation on Windows, nothing on Linux), so only the pairing is asserted: a loopback
        // fallback must say so AND hand over the command that fixes it.
        if (!state.ReachableFromNetwork)
            Assert.Contains("netsh http add urlacl", state.Message);
    }

    /// <summary>Saving an unrelated option re-applies these too. Rebinding the socket each time
    /// would drop whatever display is connected, for nothing.</summary>
    [Fact]
    public void ReapplyingTheSameBindingLeavesTheSocketAlone()
    {
        var port = FreePort();
        using var server = new InfoDisplayServer(() => Task.FromResult(Sample()));

        var first = server.Apply(enabled: true, port);
        var again = server.Apply(enabled: true, port);

        Assert.True(first.Running);
        Assert.Same(first, again);
    }

    [Fact]
    public async Task TurningItOffStopsAnswering()
    {
        var port = FreePort();
        using var server = new InfoDisplayServer(() => Task.FromResult(Sample()));
        var url = $"http://localhost:{port}{InfoDisplayServer.NowPath}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        Assert.True(server.Apply(enabled: true, port).Running);
        using (var served = await client.GetAsync(url))
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        Assert.False(server.Apply(enabled: false, port).Running);

        // Proof it really let go of the socket rather than just reporting that it had — the state
        // saying "off" while the port still answers is the bug this exists to catch.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
    }

    // --- what a display actually reads ---------------------------------------------------------

    [Fact]
    public async Task TheFeedServesTheSnapshotUnderTheKeysADisplayBinds()
    {
        var port = FreePort();
        using var server = new InfoDisplayServer(() => Task.FromResult(Sample()));
        Assert.True(server.Apply(enabled: true, port).Running);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync($"http://localhost:{port}{InfoDisplayServer.NowPath}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Not a browser-visible feature without this: the display's page is served from elsewhere.
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal("Something warm to end on.", root.GetProperty("remark").GetString());
        Assert.Equal("Nightdrive", root.GetProperty("title").GetString());
        Assert.Equal("Kavinsky", root.GetProperty("artist").GetString());
        Assert.Equal("Deep Focus FM", root.GetProperty("station").GetString());
        Assert.Equal("dj", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("djRunning").GetBoolean());
        Assert.True(root.GetProperty("playing").GetBoolean());
        Assert.Equal("late-night coding", root.GetProperty("vibe").GetString());
    }

    /// <summary>
    /// A silent DJ must serialize as <c>"remark": null</c>, not as a missing key. Skipping nulls
    /// would make "nothing to say" indistinguishable from a renamed field on the display's side —
    /// both arrive as undefined, and only one of them is fine.
    /// </summary>
    [Fact]
    public void AnAbsentRemarkIsWrittenRatherThanOmitted()
    {
        using var body = JsonDocument.Parse(Sample(remark: null).ToJson());

        Assert.True(body.RootElement.TryGetProperty("remark", out var remark));
        Assert.Equal(JsonValueKind.Null, remark.ValueKind);
    }

    [Fact]
    public async Task ItIsReadOnlyAndSaysSo()
    {
        var port = FreePort();
        using var server = new InfoDisplayServer(() => Task.FromResult(Sample()));
        Assert.True(server.Apply(enabled: true, port).Running);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var root = $"http://localhost:{port}";

        using var posted = await client.PostAsync(root + InfoDisplayServer.NowPath, new StringContent(""));
        using var missing = await client.GetAsync(root + "/api/nowhere");
        using var index = await client.GetAsync(root + "/");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains(InfoDisplayServer.NowPath, await index.Content.ReadAsStringAsync());
    }

    /// <summary>A reader that throws is the player mid-shutdown, or a dispatcher that has gone
    /// away. The display gets an error status and keeps polling; it must not take the server with
    /// it, which would turn one bad moment into a feed that stays dead until the app restarts.</summary>
    [Fact]
    public async Task AFailingReaderCostsOneRequestRatherThanTheFeed()
    {
        var port = FreePort();
        var fail = true;
        using var server = new InfoDisplayServer(() =>
            fail ? throw new InvalidOperationException("shutting down") : Task.FromResult(Sample()));
        Assert.True(server.Apply(enabled: true, port).Running);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var url = $"http://localhost:{port}{InfoDisplayServer.NowPath}";

        using var broken = await client.GetAsync(url);
        fail = false;
        using var recovered = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, broken.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    // --- the setting ---------------------------------------------------------------------------

    /// <summary>Off by default: it opens an unauthenticated port on whatever network the machine
    /// is on, and a laptop that started doing that by itself in a cafe would be a defect.</summary>
    [Fact]
    public void ItIsOffUntilSomeoneAsksForIt()
    {
        var settings = new AppSettings();

        Assert.False(settings.InfoDisplayEnabled);
        Assert.Equal(InfoDisplayServer.DefaultPort, settings.ResolveInfoDisplayPort());
    }

    /// <summary>Falls back rather than clamping: answering a request for port 80 with port 1024
    /// would look like the setting was ignored, and the app can't bind 80 anyway.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(70000)]
    [InlineData(-1)]
    public void AnUnusablePortFallsBackToTheDefault(int stored)
    {
        Assert.Equal(InfoDisplayServer.DefaultPort,
            new AppSettings { InfoDisplayPort = stored }.ResolveInfoDisplayPort());
    }

    [Fact]
    public void AUsablePortIsKept()
    {
        Assert.Equal(9100, new AppSettings { InfoDisplayPort = 9100 }.ResolveInfoDisplayPort());
    }

    /// <summary>Asks the OS for a port nobody is on, then gives it straight back. Racy in
    /// principle; in a test process that binds it microseconds later, not in practice.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
