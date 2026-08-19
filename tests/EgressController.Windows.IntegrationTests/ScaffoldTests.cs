namespace EgressController.Windows.IntegrationTests;

/// <summary>
/// Placeholder so this project contributes tests immediately (MTP treats a zero-test run as
/// exit code 8). Real destructive tests (System Proxy / adapters) are gated behind
/// EgressController.TestGuard in later steps and run only on the target Windows machine.
/// </summary>
public class ScaffoldTests
{
    [Fact]
    public void Skeleton_is_ready() => Assert.True(true);
}