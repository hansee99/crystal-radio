using System.IO;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// Both SQLite stores must survive being unable to open their database (#57).
///
/// <para>On a machine with Windows Smart App Control enforcing, the SQLite provider assembly was
/// blocked from loading and the type initializer threw. From an unguarded constructor that killed
/// the app on startup with a crash dialog, before any window appeared. A blocked assembly can't be
/// simulated here, but the failure it produces — the constructor throwing — is reproduced by
/// pointing the store at a path that cannot be opened.</para>
///
/// <para>What the tests pin is that the failure is contained: constructing succeeds, the store
/// reports itself unavailable, and every operation is inert rather than throwing. Nothing
/// downstream has to know.</para>
/// </summary>
public sealed class StoreUnavailableTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "radioplayer-tests", "unavailable-" + Guid.NewGuid().ToString("N"));

    public StoreUnavailableTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    /// <summary>A directory where the database file should be: opening it always fails.</summary>
    private string UnopenablePath(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // --- the station catalog ---------------------------------------------------------------

    [Fact]
    public void AnUnopenableCatalogDoesNotThrowFromTheConstructor()
    {
        using var store = new EnrichmentStore(UnopenablePath("enrichment.db"));

        Assert.False(store.IsAvailable);
    }

    [Fact]
    public void AnUnavailableCatalogReadsEmptyAndWritesNothing()
    {
        using var store = new EnrichmentStore(UnopenablePath("enrichment.db"));

        // Writes are swallowed...
        store.Upsert(new EnrichmentRecord("uuid", "a description", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "Chill FM", Url: "http://x/stream"));
        store.SetEmbedding("uuid", [1f, 0f], "m");
        store.TopUpPlayableFields("uuid", "Chill FM", "http://x/stream", "Mp3", 128, "DE");

        // ...and reads come back empty rather than throwing.
        Assert.Null(store.Get("uuid"));
        Assert.Empty(store.GetEmbeddedRows("m"));
        Assert.Empty(store.GetPlayableRows("m"));
        Assert.Empty(store.GetRowsNeedingEmbedding("m"));
    }

    /// <summary>Everything is stale when nothing is stored, so an unavailable catalog asks the
    /// enrichment path to try — which is right: it degrades to fetching, not to pretending.</summary>
    [Fact]
    public void AnUnavailableCatalogReportsEverythingAsStale()
    {
        using var store = new EnrichmentStore(UnopenablePath("enrichment.db"));

        Assert.True(store.IsStale(null));
    }

    [Fact]
    public void DisposingAnUnavailableCatalogIsHarmless()
    {
        var store = new EnrichmentStore(UnopenablePath("enrichment.db"));

        store.Dispose();
        store.Dispose();
    }

    // --- the song library ------------------------------------------------------------------

    [Fact]
    public void AnUnopenableLibraryDoesNotThrowFromTheConstructor()
    {
        using var store = new LibraryStore(UnopenablePath("library.db"));

        Assert.False(store.IsAvailable);
    }

    [Fact]
    public void AnUnavailableLibraryReadsEmptyAndWritesNothing()
    {
        using var store = new LibraryStore(UnopenablePath("library.db"));
        var song = new SavedSong("C:\\music\\a.mp3", "Heroes", "Bowie", "Station", "mp3",
            DateTimeOffset.UtcNow);

        store.Upsert(song);
        store.SetEnrichment(song.Path, "a description", null);
        store.SetTrackNames(song.Path, "Heroes", "David Bowie");
        store.SetEmbedding(song.Path, [1f, 0f], "m");
        store.Remove(song.Path);

        Assert.Null(store.Get(song.Path));
        Assert.Empty(store.GetAll());
        Assert.Empty(store.GetAll(SongSource.UserSaved));
        Assert.Empty(store.GetEmbeddedRows("m"));
        Assert.Empty(store.GetRowsNeedingEmbedding("m"));
    }

    // --- and the normal case still works ----------------------------------------------------

    [Fact]
    public void AWorkingStoreReportsItselfAvailable()
    {
        using var enrichment = new EnrichmentStore(Path.Combine(_dir, "ok-enrichment.db"));
        using var library = new LibraryStore(Path.Combine(_dir, "ok-library.db"));

        Assert.True(enrichment.IsAvailable);
        Assert.True(library.IsAvailable);

        // And still stores things, so the guards didn't turn a healthy store inert.
        library.Upsert(new SavedSong("C:\\music\\a.mp3", "Heroes", "Bowie", null, "mp3",
            DateTimeOffset.UtcNow));
        Assert.Single(library.GetAll());
    }
}
