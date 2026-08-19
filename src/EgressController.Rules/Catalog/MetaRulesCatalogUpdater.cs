using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EgressController.Core.Contracts;

namespace EgressController.Rules.Catalog;

public sealed record CatalogUpdateResult(bool Succeeded, RuleCatalog? Catalog, string? Error)
{
    public static CatalogUpdateResult Success(RuleCatalog catalog) => new(true, catalog, null);
    public static CatalogUpdateResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Reads the generated MetaCubeX <c>meta</c> branch without cloning Git. The branch is a
/// generated snapshot and may be force-pushed, so the updater first resolves a commit SHA and
/// then walks the immutable Git trees by SHA. The catalog is therefore internally consistent even
/// while the floating branch is being regenerated.
/// </summary>
public sealed partial class MetaRulesCatalogUpdater
{
    public const string Repository = "MetaCubeX/meta-rules-dat";
    public const string Branch = "meta";
    public const int MaxJsonBytes = 4 * 1024 * 1024;

    private static readonly Uri RefUri = new($"https://api.github.com/repos/{Repository}/git/ref/heads/{Branch}");
    private static readonly Uri CommitUriPrefix = new($"https://api.github.com/repos/{Repository}/git/commits/");
    private static readonly Uri TreeUriPrefix = new($"https://api.github.com/repos/{Repository}/git/trees/");

    private readonly IRemoteFetcher _fetcher;

    public MetaRulesCatalogUpdater(IRemoteFetcher fetcher)
        => _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));

    public async Task<CatalogUpdateResult> FetchLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            GitRefDocument reference = await FetchJsonAsync(RefUri, MetaRulesJsonContext.Default.GitRefDocument, cancellationToken)
                .ConfigureAwait(false);
            string commitSha = RequireSha(reference.Object?.Sha, "meta ref commit");

            GitCommitDocument commit = await FetchJsonAsync(
                new Uri(CommitUriPrefix, commitSha),
                MetaRulesJsonContext.Default.GitCommitDocument,
                cancellationToken).ConfigureAwait(false);
            string rootTreeSha = RequireSha(commit.Tree?.Sha, "meta commit tree");

            GitTreeDocument root = await FetchTreeAsync(rootTreeSha, "root", cancellationToken).ConfigureAwait(false);
            GitTreeEntry geo = FindTree(root, "geo");
            GitTreeDocument geoTree = await FetchTreeAsync(RequireSha(geo.Sha, "geo tree"), "geo", cancellationToken)
                .ConfigureAwait(false);
            GitTreeEntry geosite = FindTree(geoTree, "geosite");
            GitTreeDocument geositeTree = await FetchTreeAsync(
                RequireSha(geosite.Sha, "geosite tree"),
                "geo/geosite",
                cancellationToken).ConfigureAwait(false);

            var entries = new List<RuleCatalogEntry>();
            foreach (GitTreeEntry item in geositeTree.Tree ?? Array.Empty<GitTreeEntry>())
            {
                if (!string.Equals(item.Type, "blob", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(item.Path)
                    || !item.Path.EndsWith(".list", StringComparison.OrdinalIgnoreCase)
                    || item.Path.Contains('/', StringComparison.Ordinal)
                    || item.Path.Contains('\\', StringComparison.Ordinal))
                    continue;

                string name = Path.GetFileNameWithoutExtension(item.Path);
                if (name.Length == 0)
                    continue;
                entries.Add(new RuleCatalogEntry(name, "geo/geosite/" + item.Path, RequireSha(item.Sha, item.Path)));
            }

            if (entries.Count == 0)
                return CatalogUpdateResult.Failure("GitHub meta catalog 中没有找到 geo/geosite/*.list。");

            entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            if (entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
                return CatalogUpdateResult.Failure("GitHub meta catalog 包含重复的规则名。");

            var snapshot = new RuleCatalogSnapshot(commitSha, RequireSha(geosite.Sha, "geosite tree"), entries);
            return CatalogUpdateResult.Success(new RuleCatalog(snapshot));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CatalogUpdateResult.Failure(ex.Message);
        }
    }

    private async Task<GitTreeDocument> FetchTreeAsync(
        string treeSha,
        string description,
        CancellationToken cancellationToken)
    {
        GitTreeDocument tree = await FetchJsonAsync(
            new Uri(TreeUriPrefix, treeSha),
            MetaRulesJsonContext.Default.GitTreeDocument,
            cancellationToken).ConfigureAwait(false);
        if (tree.Truncated)
            throw new InvalidDataException($"GitHub {description} tree 被截断，拒绝使用不完整 catalog。");
        return tree;
    }

    private async Task<T> FetchJsonAsync<T>(
        Uri uri,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
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

    private static GitTreeEntry FindTree(GitTreeDocument document, string name)
        => (document.Tree ?? Array.Empty<GitTreeEntry>()).FirstOrDefault(entry =>
               string.Equals(entry.Type, "tree", StringComparison.OrdinalIgnoreCase)
               && string.Equals(entry.Path, name, StringComparison.Ordinal))
           ?? throw new InvalidDataException($"GitHub catalog 缺少 {name} tree。");

    private static string RequireSha(string? sha, string description)
    {
        if (string.IsNullOrWhiteSpace(sha)
            || sha.Length != 40
            || sha.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException($"{description} SHA 无效。");
        return sha.ToLowerInvariant();
    }

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
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("size")] long? Size);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(GitRefDocument))]
    [JsonSerializable(typeof(GitCommitDocument))]
    [JsonSerializable(typeof(GitTreeDocument))]
    private sealed partial class MetaRulesJsonContext : JsonSerializerContext;
}
