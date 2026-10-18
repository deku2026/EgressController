using EgressController.App;
using EgressController.Core.Profile;
using EgressController.Core.Models;
using EgressController.Launcher.Discovery;

namespace EgressController.Windows.IntegrationTests;

public sealed class FixedInventoryRoutingTests
{
    [Fact]
    public void Refreshed_metadata_does_not_enroll_new_paths_until_explicit_inventory_commit()
    {
        const string main = @"C:\Games\Example\main.exe", unapproved = @"C:\Games\Example\new-helper.exe";
        var target = new LaunchTarget { Id = main, Name = "Example", CanonicalExecutable = main, OwnedExecutables = [main, unapproved] };
        var profile = EgressProfileDocument.Default with { Applications = [new() { DiscoveryKey = target.DiscoveryKey, ExecutablePaths = [main] }] };
        var route = Assert.Single(AppController.ResolveApplicationRoutes(profile, ApplicationInventorySnapshot.Create([target])));
        Assert.Equal(main, Assert.Single(route.ExecutablePaths));
    }

    [Fact]
    public void Discovery_metadata_and_order_do_not_request_a_new_runtime_but_explicit_new_exes_do()
    {
        var selection = new EgressApplicationSelection { DiscoveryKey = @"exe:C:\Apps\AI\main.exe",
            ExecutablePaths = [@"C:\Apps\AI\main.exe", @"C:\Apps\AI\nested\helper.exe"], DirectoryRoots = [@"C:\Apps\AI"] };
        var original = EgressProfileDocument.Default with { Applications = [selection] };
        var metadata = original with { Applications = [selection with { DisplayName = "Renamed", DirectoryRoots = [@"C:\Link"],
            ExecutablePaths = [selection.ExecutablePaths[1], selection.ExecutablePaths[0], selection.ExecutablePaths[0]] }] };
        Assert.Equal(AppController.ApplicationRoutingKey(original), AppController.ApplicationRoutingKey(metadata));
        var newInventory = original with { Applications = [selection with { ExecutablePaths = [.. selection.ExecutablePaths, @"C:\Apps\AI\new.exe"] }] };
        Assert.NotEqual(AppController.ApplicationRoutingKey(original), AppController.ApplicationRoutingKey(newInventory));
    }
}
