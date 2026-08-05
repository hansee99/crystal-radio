using System.IO;
using RadioPlayer.Services;
using RadioPlayer.Tests.Fakes;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Why an empty curation happened, not just that it did.
///
/// <para>The bug this closes: a DJ session started with the prompt "happy musing for a coding
/// session" — one letter from "music" — and every one of 83 library songs scored below the 0.30
/// relevance floor, so the warm-start seed came back empty and the app bridged to live radio with a
/// status line identical to a normal cold start. Measured on the real index, that prompt peaked at
/// 0.197 while "happy music for a coding session" reached 0.501 over the same songs. The library was
/// fine; the query missed; nothing said so.</para>
/// </summary>
public class CurationOutcomeTests
{
    // --- The result type carries the reason ----------------------------------------------------

    [Fact]
    public void AnEmptyResultAlwaysCarriesAReason()
    {
        var result = CurationResult.Empty(CurationOutcome.PromptOutOfDomain, 0.197);

        Assert.False(result.HasSongs);
        Assert.Equal(CurationOutcome.PromptOutOfDomain, result.Outcome);
        Assert.Equal(0.197, result.BestScore);
    }

    /// <summary>The four empties are genuinely different situations and deserve different messages:
    /// an unmatched prompt, a library that lacks the genre, a library with no vectors at all, and a
    /// ranker that looked at good candidates and declined them.</summary>
    [Fact]
    public void TheReasonsAreDistinct()
    {
        var reasons = Enum.GetValues<CurationOutcome>();

        Assert.Contains(CurationOutcome.PromptOutOfDomain, reasons);
        Assert.Contains(CurationOutcome.NothingRelevant, reasons);
        Assert.Contains(CurationOutcome.LibraryEmpty, reasons);
        Assert.Contains(CurationOutcome.RankerDeclined, reasons);
        Assert.Equal(reasons.Length, reasons.Distinct().Count());
    }

    // --- The warm-start passes the reason on ---------------------------------------------------

    private static (DjQueueService Sut, FakeSongCurator Curator) Build()
    {
        var curator = new FakeSongCurator();
        return (new DjQueueService(new FakeLocalQueuePlayer(), curator, new FakeHarvestSource(),
                                   maxSeed: 20, lowWatermark: 1), curator);
    }

    [Theory]
    [InlineData(CurationOutcome.PromptOutOfDomain)]
    [InlineData(CurationOutcome.NothingRelevant)]
    [InlineData(CurationOutcome.RankerDeclined)]
    [InlineData(CurationOutcome.LibraryEmpty)]
    public async Task AnEmptySeedExposesWhyItWasEmpty(CurationOutcome reason)
    {
        var (sut, curator) = Build();
        curator.Enqueue([], reason);

        var seeded = await sut.StartAsync("happy musing for a coding session");

        Assert.False(seeded);
        // Without this the caller can only say "bridging", which is what made a typo invisible.
        Assert.Equal(reason, sut.SeedOutcome);
    }

    [Fact]
    public async Task ASuccessfulSeedReportsOk()
    {
        var (sut, curator) = Build();
        var file = Path.Combine(Path.GetTempPath(), $"crystal-{Guid.NewGuid():n}.mp3");
        File.WriteAllBytes(file, new byte[32]);
        try
        {
            curator.Enqueue([new CuratedSong(file, "T", "A", null)]);

            Assert.True(await sut.StartAsync("happy music for a coding session"));
            Assert.Equal(CurationOutcome.Ok, sut.SeedOutcome);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The seed can be non-empty and still fail to start anything, when every file has been evicted
    /// from the size-capped harvest cache since it was indexed. That is not a prompt problem, and
    /// the reason must not be reported as one.
    /// </summary>
    [Fact]
    public async Task AVanishedFileIsNotBlamedOnThePrompt()
    {
        var (sut, curator) = Build();
        curator.Enqueue([new CuratedSong(
            Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():n}.mp3"), "T", "A", null)]);

        Assert.False(await sut.StartAsync("a perfectly good prompt"));
        Assert.Equal(CurationOutcome.Ok, sut.SeedOutcome);   // the curator DID find songs
    }
}
