using EgressController.App;
using EgressController.Core.Profile;

namespace EgressController.Windows.IntegrationTests;

public sealed class FixedInventoryRoutingTests
{
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
