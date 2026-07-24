using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RadioPlayer.Services;

/// <summary>A song saved to the local library, with its AI-derived metadata (Phase C).</summary>
public sealed record SavedSong(
    string Path,
    string Title,
    string Artist,
    string? Station,
    string Codec,
    DateTimeOffset SavedAt,
    string? Description = null,
    string? FacetsJson = null);

/// <summary>One embedded library row for in-memory cosine search (Phase D).</summary>
public sealed record SavedSongVector(string Path, string Title, string Artist, string? Description, float[] Vector);

/// <summary>
/// The only class that touches the local song-library SQLite database (library.db under
/// %LocalAppData%\RadioPlayer) — the index behind the future offline/AI-curated player. Keyed
/// by the saved file path. Deliberately separate from the station enrichment DB: different
/// domain, different lifecycle. Thread-safe via a single connection guarded by a lock, matching
/// <see cref="EnrichmentStore"/> (enrichment/embedding run on background threads).
/// </summary>
public sealed class LibraryStore : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly object _lock = new();
    private readonly SqliteConnection _connection;

    public LibraryStore(string? databasePath = null)
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
        Execute("PRAGMA journal_mode=WAL;");
        EnsureSchema();
    }

    public static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioPlayer", "library.db");

    /// <summary>Insert (or refresh core fields of) a saved song. Description/facets/embedding
    /// are filled in later by the enrichment pass and left untouched here on conflict.</summary>
    public void Upsert(SavedSong song)
    {
        ArgumentNullException.ThrowIfNull(song);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO songs (path, title, artist, station, codec, saved_at, description, facets)
                VALUES ($path, $title, $artist, $station, $codec, $at, $desc, $facets)
                ON CONFLICT(path) DO UPDATE SET
                    title   = excluded.title,
                    artist  = excluded.artist,
                    station = excluded.station,
                    codec   = excluded.codec;
                """;
            cmd.Parameters.AddWithValue("$path", song.Path);
            cmd.Parameters.AddWithValue("$title", song.Title);
            cmd.Parameters.AddWithValue("$artist", song.Artist);
            cmd.Parameters.AddWithValue("$station", (object?)song.Station ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$codec", song.Codec);
            cmd.Parameters.AddWithValue("$at", song.SavedAt.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$desc", (object?)song.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$facets", (object?)song.FacetsJson ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Store the AI-derived description + facets for a song (leaves the embedding alone).</summary>
    public void SetEnrichment(string path, string description, string? facetsJson)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE songs SET description = $desc, facets = $facets WHERE path = $path;";
            cmd.Parameters.AddWithValue("$desc", description);
            cmd.Parameters.AddWithValue("$facets", (object?)facetsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Store an L2-normalized embedding (and the model it came from) for a song.</summary>
    public void SetEmbedding(string path, float[] vector, string model)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE songs SET embedding = $emb, embedding_model = $model WHERE path = $path;";
            cmd.Parameters.AddWithValue("$emb", blob);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    public SavedSong? Get(string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT path, title, artist, station, codec, saved_at, description, facets
                FROM songs WHERE path = $path;
                """;
            cmd.Parameters.AddWithValue("$path", path);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadSong(reader) : null;
        }
    }

    /// <summary>All library rows, newest first (for reconciliation and the Phase D library view).</summary>
    public IReadOnlyList<SavedSong> GetAll()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT path, title, artist, station, codec, saved_at, description, facets
                FROM songs ORDER BY saved_at DESC;
                """;
            var songs = new List<SavedSong>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                songs.Add(ReadSong(reader));
            return songs;
        }
    }

    public void Remove(string path)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM songs WHERE path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Rows with a description but no current-model embedding yet (backfill input).</summary>
    public IReadOnlyList<(string Path, string Description)> GetRowsNeedingEmbedding(string model)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT path, description FROM songs
                WHERE description IS NOT NULL AND description <> ''
                  AND (embedding IS NULL OR embedding_model IS NULL OR embedding_model <> $model);
                """;
            cmd.Parameters.AddWithValue("$model", model);

            var rows = new List<(string, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
            return rows;
        }
    }

    /// <summary>Load all rows embedded with <paramref name="model"/> for in-memory cosine search (Phase D).</summary>
    public IReadOnlyList<SavedSongVector> GetEmbeddedRows(string model)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT path, title, artist, description, embedding FROM songs
                WHERE embedding IS NOT NULL AND embedding_model = $model;
                """;
            cmd.Parameters.AddWithValue("$model", model);

            var rows = new List<SavedSongVector>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var blob = (byte[])reader[4];
                var vector = new float[blob.Length / sizeof(float)];
                Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
                rows.Add(new SavedSongVector(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), vector));
            }
            return rows;
        }
    }

    private static SavedSong ReadSong(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7));

    private void EnsureSchema()
    {
        lock (_lock)
        {
            var version = Convert.ToInt64(ExecuteScalar("PRAGMA user_version;"));
            if (version >= SchemaVersion)
                return;

            Execute("""
                CREATE TABLE IF NOT EXISTS songs (
                    path            TEXT PRIMARY KEY NOT NULL,
                    title           TEXT NOT NULL,
                    artist          TEXT NOT NULL,
                    station         TEXT,
                    codec           TEXT NOT NULL,
                    saved_at        TEXT NOT NULL,
                    description     TEXT,        -- AI-derived song/artist profile
                    facets          TEXT,        -- JSON: genres/moods/era
                    embedding       BLOB,        -- L2-normalized float32 vector
                    embedding_model TEXT         -- model id the vector came from
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

    public void Dispose() => _connection.Dispose();
}
