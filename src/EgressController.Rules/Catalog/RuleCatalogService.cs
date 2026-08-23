using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EgressController.Core.Contracts;

namespace EgressController.Rules.Catalog;

public sealed record SingBoxCatalogUpdateResult(bool Succeeded, SingBoxRuleCatalog? Catalog, string? Error)
{
    public static SingBoxCatalogUpdateResult Success(SingBoxRuleCatalog catalog) => new(true, catalog, null);
    public static SingBoxCatalogUpdateResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Updates the MetaCubeX <c>sing</c> branch only when explicitly requested. The branch is first
/// resolved to an immutable commit, then the generated geo/geosite tree is walked by SHA. A
/// failed refresh never replaces the last locally persisted catalog.
/// </summary>
public sealed partial class RuleCatalogService
{
    public const string Repository = "MetaCubeX/meta-rules-dat";
    public const string Branch = "sing";
    public const int MaxJsonBytes = 8 * 1024 * 1024;

    private static readonly Uri RefUri = new($"https://api.github.com/repos/{Repository}/git/ref/heads/{Branch}");
    private static readonly Uri CommitUriPrefix = new($"https://api.github.com/repos/{Repository}/git/commits/");
    private static readonly Uri TreeUriPrefix = new($"https://api.github.com/repos/{Repository}/git/trees/");

    private readonly IRemoteFetcher _fetcher;
    private readonly string _catalogPath;
    private readonly object _gate = new();
    private volatile SingBoxRuleCatalog? _current;

    public RuleCatalogService(IRemoteFetcher fetcher, string dataRoot)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new ArgumentException("Rule data root is required.", nameof(dataRoot));
        string root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        _catalogPath = Path.Combine(root, "catalog.json");
    }

    public SingBoxRuleCatalog? Current => _current;

    public string CatalogPath => _catalogPath;

    public SingBoxRuleCatalog? LoadCached(out string? error)
    {
        error = null;
        if (!File.Exists(_catalogPath))
            return _current;
        try
        {
            CatalogCacheDocument? document = JsonSerializer.Deserialize(
                File.ReadAllBytes(_catalogPath),
                RuleCatalogJsonContext.Default.CatalogCacheDocument);
            if (document is null)
                throw new InvalidDataException("catalog.json 为空。");
            ValidateDocument(document);
            var catalog = new SingBoxRuleCatalog(new SingBoxRuleCatalogSnapshot(
                document.CommitSha,
                document.TreeSha,
                document.Entries));
            lock (_gate)
                _current = catalog;
            return catalog;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            error = "读取 SRS catalog 缓存失败：" + ex.Message;
            return _current;
        }
    }

    public IReadOnlyList<SingBoxRuleCatalogEntry> Search(string query, int max = 50)
        => (_current ?? LoadCached(out _))?.Search(query, max)
           ?? Array.Empty<SingBoxRuleCatalogEntry>();

    public async Task<SingBoxCatalogUpdateResult> UpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            GitRefDocument reference = await FetchJsonAsync(
                RefUri,
                RuleCatalogJsonContext.Default.GitRefDocument,
                cancellationToken).ConfigureAwait(false);
            string commitSha = RequireSha(reference.Object?.Sha, "sing branch commit");

            GitCommitDocument commit = await FetchJsonAsync(
                new Uri(CommitUriPrefix, commitSha),
                RuleCatalogJsonContext.Default.GitCommitDocument,
                cancellationToken).ConfigureAwait(false);
            string rootTreeSha = RequireSha(commit.Tree?.Sha, "sing commit tree");

            GitTreeDocument root = await FetchTreeAsync(rootTreeSha, "root", cancellationToken).ConfigureAwait(false);
            GitTreeEntry geo = FindTree(root, "geo");
            GitTreeDocument geoTree = await FetchTreeAsync(
                RequireSha(geo.Sha, "geo tree"),
                "geo",
                cancellationToken).ConfigureAwait(false);
            GitTreeEntry geosite = FindTree(geoTree, "geosite");
            string geositeSha = RequireSha(geosite.Sha, "geo/geosite tree");
            GitTreeDocument geositeTree = await FetchTreeAsync(geositeSha, "geo/geosite", cancellationToken)
                .ConfigureAwait(false);

