using System.Text.Json;
using EgressController.Core.Protection;
using Microsoft.Data.Sqlite;

namespace EgressController.Guardian;

public interface IGuardStore
{
    GuardPolicy? LoadPolicy();
    void SavePolicy(GuardPolicy policy);
    GuardCheckpoint LoadCheckpoint();
    void SaveCheckpoint(GuardCheckpoint checkpoint);
}

/// <summary>The service owns writes after installation. A policy ACK follows its durable transaction.</summary>
public sealed class GuardStore : IGuardStore
{
    private readonly string _connectionString;
    public GuardStore(string directory)
    {
        Directory.CreateDirectory(directory);
        SQLitePCL.Batteries_V2.Init();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "guard.db"), Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 5,
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS guard_state (name TEXT PRIMARY KEY, value TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    public GuardPolicy? LoadPolicy() => Read("policy") is { } text
        ? (JsonSerializer.Deserialize(text, GuardJson.Default.GuardPolicy) ?? throw new InvalidDataException("守护名单为空。")).Validate() : null;
    public void SavePolicy(GuardPolicy policy) => Write("policy", JsonSerializer.Serialize(policy.Validate(), GuardJson.Default.GuardPolicy));
    public GuardCheckpoint LoadCheckpoint() => Read("checkpoint") is { } text
        ? JsonSerializer.Deserialize(text, GuardJson.Default.GuardCheckpoint) ?? throw new InvalidDataException("守护跟踪记录无效。")
        : new([], []);
    public void SaveCheckpoint(GuardCheckpoint checkpoint) => Write("checkpoint", JsonSerializer.Serialize(checkpoint, GuardJson.Default.GuardCheckpoint));
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
    private string? Read(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM guard_state WHERE name=$key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
    private void Write(string key, string value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO guard_state(name,value) VALUES($key,$value) ON CONFLICT(name) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
