using EgressController.Core.Models;
using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class ApplicationInventorySnapshotTests
{
    [Fact]
    public void Snapshot_normalizes_paths_and_expands_selected_applications_stably()
    {
        var target = new LaunchTarget
        {
            Id = "sample",
            Name = "Sample",
            Kind = LaunchKind.DirectExe,
            CanonicalExecutable = @"C:\Apps\Sample\sample.exe",
            OwnedExecutables =
            [
                @"C:\Apps\Sample\z-helper.exe",
                @"C:\Apps\Sample\sample.exe",
                @"C:\Apps\Sample\a-helper.exe",
                @"C:\Apps\Sample\not-a-dll.dll",
            ],
        };

        ApplicationInventorySnapshot snapshot = ApplicationInventorySnapshot.Create([target]);

        Assert.True(snapshot.TryGet(target.DiscoveryKey, out ApplicationInventoryEntry? entry));
        Assert.NotNull(entry);
        Assert.True(entry!.CanRoute);
        Assert.True(entry.CanLaunch);
        Assert.Equal(
            [
                @"C:\Apps\Sample\a-helper.exe",
                @"C:\Apps\Sample\sample.exe",
                @"C:\Apps\Sample\z-helper.exe",
            ],
            entry.ExecutablePaths);
        Assert.Equal(entry.ExecutablePaths, snapshot.ExpandSelected([target.DiscoveryKey, target.DiscoveryKey]));
    }

    [Fact]
    public void Routing_capability_is_independent_from_launch_capability()
    {
        var target = new LaunchTarget
        {
            Id = "wrapper",
            Name = "Unlaunchable wrapper",
            Kind = LaunchKind.CliWrapperResolved,
            Command = "sample.cmd",
            ResolutionUnsupported = true,
            OwnedExecutables = [@"C:\Apps\Sample\helper.exe"],
        };

        ApplicationInventoryEntry entry = Assert.Single(ApplicationInventorySnapshot.Create([target]).Entries);
        Assert.True(entry.CanRoute);
        Assert.False(entry.CanLaunch);
    }

    [Fact]
    public void Packaged_target_without_resolved_exe_can_launch_but_is_not_routable_yet()
    {
        var target = new LaunchTarget
        {
            Id = "package",
            Name = "Package",
            Kind = LaunchKind.PackagedAumid,
            Aumid = "Contoso.Package!App",
        };

        ApplicationInventoryEntry entry = Assert.Single(ApplicationInventorySnapshot.Create([target]).Entries);
        Assert.False(entry.CanRoute);
        Assert.True(entry.CanLaunch);
    }
}
