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
    // v1: Phase 1 (description/facets/source). v2: Phase 2 vectors use the reserved columns.
    private const int SchemaVersion = 2;

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
            // embedding / embedding_model are reserved for Phase 2 — left NULL here.
            cmd.CommandText = """
                INSERT INTO stations (stationuuid, description, facets, source, enriched_at)
                VALUES ($uuid, $desc, $facets, $source, $at)
                ON CONFLICT(stationuuid) DO UPDATE SET
                    description = excluded.description,
                    facets      = excluded.facets,
                    source      = excluded.source,
                    enriched_at = excluded.enriched_at;
                """;
            cmd.Parameters.AddWithValue("$uuid", record.StationUuid);
            cmd.Parameters.AddWithValue("$desc", record.Description);
            cmd.Parameters.AddWithValue("$facets", (object?)record.Facets ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", SourceToString(record.Source));
            cmd.Parameters.AddWithValue("$at", record.EnrichedAt.ToString("o", CultureInfo.InvariantCulture));
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

    private void EnsureSchema()
    {
        lock (_lock)
        {
            var version = Convert.ToInt64(ExecuteScalar("PRAGMA user_version;"));
            if (version >= SchemaVersion)
                return;

            // Fresh DB (version 0) → create with all columns. The Phase 1→2 migration is a
            // no-op structurally: the embedding/embedding_model columns were reserved in v1,
            // so v2 just starts populating them. Bumping user_version records the upgrade.
            Execute("""
                CREATE TABLE IF NOT EXISTS stations (
                    stationuuid     TEXT PRIMARY KEY NOT NULL,
                    description     TEXT NOT NULL,
                    facets          TEXT,
                    source          TEXT NOT NULL,
                    enriched_at     TEXT NOT NULL,
                    embedding       BLOB,        -- Phase 2: L2-normalized float32 vector
                    embedding_model TEXT         -- Phase 2: model id the vector came from
                );
                """);
            Execute($"PRAGMA user_version={SchemaVersion};");
        }
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
