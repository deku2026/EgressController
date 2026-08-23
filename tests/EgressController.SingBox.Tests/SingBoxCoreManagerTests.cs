using System.IO.Compression;
using System.Security.Cryptography;
using EgressController.SingBox.Cli;
using EgressController.SingBox.Core;
using EgressController.State.SingBox;

namespace EgressController.SingBox.Tests;

public sealed class SingBoxCoreManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EgressController.SingBoxTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Managed_core_promotes_only_after_digest_version_and_check_pass()
    {
        byte[] zip = MakeZip(("sing-box-1.13.19-windows-amd64/sing-box.exe", "fake-sing-box"));
        var releaseClient = new FakeReleaseClient(zip, digest: Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());
        var manager = new SingBoxCoreManager(_directory, releaseClient, new FakeCli());

        SingBoxCoreCandidate candidate = await manager.PrepareManagedAsync(TestContext.Current.CancellationToken);

        Assert.True(candidate.IsManaged);
        Assert.Equal("1.13.19", candidate.Version);
        Assert.True(File.Exists(candidate.ExecutablePath));
        Assert.Equal(candidate.ExecutablePath, new SingBoxStateStore(_directory).LoadCurrent()!.ExecutablePath);
        Assert.Null(new SingBoxStateStore(_directory).LoadLastGood());

        manager.MarkLastGood(candidate);
        Assert.Equal(candidate.Sha256, new SingBoxStateStore(_directory).LoadLastGood()!.Sha256);
    }

    [Fact]
    public async Task Digest_mismatch_does_not_replace_or_publish_a_core()
    {
        byte[] zip = MakeZip(("sing-box-1.13.19-windows-amd64/sing-box.exe", "fake-sing-box"));
        var releaseClient = new FakeReleaseClient(zip, digest: new string('0', 64));
        var manager = new SingBoxCoreManager(_directory, releaseClient, new FakeCli());

        SingBoxCoreException exception = await Assert.ThrowsAsync<SingBoxCoreException>(
            () => manager.PrepareManagedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("core.digest", exception.Code);
        Assert.Null(new SingBoxStateStore(_directory).LoadCurrent());
        Assert.False(Directory.Exists(Path.Combine(_directory, "core", "1.13.19")));
    }

    [Fact]
    public async Task Zip_slip_is_rejected_before_any_file_leaves_the_staging_root()
    {
        byte[] zip = MakeZip(
            ("sing-box-1.13.19-windows-amd64/sing-box.exe", "fake-sing-box"),
            ("../escape.txt", "must-not-appear"));
        var manager = new SingBoxCoreManager(
            _directory,
            new FakeReleaseClient(zip, digest: Convert.ToHexString(SHA256.HashData(zip))),
            new FakeCli());

        SingBoxCoreException exception = await Assert.ThrowsAsync<SingBoxCoreException>(
            () => manager.PrepareManagedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("core.zip-slip", exception.Code);
        Assert.False(File.Exists(Path.Combine(_directory, "core", "escape.txt")));
        Assert.Null(new SingBoxStateStore(_directory).LoadCurrent());
    }

    [Fact]
    public async Task Unsupported_stable_minor_is_rejected_without_download()
    {
        var releaseClient = new FakeReleaseClient(
            MakeZip(("sing-box-1.14.0-windows-amd64/sing-box.exe", "fake")),
            digest: null,
            tagName: "v1.14.0");
        var manager = new SingBoxCoreManager(_directory, releaseClient, new FakeCli());

        SingBoxCoreException exception = await Assert.ThrowsAsync<SingBoxCoreException>(
            () => manager.PrepareManagedAsync(TestContext.Current.CancellationToken));

        Assert.Equal("core.version", exception.Code);
        Assert.Equal(0, releaseClient.DownloadCount);
    }

    [Fact]
    public async Task System_core_uses_the_same_version_and_check_gate_without_copying_the_file()
    {
        string? executable = FindOnPath("sing-box.exe");
        if (executable is null)
            Assert.Skip("sing-box.exe is not installed on this test machine.");

        var manager = new SingBoxCoreManager(_directory, new FakeReleaseClient([], null), new SingBoxCli());
        SingBoxCoreCandidate candidate = await manager.PrepareSystemAsync(executable, TestContext.Current.CancellationToken);

        Assert.False(candidate.IsManaged);
        Assert.Equal(Path.GetFullPath(executable), candidate.ExecutablePath);
        Assert.True(File.Exists(candidate.ExecutablePath));
        Assert.False(File.Exists(Path.Combine(_directory, "core", candidate.Version, "sing-box.exe")));
    }

    private static byte[] MakeZip(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path);
                using Stream stream = entry.Open();
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    private static string? FindOnPath(string fileName)
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeReleaseClient(byte[] zip, string? digest, string tagName = "v1.13.19") : ISingBoxReleaseClient
    {
        public int DownloadCount { get; private set; }
        private readonly SingBoxRelease _release = new()
        {
            TagName = tagName,
            Assets =
            [
                new SingBoxReleaseAsset
                {
                    Name = $"sing-box-{tagName.TrimStart('v')}-windows-amd64.zip",
                    BrowserDownloadUrl = "https://example.invalid/sing-box.zip",
                    Size = zip.LongLength,
                    Digest = digest is null ? null : "sha256:" + digest,
                },
            ],
        };

        public Task<SingBoxRelease> GetLatestStableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_release);

        public async Task DownloadAsync(SingBoxReleaseAsset asset, Stream destination, CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            await destination.WriteAsync(zip, cancellationToken);
        }
    }

    private sealed class FakeCli : ISingBoxCli
    {
        public Task<SingBoxVersionInfo> GetVersionAsync(string executablePath, CancellationToken cancellationToken = default)
            => Task.FromResult(new SingBoxVersionInfo
            {
                Version = new Version(1, 13, 19),
                RawOutput = "sing-box version 1.13.19",
            });

        public Task<SingBoxCommandResult> CheckAsync(string executablePath, string configPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new SingBoxCommandResult
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
            });
    }
}
