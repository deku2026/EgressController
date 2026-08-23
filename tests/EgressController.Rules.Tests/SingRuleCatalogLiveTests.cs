using EgressController.Rules.Artifacts;
using EgressController.Rules.Catalog;
using EgressController.Transport.Upstream;

namespace EgressController.Rules.Tests;

/// <summary>Opt-in realtime smoke test for the exact SOCKS5 control-plane path used by the app.</summary>
public sealed class SingRuleCatalogLiveTests
{
    [Fact]
    public async Task Official_sing_catalog_and_google_srs_are_reachable_through_7890()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESS_LIVE_RULES_TEST"), "1", StringComparison.Ordinal))
            Assert.Skip("set EGRESS_LIVE_RULES_TEST=1 to run the realtime SOCKS5 7890 smoke test.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.LiveSrsTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var fetcher = new Socks5RemoteFetcher("127.0.0.1", 7890);
            var service = new RuleCatalogService(fetcher, root);
            SingBoxCatalogUpdateResult update = await service.UpdateAsync(TestContext.Current.CancellationToken);

            Assert.True(update.Succeeded, update.Error);
            Assert.NotNull(update.Catalog);
            Assert.True(update.Catalog!.Count > 1000);
            Assert.True(update.Catalog.TryGet("115", out _));
            Assert.True(update.Catalog.TryGet("google", out _));
            Assert.True(update.Catalog.TryGet("openai", out _));

            var store = new RuleArtifactStore(Path.Combine(root, "artifacts"), fetcher);
            RuleArtifactResult artifact = await store.EnsureAsync(
                update.Catalog.Snapshot,
                "google",
                TestContext.Current.CancellationToken);

            Assert.True(artifact.Succeeded, artifact.Error);
            Assert.True(File.Exists(artifact.Path));
            Assert.NotEmpty(await File.ReadAllBytesAsync(artifact.Path!, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
