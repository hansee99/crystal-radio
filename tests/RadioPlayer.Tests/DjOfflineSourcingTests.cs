using System.IO;
using System.Net.Http;
using RadioPlayer.Models;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// What happens to DJ station sourcing when the Radio Browser mirrors are unreachable (#26).
///
/// <para>All of these stop before a harvester ever connects: sourcing that finds nothing returns
/// early from <see cref="DjHarvestService.StartAsync"/> without starting the BASS harvest thread,
/// so <c>IsRunning == false</c> plus <c>LastSourcingOutcome</c> is the whole observable result. The
/// positive case — offline stations that the ranker accepts — would start real audio capture, so
/// what's asserted there is what the ranker was handed, which is the part that decides the
/// session's quality.</para>
/// </summary>
public sealed class DjOfflineSourcingTests : IDisposable
{
    private readonly string _harvestDir = Path.Combine(Path.GetTempPath(),
        "radioplayer-tests", "offline-" + Guid.NewGuid().ToString("N"));

    private readonly FakeStationSearchService _directory = new();
    private readonly FakePromptInterpreter _interpreter = new();
    private readonly FakeAgenticSearchService _web = new();
    private readonly FakeSearchRanker _ranker = new();
    private readonly FakeEnrichmentService _enrichment = new();
    private readonly FakeSemanticSearchService _semantic = new();
    private readonly FakeSongLibraryService _library = new();

    private DjHarvestService Build() => new(
        _directory, _interpreter, _web, _ranker, _enrichment, _library, _harvestDir,
        harvesterCount: 2, reserveCount: 3, semanticSearch: _semantic);

    /// <summary>The observed outage: every mirror refuses the connection.</summary>
    private void DirectoryIsDown() => _directory.Fault = new HttpRequestException("mirrors are down");

    public void Dispose()
    {
        try { if (Directory.Exists(_harvestDir)) Directory.Delete(_harvestDir, recursive: true); }
        catch (IOException) { /* best-effort test cleanup */ }
    }

    [Fact]
    public async Task AnOutageFallsBackToTheLocalCatalog()
    {
        DirectoryIsDown();
        _semantic.AddOffline("Deep Focus FM", 0.52).AddOffline("Ambient Works", 0.41);
        _ranker.Keep = [];   // nothing passes, so no harvest thread starts

        using var harvest = Build();
        await harvest.StartAsync("ambient music for coding");

        Assert.Equal(1, _semantic.OfflineCalls);
        var judged = Assert.Single(_ranker.Seen);
        Assert.Equal(["Deep Focus FM", "Ambient Works"], judged.Select(c => c.Name));
        // The cached description is what matched the query, so it's what the ranker should judge.
        Assert.All(judged, c => Assert.Equal("a description", c.Text));
    }

    [Fact]
    public async Task WithoutAnOutageTheLocalCatalogIsNotConsulted()
    {
        _directory.Add("uuid-1", "Directory FM");
        _semantic.AddOffline("Offline FM", 0.9);
        _ranker.Keep = [];

        using var harvest = Build();
        await harvest.StartAsync("ambient music for coding");

        Assert.Equal(0, _semantic.OfflineCalls);
        Assert.Equal(["Directory FM"], Assert.Single(_ranker.Seen).Select(c => c.Name));
    }

    /// <summary>
    /// The hard requirement: nothing rather than something off-vibe. A thin catalog with no real
    /// match must produce an empty session and say the directory is the reason.
    /// </summary>
    [Fact]
    public async Task AnOutageWithNoLocalMatchStartsNothingAndSaysWhy()
    {
        DirectoryIsDown();   // and the catalog holds nothing above the floor

        using var harvest = Build();
        await harvest.StartAsync("ambient music for coding");

        Assert.False(harvest.IsRunning);
        Assert.Equal(DjSourcingOutcome.OfflineNoMatch, harvest.LastSourcingOutcome);
    }

