using System.Text;
using EgressController.Rules.Artifacts;
using EgressController.Rules.Catalog;
using EgressController.SingBox.Cli;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Tests;

/// <summary>Opt-in end-to-end proof that a downloaded SRS is accepted by the installed core.</summary>
public sealed class SrsArtifactCheckLiveTests
{
    [Fact]
    public async Task Downloaded_google_srs_passes_installed_sing_box_check()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESS_LIVE_RULES_TEST"), "1", StringComparison.Ordinal))
            Assert.Skip("set EGRESS_LIVE_RULES_TEST=1 to run the realtime SRS/core check smoke test.");

        string? executable = FindOnPath("sing-box.exe");
        if (executable is null)
            Assert.Skip("sing-box.exe is not installed on PATH.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.SrsCheckTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var fetcher = new Socks5RemoteFetcher("127.0.0.1", 7890);
            var service = new RuleCatalogService(fetcher, root);
            SingBoxCatalogUpdateResult update = await service.UpdateAsync(TestContext.Current.CancellationToken);
            Assert.True(update.Succeeded, update.Error);
            Assert.True(update.Catalog!.TryGet("google", out _));

            var store = new RuleArtifactStore(Path.Combine(root, "artifacts"), fetcher);
            RuleArtifactResult artifact = await store.EnsureAsync(
                update.Catalog.Snapshot,
                "google",
                TestContext.Current.CancellationToken);
            Assert.True(artifact.Succeeded, artifact.Error);

            string configPath = Path.Combine(root, "check.json");
            string json = $$"""
                {
                  "log": { "disabled": true },
                  "inbounds": [],
                  "outbounds": [{ "type": "direct", "tag": "direct" }],
                  "route": {
                    "rule_set": [{
                      "tag": "google",
                      "type": "local",
                      "format": "binary",
                      "path": {{JsonString(artifact.Path!)}}
                    }],
                    "rules": [{ "rule_set": ["google"], "outbound": "direct" }],
                    "final": "direct"
                  }
                }
                """;
            await File.WriteAllTextAsync(configPath, json, new UTF8Encoding(false), TestContext.Current.CancellationToken);
            SingBoxCommandResult check = await new SingBoxCli().CheckAsync(
                executable,
                configPath,
                TestContext.Current.CancellationToken);

            Assert.True(check.Succeeded, check.StandardError + check.StandardOutput);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string JsonString(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string? FindOnPath(string fileName)
        => Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
}
