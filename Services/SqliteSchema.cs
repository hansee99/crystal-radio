using Microsoft.Data.Sqlite;

namespace RadioPlayer.Services;

/// <summary>
/// Schema helpers shared by the two SQLite stores.
///
/// <para>Both migrate the same way, and for the same reason: <c>PRAGMA user_version</c> is a claim
/// about a database, not a fact about it. A version-driven migration that trusts the number will
/// happily mark a database as current while the columns it promised are missing, and every query
/// after that dies on "no such column" — which is a broken library, not a failed upgrade. Asking
/// the database what it actually has costs one pragma and cannot be wrong.</para>
/// </summary>
internal static class SqliteSchema
{
    /// <summary>Column names on <paramref name="table"/>, case-insensitively.</summary>
    public static HashSet<string> ExistingColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));   // 1 = name
        return columns;
    }

    /// <summary>
    /// Runs each ALTER whose column is absent. The DDL is expected to be
    /// <c>ALTER TABLE … ADD COLUMN …</c>, so this is additive by construction: no existing row is
    /// rewritten and no existing value is read, which is what makes it safe to run against a
    /// database holding data that cost real money to produce.
    /// </summary>
    public static void AddMissingColumns(SqliteConnection connection, string table,
        IEnumerable<(string Column, string Ddl)> columns)
    {
        var present = ExistingColumns(connection, table);
        foreach (var (column, ddl) in columns)
        {
            if (present.Contains(column))
                continue;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
    }
}
