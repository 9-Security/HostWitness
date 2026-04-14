using System.Text;
using Microsoft.Data.Sqlite;
using WinDFIR.Core.Entities;
using WinDFIR.Core.Index;

namespace WinDFIR.Core.Snapshot;

/// <summary>
/// Export and load activity index to/from SQLite for offline query and persistence.
/// </summary>
public static class SqliteIndexPersistence
{
    private const string TableName = "events";
    private const string MetaTableName = "meta";
    private const string ColId = "id";
    private const string ColTimestamp = "timestamp";
    private const string ColCategory = "category";
    private const string ColData = "data";
    private const string CurrentSchemaVersion = "2";

    /// <summary>Rows per multi-INSERT batch (must keep total bound parameters under SQLite limits).</summary>
    private const int ExportBatchRowCount = 120;

    private static string BuildConnectionString(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || dbPath.IndexOf(';') >= 0)
            throw new ArgumentException("Invalid database path.", nameof(dbPath));
        return new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
    }

    /// <summary>Exports all events from the index to a SQLite database file. Overwrites existing file.</summary>
    /// <returns>Number of events written.</returns>
    public static int Export(IActivityIndex index, string dbPath)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        using var conn = new SqliteConnection(BuildConnectionString(dbPath));
        conn.Open();
        CreateSchema(conn);
        using var tx = conn.BeginTransaction();

        var count = 0;
        var batch = new List<ActivityEvent>(ExportBatchRowCount);
        foreach (var e in index.GetEventsByTimeRange(DateTime.MinValue, DateTime.MaxValue))
        {
            batch.Add(e);
            if (batch.Count < ExportBatchRowCount)
                continue;

            count += ExecuteInsertBatch(conn, tx, batch);
            batch.Clear();
        }

        if (batch.Count > 0)
            count += ExecuteInsertBatch(conn, tx, batch);

        tx.Commit();

        return count;
    }

    /// <summary>Loads all events from a SQLite database (created by Export). Returns empty list if file missing or invalid.</summary>
    public static List<ActivityEvent> LoadEvents(string dbPath)
    {
        return LoadEventsByTimeRange(dbPath, DateTime.MinValue, DateTime.MaxValue, category: null);
    }

    /// <summary>Loads events from SQLite filtered by category (full time range).</summary>
    public static List<ActivityEvent> LoadEventsByCategory(string dbPath, string category)
    {
        return LoadEventsByTimeRange(dbPath, DateTime.MinValue, DateTime.MaxValue, category);
    }

    /// <summary>Loads events from SQLite within the given time range (inclusive). Optional category filter.</summary>
    public static List<ActivityEvent> LoadEventsByTimeRange(string dbPath, DateTime startUtc, DateTime endUtc, string? category = null)
    {
        return EnumerateEventsByTimeRange(dbPath, startUtc, endUtc, category).ToList();
    }

    /// <summary>
    /// Streams events from SQLite within the given time range (inclusive). Optional category filter.
    /// Use this for large databases to avoid one-shot allocations.
    /// </summary>
    public static IEnumerable<ActivityEvent> EnumerateEventsByTimeRange(string dbPath, DateTime startUtc, DateTime endUtc, string? category = null)
    {
        if (!File.Exists(dbPath))
            yield break;

        using var conn = new SqliteConnection(BuildConnectionString(dbPath));
        conn.Open();
        EnsureSchema(conn);

        var startStr = startUtc.ToString("O");
        var endStr = endUtc.ToString("O");
        var hasCategoryColumn = HasColumn(conn, TableName, ColCategory);
        var useSqlCategoryFilter = hasCategoryColumn && !string.IsNullOrWhiteSpace(category);
        var sql = useSqlCategoryFilter
            ? $"SELECT [{ColData}] FROM [{TableName}] WHERE [{ColTimestamp}] >= @start AND [{ColTimestamp}] <= @end AND [{ColCategory}] = @category ORDER BY [{ColTimestamp}]"
            : $"SELECT [{ColData}] FROM [{TableName}] WHERE [{ColTimestamp}] >= @start AND [{ColTimestamp}] <= @end ORDER BY [{ColTimestamp}]";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        if (useSqlCategoryFilter)
            cmd.Parameters.AddWithValue("@category", category!.Trim());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var json = reader.GetString(0);
            var ev = SnapshotImporter.ParseEventFromJson(json);
            if (ev != null)
            {
                // Backward compatibility for old DBs without category column.
                if (string.IsNullOrEmpty(category) ||
                    useSqlCategoryFilter ||
                    string.Equals(ev.Category, category, StringComparison.OrdinalIgnoreCase))
                    yield return ev;
            }
        }
    }

    /// <summary>
    /// Loads a single page from SQLite (ordered by timestamp asc). Useful for progressive loading.
    /// </summary>
    public static List<ActivityEvent> LoadEventsPage(
        string dbPath,
        DateTime startUtc,
        DateTime endUtc,
        int offset,
        int limit,
        string? category = null)
    {
        if (!File.Exists(dbPath) || limit <= 0 || offset < 0)
            return new List<ActivityEvent>();

        var list = new List<ActivityEvent>(Math.Min(limit, 2048));
        using var conn = new SqliteConnection(BuildConnectionString(dbPath));
        conn.Open();
        EnsureSchema(conn);

        var startStr = startUtc.ToString("O");
        var endStr = endUtc.ToString("O");
        var hasCategoryColumn = HasColumn(conn, TableName, ColCategory);
        var useSqlCategoryFilter = hasCategoryColumn && !string.IsNullOrWhiteSpace(category);
        var sql = useSqlCategoryFilter
            ? $"SELECT [{ColData}] FROM [{TableName}] WHERE [{ColTimestamp}] >= @start AND [{ColTimestamp}] <= @end AND [{ColCategory}] = @category ORDER BY [{ColTimestamp}] LIMIT @limit OFFSET @offset"
            : $"SELECT [{ColData}] FROM [{TableName}] WHERE [{ColTimestamp}] >= @start AND [{ColTimestamp}] <= @end ORDER BY [{ColTimestamp}] LIMIT @limit OFFSET @offset";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        if (useSqlCategoryFilter)
            cmd.Parameters.AddWithValue("@category", category!.Trim());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var json = reader.GetString(0);
            var ev = SnapshotImporter.ParseEventFromJson(json);
            if (ev == null)
                continue;

            if (string.IsNullOrEmpty(category) ||
                useSqlCategoryFilter ||
                string.Equals(ev.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(ev);
            }
        }

        return list;
    }

    public static int CountEventsByTimeRange(string dbPath, DateTime startUtc, DateTime endUtc, string? category = null)
    {
        if (!File.Exists(dbPath))
            return 0;

        using var conn = new SqliteConnection(BuildConnectionString(dbPath));
        conn.Open();
        EnsureSchema(conn);

        var startStr = startUtc.ToString("O");
        var endStr = endUtc.ToString("O");
        var hasCategoryColumn = HasColumn(conn, TableName, ColCategory);
        var useSqlCategoryFilter = hasCategoryColumn && !string.IsNullOrWhiteSpace(category);
        var sql = useSqlCategoryFilter
            ? $"SELECT COUNT(1) FROM [{TableName}] WHERE [{ColTimestamp}] >= @start AND [{ColTimestamp}] <= @end AND [{ColCategory}] = @category"
            : $"SELECT COUNT(1) FROM [{TableName}] WHERE [{ColTimestamp}] >= @start AND [{ColTimestamp}] <= @end";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@start", startStr);
        cmd.Parameters.AddWithValue("@end", endStr);
        if (useSqlCategoryFilter)
            cmd.Parameters.AddWithValue("@category", category!.Trim());
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static int ExecuteInsertBatch(SqliteConnection conn, SqliteTransaction tx, List<ActivityEvent> batch)
    {
        if (batch.Count == 0)
            return 0;

        var sb = new StringBuilder(batch.Count * 64);
        sb.Append($"INSERT INTO [{TableName}] ([{ColTimestamp}], [{ColCategory}], [{ColData}]) VALUES ");
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append($"(@ts{i},@cat{i},@data{i})");
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sb.ToString();
        for (var i = 0; i < batch.Count; i++)
        {
            var e = batch[i];
            cmd.Parameters.AddWithValue($"@ts{i}", e.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue($"@cat{i}", e.Category ?? string.Empty);
            cmd.Parameters.AddWithValue($"@data{i}", SessionPersistence.SerializeEventToJson(e));
        }

        cmd.ExecuteNonQuery();
        return batch.Count;
    }

    private static bool HasColumn(SqliteConnection conn, string tableName, string columnName)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info([{tableName}])";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // PRAGMA table_info columns:
                // cid, name, type, notnull, dflt_value, pk
                var name = reader.GetString(1);
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    private static string? ReadMeta(SqliteConnection conn, string key)
    {
        if (!TableExists(conn, MetaTableName))
            return null;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT [value] FROM [{MetaTableName}] WHERE [key]=@key LIMIT 1";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void UpsertMeta(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO [{MetaTableName}]([key],[value]) VALUES(@key,@value) ON CONFLICT([key]) DO UPDATE SET [value]=excluded.[value]";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS [{MetaTableName}] (
  [key] TEXT NOT NULL PRIMARY KEY,
  [value] TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS [{TableName}] (
  [{ColId}] INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
  [{ColTimestamp}] TEXT NOT NULL,
  [{ColCategory}] TEXT NOT NULL,
  [{ColData}] TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS [idx_events_timestamp] ON [{TableName}] ([{ColTimestamp}]);
CREATE INDEX IF NOT EXISTS [idx_events_category_timestamp] ON [{TableName}] ([{ColCategory}], [{ColTimestamp}]);
";
        cmd.ExecuteNonQuery();
        UpsertMeta(conn, "schema_version", CurrentSchemaVersion);
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        if (!TableExists(conn, TableName))
        {
            CreateSchema(conn);
            return;
        }

        // Ensure meta table exists.
        using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = $@"CREATE TABLE IF NOT EXISTS [{MetaTableName}] (
  [key] TEXT NOT NULL PRIMARY KEY,
  [value] TEXT NOT NULL
);";
            metaCmd.ExecuteNonQuery();
        }

        var needsMigration = !HasColumn(conn, TableName, ColCategory);
        if (needsMigration)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE [{TableName}] ADD COLUMN [{ColCategory}] TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();
            BackfillCategoryFromJson(conn);
        }

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = $@"
CREATE INDEX IF NOT EXISTS [idx_events_timestamp] ON [{TableName}] ([{ColTimestamp}]);
CREATE INDEX IF NOT EXISTS [idx_events_category_timestamp] ON [{TableName}] ([{ColCategory}], [{ColTimestamp}]);";
            idx.ExecuteNonQuery();
        }

        var schemaVersion = ReadMeta(conn, "schema_version");
        if (string.IsNullOrWhiteSpace(schemaVersion) || schemaVersion != CurrentSchemaVersion || needsMigration)
            UpsertMeta(conn, "schema_version", CurrentSchemaVersion);
    }

    private static void BackfillCategoryFromJson(SqliteConnection conn)
    {
        var rows = new List<(long Id, string Category)>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = $"SELECT [{ColId}], [{ColData}] FROM [{TableName}]";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var json = reader.GetString(1);
                var ev = SnapshotImporter.ParseEventFromJson(json);
                var category = ev?.Category ?? string.Empty;
                rows.Add((id, category));
            }
        }

        using var tx = conn.BeginTransaction();
        using var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = $"UPDATE [{TableName}] SET [{ColCategory}] = @cat WHERE [{ColId}] = @id";
        var pCat = update.Parameters.Add("@cat", SqliteType.Text);
        var pId = update.Parameters.Add("@id", SqliteType.Integer);
        foreach (var row in rows)
        {
            pCat.Value = row.Category;
            pId.Value = row.Id;
            update.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
