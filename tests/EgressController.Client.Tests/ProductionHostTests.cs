using EgressController.Desktop;
using EgressController.Desktop.Preview;

namespace EgressController.Client.Tests;

public sealed class ProductionHostTests
{
    [Theory]
    [InlineData(StartupMode.Production, true, true, true)]
    [InlineData(StartupMode.Production, false, true, false)]
    [InlineData(StartupMode.Production, true, false, false)]
    [InlineData(StartupMode.Preview, true, true, false)]
    [InlineData(StartupMode.MockSmoke, true, true, false)]
    [InlineData(StartupMode.Preview, false, true, false)]
    public void OnlyManagedProductionUsesTheCompleteNativeProduct(StartupMode mode, bool managed, bool mac, bool expected)
        => Assert.Equal(expected, ProductionHostLauncher.NeedsHandoff(mode, managed, mac));

    [Fact]
    public void ProductPathBelongsToThisBuildOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "synthetic-build-output");
        Assert.Equal(Path.Combine(directory, "production", "Egress.app", "Contents", "MacOS", "EgressController.Desktop"), ProductionHostLauncher.ExecutablePath(directory));
    }

    [Fact]
    public void MockSafetyRejectsProcessReplacementBeforeAnyNativeOperation()
    {
        PreviewEnvironment.Enter();
        Assert.Throws<InvalidOperationException>(() => ProductionHostLauncher.ReplaceWithNativeProduct("/synthetic/not-present"));
    }
}
