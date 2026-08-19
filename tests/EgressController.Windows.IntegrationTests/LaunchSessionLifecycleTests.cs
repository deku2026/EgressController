using EgressController.App;
using EgressController.App.ViewModels;
using EgressController.Core.Models;

namespace EgressController.Windows.IntegrationTests;

public class LaunchSessionLifecycleTests
{
    [Fact]
    public async Task Manually_added_executable_survives_an_active_rescan()
    {
        string source = Path.Combine(Environment.SystemDirectory, "where.exe");
        Assert.True(File.Exists(source));

        string root = Path.Combine(Path.GetTempPath(), "egress-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "manual-app.exe");
        File.Copy(source, executable);

        try
        {
            await using var host = new RouterHost(localPort: 0);
            LaunchTarget target = host.AddExecutable(executable);
            Assert.True(host.SetTargetManaged(target.Id, true));

            IReadOnlyList<LaunchTarget> rescanned = host.ScanTargets();
            LaunchTarget? retained = rescanned.FirstOrDefault(item => item.DiscoveryKey == target.DiscoveryKey);
            Assert.NotNull(retained);
            Assert.True(retained!.Managed);
            Assert.Contains(retained.OwnedExecutables, item =>
                string.Equals(item, executable, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task App_entry_status_changes_after_managed_process_exits()
    {
        string source = Path.Combine(Environment.SystemDirectory, "where.exe");
        Assert.True(File.Exists(source));

        string root = Path.Combine(Path.GetTempPath(), "egress-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "short-lived-app.exe");
        File.Copy(source, executable);

        try
        {
            await using var host = new RouterHost(localPort: 0);
            LaunchTarget target = host.AddExecutable(executable);
            using var entry = new AppEntryViewModel(host, target, () => { });
            entry.Managed = true;

            entry.LaunchCommand.Execute(null);
            Assert.Contains("运行中", entry.Status);

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && host.Sessions.All().Count != 0)
                await Task.Delay(50, TestContext.Current.CancellationToken);

            entry.RefreshStatus();
            Assert.StartsWith("已结束", entry.Status, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Managed_session_is_removed_when_launched_root_exits()
    {
        string source = Path.Combine(Environment.SystemDirectory, "where.exe");
        Assert.True(File.Exists(source));

        string root = Path.Combine(Path.GetTempPath(), "egress-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "short-lived-app.exe");
        File.Copy(source, executable);

        try
        {
            await using var host = new RouterHost();
            LaunchTarget target = host.AddExecutable(executable);
            Assert.True(host.SetTargetManaged(target.Id, true));

            host.LaunchTarget(target.Id);
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && host.Sessions.All().Count != 0)
                await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.Empty(host.Sessions.All());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
