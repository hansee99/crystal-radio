using System.Net;
using System.Net.Http;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Mirror failover. Every AI feature in the app funnels through this one directory, so a single
/// mirror having a bad day must not take station search, DJ sourcing and semantic resolution down
/// with it — which is exactly what happened on 2026-08-02: a 503 sweep on one mirror failed a DJ
/// start outright while another mirror was answering normally throughout.
/// </summary>
public class StationSearchMirrorTests
{
    private const string StationJson = """
        [ { "stationuuid": "u1", "name": "Jazz FM", "url": "http://a/s", "url_resolved": "http://a/s",
            "codec": "MP3", "bitrate": 128, "hls": 0, "lastcheckok": 1, "countrycode": "DE" } ]
        """;

    private static HttpResponseMessage Ok(string json = StationJson) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ServiceUnavailable() =>
        new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };

    /// <summary>Fails every host containing <paramref name="deadHost"/>, serves everything else.</summary>
    private static FakeHttpMessageHandler WithDeadMirror(string deadHost) => new()
    {
        Fallback = req => req.RequestUri!.Host.Contains(deadHost, StringComparison.OrdinalIgnoreCase)
            ? ServiceUnavailable()
            : Ok()
    };

    private static StationSearchQuery Query(params string[] tags) => new() { Tags = tags };

    [Fact]
    public async Task FailsOverToTheNextMirror()
    {
        var handler = WithDeadMirror("de1");
        var service = new StationSearchService(handler.Client());

        var results = await service.SearchCandidatesAsync(Query("jazz"));

        Assert.Single(results);
        Assert.Contains(handler.RequestUris, u => u.Host.StartsWith("de1"));
        Assert.Contains(handler.RequestUris, u => u.Host.StartsWith("de2"));
    }

    /// <summary>
    /// The regression, and the reason this file exists. A multi-tag query fans out one request per
    /// tag CONCURRENTLY. The failover used to read the shared mirror counter afresh on every
    /// attempt and increment it on every failure, so N parallel calls all chose the same mirror,
    /// all failed, and all advanced the counter by N — which with three mirrors wrapped straight
    /// back onto the dead one. Each call then spent its whole retry budget on that single host and
    /// gave up without ever trying the live mirror sitting between them.
    ///
    /// Needs three mirrors (dead, ALIVE, dead) to show: the bug is skipping the middle one. Also
    /// needs Latency, or the fake completes inline and the calls never actually overlap.
    /// </summary>
    [Fact]
    public async Task AConcurrentMultiTagSearchReachesTheLiveMirrorBetweenTwoDeadOnes()
    {
        var handler = new FakeHttpMessageHandler
        {
            Latency = TimeSpan.FromMilliseconds(15),
            Fallback = req => req.RequestUri!.Host.StartsWith("live") ? Ok() : ServiceUnavailable()
        };
        var service = new StationSearchService(handler.Client(),
            ["https://dead1.example", "https://live.example", "https://dead2.example"]);

        // Four tags → the AND query plus one per tag: five requests in flight together.
        var results = await service.SearchCandidatesAsync(Query("deep house", "chillout", "ambient", "focus"));

        Assert.NotEmpty(results);
        Assert.Contains(handler.RequestUris, u => u.Host == "live.example");
    }

    [Fact]
    public async Task EachCallTriesEveryMirrorAtMostOnce()
    {
        // The clearest symptom in the 2026-08-02 log: one mirror appearing four times inside a
        // single failover round while others were never reached at all.
        var handler = new FakeHttpMessageHandler
        {
            Latency = TimeSpan.FromMilliseconds(15),
            Fallback = _ => ServiceUnavailable()
        };
        var service = new StationSearchService(handler.Client(),
            ["https://m1.example", "https://m2.example", "https://m3.example"]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.SearchCandidatesAsync(Query("a", "b", "c", "d")));

        // 5 concurrent queries × 3 mirrors each, and no mirror hit twice by the same query.
        foreach (var host in new[] { "m1.example", "m2.example", "m3.example" })
            Assert.Equal(5, handler.RequestUris.Count(u => u.Host == host));
    }

    /// <summary>Once a mirror answers, later calls should start there rather than paying the dead
    /// mirror's timeout again on every single query.</summary>
    [Fact]
    public async Task SticksToTheMirrorThatWorked()
    {
        var handler = WithDeadMirror("de1");
        var service = new StationSearchService(handler.Client());

        await service.SearchCandidatesAsync(Query("jazz"));
        var afterFirst = handler.RequestUris.Count;
        await service.SearchCandidatesAsync(Query("rock"));

        var second = handler.RequestUris.Skip(afterFirst).ToList();
        Assert.Single(second);
        Assert.StartsWith("de2", second[0].Host);
    }

    [Fact]
    public async Task GivesUpOnlyWhenEveryMirrorIsDown()
    {
        var handler = new FakeHttpMessageHandler { Fallback = _ => ServiceUnavailable() };
        var service = new StationSearchService(handler.Client());

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchCandidatesAsync(Query("jazz")));

        // Every distinct mirror tried exactly once — no wasted retries on an already-dead host.
        Assert.Equal(handler.RequestUris.Select(u => u.Host).Distinct().Count(), handler.RequestUris.Count);
        Assert.True(handler.RequestUris.Count > 1, "should have tried more than one mirror");
    }

    /// <summary>An explicitly configured mirror is the caller's choice — don't silently reach for
    /// a public one behind their back.</summary>
    [Fact]
    public async Task DoesNotFailOverWhenASingleMirrorWasConfigured()
    {
        var handler = new FakeHttpMessageHandler { Fallback = _ => ServiceUnavailable() };
        var service = new StationSearchService(handler.Client(), mirror: "https://my.mirror.example");

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchCandidatesAsync(Query("jazz")));

        Assert.All(handler.RequestUris, u => Assert.Equal("my.mirror.example", u.Host));
    }

    [Fact]
    public async Task PropagatesCancellationInsteadOfBurningThroughTheMirrorList()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeHttpMessageHandler { Fallback = _ => Ok() };
        var service = new StationSearchService(handler.Client());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SearchCandidatesAsync(Query("jazz"), 0, cts.Token));
    }

    /// <summary>The mirror list must not carry hosts known to be gone: with only a couple of
    /// entries, a dead one is a large slice of the retry budget spent on nothing.</summary>
    [Fact]
    public async Task DoesNotStillCarryTheRetiredAt1Mirror()
    {
        var handler = new FakeHttpMessageHandler { Fallback = _ => ServiceUnavailable() };
        var service = new StationSearchService(handler.Client());

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchCandidatesAsync(Query("jazz")));

        Assert.DoesNotContain(handler.RequestUris, u => u.Host.StartsWith("at1"));
    }
}
