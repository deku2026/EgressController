using EgressController.Runtime;
namespace EgressController.Runtime.Tests;

public sealed class WorkspaceMaintenanceTests
{
    [Fact]
    public void ResetDeletesOnlyGeneratedStateAndSurvivesRetryWithoutFollowingLinks()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-maintenance-" + Guid.NewGuid().ToString("N"));
        string outside = root + "-outside";
        try
        {
            foreach (string name in new[] { "runtime", "resource-cache", "probes", "helper-staging", "configuration", "logs", "core-cache" })
            { Directory.CreateDirectory(Path.Combine(root, name)); File.WriteAllText(Path.Combine(root, name, "fixture"), "private fixture"); }
            Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "retained"), "outside");
            if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(root, "runtime", "outside"), outside);
            var maintenance = new WorkspaceResetMaintenance(root); maintenance.Begin();
            Assert.True(new WorkspaceResetMaintenance(root).Pending);
            foreach (string name in new[] { "runtime", "resource-cache", "probes", "helper-staging" }) Assert.False(Directory.Exists(Path.Combine(root, name)));
            foreach (string name in new[] { "configuration", "logs", "core-cache" }) Assert.True(File.Exists(Path.Combine(root, name, "fixture")));
            Assert.True(File.Exists(Path.Combine(outside, "retained")));
            maintenance.Begin(); Assert.True(maintenance.Pending); maintenance.Complete(); Assert.False(maintenance.Pending);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); if (Directory.Exists(outside)) Directory.Delete(outside, true); }
    }
    [Fact]
    public void DebugReleaseAndInstalledInstancesCannotWriteTheSameWorkspaceConcurrently()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-lease-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new WorkspaceLease(root)) Assert.Throws<IOException>(() => new WorkspaceLease(root));
            using var replacement = new WorkspaceLease(root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ForgettingResetConfigurationCannotReapplyOldInventoryOnFutureFailure()
    {
        var core = new MockCoreRuntime(); var store = new InMemoryGenerationStore();
        var coordinator = new ConfigurationCoordinator(core, store, new BoundedLifecycleLog());
        Assert.True((await coordinator.ApplyAsync(ConfigurationTests.Policy(), ConfigurationTests.Options(), ConfigurationTests.Assets(), "fixture", TestContext.Current.CancellationToken)).Succeeded);
        Assert.True(await coordinator.StopAsync("reset", TestContext.Current.CancellationToken));
        await coordinator.ForgetConfigurationAsync(TestContext.Current.CancellationToken);
        Assert.Null(coordinator.Applied); Assert.Null(coordinator.DesiredPolicyYaml); Assert.Null(await store.ReadCurrentAsync(TestContext.Current.CancellationToken));
        Assert.False((await coordinator.ApplyAsync(new(), ConfigurationTests.Options(), ConfigurationTests.Assets(), "new incomplete setup", TestContext.Current.CancellationToken)).Succeeded);
        Assert.Null(coordinator.Applied); Assert.Null(core.Current);
    }
}
