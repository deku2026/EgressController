using EgressController.Core.Ipc;
using EgressController.ElevatedHost;

namespace EgressController.ElevatedHost.Tests;

public sealed class SingBoxProcessHostIntegrationTests
{
    [Fact]
    public async Task Installed_core_runs_with_fixed_run_config_arguments_and_stops_cleanly()
    {
        string? executable = FindOnPath("sing-box.exe");
        if (executable is null)
            Assert.Skip("sing-box.exe is not installed on PATH.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.HostIntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string configPath = Path.Combine(root, "config.json");
        await File.WriteAllTextAsync(configPath, """
            {
              "log": { "disabled": true },
              "inbounds": [],
              "outbounds": [{ "type": "direct", "tag": "direct" }],
              "route": { "final": "direct" }
            }
            """, TestContext.Current.CancellationToken);
        try
        {
            var policy = new ElevatedHostPathPolicy
            {
                DataRoot = root,
                AllowedSystemCorePath = executable,
            };
            await using var host = new SingBoxProcessHost(policy);
            var request = ElevatedIpcMessage.Request(ElevatedIpcKind.Start, Environment.ProcessId) with
            {
                CorePath = Path.GetFullPath(executable),
                ConfigPath = configPath,
                CoreSha256 = await ElevatedHostPathPolicy.ComputeSha256Async(executable, TestContext.Current.CancellationToken),
                ConfigSha256 = await ElevatedHostPathPolicy.ComputeSha256Async(configPath, TestContext.Current.CancellationToken),
            };

            SingBoxHostStatus running = await host.StartAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal("running", running.State);
            Assert.NotNull(running.ProcessId);

            SingBoxHostStatus stopped = await host.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal("stopped", stopped.State);
            Assert.Null(host.Status.ProcessId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string? FindOnPath(string fileName)
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
}
