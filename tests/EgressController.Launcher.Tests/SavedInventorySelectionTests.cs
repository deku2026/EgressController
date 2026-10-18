using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class SavedInventorySelectionTests
{
    [Fact]
    public void Changing_exit_preserves_previously_saved_executables_even_if_the_current_scan_misses_them()
    {
        var app = new LaunchTarget { Id = "app", Name = "AI", Kind = LaunchKind.DirectExe,
            CanonicalExecutable = @"C:\AI\main.exe", Command = @"C:\AI\main.exe",
            OwnedExecutables = [@"C:\AI\main.exe", @"C:\AI\v2\helper.exe"] };
        var saved = new EgressApplicationSelection { DiscoveryKey = app.DiscoveryKey,
            ExecutablePaths = [@"C:\AI\main.exe", @"C:\AI\v1\helper.exe"], Target = EgressRouteTarget.DefaultAdapter };
        var updated = ApplicationInventorySnapshot.Create([app]).CaptureSelection(app.DiscoveryKey,
            EgressRouteTarget.ForPort(1080), saved);
        Assert.Equal(3, updated.ExecutablePaths.Count);
        Assert.Contains(@"C:\AI\v1\helper.exe", updated.ExecutablePaths);
        Assert.Equal(EgressRouteTarget.ForPort(1080), updated.Target);
        Assert.Throws<ArgumentException>(() => ApplicationInventorySnapshot.Create([app])
            .CaptureSelection("other", EgressRouteTarget.DefaultAdapter, saved));
    }
}
