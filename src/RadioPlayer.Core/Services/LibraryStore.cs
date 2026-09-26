using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RadioPlayer.Services;

/// <summary>Distinguishes songs the user explicitly saved from ones DJ-mode harvesting indexed
/// on its own — the "Songs" library UI tab shows only <see cref="UserSaved"/>, while
/// <see cref="SongCurator"/>'s semantic recall (warm-start, curated playlists) draws from both,
/// so harvest history genuinely improves future curation without cluttering the user's list.</summary>
public enum SongSource { UserSaved, Harvested }

/// <summary>A song saved to the local library, with its AI-derived metadata (Phase C).</summary>
public sealed record SavedSong(
    string Path,
    string Title,
    string Artist,
    string? Station,
    string Codec,
    DateTimeOffset SavedAt,
    string? Description = null,
    string? FacetsJson = null,
    SongSource Source = SongSource.UserSaved);

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
    private const int SchemaVersion = 2;

    private readonly object _lock = new();
    private readonly SqliteConnection? _connection;

    /// <summary>
    /// False when the database could not be opened, in which case the index is inert: nothing is
    /// recorded and every read comes back empty.
    ///
    /// <para>Same reason as EnrichmentStore's (#57) — a blocked SQLite provider assembly threw from
    /// an unguarded constructor and took the app's startup with it. The saved FILES are untouched
    /// either way; what is lost is the index over them, so the library list is empty and curation
    /// has nothing to draw on until the database works again.</para>
    /// </summary>
    public bool IsAvailable => _connection is not null;

    public LibraryStore(string? databasePath = null)
    {
        var path = databasePath ?? DefaultDatabasePath();

        try
        {
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
        catch (Exception ex)
        {
            _connection = null;
            AppLog.Error($"[Library] song index unavailable ({path}) — continuing without it; "
                         + "saved files are untouched, but they will not be listed or curated", ex);
        }
    }

    public static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RadioPlayer", "library.db");

    /// <summary>Insert (or refresh core fields of) a saved song. Description/facets/embedding
    /// are filled in later by the enrichment pass and left untouched here on conflict.</summary>
    public void Upsert(SavedSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (_connection is null) return;

        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO songs (path, title, artist, station, codec, saved_at, description, facets, source)
                VALUES ($path, $title, $artist, $station, $codec, $at, $desc, $facets, $source)
                ON CONFLICT(path) DO UPDATE SET
                    title   = excluded.title,
                    artist  = excluded.artist,
                    station = excluded.station,
                    codec   = excluded.codec,
                    source  = excluded.source;
                """;
            cmd.Parameters.AddWithValue("$path", song.Path);
            cmd.Parameters.AddWithValue("$title", song.Title);
            cmd.Parameters.AddWithValue("$artist", song.Artist);
            cmd.Parameters.AddWithValue("$station", (object?)song.Station ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$codec", song.Codec);
            cmd.Parameters.AddWithValue("$at", song.SavedAt.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$desc", (object?)song.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$facets", (object?)song.FacetsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", song.Source.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Store the AI-derived description + facets for a song (leaves the embedding alone).</summary>
    public void SetEnrichment(string path, string description, string? facetsJson)
    {
        if (_connection is null) return;
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE songs SET description = $desc, facets = $facets WHERE path = $path;";
            cmd.Parameters.AddWithValue("$desc", description);
            cmd.Parameters.AddWithValue("$facets", (object?)facetsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Corrects a row's title and artist (#31). Only enrichment calls this, after a repair has been
    /// verified against the surviving characters — the row's path is its identity, so renaming the
    /// track does not disturb the file, the embedding, or anything holding a reference to it.
    /// </summary>
    public void SetTrackNames(string path, string title, string artist)
    {
        if (_connection is null) return;
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE songs SET title = $title, artist = $artist WHERE path = $path;";
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$artist", artist);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Store an L2-normalized embedding (and the model it came from) for a song.</summary>
    public void SetEmbedding(string path, float[] vector, string model)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (_connection is null) return;
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE songs SET embedding = $emb, embedding_model = $model WHERE path = $path;";
            cmd.Parameters.AddWithValue("$emb", blob);
            cmd.Parameters.AddWithValue("$model", model);
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    public SavedSong? Get(string path)
    {
        if (_connection is null) return null;
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT path, title, artist, station, codec, saved_at, description, facets, source
                FROM songs WHERE path = $path;
                """;
            cmd.Parameters.AddWithValue("$path", path);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadSong(reader) : null;
        }
    }

    /// <summary>All library rows, newest first (for reconciliation and the Phase D library view).
    /// Pass <paramref name="source"/> to restrict to user-saved or harvested rows only — the
    /// default (null) draws from everything, which is what recall/curation and reconciliation
    /// want; the "Songs" library UI tab passes <see cref="SongSource.UserSaved"/> so ephemeral
    /// harvested songs don't clutter it.</summary>
    public IReadOnlyList<SavedSong> GetAll(SongSource? source = null)
    {
        if (_connection is null) return [];
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = source is null
                ? """
                  SELECT path, title, artist, station, codec, saved_at, description, facets, source
                  FROM songs ORDER BY saved_at DESC;
                  """
                : """
                  SELECT path, title, artist, station, codec, saved_at, description, facets, source
                  FROM songs WHERE source = $source ORDER BY saved_at DESC;
                  """;
            if (source is not null)
                cmd.Parameters.AddWithValue("$source", source.Value.ToString());
            var songs = new List<SavedSong>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                songs.Add(ReadSong(reader));
            return songs;
        }
    }

    public void Remove(string path)
    {
        if (_connection is null) return;
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM songs WHERE path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Rows with a description but no current-model embedding yet (backfill input).</summary>
    public IReadOnlyList<(string Path, string Description)> GetRowsNeedingEmbedding(string model)
    {
        if (_connection is null) return [];
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
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
        if (_connection is null) return [];
        lock (_lock)
        {
            using var cmd = _connection!.CreateCommand();
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
        r.IsDBNull(7) ? null : r.GetString(7),
        Enum.Parse<SongSource>(r.GetString(8)));

    /// <summary>
    /// Columns added after v1, each with the ALTER that introduces it. Driven by what the database
    /// actually has rather than by <c>user_version</c> — see <see cref="SqliteSchema"/> for why.
    /// </summary>
    private static readonly (string Column, string Ddl)[] AddedColumns =
    [
        // v1→v2: DJ-mode harvesting needs to tell apart songs the user explicitly saved from ones
        // it indexed on its own, without touching any existing row's data — every pre-existing row
        // is, by definition, something the user saved.
        ("source", "ALTER TABLE songs ADD COLUMN source TEXT NOT NULL DEFAULT 'UserSaved';"),
    ];

    private void EnsureSchema()
    {
        lock (_lock)
        {
            // Only the ORIGINAL v1 shape — everything since is an ALTER below, so a fresh database
            // and an upgraded one end up structurally identical.
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

            // Only ever called from the constructor's try, where the connection is open.
            SqliteSchema.AddMissingColumns(_connection!, "songs", AddedColumns);
            Execute($"PRAGMA user_version={SchemaVersion};");
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? ExecuteScalar(string sql)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => _connection?.Dispose();
}
