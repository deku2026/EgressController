using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EgressController.Rules.Catalog;

public sealed record RuleCacheCatalogDocument(
    string CommitSha,
    string TreeSha,
    RuleCatalogEntry[] Entries,
    DateTimeOffset UpdatedAtUtc);

public sealed record RuleCacheActiveDocument(
    string CommitSha,
    string TreeSha,
    string[] SelectedNames,
    DateTimeOffset ActivatedAtUtc);

public sealed record CachedActiveRules(
    RuleCacheActiveDocument Manifest,
    IReadOnlyDictionary<string, byte[]> Bodies);

/// <summary>
/// Disk cache for remote rule catalogs and commit-pinned active snapshots. A new snapshot is
/// staged under its own directory and the small active manifest is written last; a crash can
/// therefore leave an unused staged directory, but cannot expose a partially migrated rule set.
/// </summary>
public sealed partial class RuleCacheStore
{
    private readonly string _root;
    private readonly string _catalogPath;
    private readonly string _activePath;
    private readonly string _snapshotsPath;

    public RuleCacheStore(string root)
    {
        _root = Path.GetFullPath(root);
        _catalogPath = Path.Combine(_root, "catalog.json");
        _activePath = Path.Combine(_root, "active.json");
        _snapshotsPath = Path.Combine(_root, "snapshots");
    }

    public string RootDirectory => _root;

    public bool TryLoadCatalog(out RuleCatalog? catalog, out string? error)
    {
        catalog = null;
        error = null;
        if (!File.Exists(_catalogPath))
            return false;

        try
        {
            RuleCacheCatalogDocument? document = ReadJson(
                _catalogPath,
                RuleCacheJsonContext.Default.RuleCacheCatalogDocument);
            if (document is null || document.Entries is null || document.Entries.Length == 0)
                throw new InvalidDataException("规则 catalog 缓存为空。");
            ValidateSha(document.CommitSha, "catalog commit");
            ValidateSha(document.TreeSha, "catalog tree");
            if (document.Entries.Any(entry => entry is null
                || string.IsNullOrWhiteSpace(entry.Name)
                || string.IsNullOrWhiteSpace(entry.Path)
                || string.IsNullOrWhiteSpace(entry.BlobSha)))
                throw new InvalidDataException("规则 catalog 缓存包含无效条目。");
            catalog = new RuleCatalog(new RuleCatalogSnapshot(
                document.CommitSha,
                document.TreeSha,
                document.Entries));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            error = "读取规则 catalog 缓存失败：" + ex.Message;
            return false;
        }
    }

    public void SaveCatalog(RuleCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var document = new RuleCacheCatalogDocument(
            snapshot.CommitSha,
            snapshot.TreeSha,
            snapshot.Entries.ToArray(),
            DateTimeOffset.UtcNow);
        WriteJsonAtomic(_catalogPath, document, RuleCacheJsonContext.Default.RuleCacheCatalogDocument);
    }

    public bool TryLoadActive(out CachedActiveRules? active, out string? error)
    {
        active = null;
        error = null;
        if (!File.Exists(_activePath))
            return false;

        try
        {
            RuleCacheActiveDocument? manifest = ReadJson(
                _activePath,
                RuleCacheJsonContext.Default.RuleCacheActiveDocument);
            if (manifest is null || manifest.SelectedNames is null)
                throw new InvalidDataException("活动规则缓存为空。");
            ValidateSha(manifest.CommitSha, "active commit");
            ValidateSha(manifest.TreeSha, "active tree");
            if (manifest.SelectedNames.Length == 0)
            {
                active = new CachedActiveRules(manifest, new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase));
                return true;
            }

            if (manifest.SelectedNames.Any(string.IsNullOrWhiteSpace)
                || manifest.SelectedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.SelectedNames.Length)
                throw new InvalidDataException("活动规则缓存包含重复或空的规则名。");

            string directory = SnapshotDirectory(manifest.CommitSha);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException("active rule snapshot directory not found");

            var bodies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in manifest.SelectedNames)
            {
                string path = SafeRulePath(directory, name);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"active rule file not found: {name}");
                bodies[name] = File.ReadAllBytes(path);
            }

            active = new CachedActiveRules(manifest, bodies);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            error = "读取活动规则缓存失败：" + ex.Message;
            return false;
        }
    }

    public void PublishActive(
        RuleCatalogSnapshot snapshot,
        IReadOnlyDictionary<string, byte[]> bodies)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(bodies);

        var selectedNames = snapshot.Entries
            .Where(entry => bodies.ContainsKey(entry.Name))
            .Select(entry => entry.Name)
            .ToArray();
        if (selectedNames.Length != bodies.Count)
            throw new InvalidDataException("活动规则缓存与 catalog 不一致。");

        string snapshotDirectory = SnapshotDirectory(snapshot.CommitSha);
        string stagingDirectory = snapshotDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            foreach (string name in selectedNames)
            {
                byte[] body = bodies[name] ?? throw new InvalidDataException($"活动规则为空：{name}");
                File.WriteAllBytes(SafeRulePath(stagingDirectory, name), body);
            }

            if (!Directory.Exists(snapshotDirectory))
                Directory.Move(stagingDirectory, snapshotDirectory);
            else
                Directory.Delete(stagingDirectory, recursive: true);

            var manifest = new RuleCacheActiveDocument(
                snapshot.CommitSha,
                snapshot.TreeSha,
                selectedNames,
                DateTimeOffset.UtcNow);
            WriteJsonAtomic(_activePath, manifest, RuleCacheJsonContext.Default.RuleCacheActiveDocument);
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch
            {
                // Cache cleanup is best effort; the active manifest still points to the old
                // snapshot if publication failed.
            }
            throw;
        }
    }

    public bool TryReadRule(string commitSha, string name, out byte[] body)
    {
        body = Array.Empty<byte>();
        try
        {
            string path = SafeRulePath(SnapshotDirectory(commitSha), name);
            if (!File.Exists(path))
                return false;
            body = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private string SnapshotDirectory(string commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new ArgumentException("Commit SHA is required.", nameof(commitSha));
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(commitSha))).ToLowerInvariant()[..32];
        return Path.Combine(_snapshotsPath, id);
    }

    private static void ValidateSha(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 40
            || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"{description} SHA 无效。");
    }

    private static string SafeRulePath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid rule name.", nameof(name));
        return Path.Combine(directory, name + ".list");
    }

    private static T? ReadJson<T>(string path, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(File.ReadAllBytes(path), typeInfo);

    private static void WriteJsonAtomic<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // Best-effort cleanup of a failed cache write.
            }
        }
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(RuleCacheCatalogDocument))]
    [JsonSerializable(typeof(RuleCacheActiveDocument))]
    private sealed partial class RuleCacheJsonContext : JsonSerializerContext;
}
