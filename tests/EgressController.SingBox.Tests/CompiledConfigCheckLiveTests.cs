using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Rules.Artifacts;
using EgressController.Rules.Catalog;
using EgressController.SingBox.Cli;
using EgressController.SingBox.Configuration;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Tests;

/// <summary>Realtime proof that the compiler's complete config, not only a hand-written subset, checks.</summary>
public sealed class CompiledConfigCheckLiveTests
{
    [Fact]
    public async Task Complete_compiled_config_passes_installed_sing_box_check()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESS_LIVE_RULES_TEST"), "1", StringComparison.Ordinal))
            Assert.Skip("set EGRESS_LIVE_RULES_TEST=1 to run the realtime compiler/core check smoke test.");

        string? executable = FindOnPath("sing-box.exe");
        if (executable is null)
            Assert.Skip("sing-box.exe is not installed on PATH.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.CompiledCheckTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var fetcher = new Socks5RemoteFetcher("127.0.0.1", 7890);
            var catalogService = new RuleCatalogService(fetcher, Path.Combine(root, "catalog"));
            SingBoxCatalogUpdateResult update = await catalogService.UpdateAsync(TestContext.Current.CancellationToken);
            Assert.True(update.Succeeded, update.Error);
            Assert.True(update.Catalog!.TryGet("google", out _));

            var artifacts = new RuleArtifactStore(Path.Combine(root, "artifacts"), fetcher);
            RuleArtifactResult artifact = await artifacts.EnsureAsync(
                update.Catalog.Snapshot,
                "google",
                TestContext.Current.CancellationToken);
            Assert.True(artifact.Succeeded, artifact.Error);

            var compiler = new EgressProfileCompiler();
            EgressProfileCompileInput input = new()
            {
                Profile = new EgressProfileDocument
                {
                    EsimRuleSets = new[] { "google" },
                    EsimDomains = new[] { "openai.com" },
                },
                Environment = MakeEnvironment(),
                ApplicationExecutablePaths = new[] { @"C:\Apps\Chrome\chrome.exe" },
                UpstreamOwnerPaths = new[] { @"C:\Apps\Mihomo\mihomo.exe" },
                RuleSets = new[] { new SingBoxRuleSetInput("google", artifact.Path!) },
                ControllerPort = 19091,
                ControllerSecret = EgressProfileCompiler.CreateControllerSecret(),
                LogPath = Path.Combine(root, "sing-box.log"),
            };
            EgressProfileCompilationResult compiled = compiler.Compile(input);
            string configPath = Path.Combine(root, "config.next.json");
            EgressProfileCompiler.WriteNext(configPath, compiled);

            SingBoxCommandResult check = await new SingBoxCli().CheckAsync(
                executable,
                configPath,
                TestContext.Current.CancellationToken);

            Assert.True(check.Succeeded, check.StandardError + check.StandardOutput);

            EgressProfileCompilationResult failClosed = compiler.Compile(input with
            {
                DohRouting = new DohRoutingDecision { FailClosed = true },
            });
            string failClosedPath = Path.Combine(root, "config.fail-closed.json");
            EgressProfileCompiler.WriteNext(failClosedPath, failClosed);
            SingBoxCommandResult failClosedCheck = await new SingBoxCli().CheckAsync(
                executable,
                failClosedPath,
                TestContext.Current.CancellationToken);
            Assert.True(failClosedCheck.Succeeded, failClosedCheck.StandardError + failClosedCheck.StandardOutput);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static NetworkEnvironmentSnapshot MakeEnvironment()
        => new()
        {
            Primary = new AdapterSelection
            {
                AdapterId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Alias = "Ethernet",
                Luid = 1,
                IfIndex = 10,
                Ipv6IfIndex = 10,
                IsUp = true,
                AddressState = AdapterAddressState.DualStack,
                Ipv4BindAddress = IPAddress.Parse("192.0.2.10"),
                Ipv6BindAddress = IPAddress.Parse("2001:db8::10"),
            },
            Esim = new AdapterSelection
            {
                AdapterId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Alias = "Cellular",
                Luid = 2,
                IfIndex = 11,
                Ipv6IfIndex = 11,
                IsUp = true,
                AddressState = AdapterAddressState.DualStack,
                Ipv4BindAddress = IPAddress.Parse("198.51.100.10"),
                Ipv6BindAddress = IPAddress.Parse("2001:db8:1::10"),
            },
        };

    private static string? FindOnPath(string fileName)
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
}
