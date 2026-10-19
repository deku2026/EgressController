using EgressController.Core.Models;
using Microsoft.Data.Sqlite;

namespace EgressController.State.Applications;

/// <summary>Discovery, exact EXE inventories and manual categories survive rescans independently
/// of enabled routes. No reflection-based serialization or service side effects.</summary>
public sealed class ApplicationCatalogStore(string directory)
{
    private readonly object _gate = new();
    public string DatabasePath { get; } = Path.Combine(Path.GetFullPath(directory), "applications.db");
    private SqliteConnection Open()
    {
        SqliteRuntime.EnsureInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 2 }.ToString());
        try
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS apps(
                  id TEXT PRIMARY KEY, name TEXT NOT NULL, kind INTEGER NOT NULL, command TEXT,
                  executable TEXT, family TEXT, aumid TEXT, icon TEXT, source TEXT, publisher TEXT,
                  version TEXT, catalog TEXT, manual INTEGER NOT NULL, ready INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS app_paths(app TEXT NOT NULL, kind INTEGER NOT NULL,
                  path TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(app,kind,path));
                CREATE TABLE IF NOT EXISTS app_categories(app TEXT PRIMARY KEY, category TEXT NOT NULL);
                """;
            command.ExecuteNonQuery(); return db;
        }
        catch { db.Dispose(); throw; }
    }

    public IReadOnlyList<LaunchTarget> Read()
    {
        lock (_gate)
        {
            using var db = Open();
            var paths = new Dictionary<(string, int), List<string>>();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT app,kind,path FROM app_paths ORDER BY path";
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var key = (reader.GetString(0), reader.GetInt32(1));
                    if (!paths.TryGetValue(key, out var list)) paths[key] = list = [];
                    list.Add(reader.GetString(2));
                }
            command.CommandText = "SELECT id,name,kind,command,executable,family,aumid,icon,source,publisher,version,catalog,manual,ready FROM apps ORDER BY name";
            using var rows = command.ExecuteReader();
            var targets = new List<LaunchTarget>();
            while (rows.Read())
            {
                string key = rows.GetString(0);
                string? Text(int column) => rows.IsDBNull(column) ? null : rows.GetString(column);
                targets.Add(new() { Id = key, SavedDiscoveryKey = key, Name = rows.GetString(1), Kind = (LaunchKind)rows.GetInt32(2),
                    Command = Text(3), CanonicalExecutable = Text(4), PackageFamily = Text(5), Aumid = Text(6), IconPath = Text(7),
                    Source = Text(8), Publisher = Text(9), Version = Text(10), CatalogId = Text(11), IsManual = rows.GetBoolean(12),
                    InventoryReady = rows.GetBoolean(13), OwnedRoots = paths.GetValueOrDefault((key, 0)) ?? [],
                    OwnedExecutables = paths.GetValueOrDefault((key, 1)) ?? [], RelatedDiscoveryKeys = paths.GetValueOrDefault((key, 2)) ?? [] });
            }
            return targets;
        }
    }

    public void Save(IEnumerable<LaunchTarget> targets)
    {
        lock (_gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            foreach (var target in targets)
            {
                using var command = db.CreateCommand(); command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO apps VALUES($id,$name,$kind,$command,$exe,$family,$aumid,$icon,$source,$publisher,$version,$catalog,$manual,$ready)
                    ON CONFLICT(id) DO UPDATE SET name=$name,kind=$kind,command=$command,executable=$exe,
                      family=$family,aumid=$aumid,icon=$icon,source=$source,publisher=$publisher,version=$version,
                      catalog=$catalog,manual=MAX(manual,$manual),ready=CASE WHEN $invalidate=1 THEN 0 ELSE MAX(ready,$ready) END;
                    """;
                void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                Add("$id", target.DiscoveryKey); Add("$name", target.Name); Add("$kind", (int)target.Kind); Add("$command", target.Command);
                Add("$exe", target.CanonicalExecutable); Add("$family", target.PackageFamily); Add("$aumid", target.Aumid); Add("$icon", target.IconPath);
                Add("$source", target.Source); Add("$publisher", target.Publisher); Add("$version", target.Version); Add("$catalog", target.CatalogId);
                Add("$manual", target.IsManual); Add("$ready", target.InventoryReady);
                Add("$invalidate", !target.InventoryReady && (target.IsManual || target.OwnedExecutables.Count > 0)); command.ExecuteNonQuery();
                command.CommandText = "INSERT OR IGNORE INTO app_paths(app,kind,path) VALUES($id,$kind,$path)";
                command.Parameters.Clear(); Add("$id", target.DiscoveryKey); Add("$kind", 0); Add("$path", "");
                // A pending root expansion still carries the last valid snapshot. Persist it
                // alongside ready=false so restarting cannot erase the pending-update notice.
                foreach (var (kind, values) in new[] { (0, target.OwnedRoots), (1, target.OwnedExecutables), (2, target.RelatedDiscoveryKeys) })
                    foreach (string path in values)
                    { command.Parameters["$kind"].Value = kind; command.Parameters["$path"].Value = path; command.ExecuteNonQuery(); }
            }
            transaction.Commit();
        }
    }

    public IReadOnlyDictionary<string, string> Categories()
    {
        lock (_gate)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT app,category FROM app_categories";
            using var rows = command.ExecuteReader(); var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (rows.Read()) result[rows.GetString(0)] = rows.GetString(1);
            return result;
        }
    }

    public void SetCategory(LaunchTarget target, string? category)
    {
        if (!ApplicationCategory.IsValid(category)) throw new ArgumentException("未知分类。", nameof(category));
        lock (_gate)
        {
            using var db = Open(); using var transaction = db.BeginTransaction();
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_categories(app,category) VALUES($id,$category) ON CONFLICT(app) DO UPDATE SET category=$category";
            command.Parameters.AddWithValue("$id", ""); command.Parameters.AddWithValue("$category", category ?? "");
            foreach (string key in target.SelectionKeys)
            { command.Parameters["$id"].Value = key; command.ExecuteNonQuery(); }
            transaction.Commit();
        }
    }
}