            var entries = new List<SingBoxRuleCatalogEntry>();
            foreach (GitTreeEntry item in geositeTree.Tree ?? Array.Empty<GitTreeEntry>())
            {
                if (!string.Equals(item.Type, "blob", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(item.Path)
                    || item.Path.Contains('/', StringComparison.Ordinal)
                    || item.Path.Contains('\\')
                    || !item.Path.EndsWith(".srs", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileNameWithoutExtension(item.Path);
                if (string.IsNullOrWhiteSpace(name)
                    || !TryValidateName(name)
                    || !TryValidateSha(item.Sha))
                    continue;
                entries.Add(new SingBoxRuleCatalogEntry(
                    name,
                    "geo/geosite/" + name + ".srs",
                    item.Sha!.ToLowerInvariant(),
                    item.Size));
            }

            if (entries.Count == 0)
                return SingBoxCatalogUpdateResult.Failure("GitHub sing catalog 中没有找到 geo/geosite/*.srs。");

            entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            if (entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
                return SingBoxCatalogUpdateResult.Failure("GitHub sing catalog 包含重复的规则名。");

            var snapshot = new SingBoxRuleCatalogSnapshot(commitSha, geositeSha, entries);
            SaveAtomic(snapshot);
            var catalog = new SingBoxRuleCatalog(snapshot);
            lock (_gate)
                _current = catalog;
            return SingBoxCatalogUpdateResult.Success(catalog);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SingBoxCatalogUpdateResult.Failure(ex.Message);
        }
    }

    private async Task<GitTreeDocument> FetchTreeAsync(
        string treeSha,
        string description,
        CancellationToken cancellationToken)
    {
        GitTreeDocument tree = await FetchJsonAsync(
            new Uri(TreeUriPrefix, treeSha),
            RuleCatalogJsonContext.Default.GitTreeDocument,
            cancellationToken).ConfigureAwait(false);
        if (tree.Truncated)
            throw new InvalidDataException($"GitHub {description} tree 被截断，拒绝使用不完整 SRS catalog。");
        return tree;
    }

    private async Task<T> FetchJsonAsync<T>(Uri uri, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        RemoteFetchResult result = await _fetcher.FetchAsync(uri, MaxJsonBytes, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new IOException($"请求 {uri} 失败（HTTP {result.StatusCode?.ToString() ?? "无状态"}）。");
        if (result.Body is null || result.Body.Length == 0)
            throw new InvalidDataException($"请求 {uri} 返回空内容。");
        try
        {
            return JsonSerializer.Deserialize(result.Body, typeInfo)
                ?? throw new InvalidDataException($"请求 {uri} 返回空 JSON 对象。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"请求 {uri} 返回的 JSON 无效：{ex.Message}", ex);
        }
    }

    private void SaveAtomic(SingBoxRuleCatalogSnapshot snapshot)
    {
        var document = new CatalogCacheDocument(
            snapshot.CommitSha,
            snapshot.TreeSha,
            snapshot.Entries.ToArray(),
            DateTimeOffset.UtcNow);
        string temporaryPath = _catalogPath + ".tmp-" + Guid.NewGuid().ToString("N");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, RuleCatalogJsonContext.Default.CatalogCacheDocument);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _catalogPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best effort cleanup; the previous catalog remains valid.
            }
        }
    }

    private static GitTreeEntry FindTree(GitTreeDocument document, string name)
        => (document.Tree ?? Array.Empty<GitTreeEntry>()).FirstOrDefault(entry =>
               string.Equals(entry.Type, "tree", StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.Path, name, StringComparison.Ordinal))
           ?? throw new InvalidDataException($"GitHub sing catalog 缺少 {name} tree。");

    private static void ValidateDocument(CatalogCacheDocument document)
    {
        if (!TryValidateSha(document.CommitSha) || !TryValidateSha(document.TreeSha))
            throw new InvalidDataException("SRS catalog commit/tree SHA 无效。");
        if (document.Entries is null || document.Entries.Length == 0)
            throw new InvalidDataException("SRS catalog 没有条目。");
        if (document.Entries.Any(entry => entry is null
            || !TryValidateName(entry.Name)
            || !TryValidatePath(entry.Path, entry.Name)
            || !TryValidateSha(entry.BlobSha)
            || (entry.Size is < 0)))
            throw new InvalidDataException("SRS catalog 包含无效条目。");
        if (document.Entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != document.Entries.Length)
            throw new InvalidDataException("SRS catalog 包含重复名称。");
    }

    private static string RequireSha(string? value, string description)
    {
        if (!TryValidateSha(value))
            throw new InvalidDataException($"{description} SHA 无效。");
        return value!.ToLowerInvariant();
    }

    private static bool TryValidateSha(string? value)
        => value is not null && value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool TryValidateName(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && value is not "." and not ".."
           && !value.Contains('/')
           && !value.Contains('\\');

    private static bool TryValidatePath(string? path, string? name)
        => path is not null
           && name is not null
           && string.Equals(path, "geo/geosite/" + name + ".srs", StringComparison.Ordinal)
           && !path.Contains("..", StringComparison.Ordinal);

    private sealed partial record CatalogCacheDocument(
        string CommitSha,
        string TreeSha,
        SingBoxRuleCatalogEntry[] Entries,
        DateTimeOffset UpdatedAtUtc);

    private sealed partial record GitRefDocument([property: JsonPropertyName("object")] GitObjectDocument? Object);
    private sealed partial record GitObjectDocument(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("type")] string? Type);
    private sealed partial record GitCommitDocument([property: JsonPropertyName("tree")] GitObjectDocument? Tree);
    private sealed partial record GitTreeDocument(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("tree")] GitTreeEntry[]? Tree);
    private sealed partial record GitTreeEntry(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("size")] long? Size);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(CatalogCacheDocument))]
    [JsonSerializable(typeof(GitRefDocument))]
    [JsonSerializable(typeof(GitCommitDocument))]
    [JsonSerializable(typeof(GitTreeDocument))]
    private sealed partial class RuleCatalogJsonContext : JsonSerializerContext;
}
