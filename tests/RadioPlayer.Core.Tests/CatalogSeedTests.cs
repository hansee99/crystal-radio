using System.IO;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// First-run seeding of the station catalog (#40/#43). The rule that matters is the negative one:
/// a returning user's own catalog is worth more than the shipped snapshot, so seeding must never
/// overwrite one — that would throw away real LLM spend and everything they have explored.
/// </summary>
public sealed class CatalogSeedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "radioplayer-tests", "seed-" + Guid.NewGuid().ToString("N"));

    private string Seed => Path.Combine(_dir, "seed.db");
    private string Target => Path.Combine(_dir, "profile", "enrichment.db");

    public CatalogSeedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public void SeedsWhenTheUserHasNoCatalogYet()
    {
        File.WriteAllText(Seed, "shipped catalog");

        Assert.True(CatalogSeed.EnsureSeeded(Seed, Target));
        Assert.Equal("shipped catalog", File.ReadAllText(Target));
    }

    [Fact]
    public void NeverOverwritesACatalogTheUserAlreadyHas()
    {
        File.WriteAllText(Seed, "shipped catalog");
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "2,563 stations the user actually explored");

        Assert.False(CatalogSeed.EnsureSeeded(Seed, Target));
        Assert.Equal("2,563 stations the user actually explored", File.ReadAllText(Target));
    }

    /// <summary>A dev build has no installer behind it, so there is simply no seed to copy.</summary>
    [Fact]
    public void DoesNothingWhenNoSeedWasShipped()
    {
        Assert.False(CatalogSeed.EnsureSeeded(Path.Combine(_dir, "absent.db"), Target));
        Assert.False(File.Exists(Target));
    }

    /// <summary>Seeding is a quality improvement, never a reason the app fails to start.</summary>
    [Fact]
    public void SurvivesAnUnreadableSeed()
    {
        Directory.CreateDirectory(Seed);   // a directory where a file is expected

        Assert.False(CatalogSeed.EnsureSeeded(Seed, Target));
    }
}
