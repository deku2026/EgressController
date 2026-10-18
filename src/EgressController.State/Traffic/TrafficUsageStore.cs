using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EgressController.State.Traffic;

/// <summary>Daily application totals and connection checkpoints commit together. Re-reading a
/// snapshot, reconnecting the display monitor or reopening the app cannot count the same bytes twice.</summary>
public sealed class TrafficUsageStore(string dataDirectory, TimeZoneInfo? timeZone = null)
{
    private static readonly Lazy<bool> Provider = new(() => { SQLitePCL.Batteries_V2.Init(); return true; });
    private readonly object _gate = new();
    private readonly TimeZoneInfo _zone = timeZone ?? TimeZoneInfo.Local;
    public string DatabasePath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "traffic.db");
    private bool _initialized;
    private DateOnly? _lastPruned;

    public void Record(IReadOnlyList<TrafficCounter> counters, DateTimeOffset observedAtUtc)
    {
        if (counters.Count == 0) return;
        lock (_gate)
        {
            using var database = Open();
            using var transaction = database.BeginTransaction();
            using var read = database.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT upload, download, observed FROM traffic_checkpoint WHERE connection_id=$id AND started=$started";
            read.Parameters.Add("$id", SqliteType.Text);
            read.Parameters.Add("$started", SqliteType.Integer);
            using var checkpoint = database.CreateCommand();
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO traffic_checkpoint(connection_id, started, upload, download, observed)
                VALUES ($id,$started,$up,$down,$observed)
                ON CONFLICT(connection_id,started) DO UPDATE SET upload=$up,download=$down,observed=$observed;
                """;
            foreach (string name in new[] { "$id", "$started", "$up", "$down", "$observed" })
                checkpoint.Parameters.AddWithValue(name, name == "$id" ? "" : 0L);
            using var add = database.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = """
                INSERT INTO application_traffic(day,process_path,exit,upload,download)
                VALUES ($day,$path,$exit,$up,$down)
                ON CONFLICT(day,process_path,exit) DO UPDATE SET
                  upload=CASE WHEN upload>9223372036854775807-$up THEN 9223372036854775807 ELSE upload+$up END,
                  download=CASE WHEN download>9223372036854775807-$down THEN 9223372036854775807 ELSE download+$down END;
                """;
            foreach (string name in new[] { "$day", "$path", "$exit", "$up", "$down" })
                add.Parameters.AddWithValue(name, name is "$day" or "$path" ? "" : 0L);
            string day = Day(observedAtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            long observed = observedAtUtc.ToUnixTimeMilliseconds();
            foreach (var counter in counters)
            {
                if (string.IsNullOrWhiteSpace(counter.ConnectionId) || string.IsNullOrWhiteSpace(counter.ProcessPath)
                    || counter.Upload < 0 || counter.Download < 0) continue;
                long started = counter.StartedAtUtc?.ToUnixTimeMilliseconds() ?? 0;
                read.Parameters["$id"].Value = counter.ConnectionId;
                read.Parameters["$started"].Value = started;
                long up = counter.Upload, down = counter.Download;
                using (var previous = read.ExecuteReader())
                {
                    if (previous.Read())
                    {
                        if (previous.GetInt64(2) >= observed) continue;
                        up = Delta(up, previous.GetInt64(0));
                        down = Delta(down, previous.GetInt64(1));
                    }
                }
                checkpoint.Parameters["$id"].Value = counter.ConnectionId;
                checkpoint.Parameters["$started"].Value = started;
                checkpoint.Parameters["$up"].Value = counter.Upload;
                checkpoint.Parameters["$down"].Value = counter.Download;
                checkpoint.Parameters["$observed"].Value = observed;
                checkpoint.ExecuteNonQuery();
                if (up == 0 && down == 0) continue;
                add.Parameters["$day"].Value = day;
                add.Parameters["$path"].Value = counter.ProcessPath.Trim();
                add.Parameters["$exit"].Value = (int)counter.Exit;
                add.Parameters["$up"].Value = up;
                add.Parameters["$down"].Value = down;
                add.ExecuteNonQuery();
            }
            using var first = database.CreateCommand();
            first.Transaction = transaction;
            first.CommandText = "INSERT OR IGNORE INTO traffic_metadata(id,recorded_since) VALUES(1,$at)";
            first.Parameters.AddWithValue("$at", observed);
            first.ExecuteNonQuery();
            transaction.Commit();
            Prune(database, observedAtUtc);
        }
    }

    public TrafficUsageSnapshot Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var database = Open();
            var today = Day(now);
            using var query = database.CreateCommand();
            query.CommandText = "SELECT day,process_path,exit,upload,download FROM application_traffic WHERE day >= $from AND day <= $through ORDER BY day,process_path";
            query.Parameters.AddWithValue("$from", today.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            query.Parameters.AddWithValue("$through", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var entries = new List<TrafficUsage>();
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                    entries.Add(new(reader.GetString(1), DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        (TrafficExit)reader.GetInt32(2), Math.Max(0, reader.GetInt64(3)), Math.Max(0, reader.GetInt64(4))));
            query.CommandText = "SELECT recorded_since FROM traffic_metadata WHERE id=1";
            query.Parameters.Clear();
            var at = query.ExecuteScalar();
            Prune(database, now);
            return new(today.AddDays(-29), today, at is long stamp ? DateTimeOffset.FromUnixTimeMilliseconds(stamp) : null, entries);
        }
    }

    private SqliteConnection Open()
    {
        _ = Provider.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            database.Open();
            if (!_initialized)
            {
                using var command = database.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS application_traffic(
                      day TEXT NOT NULL, process_path TEXT NOT NULL COLLATE NOCASE, exit INTEGER NOT NULL,
                      upload INTEGER NOT NULL, download INTEGER NOT NULL, PRIMARY KEY(day,process_path,exit));
                    CREATE TABLE IF NOT EXISTS traffic_checkpoint(
                      connection_id TEXT NOT NULL, started INTEGER NOT NULL, upload INTEGER NOT NULL,
                      download INTEGER NOT NULL, observed INTEGER NOT NULL, PRIMARY KEY(connection_id,started));
                    CREATE TABLE IF NOT EXISTS traffic_metadata(id INTEGER PRIMARY KEY, recorded_since INTEGER NOT NULL);
                    """;
                command.ExecuteNonQuery();
                _initialized = true;
            }
            return database;
        }
        catch { database.Dispose(); throw; }
    }

    private void Prune(SqliteConnection database, DateTimeOffset now)
    {
        var today = Day(now);
        if (_lastPruned == today) return;
        using var command = database.CreateCommand();
        command.CommandText = "DELETE FROM application_traffic WHERE day < $from; DELETE FROM traffic_checkpoint WHERE observed < $old;";
        command.Parameters.AddWithValue("$from", today.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$old", now.AddDays(-31).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        _lastPruned = today;
    }
    private DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _zone).Date);
    private static long Delta(long current, long previous) => current >= previous ? current - previous : current;
}
