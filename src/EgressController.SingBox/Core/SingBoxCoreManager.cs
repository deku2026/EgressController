using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Profile;
using EgressController.SingBox.Cli;
using EgressController.State.SingBox;

namespace EgressController.SingBox.Core;

public sealed record SingBoxCoreCandidate
{
    public required string Mode { get; init; }
    public required string Version { get; init; }
    public required string ExecutablePath { get; init; }
    public required string Sha256 { get; init; }
    public required bool IsManaged { get; init; }
}

public sealed class SingBoxCoreManager
{
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private readonly string _coreDirectory;
    private readonly SingBoxStateStore _stateStore;
    private readonly ISingBoxReleaseClient _releaseClient;
    private readonly ISingBoxCli _cli;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SingBoxCoreManager(
        string dataDirectory,
        ISingBoxReleaseClient releaseClient,
        ISingBoxCli cli,
        SingBoxStateStore? stateStore = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("data directory is required", nameof(dataDirectory));
        _coreDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "core");
        _stateStore = stateStore ?? new SingBoxStateStore(dataDirectory);
        _releaseClient = releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
    }

    public async Task<SingBoxCoreCandidate> PrepareAsync(
        EgressCoreSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        string mode = (selection.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode != EgressProfileSchema.ManagedCore)
            throw new SingBoxCoreException("只支持由 EgressController 管理的 sing-box core。", "core.mode");
        return await PrepareManagedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SingBoxCoreCandidate> PrepareManagedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SingBoxRelease release;
            try
            {
                release = await _releaseClient.GetLatestStableAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                SingBoxCoreCandidate? cached = await TryUseCachedCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                    return cached;

                throw new SingBoxCoreException(
                    "无法获取 sing-box stable release，且没有可验证的本地 core。",
                    "core.release",
                    exception);
            }
            if (!SingBoxReleaseClient.TryParseSupportedVersion(release.TagName, out Version? version))
                throw new SingBoxCoreException($"当前 stable core {release.TagName} 超出 EgressController 支持范围。", "core.version");
            Version supportedVersion = version
                ?? throw new SingBoxCoreException("无法解析 stable core 版本。", "core.version");

            SingBoxReleaseAsset asset = release.Assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, $"sing-box-{supportedVersion}-windows-amd64.zip", StringComparison.Ordinal))
                ?? throw new SingBoxCoreException("stable release 缺少 windows-amd64 ZIP。", "core.asset");

            SingBoxCorePointer? current = _stateStore.LoadCurrent();
            if (current is not null
                && string.Equals(current.Version, supportedVersion.ToString(), StringComparison.Ordinal)
                && File.Exists(current.ExecutablePath))
            {
                try
                {
                    return await ValidateCandidateAsync(
                        EgressProfileSchema.ManagedCore,
                        current.Version,
                        current.ExecutablePath,
                        current.Sha256,
                        isManaged: true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (SingBoxCoreException)
                {
                    // A damaged current core is not used, but its directory remains recoverable
                    // until the newly downloaded candidate has passed all checks.
                }
            }

            string stagingDirectory = Path.Combine(_coreDirectory, ".staging", Guid.NewGuid().ToString("N"));
            string zipPath = Path.Combine(_coreDirectory, ".downloads", Guid.NewGuid().ToString("N") + ".zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                await DownloadAssetAsync(asset, zipPath, cancellationToken).ConfigureAwait(false);
                string? expectedDigest = NormalizeDigest(asset.Digest);
                string actualDigest = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
                if (expectedDigest is not null
                    && !string.Equals(expectedDigest, actualDigest, StringComparison.OrdinalIgnoreCase))
                    throw new SingBoxCoreException("sing-box ZIP SHA-256 校验失败。", "core.digest");

                string archiveRoot = ExtractZipSafely(zipPath, stagingDirectory);
                string executable = Directory.EnumerateFiles(archiveRoot, "sing-box.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "sing-box.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new SingBoxCoreException("sing-box ZIP 中没有 sing-box.exe。", "core.executable");

                string? archiveDirectory = Path.GetDirectoryName(executable);
                if (archiveDirectory is null)
                    throw new SingBoxCoreException("无法定位 sing-box archive root。", "core.archive");
                string promotedDirectory = Path.Combine(_coreDirectory, supportedVersion.ToString());
                if (Directory.Exists(promotedDirectory))
                    Directory.Delete(promotedDirectory, recursive: true);
                Directory.Move(archiveDirectory, promotedDirectory);
                string promotedExecutable = Path.Combine(promotedDirectory, "sing-box.exe");
                string executableDigest = await ComputeSha256Async(promotedExecutable, cancellationToken).ConfigureAwait(false);
                SingBoxCoreCandidate candidate = await ValidateCandidateAsync(
                    EgressProfileSchema.ManagedCore,
                    version.ToString(),
                    promotedExecutable,
                    executableDigest,
                    isManaged: true,
                    cancellationToken).ConfigureAwait(false);

                _stateStore.SaveCurrent(new SingBoxCorePointer
                {
                    Version = candidate.Version,
                    ExecutablePath = candidate.ExecutablePath,
                    Sha256 = candidate.Sha256,
                    VerifiedAtUtc = DateTimeOffset.UtcNow,
                });
                return candidate;
            }
            catch
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
                throw;
            }
            finally
            {
                TryDeleteFile(zipPath);
                try
                {
                    if (Directory.Exists(stagingDirectory))
                        Directory.Delete(stagingDirectory, recursive: true);
                }
                catch { }
                TryDeleteEmptyDirectory(Path.GetDirectoryName(stagingDirectory));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SingBoxCoreCandidate?> TryUseCachedCurrentAsync(CancellationToken cancellationToken)
    {
        SingBoxCorePointer? current = _stateStore.LoadCurrent();
        if (current is null || !File.Exists(current.ExecutablePath))
            return null;

        try
        {
            return await ValidateCandidateAsync(
                EgressProfileSchema.ManagedCore,
                current.Version,
                current.ExecutablePath,
                current.Sha256,
                isManaged: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SingBoxCoreException)
        {
            return null;
        }
    }

    public void MarkLastGood(SingBoxCoreCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _stateStore.SaveLastGood(new SingBoxCorePointer
        {
            Version = candidate.Version,
            ExecutablePath = candidate.ExecutablePath,
            Sha256 = candidate.Sha256,
            VerifiedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Runs the real core check against the complete generated runtime config.</summary>
    public async Task CheckConfigAsync(
        SingBoxCoreCandidate candidate,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            throw new SingBoxCoreException("生成的 sing-box 配置文件不存在。", "config.missing");

        SingBoxCommandResult result = await _cli.CheckAsync(
            candidate.ExecutablePath,
            Path.GetFullPath(configPath),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            string detail = TrimOutput(result.StandardError, result.StandardOutput);
            throw new SingBoxCoreException(
                "sing-box 配置校验失败：" + detail,
                "config.check");
        }
    }

    private async Task<SingBoxCoreCandidate> ValidateCandidateAsync(
        string mode,
        string version,
        string executablePath,
        string expectedSha256,
        bool isManaged,
        CancellationToken cancellationToken)
    {
        string actualDigest = await ComputeSha256Async(executablePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualDigest, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new SingBoxCoreException("当前 sing-box.exe SHA-256 与记录不一致。", "core.current.digest");
        SingBoxVersionInfo actualVersion = await _cli.GetVersionAsync(executablePath, cancellationToken).ConfigureAwait(false);
        EnsureSupportedVersion(actualVersion.Version, "core.version");
        await CheckMinimalConfigAsync(executablePath, cancellationToken).ConfigureAwait(false);
        return new SingBoxCoreCandidate
        {
            Mode = mode,
            Version = version,
            ExecutablePath = executablePath,
            Sha256 = actualDigest,
            IsManaged = isManaged,
        };
    }

    private async Task CheckMinimalConfigAsync(string executablePath, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetDirectoryName(executablePath) ?? _coreDirectory, ".checks");
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(
            configPath,
            MinimalCheckConfig,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        try
        {
            SingBoxCommandResult result = await _cli.CheckAsync(executablePath, configPath, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                throw new SingBoxCoreException(
                    $"sing-box check 失败：{TrimOutput(result.StandardError, result.StandardOutput)}",
                    "core.check");
        }
        finally
        {
            TryDeleteFile(configPath);
        }
    }

    private async Task DownloadAssetAsync(SingBoxReleaseAsset asset, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough);
        await _releaseClient.DownloadAsync(asset, stream, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (stream.Length <= 0 || stream.Length > MaxDownloadBytes)
            throw new SingBoxCoreException("下载的 sing-box ZIP 大小非法。", "core.download.size");
    }

    private static string ExtractZipSafely(string zipPath, string stagingDirectory)
    {
        string fullStaging = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(stagingDirectory, relative));
            if (!destination.StartsWith(fullStaging, StringComparison.OrdinalIgnoreCase))
                throw new SingBoxCoreException("ZIP 包含越界路径，已拒绝解压。", "core.zip-slip");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
        return stagingDirectory;
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;
        string value = digest.Trim();
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : throw new SingBoxCoreException("release digest 不是 SHA-256。", "core.digest.format");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void EnsureSupportedVersion(Version version, string code)
    {
        if (version.Major != 1 || version.Minor != 13 || version.Build < 0)
            throw new SingBoxCoreException($"sing-box {version} 不在当前支持的 1.13.x 范围内。", code);
    }

    private static string TrimOutput(string stderr, string stdout)
    {
        string value = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return value.Trim().Length <= 1200 ? value.Trim() : value.Trim()[..1200];
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch { }
    }

    private const string MinimalCheckConfig = """
        {
          "log": { "level": "error" },
          "inbounds": [
            {
              "type": "tun",
              "tag": "tun-in",
              "address": ["172.19.0.1/30", "fdfe:dcba:9876::1/126"],
              "auto_route": true,
              "strict_route": true,
              "stack": "system"
            }
          ],
          "outbounds": [{ "type": "direct", "tag": "direct" }],
          "route": { "final": "direct" },
          "experimental": {
            "clash_api": {
              "external_controller": "127.0.0.1:9090",
              "secret": "check-only"
            }
          }
        }
        """;
}

public sealed class SingBoxCoreException(string message, string code, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}
