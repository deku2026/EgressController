using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace EgressController.State.Quota;

/// <summary>
/// Small durable counter for the user-entered eSIM package. It deliberately stores only the
/// package baseline and bytes observed by the connection API; it is not a carrier billing API.
/// </summary>
public sealed class EgressQuotaStore
{
    private readonly object _gate = new();

    public EgressQuotaStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Quota data directory is required.", nameof(dataDirectory));

        string directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "usage.db");
        Batteries_V2.Init();
        EnsureSchema();
    }

    public string DatabasePath { get; }

    public EgressQuotaSnapshot Load()
    {
        lock (_gate)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT total_bytes, starting_remaining_bytes, used_bytes, updated_at_utc FROM quota WHERE id = 1;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return EgressQuotaSnapshot.Empty;

            long total = ReadNonNegativeInt64(reader, 0);
            long startingRemaining = Math.Min(total, ReadNonNegativeInt64(reader, 1));
            long used = ReadNonNegativeInt64(reader, 2);
            DateTimeOffset updatedAt = ParseTimestamp(reader.IsDBNull(3) ? null : reader.GetString(3));
            return new EgressQuotaSnapshot(total, startingRemaining, used, updatedAt);
        }
    }

    public EgressQuotaSnapshot Configure(long totalBytes, long remainingBytes)
    {
        if (totalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes), "套餐总量不能为负数。");
        if (remainingBytes < 0 || remainingBytes > totalBytes)
            throw new ArgumentOutOfRangeException(nameof(remainingBytes), "当前剩余量必须在 0 和套餐总量之间。");

        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO quota(id, total_bytes, starting_remaining_bytes, used_bytes, updated_at_utc)
                VALUES (1, $total, $remaining, 0, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    total_bytes = excluded.total_bytes,
                    starting_remaining_bytes = excluded.starting_remaining_bytes,
                    used_bytes = 0,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$total", totalBytes);
            command.Parameters.AddWithValue("$remaining", remainingBytes);
            command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            return new EgressQuotaSnapshot(totalBytes, remainingBytes, 0, now);
        }
    }

    public EgressQuotaSnapshot AddUsage(long bytes)
    {
        if (bytes <= 0)
            return Load();

        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE quota
                SET used_bytes = CASE
                    WHEN used_bytes > total_bytes - $delta THEN total_bytes
                    ELSE used_bytes + $delta
                END,
                updated_at_utc = $updated
                WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$delta", bytes);
            command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            return Load();
        }
    }

    public EgressQuotaSnapshot ClearUsage()
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE quota SET used_bytes = 0, updated_at_utc = $updated WHERE id = 1;";
            command.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            return Load();
        }
    }

    private void EnsureSchema()
    {
        lock (_gate)
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS quota (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    total_bytes INTEGER NOT NULL,
                    starting_remaining_bytes INTEGER NOT NULL,
                    used_bytes INTEGER NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long ReadNonNegativeInt64(SqliteDataReader reader, int ordinal)
    {
        long value = reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);
        return Math.Max(0, value);
    }

    private static DateTimeOffset ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
}

public sealed record EgressQuotaSnapshot(
    long TotalBytes,
    long StartingRemainingBytes,
    long UsedBytes,
    DateTimeOffset UpdatedAtUtc)
{
    public static EgressQuotaSnapshot Empty { get; } = new(0, 0, 0, DateTimeOffset.UtcNow);

    public long RemainingBytes
        => StartingRemainingBytes > UsedBytes ? StartingRemainingBytes - UsedBytes : 0;

    public double RemainingPercent
        => TotalBytes <= 0 ? 0 : Math.Clamp(RemainingBytes * 100d / TotalBytes, 0, 100);

    public double UsedPercent => TotalBytes <= 0 ? 0 : Math.Clamp(100 - RemainingPercent, 0, 100);
}
