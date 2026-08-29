using System.Security.Cryptography;
using System.Text;
using EgressController.App.Services;
using EgressController.SingBox.Runtime;

namespace EgressController.Windows.IntegrationTests;

public sealed class DirectSingBoxProcessClientTests
{
    [Fact]
    public async Task Managed_core_can_start_and_stop_without_elevated_host_or_pipe()
    {
        string? corePath = FindSingBox();
        if (corePath is null)
            Assert.Skip("sing-box.exe is not installed on PATH or in the local Scoop apps directory.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.DirectCoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configPath = Path.Combine(root, "config.json");
        await File.WriteAllTextAsync(configPath, """
            {
              "log": { "disabled": true },
              "inbounds": [],
              "outbounds": [{ "type": "direct", "tag": "direct" }],
              "route": { "final": "direct" }
            }
            """, new UTF8Encoding(false), TestContext.Current.CancellationToken);

        try
        {
            await using var client = new DirectSingBoxProcessClient();
            SingBoxProcessStatus started = await client.StartAsync(
                Candidate(corePath, configPath),
                restart: false,
                TestContext.Current.CancellationToken);

            Assert.True(started.Succeeded, started.ErrorMessage);
            Assert.Equal("running", started.State);
            Assert.NotNull(started.ProcessId);

            SingBoxProcessStatus status = await client.GetStatusAsync(TestContext.Current.CancellationToken);
            Assert.True(status.Succeeded, status.ErrorMessage);
            Assert.Equal("running", status.State);

            SingBoxProcessStatus stopped = await client.StopAsync(TestContext.Current.CancellationToken);
            Assert.True(stopped.Succeeded, stopped.ErrorMessage);
            Assert.Equal("stopped", stopped.State);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_candidate_is_reported_before_process_start()
    {
        await using var client = new DirectSingBoxProcessClient();
        SingBoxProcessStatus result = await client.StartAsync(
            new SingBoxRuntimeCandidate
            {
                CoreVersion = "test",
                CorePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "sing-box.exe"),
                CoreSha256 = new string('0', 64),
                ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"),
                ConfigSha256 = new string('0', 64),
            },
            restart: false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("candidate.invalid", result.ErrorCode);
        Assert.Null(result.ProcessId);
    }

    private static SingBoxRuntimeCandidate Candidate(string corePath, string configPath)
        => new()
        {
            CoreVersion = "installed",
            CorePath = corePath,
            CoreSha256 = Hash(corePath),
            ConfigPath = configPath,
            ConfigSha256 = Hash(configPath),
        };

    private static string Hash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string? FindSingBox()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "sing-box", "current", "sing-box.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "sing-box", "1.13.19", "sing-box.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, "sing-box.exe"))
                .FirstOrDefault(File.Exists);
    }
}
