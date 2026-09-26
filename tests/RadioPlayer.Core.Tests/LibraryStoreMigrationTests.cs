using System.IO;
using Microsoft.Data.Sqlite;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The v1 → v2 upgrade of library.db (#10). Unlike the enrichment catalog, which is derived data
/// that costs money to rebuild, this holds the songs the user chose to keep — so a bad migration
/// here is not an expense, it is a loss.
///
/// <para>The v1 shape is built by hand from the DDL the original build actually shipped
/// (<c>dfda9e9</c>), not by asking the current code to make one. A test that lets the code under
/// test create its own fixture cannot fail the way a real upgrade fails.</para>
/// </summary>
public sealed class LibraryStoreMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "radioplayer-tests", "libmig-" + Guid.NewGuid().ToString("N"));

    public LibraryStoreMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private string NewDbPath() => Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");

    /// <summary>The exact table the first shipped build created, plus its user_version stamp.</summary>
    private static void CreateV1(string path, params (string Path, string Title, string Artist)[] rows)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        Exec(cn, """
            CREATE TABLE songs (
                path            TEXT PRIMARY KEY NOT NULL,
                title           TEXT NOT NULL,
                artist          TEXT NOT NULL,
                station         TEXT,
                codec           TEXT NOT NULL,
                saved_at        TEXT NOT NULL,
                description     TEXT,
                facets          TEXT,
                embedding       BLOB,
                embedding_model TEXT
            );
            """);
        Exec(cn, "PRAGMA user_version=1;");

        foreach (var (p, title, artist) in rows)
        {
            using var cmd = cn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO songs (path, title, artist, station, codec, saved_at, description, facets)
                VALUES ($p, $t, $a, 'Radio Paradise', 'mp3', '2026-07-21T10:00:00.0000000+00:00',
                        'A description that cost an LLM call.', '{"genres":["rock"]}');
                """;
            cmd.Parameters.AddWithValue("$p", p);
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$a", artist);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Exec(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void SetEmbeddingDirectly(string path, string songPath, float[] vector, string model)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "UPDATE songs SET embedding=$e, embedding_model=$m WHERE path=$p;";
        cmd.Parameters.AddWithValue("$e", blob);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$p", songPath);
        cmd.ExecuteNonQuery();
    }

    private static List<string> Columns(string path)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        var names = new List<string>();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(songs);";
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(1));
        return names;
    }

    private static long UserVersion(string path)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // --- The upgrade ---------------------------------------------------------------------

    [Fact]
    public void UpgradingAV1DatabaseKeepsEverySong()
    {
        var path = NewDbPath();
        CreateV1(path,
            ("C:\\music\\a.mp3", "Silent Lucidity", "Queensrÿche"),
            ("C:\\music\\b.mp3", "Wish You Were Here", "Pink Floyd"));

        using var store = new LibraryStore(path);
        var songs = store.GetAll();

        Assert.Equal(2, songs.Count);
        var a = songs.Single(s => s.Path == "C:\\music\\a.mp3");
        Assert.Equal("Silent Lucidity", a.Title);
        Assert.Equal("Queensrÿche", a.Artist);
        Assert.Equal("Radio Paradise", a.Station);
        Assert.Equal("A description that cost an LLM call.", a.Description);
        Assert.Equal("{\"genres\":[\"rock\"]}", a.FacetsJson);
    }

    /// <summary>
    /// Every row that existed before the column did is, by definition, something the user saved.
    /// Defaulting them to Harvested would expose them to the harvested-only retirement paths, which
    /// delete files — see SongLibraryService.RetireIfNoLongerSongLike.
    /// </summary>
    [Fact]
    public void PreExistingSongsBecomeUserSavedNotHarvested()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\a.mp3", "Silent Lucidity", "Queensrÿche"));

        using var store = new LibraryStore(path);

        Assert.Equal(SongSource.UserSaved, store.Get("C:\\music\\a.mp3")!.Source);
        Assert.Single(store.GetAll(SongSource.UserSaved));
        Assert.Empty(store.GetAll(SongSource.Harvested));
    }

    /// <summary>Embeddings are the expensive part; an upgrade must not disturb a single byte.</summary>
    [Fact]
    public void UpgradingKeepsExistingEmbeddings()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\a.mp3", "Silent Lucidity", "Queensrÿche"));
        SetEmbeddingDirectly(path, "C:\\music\\a.mp3", [0.6f, 0.8f], "test-model");

        using var store = new LibraryStore(path);
        var row = Assert.Single(store.GetEmbeddedRows("test-model"));

        Assert.Equal("C:\\music\\a.mp3", row.Path);
        Assert.Equal([0.6f, 0.8f], row.Vector);
    }

    [Fact]
    public void TheVersionIsStampedAfterUpgrading()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\a.mp3", "T", "A"));
        Assert.Equal(1, UserVersion(path));

        using (var store = new LibraryStore(path)) { }

        Assert.Equal(2, UserVersion(path));
    }

    /// <summary>
    /// The trap that makes a version-driven migration dangerous: a database whose user_version
    /// already claims to be current but whose columns are not. Left to the version number, the
    /// upgrade is skipped and every later query dies on "no such column: source" — a broken
    /// library rather than a failed upgrade.
    /// </summary>
    [Fact]
    public void ColumnsAreAddedEvenIfTheVersionAlreadyClaimsToBeCurrent()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\a.mp3", "Silent Lucidity", "Queensrÿche"));
        using (var cn = new SqliteConnection($"Data Source={path}"))
        {
            cn.Open();
            Exec(cn, "PRAGMA user_version=2;");   // lies
        }

        using var store = new LibraryStore(path);

        Assert.Contains("source", Columns(path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(SongSource.UserSaved, store.Get("C:\\music\\a.mp3")!.Source);
    }

    /// <summary>The same trap in its other form: a table that exists with no version stamp at all,
    /// which a restore from backup can produce.</summary>
    [Fact]
    public void ColumnsAreAddedWhenTheDatabaseHasNoVersionStamp()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\a.mp3", "Silent Lucidity", "Queensrÿche"));
        using (var cn = new SqliteConnection($"Data Source={path}"))
        {
            cn.Open();
            Exec(cn, "PRAGMA user_version=0;");
        }

        using var store = new LibraryStore(path);

        Assert.Contains("source", Columns(path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Silent Lucidity", store.Get("C:\\music\\a.mp3")!.Title);
    }

    [Fact]
    public void OpeningTwiceIsHarmless()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\a.mp3", "Silent Lucidity", "Queensrÿche"));

        using (var first = new LibraryStore(path)) { }
        using var second = new LibraryStore(path);

        Assert.Single(second.GetAll());
        Assert.Equal(SongSource.UserSaved, second.Get("C:\\music\\a.mp3")!.Source);
    }

    /// <summary>A database built from scratch and one upgraded from v1 must be the same shape, or
    /// the two populations drift and only one of them is ever tested.</summary>
    [Fact]
    public void AFreshDatabaseHasTheSameShapeAsAnUpgradedOne()
    {
        var fresh = NewDbPath();
        var upgraded = NewDbPath();
        CreateV1(upgraded, ("C:\\music\\a.mp3", "T", "A"));

        using (var a = new LibraryStore(fresh)) { }
        using (var b = new LibraryStore(upgraded)) { }

        Assert.Equal(Columns(fresh), Columns(upgraded));
        Assert.Equal(UserVersion(fresh), UserVersion(upgraded));
    }

    /// <summary>Harvested rows still round-trip after the upgrade — the column has to be usable,
    /// not merely present.</summary>
    [Fact]
    public void HarvestedSongsCanBeStoredAndFilteredAfterTheUpgrade()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\saved.mp3", "Silent Lucidity", "Queensrÿche"));

        using var store = new LibraryStore(path);
        store.Upsert(new SavedSong("C:\\music\\harvested.mp3", "Bittersweet", "Chilltrax",
            "Chilltrax", "mp3", DateTimeOffset.UtcNow, Source: SongSource.Harvested));

        Assert.Equal(2, store.GetAll().Count);
        Assert.Equal("C:\\music\\saved.mp3", Assert.Single(store.GetAll(SongSource.UserSaved)).Path);
        Assert.Equal("C:\\music\\harvested.mp3", Assert.Single(store.GetAll(SongSource.Harvested)).Path);
    }

    /// <summary>#31's repair writes to this table, so it has to work on an upgraded database too.</summary>
    [Fact]
    public void TrackNamesCanBeRepairedAfterTheUpgrade()
    {
        var path = NewDbPath();
        CreateV1(path, ("C:\\music\\a.mp3", "Breaking the Silence", "Queensr\uFFFDche"));

        using var store = new LibraryStore(path);
        store.SetTrackNames("C:\\music\\a.mp3", "Breaking the Silence", "Queensrÿche");

        var song = store.Get("C:\\music\\a.mp3")!;
        Assert.Equal("Queensrÿche", song.Artist);
        Assert.Equal(SongSource.UserSaved, song.Source);   // the repair must not change provenance
    }
}
