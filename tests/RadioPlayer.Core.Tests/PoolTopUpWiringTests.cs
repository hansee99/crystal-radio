using System.IO;
using System.Net.Http;
using RadioPlayer.Models;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// That the top-up is actually wired to the moment it exists for (#41) — the policy tests in
/// <see cref="PoolTopUpPolicyTests"/> pin the decisions, not the plumbing.
///
/// <para>This one runs a real session: it initialises the harvest thread's BASS device and starts a
/// real <c>StreamHarvester</c> against an unroutable url, which fails to connect and retires
/// immediately. With an empty reserve that is exactly the "slot lost for good" case, so a top-up
/// must follow. Timing-dependent by nature (the attempt is fire-and-forget on a worker thread), so
/// it polls with a generous timeout rather than sleeping a fixed amount.</para>
/// </summary>
public sealed class PoolTopUpWiringTests : IDisposable
{
    private readonly string _harvestDir = Path.Combine(Path.GetTempPath(),
        "radioplayer-tests", "topup-" + Guid.NewGuid().ToString("N"));

    private readonly FakeStationSearchService _directory = new();
    private readonly FakePromptInterpreter _interpreter = new();
    private readonly FakeAgenticSearchService _web = new();
    private readonly FakeSearchRanker _ranker = new();
    private readonly FakeEnrichmentService _enrichment = new();
    private readonly FakeSemanticSearchService _semantic = new();
    private readonly FakeSongLibraryService _library = new();

    public void Dispose()
    {
        try { if (Directory.Exists(_harvestDir)) Directory.Delete(_harvestDir, recursive: true); }
        catch (IOException) { /* best-effort test cleanup */ }
    }

    /// <summary>Port 1 on loopback: refuses instantly, so the harvester dies without a network wait.</summary>
    private const string DeadUrl = "http://127.0.0.1:1/stream";

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task LosingTheLastHarvesterSlotTriggersATopUpThatSkipsWhatWasAlreadyTried()
    {
        // Three stations available, but a 1-harvester/0-reserve session only asks for one — so the
        // other two are still out there when the slot is lost.
        _directory.Add("uuid-1", "Dead One", DeadUrl);
        _directory.Add("uuid-2", "Dead Two", DeadUrl + "/2");
        _directory.Add("uuid-3", "Dead Three", DeadUrl + "/3");

        using var harvest = new DjHarvestService(
            _directory, _interpreter, _web, _ranker, _enrichment, _library, _harvestDir,
            harvesterCount: 1, reserveCount: 0, semanticSearch: _semantic);

        await harvest.StartAsync("ambient music for coding");

        // The only harvester can't connect, the reserve is empty, so the slot would have been lost
        // for good. Instead: a second sourcing pass.
        Assert.True(await Eventually(() => _ranker.Seen.Count >= 2),
            $"expected a second sourcing pass after the slot was lost; ranker saw {_ranker.Seen.Count}");
        Assert.True(_directory.Calls.Count >= 2, "the top-up should have re-queried the directory");

        // And it left out the station this session already tried and buried.
        Assert.DoesNotContain("Dead One", _ranker.Seen[1].Select(c => c.Name));
        Assert.Contains("Dead Two", _ranker.Seen[1].Select(c => c.Name));
        harvest.Stop();
    }

    /// <summary>
    /// Pattern B is a multi-round-trip Sonnet loop, and a thin top-up means most of the good matches
    /// are already in the pool — which is why they were excluded. Escalating there pays a lot to
    /// rediscover them.
    /// </summary>
    [Fact]
    public async Task ATopUpDoesNotEscalateToWebSearch()
    {
        _directory.Add("uuid-1", "Dead FM", DeadUrl);
        _web.IsConfigured = true;
        using var harvest = new DjHarvestService(
            _directory, _interpreter, _web, _ranker, _enrichment, _library, _harvestDir,
            harvesterCount: 1, reserveCount: 0, semanticSearch: _semantic);

        await harvest.StartAsync("ambient music for coding");
        await Eventually(() => _ranker.Seen.Count >= 2);

        // The initial sourcing may escalate (one station is thinner than the one requested); the
        // top-up must not add a second.
        Assert.True(_web.Calls <= 1, $"web search ran {_web.Calls} times — a top-up escalated");
        harvest.Stop();
    }
}
