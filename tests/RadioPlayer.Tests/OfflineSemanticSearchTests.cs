using System.IO;
using System.Net.Http;
using RadioPlayer.Models;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// <see cref="SemanticSearchService.SearchOfflineAsync"/> — the retrieval half of #26. Two things
/// matter: no step in the path may touch the directory (the directory being down is the whole
/// reason it exists), and it must drop weak matches instead of returning the least-bad ones.
/// </summary>
public sealed class OfflineSemanticSearchTests : IDisposable
{
    /// <summary>
    /// Embeds text to a 2-D unit vector by keyword, so cosine similarity between a query and a
    /// description is predictable: identical keyword → 1.0, opposite → 0.0.
    /// </summary>
    private sealed class AxisEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelId => "test-axis-2d";
        public int Dimension => 2;
        public bool IsAvailable { get; set; } = true;

        public float[]? Embed(string text)
        {
            if (!IsAvailable) return null;
            var lower = text.ToLowerInvariant();
            if (lower.Contains("ambient")) return [1f, 0f];
            if (lower.Contains("polka")) return [0f, 1f];
            // Scores 0.32 against "ambient" — the shape of the real false positives measured for
            // #26 (a jazz station at 0.319 for a metal prompt), which the floor exists to exclude.
            if (lower.Contains("marginal")) return [0.32f, 0.947355f];
            // Half way between the two axes: cosine 0.707 to either — a partial match.
            return [0.7071068f, 0.7071068f];
        }
    }

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        "radioplayer-tests", "offline-semantic-" + Guid.NewGuid().ToString("N") + ".db");

    private readonly AxisEmbeddingProvider _embeddings = new();

    /// <summary>Faults on every call, so any directory access at all fails the test loudly.</summary>
    private readonly FakeStationSearchService _directory =
        new() { Fault = new HttpRequestException("the directory is down") };

    private EnrichmentStore _store = null!;

    private SemanticSearchService Build()
    {
        _store = new EnrichmentStore(_dbPath);
        return new SemanticSearchService(_embeddings, _store, _directory);
    }

    /// <summary>A catalog row complete enough to play: description, vector, name and url.</summary>
    private void AddPlayable(string uuid, string description, string url,
        StreamFormat format = StreamFormat.Mp3, string name = "Station")
    {
        _store.Upsert(new EnrichmentRecord(uuid, description, null, EnrichmentSource.Homepage,
            DateTimeOffset.UtcNow, Name: name, Url: url, Codec: format.ToString(),
            Bitrate: 128, Country: "DE"));
        _store.SetEmbedding(uuid, _embeddings.Embed(description)!, _embeddings.ModelId);
    }

    public void Dispose()
    {
        _store?.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task FindsMatchingStationsWithoutTouchingTheDirectory()
    {
        var svc = Build();
        AddPlayable("a", "ambient soundscapes", "http://example/a", name: "Deep Focus FM");

        var hits = await svc.SearchOfflineAsync("ambient music for coding");

        var hit = Assert.Single(hits);
        Assert.Equal("Deep Focus FM", hit.Station.Name);
        Assert.Equal("http://example/a", hit.Station.Url);
        Assert.Equal(StreamFormat.Mp3, hit.Station.Format);
        Assert.Equal("DE", hit.Country);
        Assert.Empty(_directory.Calls);   // the point of the whole exercise
    }

    /// <summary>The floor drops, it does not demote — a session of near-misses is the failure mode.</summary>
    [Fact]
    public async Task DropsEverythingBelowTheFloor()
    {
        var svc = Build();
        AddPlayable("a", "ambient soundscapes", "http://example/a", name: "Deep Focus FM");
        AddPlayable("b", "polka favourites", "http://example/b", name: "Polka Palace");

        var hits = await svc.SearchOfflineAsync("ambient music for coding");

        Assert.Equal(["Deep Focus FM"], hits.Select(h => h.Station.Name));
    }

    [Fact]
    public async Task ReturnsNothingWhenNothingIsCloseEnough()
    {
        var svc = Build();
        AddPlayable("b", "polka favourites", "http://example/b", name: "Polka Palace");

        Assert.Empty(await svc.SearchOfflineAsync("ambient music for coding"));
    }

    /// <summary>A partial match (cosine 0.707) clears the default floor but a stricter one drops it,
    /// which is the knob #26 expects to be tuned.</summary>
    [Theory]
    [InlineData(0.30, 1)]
    [InlineData(0.70, 1)]
    [InlineData(0.80, 0)]
    public async Task TheFloorIsHonoured(double floor, int expected)
    {
        var svc = Build();
        AddPlayable("c", "a bit of everything", "http://example/c");

        Assert.Equal(expected, (await svc.SearchOfflineAsync("ambient", minScore: floor)).Count);
    }

    /// <summary>
    /// The measurement behind the default floor: a 0.32 match is a false positive in practice, so
    /// the shipped default must drop it. If someone lowers the constant back to 0.30 to make the
    /// fallback fire more often, this is the test that should stop them.
    /// </summary>
    [Fact]
    public async Task TheDefaultFloorRejectsTheFalsePositivesItWasMeasuredAgainst()
    {
        var svc = Build();
        AddPlayable("m", "a marginal station", "http://example/m", name: "Marginal FM");

        Assert.Empty(await svc.SearchOfflineAsync("ambient"));
        // Same row, floor lowered to the old 0.30: it comes straight back.
        Assert.Single(await svc.SearchOfflineAsync("ambient", minScore: 0.30));
        Assert.True(SemanticSearchService.DefaultOfflineFloor > 0.32);
    }

    [Fact]
    public async Task BestMatchFirstAndCappedAtK()
    {
        var svc = Build();
        AddPlayable("a", "ambient soundscapes", "http://example/a", name: "Exact");
        AddPlayable("c", "a bit of everything", "http://example/c", name: "Partial");

        var all = await svc.SearchOfflineAsync("ambient");
        Assert.Equal(["Exact", "Partial"], all.Select(h => h.Station.Name));
        Assert.True(all[0].Score > all[1].Score);

        Assert.Equal(["Exact"], (await svc.SearchOfflineAsync("ambient", k: 1)).Select(h => h.Station.Name));
    }

    /// <summary>
    /// A row with a description and vector but no url can't be played, so it can't be offered.
    /// This is every row cached before #26 until it's topped up.
    /// </summary>
    [Fact]
    public async Task IgnoresRowsWithNoStreamUrl()
    {
        var svc = Build();
        _store.Upsert(new EnrichmentRecord("a", "ambient soundscapes", null, EnrichmentSource.Homepage,
            DateTimeOffset.UtcNow));   // no name, no url
        _store.SetEmbedding("a", _embeddings.Embed("ambient soundscapes")!, _embeddings.ModelId);

        Assert.Empty(await svc.SearchOfflineAsync("ambient"));
    }

    /// <summary>
    /// A codec BASS can't open is dropped rather than guessed at. Nothing should get this far —
    /// only playable candidates are enriched — but a cached row is old data by definition.
    /// </summary>
    [Fact]
    public async Task IgnoresRowsWhoseCodecTheEngineCannotOpen()
    {
        var svc = Build();
        AddPlayable("a", "ambient soundscapes", "http://example/a", format: StreamFormat.Other);

        Assert.Empty(await svc.SearchOfflineAsync("ambient"));
    }

    [Fact]
    public async Task IgnoresVectorsFromADifferentModel()
    {
        var svc = Build();
        AddPlayable("a", "ambient soundscapes", "http://example/a");
        _store.SetEmbedding("a", [1f, 0f], "some-other-model");

        Assert.Empty(await svc.SearchOfflineAsync("ambient"));
    }

    [Fact]
    public async Task ReturnsNothingWhenTheEmbeddingModelIsUnavailable()
    {
        var svc = Build();
        AddPlayable("a", "ambient soundscapes", "http://example/a");
        _embeddings.IsAvailable = false;

        Assert.Empty(await svc.SearchOfflineAsync("ambient"));
        Assert.False(svc.IsAvailable);
    }

    [Fact]
    public async Task ReturnsNothingForAnEmptyQuery()
    {
        var svc = Build();
        AddPlayable("a", "ambient soundscapes", "http://example/a");

        Assert.Empty(await svc.SearchOfflineAsync("   "));
    }
}