    /// <summary>
    /// Offline with nothing to BE offline with — no embedding provider, or a catalog with no
    /// vectors — is a different answer from "the catalog was searched and came up short" (#60).
    /// Nothing was searched, so the wording that follows must not send the listener rewording a
    /// prompt that was never consulted.
    ///
    /// <para>The bug this pins is a silent one: the empty pool returns from
    /// <c>SourceFromLocalCatalogAsync</c>, <c>RankRelevantAsync</c> exits on <c>pool.Count == 0</c>
    /// before it can record anything, and the caller's fallback then labels it OfflineNoMatch.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]    // an index exists but reports itself unusable (missing ONNX model)
    [InlineData(false)]   // no semantic search wired in at all
    public async Task AnOutageWithNoLocalCatalogSaysSoRatherThanBlamingThePrompt(bool wired)
    {
        DirectoryIsDown();
        _semantic.IsAvailable = false;

        using var harvest = wired
            ? Build()
            : new DjHarvestService(_directory, _interpreter, _web, _ranker, _enrichment, _library,
                _harvestDir, harvesterCount: 2, reserveCount: 3, semanticSearch: null);
        await harvest.StartAsync("ambient music for coding");

        Assert.False(harvest.IsRunning);
        Assert.Equal(DjSourcingOutcome.OfflineNoCatalog, harvest.LastSourcingOutcome);
        Assert.Equal(0, _semantic.OfflineCalls);   // nothing was searched, hence the distinction
    }

    /// <summary>
    /// The case that must not degrade to "play the least-bad cosine hits": offline AND no ranker.
    /// Without a relevance judge there is nothing between a thin catalog and a whole session of
    /// unrelated stations, so sourcing refuses.
    /// </summary>
    [Theory]
    [InlineData(false, true)]   // no API key at all
    [InlineData(true, false)]   // configured, but the call couldn't complete
    public async Task AnOutageWithNoRankerRefusesRatherThanGuessing(bool configured, bool answers)
    {
        DirectoryIsDown();
        _semantic.AddOffline("Polka Palace", 0.31);   // above the floor, still wrong for the prompt
        _ranker.IsConfigured = configured;
        _ranker.ReturnsNull = !answers;

        using var harvest = Build();
        await harvest.StartAsync("ambient music for coding");

        Assert.False(harvest.IsRunning);
        Assert.Equal(DjSourcingOutcome.OfflineUnranked, harvest.LastSourcingOutcome);
    }

    /// <summary>
    /// The same missing ranker is still allowed to degrade when the directory answered — that pool
    /// is at least tag-matched and vote-sorted, which is the case the escape hatch was written for.
    /// Reaching a real harvester is out of scope here; what matters is that it did NOT refuse.
    /// </summary>
    [Fact]
    public async Task WithoutAnOutageAMissingRankerStillDegradesToTheDirectoryPool()
    {
        _directory.Add("uuid-1", "Directory FM");
        _ranker.IsConfigured = false;

        using var harvest = Build();
        await harvest.StartAsync("ambient music for coding");

        Assert.NotEqual(DjSourcingOutcome.OfflineUnranked, harvest.LastSourcingOutcome);
        Assert.Equal(DjSourcingOutcome.Ok, harvest.LastSourcingOutcome);
        harvest.Stop();   // it did start, so shut the harvest thread down again
    }

    /// <summary>
    /// Pattern B resolves its web findings to playable streams through the same mirrors, so
    /// escalating during an outage spends a Sonnet loop to arrive back at the same failure.
    /// </summary>
    [Fact]
    public async Task WebDiscoveryIsNotAttemptedDuringAnOutage()
    {
        DirectoryIsDown();
        _web.IsConfigured = true;
        _semantic.AddOffline("Deep Focus FM", 0.52);
        _ranker.Keep = [];   // a thin result, which would normally trigger escalation

        using var harvest = Build();
        await harvest.StartAsync("ambient music for coding");

        Assert.Equal(0, _web.Calls);
    }

    [Fact]
    public async Task WebDiscoveryStillEscalatesWhenTheDirectoryIsUp()
    {
        _directory.Add("uuid-1", "Directory FM");
        _web.IsConfigured = true;
        _ranker.Keep = [];   // thin, so escalation should fire

        using var harvest = Build();
        await harvest.StartAsync("ambient music for coding");

        Assert.Equal(1, _web.Calls);
    }

    /// <summary>
    /// A user pressing Cancel is not an outage. Treating it as one would run a fallback search
    /// they explicitly asked to stop.
    /// </summary>
    [Fact]
    public async Task CancellingIsNotTreatedAsAnOutage()
    {
        using var cts = new CancellationTokenSource();
        _directory.Fault = new TaskCanceledException("cancelled");
        cts.Cancel();

        using var harvest = Build();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harvest.StartAsync("ambient music for coding", ct: cts.Token));

        Assert.Equal(0, _semantic.OfflineCalls);
    }
}
