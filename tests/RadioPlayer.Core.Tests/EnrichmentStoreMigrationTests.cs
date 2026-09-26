using System.IO;
using Microsoft.Data.Sqlite;
using RadioPlayer.Services;
using Xunit;

namespace RadioPlayer.Tests;

/// <summary>
/// The v1/v2 → v3 migration of enrichment.db, tested against databases built in the OLD shapes.
///
/// <para>Held to this standard because of what is in that file. On one real machine: 2,448 enriched
/// stations, every one embedded — descriptions and vectors that cost actual LLM spend and hours to
/// produce, and which no amount of retrying regenerates for free.</para>
///
/// <para>The specific trap being guarded: the previous EnsureSchema did
/// <c>CREATE TABLE IF NOT EXISTS</c> and then bumped <c>user_version</c>. That works only while
/// every migration is structurally a no-op, as v1→v2 was. Adding v3's columns the same way would
/// have done nothing to an existing table while still recording the upgrade — leaving the database
/// permanently marked as migrated with the columns missing, and every insert against them failing.
/// So the migration reads <c>PRAGMA table_info</c> rather than trusting the version number, and
/// these tests build the old shapes by hand to prove it.</para>
/// </summary>
public sealed class EnrichmentStoreMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crystal-migrate", Guid.NewGuid().ToString("n"));

    public EnrichmentStoreMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // release the file so the directory can go
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private string NewDbPath() => Path.Combine(_dir, $"{Guid.NewGuid():n}.db");

    /// <summary>Builds the ORIGINAL v1 table — description columns only, no vector columns.</summary>
    private static void CreateV1(string path, params (string Uuid, string Description)[] rows)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        Exec(cn, """
            CREATE TABLE stations (
                stationuuid TEXT PRIMARY KEY NOT NULL,
                description TEXT NOT NULL,
                facets      TEXT,
                source      TEXT NOT NULL,
                enriched_at TEXT NOT NULL
            );
            """);
        Exec(cn, "PRAGMA user_version=1;");
        foreach (var (uuid, description) in rows)
            Insert(cn, uuid, description);
    }

    /// <summary>Builds the v2 table — v1 plus the reserved vector columns, which is the shape every
    /// existing installation is actually in.</summary>
    private static void CreateV2(string path, params (string Uuid, string Description)[] rows)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        Exec(cn, """
            CREATE TABLE stations (
                stationuuid     TEXT PRIMARY KEY NOT NULL,
                description     TEXT NOT NULL,
                facets          TEXT,
                source          TEXT NOT NULL,
                enriched_at     TEXT NOT NULL,
                embedding       BLOB,
                embedding_model TEXT
            );
            """);
        Exec(cn, "PRAGMA user_version=2;");
        foreach (var (uuid, description) in rows)
            Insert(cn, uuid, description);
    }

    // 'homepage' lowercase, because that is the literal the store's own SourceToString writes and
    // ParseSource matches case-sensitively. The point of these fixtures is to be byte-identical to
    // what a real pre-v3 database holds, so don't "tidy" the casing.
    private static void Insert(SqliteConnection cn, string uuid, string description)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO stations (stationuuid, description, facets, source, enriched_at)
            VALUES ($u, $d, '{"genres":["rock"]}', 'homepage', '2026-08-01T10:00:00.0000000+00:00');
            """;
        cmd.Parameters.AddWithValue("$u", uuid);
        cmd.Parameters.AddWithValue("$d", description);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void SetEmbeddingDirectly(string path, string uuid, float[] vector, string model)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "UPDATE stations SET embedding=$e, embedding_model=$m WHERE stationuuid=$u;";
        cmd.Parameters.AddWithValue("$e", blob);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$u", uuid);
        cmd.ExecuteNonQuery();
    }

    private static HashSet<string> Columns(string path)
    {
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(stations);";
        using var reader = cmd.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) names.Add(reader.GetString(1));
        return names;
    }

    // --- Existing data survives, and the columns actually appear -------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UpgradingAnOlderDatabaseKeepsEveryDescription(int fromVersion)
    {
        var path = NewDbPath();
        var rows = new[] { ("uuid-a", "Deep house all night"), ("uuid-b", "Bavarian talk radio") };
        if (fromVersion == 1) CreateV1(path, rows); else CreateV2(path, rows);

        using (var store = new EnrichmentStore(path))
        {
            // The point of the exercise: descriptions cost money, so none may be lost.
            Assert.Equal("Deep house all night", store.Get("uuid-a")!.Description);
            Assert.Equal("Bavarian talk radio", store.Get("uuid-b")!.Description);
            Assert.Equal(EnrichmentSource.Homepage, store.Get("uuid-a")!.Source);
            Assert.Contains("rock", store.Get("uuid-a")!.Facets);
        }

        var columns = Columns(path);
        foreach (var added in new[] { "embedding", "embedding_model", "name", "url", "codec", "bitrate", "country" })
            Assert.Contains(added, columns);
    }

    /// <summary>An existing embedding is a vector someone paid for in compute. The migration must
    /// not disturb it.</summary>
    [Fact]
    public void UpgradingKeepsExistingEmbeddings()
    {
        var path = NewDbPath();
        CreateV2(path, ("uuid-a", "Deep house all night"));
        SetEmbeddingDirectly(path, "uuid-a", [0.1f, 0.2f, 0.3f], "all-MiniLM-L6-v2");

        using var store = new EnrichmentStore(path);

        var row = Assert.Single(store.GetEmbeddedRows("all-MiniLM-L6-v2"));
        Assert.Equal("uuid-a", row.StationUuid);
        Assert.Equal([0.1f, 0.2f, 0.3f], row.Vector);
    }

    /// <summary>
    /// The regression that motivated the rewrite: a v2 database whose user_version already claims
    /// to be current. If the migration trusted the number it would skip the ALTERs and leave the
    /// columns missing forever.
    /// </summary>
    [Fact]
    public void ColumnsAreAddedEvenIfTheVersionAlreadyClaimsToBeCurrent()
    {
        var path = NewDbPath();
        CreateV2(path, ("uuid-a", "Deep house all night"));
        using (var cn = new SqliteConnection($"Data Source={path}"))
        {
            cn.Open();
            Exec(cn, "PRAGMA user_version=3;");   // lies about being migrated
        }

        using var store = new EnrichmentStore(path);

        Assert.Contains("url", Columns(path));
        // And it is genuinely usable, not merely present.
        store.Upsert(new EnrichmentRecord("uuid-b", "Ambient", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "Chill FM", Url: "http://x/stream", Codec: "Mp3"));
        Assert.Equal("Ambient", store.Get("uuid-b")!.Description);
    }

    [Fact]
    public void OpeningTwiceIsHarmless()
    {
        var path = NewDbPath();
        CreateV2(path, ("uuid-a", "Deep house all night"));

        using (var first = new EnrichmentStore(path)) { }
        using (var second = new EnrichmentStore(path))
            Assert.Equal("Deep house all night", second.Get("uuid-a")!.Description);

        // ALTER TABLE ADD COLUMN twice would throw "duplicate column name" — it must be skipped.
        Assert.Contains("url", Columns(path));
    }

    [Fact]
    public void AFreshDatabaseHasTheSameShapeAsAnUpgradedOne()
    {
        var fresh = NewDbPath();
        var upgraded = NewDbPath();
        CreateV2(upgraded, ("uuid-a", "x"));

        using (var a = new EnrichmentStore(fresh)) { }
        using (var b = new EnrichmentStore(upgraded)) { }

        // A divergence here is how "works on my machine, broken after upgrade" starts.
        Assert.Equal(Columns(fresh).OrderBy(c => c), Columns(upgraded).OrderBy(c => c));
    }

    // --- The playable fields ------------------------------------------------------------------

    [Fact]
    public void APlayableRowNeedsBothAVectorAndAUrl()
    {
        var path = NewDbPath();
        using var store = new EnrichmentStore(path);

        // A url but no vector: can't be matched to a vibe.
        store.Upsert(new EnrichmentRecord("no-vector", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "N", Url: "http://x/1"));
        // A vector but no url: can't be played.
        store.Upsert(new EnrichmentRecord("no-url", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow));
        store.SetEmbedding("no-url", [1f, 0f], "m");
        // Both.
        store.Upsert(new EnrichmentRecord("complete", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "N", Url: "http://x/3", Codec: "Aac", Bitrate: 128, Country: "AT"));
        store.SetEmbedding("complete", [0f, 1f], "m");

        var row = Assert.Single(store.GetPlayableRows("m"));
        Assert.Equal("complete", row.StationUuid);
        Assert.Equal("http://x/3", row.Url);
        Assert.Equal("Aac", row.Codec);
        Assert.Equal(128, row.Bitrate);
        Assert.Equal("AT", row.Country);
    }

    /// <summary>
    /// Rows enriched before v3 have no url, so the offline catalog is empty until normal searching
    /// refills it. That cold start is expected and stated in #26 — what matters is that it is empty
    /// rather than broken.
    /// </summary>
    [Fact]
    public void RowsFromBeforeTheUpgradeAreAbsentFromTheOfflineCatalogRatherThanBroken()
    {
        var path = NewDbPath();
        CreateV2(path, ("legacy", "Enriched long ago"));
        SetEmbeddingDirectly(path, "legacy", [1f, 0f], "m");

        using var store = new EnrichmentStore(path);

        Assert.Empty(store.GetPlayableRows("m"));                  // no url yet
        Assert.Single(store.GetEmbeddedRows("m"));                  // but still fully usable online
        Assert.Equal("Enriched long ago", store.Get("legacy")!.Description);
    }

    /// <summary>
    /// The quiet way to empty the catalog: a refresh that only knows the uuid and description —
    /// the seed tool, or a staleness re-enrichment — writing NULL over a url the app already had.
    /// </summary>
    [Fact]
    public void ARefreshWithoutPlayableFieldsDoesNotBlankThem()
    {
        var path = NewDbPath();
        using var store = new EnrichmentStore(path);
        store.Upsert(new EnrichmentRecord("uuid-a", "First pass", null, EnrichmentSource.Homepage,
            DateTimeOffset.UtcNow, Name: "Chill FM", Url: "http://x/stream", Codec: "Mp3",
            Bitrate: 128, Country: "DE"));
        store.SetEmbedding("uuid-a", [1f, 0f], "m");

        // Re-enriched from the uuid alone, as a staleness refresh does.
        store.Upsert(new EnrichmentRecord("uuid-a", "Better description", null,
            EnrichmentSource.TagsOnly, DateTimeOffset.UtcNow));

        var row = Assert.Single(store.GetPlayableRows("m"));
        Assert.Equal("Better description", row.Description);   // the description DID update
        Assert.Equal("http://x/stream", row.Url);              // the url survived
        Assert.Equal("Chill FM", row.Name);
        Assert.Equal(128, row.Bitrate);
        Assert.Equal("DE", row.Country);
    }

    [Fact]
    public void PlayableFieldsCanBeFilledInByALaterEnrichment()
    {
        var path = NewDbPath();
        using var store = new EnrichmentStore(path);
        store.Upsert(new EnrichmentRecord("uuid-a", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow));
        store.SetEmbedding("uuid-a", [1f, 0f], "m");
        Assert.Empty(store.GetPlayableRows("m"));

        // Seen again through a normal search, this time with the candidate data attached.
        store.Upsert(new EnrichmentRecord("uuid-a", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "Chill FM", Url: "http://x/stream"));

        Assert.Equal("http://x/stream", Assert.Single(store.GetPlayableRows("m")).Url);
    }

    // --- TopUpPlayableFields: the repair path for rows cached before #26 ------------------

    /// <summary>
    /// The case that decides whether #26 is any use on a machine that already has a full cache:
    /// 2,563 rows with fresh descriptions and no url. If the url only arrived with a description
    /// refresh, they'd stay unplayable offline for the whole 30-day staleness window.
    /// </summary>
    [Fact]
    public void ToppingUpMakesALegacyRowPlayableWithoutReEnriching()
    {
        var path = NewDbPath();
        CreateV2(path, ("legacy", "Described and paid for months ago"));
        SetEmbeddingDirectly(path, "legacy", [1f, 0f], "m");

        using var store = new EnrichmentStore(path);
        var before = store.Get("legacy")!;
        Assert.Empty(store.GetPlayableRows("m"));

        store.TopUpPlayableFields("legacy", "Chill FM", "http://x/stream", "Mp3", 128, "DE");

        var row = Assert.Single(store.GetPlayableRows("m"));
        Assert.Equal("http://x/stream", row.Url);
        Assert.Equal("Chill FM", row.Name);
        Assert.Equal(128, row.Bitrate);
        // The expensive parts are untouched — that's the whole point of a separate top-up.
        Assert.Equal("Described and paid for months ago", row.Description);
        Assert.Equal(before.EnrichedAt, store.Get("legacy")!.EnrichedAt);
        Assert.Equal(before.Source, store.Get("legacy")!.Source);
        Assert.Equal([1f, 0f], row.Vector);
    }

    [Fact]
    public void ToppingUpWithNothingNewKeepsWhatIsStored()
    {
        var path = NewDbPath();
        using var store = new EnrichmentStore(path);
        store.Upsert(new EnrichmentRecord("uuid-a", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "Chill FM", Url: "http://x/stream", Codec: "Mp3",
            Bitrate: 128, Country: "DE"));
        store.SetEmbedding("uuid-a", [1f, 0f], "m");

        store.TopUpPlayableFields("uuid-a", null, null, null, 0, null);

        var row = Assert.Single(store.GetPlayableRows("m"));
        Assert.Equal("http://x/stream", row.Url);
        Assert.Equal("Chill FM", row.Name);
        Assert.Equal(128, row.Bitrate);
        Assert.Equal("DE", row.Country);
    }

    /// <summary>
    /// The realistic top-up: a directory response that has a name and url but no codec or country.
    /// Each field has to be COALESCEd on its own, or the fields this particular response happens to
    /// be missing get wiped by it.
    /// </summary>
    [Fact]
    public void ToppingUpOnlySomeFieldsLeavesTheOthersAlone()
    {
        var path = NewDbPath();
        using var store = new EnrichmentStore(path);
        store.Upsert(new EnrichmentRecord("uuid-a", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "Chill FM", Url: "http://old/stream", Codec: "Mp3",
            Bitrate: 128, Country: "DE"));
        store.SetEmbedding("uuid-a", [1f, 0f], "m");

        store.TopUpPlayableFields("uuid-a", "Chill FM", "http://new/stream", null, 0, null);

        var row = Assert.Single(store.GetPlayableRows("m"));
        Assert.Equal("http://new/stream", row.Url);
        Assert.Equal("Mp3", row.Codec);
        Assert.Equal(128, row.Bitrate);
        Assert.Equal("DE", row.Country);
    }

    /// <summary>A url_resolved that has moved should win — the directory is the authority.</summary>
    [Fact]
    public void ToppingUpReplacesAStaleUrl()
    {
        var path = NewDbPath();
        using var store = new EnrichmentStore(path);
        store.Upsert(new EnrichmentRecord("uuid-a", "d", null, EnrichmentSource.TagsOnly,
            DateTimeOffset.UtcNow, Name: "Chill FM", Url: "http://old/stream"));
        store.SetEmbedding("uuid-a", [1f, 0f], "m");

        store.TopUpPlayableFields("uuid-a", "Chill FM", "http://new/stream", null, 0, null);

        Assert.Equal("http://new/stream", Assert.Single(store.GetPlayableRows("m")).Url);
    }

    /// <summary>
    /// UPDATE, not upsert: a station nobody has described belongs to the enrichment path, and a
    /// row with a url but no description would be a catalog entry with nothing to match against.
    /// </summary>
    [Fact]
    public void ToppingUpAnUnknownStationCreatesNothing()
    {
        var path = NewDbPath();
        using var store = new EnrichmentStore(path);

        store.TopUpPlayableFields("never-seen", "Chill FM", "http://x/stream", "Mp3", 128, "DE");

        Assert.Null(store.Get("never-seen"));
        Assert.Empty(store.GetPlayableRows("m"));
    }
}
