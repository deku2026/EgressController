using EgressController.Rules.Catalog;
using EgressController.Transport.Upstream;

namespace EgressController.Rules.Tests;

/// <summary>
/// Opt-in network smoke test. It is never part of the ordinary offline test run; enable it with
/// EGRESS_LIVE_RULES_TEST=1 when the configured explicit upstream is available.
/// </summary>
public class LiveMetaRulesTests
{
    [Fact]
    public async Task Official_meta_snapshot_is_reachable_through_explicit_upstream()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("EGRESS_LIVE_RULES_TEST"), "1", StringComparison.Ordinal))
            Assert.Skip("set EGRESS_LIVE_RULES_TEST=1 to run the explicit-upstream network smoke test.");

        string host = Environment.GetEnvironmentVariable("EGRESS_UPSTREAM_HOST") ?? "127.0.0.1";
        int port = int.TryParse(Environment.GetEnvironmentVariable("EGRESS_UPSTREAM_PORT"), out int configuredPort)
            ? configuredPort
            : 7890;

        using var fetcher = new UpstreamRemoteFetcher(host, port);
        CatalogUpdateResult update = await new MetaRulesCatalogUpdater(fetcher)
            .FetchLatestAsync(TestContext.Current.CancellationToken);

        Assert.True(update.Succeeded, update.Error);
        Assert.NotNull(update.Catalog);
        Assert.True(update.Catalog!.Count > 20);
        Assert.True(update.Catalog.TryGet("google", out RuleCatalogEntry? entry), "official catalog should contain google.list");

        MigrationResult migration = await new RuleSnapshotManager(fetcher).ActivateAsync(
            new[] { entry!.Name },
            update.Catalog,
            TestContext.Current.CancellationToken);
        Assert.True(migration.Succeeded, migration.Error);
        Assert.True(migration.DownloadedBodies.TryGetValue(entry.Name, out byte[]? body));
        Assert.NotNull(body);
        Assert.NotEmpty(body);
    }
}
