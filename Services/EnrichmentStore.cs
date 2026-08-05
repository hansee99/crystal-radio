using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RadioPlayer.Services;

/// <summary>
/// The only class that touches the enrichment SQLite database (under %LocalAppData%).
/// Keyed by stationuuid. Thread-safe: a single connection guarded by a lock — enrichment
/// runs on background threads, so all access is serialized here.
/// </summary>
public sealed class EnrichmentStore : IDisposable
{
    // Bump this when the schema changes; see EnsureSchema for the migration path.
    // v1: Phase 1 (description/facets/source). v2: Phase 2 vectors.
    // v3: the fields that make a cached station playable without the directory (#26).
    private const int SchemaVersion = 3;

    // Descriptions older than this are re-enriched on next encounter.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    private readonly object _lock = new();
    private readonly SqliteConnection _connection;

    public EnrichmentStore(string? databasePath = null)
    {
        var path = databasePath ?? DefaultDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _connection.Open();

        Execute("PRAGMA busy_timeout=3000;");
        // WAL lets the seed tool and a running app write the same DB without "database is
        // locked" (each process still uses its own single connection + lock internally).
        Execute("PRAGMA journal_mode=WAL;");
        EnsureSchema();
    }

    public static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioPlayer", "enrichment.db");

    public EnrichmentRecord? Get(string stationUuid)
    {
        if (string.IsNullOrWhiteSpace(stationUuid)) return null;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT description, facets, source, enriched_at
                FROM stations WHERE stationuuid = $uuid;
                """;
            cmd.Parameters.AddWithValue("$uuid", stationUuid);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new EnrichmentRecord(
                stationUuid,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                ParseSource(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
    }

    public void Upsert(EnrichmentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            // embedding / embedding_model are written separately by SetEmbedding.
            //
            // COALESCE on the playable fields, not plain assignment: a re-enrichment that only has a
            // uuid and a description (the seed tool, a staleness refresh) would otherwise blank a
            // url the app had already cached, quietly emptying the offline catalog one row at a time.
            // A null here means "nothing new to say", never "clear it".
            cmd.CommandText = """
                INSERT INTO stations
                    (stationuuid, description, facets, source, enriched_at,
                     name, url, codec, bitrate, country)
                VALUES ($uuid, $desc, $facets, $source, $at,
                        $name, $url, $codec, $bitrate, $country)
                ON CONFLICT(stationuuid) DO UPDATE SET
                    description = excluded.description,
                    facets      = excluded.facets,
                    source      = excluded.source,
                    enriched_at = excluded.enriched_at,
                    name        = COALESCE(excluded.name,    name),
                    url         = COALESCE(excluded.url,     url),
                    codec       = COALESCE(excluded.codec,   codec),
                    bitrate     = COALESCE(excluded.bitrate, bitrate),
                    country     = COALESCE(excluded.country, country);
                """;
            cmd.Parameters.AddWithValue("$uuid", record.StationUuid);
            cmd.Parameters.AddWithValue("$desc", record.Description);
            cmd.Parameters.AddWithValue("$facets", (object?)record.Facets ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", SourceToString(record.Source));
            cmd.Parameters.AddWithValue("$at", record.EnrichedAt.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$name", (object?)record.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$url", (object?)record.Url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$codec", (object?)record.Codec ?? DBNull.Value);
            // 0 means "unknown" from the record's default, so send NULL and let COALESCE keep any
            // bitrate already stored.
            cmd.Parameters.AddWithValue("$bitrate", record.Bitrate > 0 ? record.Bitrate : DBNull.Value);
            cmd.Parameters.AddWithValue("$country", (object?)record.Country ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Refresh just the directory-supplied playable fields on a row that already exists, leaving
    /// the description and its embedding — the parts that cost money — untouched.
    /// <para>
    /// This is deliberately NOT gated on staleness, because it costs nothing: the values arrive in
    /// the same Radio Browser response the search already made, so there is no fetch and no LLM
    /// call. Without it, every row cached before #26 would sit with a NULL url until its
    /// description aged out 30 days later, and the offline catalog would stay empty for exactly
    /// that long on machines that already have a full cache.
    /// </para>
    /// UPDATE only, never INSERT: a station nobody has described yet is the enrichment path's job.
    /// A NULL argument means "no news", so it keeps whatever is stored.
    /// </summary>
    public void TopUpPlayableFields(string stationUuid, string? name, string? url,
        string? codec, int bitrate, string? country)
    {
        if (string.IsNullOrWhiteSpace(stationUuid))
            return;
        if (name is null && url is null && codec is null && bitrate <= 0 && country is null)
            return;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE stations SET
                    name    = COALESCE($name,    name),
                    url     = COALESCE($url,     url),
                    codec   = COALESCE($codec,   codec),
                    bitrate = COALESCE($bitrate, bitrate),
                    country = COALESCE($country, country)
                WHERE stationuuid = $uuid;
                """;
            cmd.Parameters.AddWithValue("$uuid", stationUuid);
            cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$codec", (object?)codec ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bitrate", bitrate > 0 ? bitrate : DBNull.Value);
            cmd.Parameters.AddWithValue("$country", (object?)country ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>True when a record is missing or past its freshness window.</summary>
    public bool IsStale(EnrichmentRecord? record) =>
        record is null || DateTimeOffset.UtcNow - record.EnrichedAt > StaleAfter;

    // --- Phase 2: vectors ----------------------------------------------------

    /// <summary>Store an L2-normalized embedding (and the model it came from) for a station.</summary>
    public void SetEmbedding(string stationUuid, float[] vector, string model)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE stations SET embedding = $emb, embedding_model = $model
                WHERE stationuuid = $uuid;
                """;
            cmd.Parameters.AddWithValue("$emb", blob);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$uuid", stationUuid);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Enriched rows that have a description but no embedding for <paramref name="model"/> (backfill input).</summary>
    public IReadOnlyList<StationDescriptionRow> GetRowsNeedingEmbedding(string model)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT stationuuid, description FROM stations
                WHERE description <> ''
                  AND (embedding IS NULL OR embedding_model IS NULL OR embedding_model <> $model);
                """;
            cmd.Parameters.AddWithValue("$model", model);

            var rows = new List<StationDescriptionRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new StationDescriptionRow(reader.GetString(0), reader.GetString(1)));
            return rows;
        }
    }

    /// <summary>Load all rows embedded with <paramref name="model"/> for in-memory cosine search.</summary>
    public IReadOnlyList<EmbeddedStationRow> GetEmbeddedRows(string model)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT stationuuid, description, embedding FROM stations
                WHERE embedding IS NOT NULL AND embedding_model = $model;
                """;
            cmd.Parameters.AddWithValue("$model", model);

            var rows = new List<EmbeddedStationRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var blob = (byte[])reader[2];
                var vector = new float[blob.Length / sizeof(float)];
                Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
                rows.Add(new EmbeddedStationRow(reader.GetString(0), reader.GetString(1), vector));
            }
            return rows;
        }
    }

    /// <summary>
    /// Cached rows that are both matchable and playable — the offline catalog (#26). Requires a
    /// vector for the current model AND a stream url, because either alone is useless: a
    /// description with no url can't be played, a url with no vector can't be matched to a vibe.
    ///
    /// <para>Rows enriched before v3 have no url and are silently absent. That is the cold-start
    /// case in the issue: the catalog fills as normal searches flow through enrichment.</para>
    /// </summary>
    public IReadOnlyList<PlayableStationRow> GetPlayableRows(string model)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT stationuuid, description, embedding, name, url, codec, bitrate, country
                FROM stations
                WHERE embedding IS NOT NULL AND embedding_model = $model
                  AND url IS NOT NULL AND url <> ''
                  AND name IS NOT NULL AND name <> '';
                """;
            cmd.Parameters.AddWithValue("$model", model);

            var rows = new List<PlayableStationRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var blob = (byte[])reader[2];
                var vector = new float[blob.Length / sizeof(float)];
                Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);

                rows.Add(new PlayableStationRow(
                    StationUuid: reader.GetString(0),
                    Description: reader.GetString(1),
                    Vector: vector,
                    Name: reader.GetString(3),
                    Url: reader.GetString(4),
                    Codec: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Bitrate: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    Country: reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
            return rows;
        }
    }

    /// <summary>
    /// Columns added after v1, each with the SQL to add it. v3's five make the catalog playable
    /// without the directory (#26) — a description and a vector are useless offline if turning a
    /// uuid into a stream still needs Radio Browser.
    /// </summary>
    private static readonly (string Column, string Ddl)[] AddedColumns =
    [
        ("embedding",       "ALTER TABLE stations ADD COLUMN embedding BLOB"),
        ("embedding_model", "ALTER TABLE stations ADD COLUMN embedding_model TEXT"),
        ("name",            "ALTER TABLE stations ADD COLUMN name TEXT"),
        ("url",             "ALTER TABLE stations ADD COLUMN url TEXT"),
        ("codec",           "ALTER TABLE stations ADD COLUMN codec TEXT"),
        ("bitrate",         "ALTER TABLE stations ADD COLUMN bitrate INTEGER"),
        ("country",         "ALTER TABLE stations ADD COLUMN country TEXT"),
    ];

    /// <summary>
    /// Creates the table on a fresh database, and adds whatever columns an existing one is missing.
    ///
    /// <para><b>Driven by the columns actually present, not by user_version.</b> The previous version
    /// used <c>CREATE TABLE IF NOT EXISTS</c> and then bumped the version — which works only while
    /// every migration is structurally a no-op, as v1→v2 was (the vector columns were reserved in
    /// v1). Adding v3's columns that way would have done nothing to an existing table while still
    /// recording the upgrade, leaving a database permanently marked as migrated with the columns
    /// absent and every insert against them failing. This database holds thousands of descriptions
    /// and embeddings that cost real money to produce, so the migration reads the truth from
    /// <c>PRAGMA table_info</c> instead of trusting a number.</para>
    /// </summary>
    private void EnsureSchema()
    {
        lock (_lock)
        {
            // Only the ORIGINAL v1 shape — everything since is an ALTER below, so a fresh database
            // and an upgraded one end up structurally identical.
            Execute("""
                CREATE TABLE IF NOT EXISTS stations (
                    stationuuid     TEXT PRIMARY KEY NOT NULL,
                    description     TEXT NOT NULL,
                    facets          TEXT,
                    source          TEXT NOT NULL,
                    enriched_at     TEXT NOT NULL
                );
                """);

            var present = ExistingColumns();
            foreach (var (column, ddl) in AddedColumns)
                if (!present.Contains(column))
                    Execute(ddl);

            Execute($"PRAGMA user_version={SchemaVersion};");
        }
    }

    /// <summary>The column names the stations table actually has right now.</summary>
    private HashSet<string> ExistingColumns()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(stations);";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));   // 1 = name
        return columns;
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? ExecuteScalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string SourceToString(EnrichmentSource s) => s switch
    {
        EnrichmentSource.Homepage => "homepage",
        EnrichmentSource.Web => "web",
        _ => "tags-only"
    };

    private static EnrichmentSource ParseSource(string s) => s switch
    {
        "homepage" => EnrichmentSource.Homepage,
        "web" => EnrichmentSource.Web,
        _ => EnrichmentSource.TagsOnly
    };

    public void Dispose() => _connection.Dispose();
}
