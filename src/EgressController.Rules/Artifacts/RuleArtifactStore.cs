using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Rules.Catalog;

namespace EgressController.Rules.Artifacts;

public sealed record RuleArtifactResult(bool Succeeded, string? Path, string? Error)
{
    public static RuleArtifactResult Success(string path) => new(true, path, null);
    public static RuleArtifactResult Failure(string error) => new(false, null, error);
}

public sealed record RuleArtifactBatchResult(
    bool Succeeded,
    IReadOnlyDictionary<string, string> Paths,
    IReadOnlyDictionary<string, string> Failures);

/// <summary>
/// Stores commit-pinned SRS files. A final path is created only after the bounded download,
/// Git blob digest and non-empty checks have passed. Concurrent requests for one commit/name
/// share one task, so a selected rule cannot be downloaded twice by overlapping UI commands.
/// </summary>
public sealed class RuleArtifactStore
{
    public const int MaxArtifactBytes = 64 * 1024 * 1024;
    public const int DefaultParallelism = 3;

    private readonly string _root;
    private readonly IRemoteFetcher _fetcher;
    private readonly ConcurrentDictionary<string, Lazy<Task<RuleArtifactResult>>> _inflight = new(StringComparer.Ordinal);

    public RuleArtifactStore(string root, IRemoteFetcher fetcher)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Rule artifact root is required.", nameof(root));
        _root = Path.GetFullPath(root);
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
    }

    public string RootDirectory => _root;

    public async Task<RuleArtifactResult> EnsureAsync(
        SingBoxRuleCatalogSnapshot snapshot,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SingBoxRuleCatalogEntry? entry = snapshot.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return RuleArtifactResult.Failure($"SRS '{name}' 不在目标 catalog 中。");

        string key = snapshot.CommitSha + "/" + entry.Name.ToLowerInvariant();
        var candidate = new Lazy<Task<RuleArtifactResult>>(
            () => DownloadAndPublishAsync(snapshot, entry, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task<RuleArtifactResult>> winner = _inflight.GetOrAdd(key, candidate);
        try
        {
            return await winner.Value.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<RuleArtifactResult>>>(key, winner));
        }
    }

    public async Task<RuleArtifactBatchResult> EnsureManyAsync(
        SingBoxRuleCatalogSnapshot snapshot,
        IEnumerable<string> names,
        int maxParallelism = DefaultParallelism,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(names);
        if (maxParallelism is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism));

        string[] requested = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var paths = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(maxParallelism, maxParallelism);

        Task[] tasks = requested.Select(async name =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RuleArtifactResult result = await EnsureAsync(snapshot, name, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded)
                    paths[name] = result.Path!;
                else
                    failures[name] = result.Error ?? "SRS 下载失败。";
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new RuleArtifactBatchResult(
            failures.IsEmpty,
            new Dictionary<string, string>(paths, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(failures, StringComparer.OrdinalIgnoreCase));
    }

    public bool TryGetPath(SingBoxRuleCatalogSnapshot snapshot, string name, out string? path)
    {
        path = null;
        if (!TryValidateCommit(snapshot.CommitSha, out string commit))
            return false;
        SingBoxRuleCatalogEntry? entry = snapshot.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null || !TryValidateName(entry.Name))
            return false;

        string candidate = Path.Combine(_root, "rules", commit, entry.Name + ".srs");
        if (!File.Exists(candidate))
            return false;
        path = candidate;
        return true;
    }

    private async Task<RuleArtifactResult> DownloadAndPublishAsync(
        SingBoxRuleCatalogSnapshot snapshot,
        SingBoxRuleCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCommit(snapshot.CommitSha, out string commit))
            return RuleArtifactResult.Failure("SRS catalog commit SHA 无效。");
        if (!TryValidateName(entry.Name))
            return RuleArtifactResult.Failure("SRS 名称包含非法路径字符。");
        if (!TryValidatePath(entry.Path, entry.Name))
            return RuleArtifactResult.Failure("SRS catalog 路径无效。");
        if (!TryValidateSha(entry.BlobSha))
            return RuleArtifactResult.Failure("SRS catalog blob SHA 无效。");

        string finalDirectory = Path.Combine(_root, "rules", commit);
        string finalPath = Path.Combine(finalDirectory, entry.Name + ".srs");
        if (File.Exists(finalPath))
        {
            try
            {
                byte[] cached = await File.ReadAllBytesAsync(finalPath, cancellationToken).ConfigureAwait(false);
                if (ValidateBody(cached, entry, out _))
                    return RuleArtifactResult.Success(finalPath);
                File.Delete(finalPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return RuleArtifactResult.Failure("读取已有 SRS 缓存失败：" + ex.Message);
            }
        }

        Uri uri = new($"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/{commit}/{entry.Path}");
        RemoteFetchResult result;
        try
        {
            result = await _fetcher.FetchAsync(uri, MaxArtifactBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RuleArtifactResult.Failure("SRS 下载异常：" + ex.Message);
        }

        if (!result.Succeeded)
            return RuleArtifactResult.Failure($"SRS 下载失败（HTTP {result.StatusCode?.ToString() ?? "无状态"}）。");

        byte[] body = result.Body ?? Array.Empty<byte>();
        if (!ValidateBody(body, entry, out string? validationError))
            return RuleArtifactResult.Failure(validationError!);

        Directory.CreateDirectory(finalDirectory);
        string temporaryPath = Path.Combine(finalDirectory, "." + entry.Name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, finalPath, overwrite: true);
            return RuleArtifactResult.Success(finalPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RuleArtifactResult.Failure("SRS 原子写入失败：" + ex.Message);
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
                // A failed temp cleanup cannot expose a partial final artifact.
            }
        }
    }

    private static bool TryValidateCommit(string value, out string normalized)
    {
        normalized = value.Trim().ToLowerInvariant();
        return TryValidateSha(normalized);
    }

    private static bool TryValidateSha(string value)
        => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool TryValidateName(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && value is not "." and not ".."
           && !value.Contains('/')
           && !value.Contains('\\');

    private static bool TryValidatePath(string path, string name)
        => string.Equals(path, "geo/geosite/" + name + ".srs", StringComparison.Ordinal)
           && !path.Contains("..", StringComparison.Ordinal);

    private static bool ValidateBody(byte[] body, SingBoxRuleCatalogEntry entry, out string? error)
    {
        if (body.Length == 0)
        {
            error = "SRS 下载结果为空。";
            return false;
        }
        if (body.Length > MaxArtifactBytes)
        {
            error = "SRS 下载结果超过大小上限。";
            return false;
        }
        if (!string.Equals(GitBlobSha1(body), entry.BlobSha, StringComparison.OrdinalIgnoreCase))
        {
            error = "SRS 内容 Git blob SHA 校验失败。";
            return false;
        }
        error = null;
        return true;
    }

    private static string GitBlobSha1(byte[] body)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
        using var sha1 = SHA1.Create();
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        sha1.TransformFinalBlock(body, 0, body.Length);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }
}
