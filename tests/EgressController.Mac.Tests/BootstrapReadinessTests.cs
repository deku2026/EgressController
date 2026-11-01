using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.Mac.Tests;

public sealed class BootstrapReadinessTests
{
    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, true)]
    [InlineData(false, true, false, false, true, false)]
    [InlineData(false, true, true, true, false, true)]
    public void ExitRetainsRecoveryObligationAfterStatusReadFailure(bool owned, bool installed, bool running, bool tun, bool dns, bool expected)
        => Assert.Equal(expected, MacRuntimeReadiness.RequiresExitRecovery(owned, new(installed, running, tun, dns, 0, null, null, DateTimeOffset.UtcNow)));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    public void ExplicitRetryRecoversOnlyConfirmedRuntimeLoss(bool applied, bool running, bool tun, bool recover)
        => Assert.Equal(recover, MacRuntimeReadiness.RequiresRecovery(new(true, running, tun, !running, 0, null, null, DateTimeOffset.UtcNow), applied));

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "status-unavailable")]
    public void ReadFailureCannotAuthorizeRuntimeReplacement(bool installed, string? failure)
        => Assert.Throws<RuntimeFailureException>(() => MacRuntimeReadiness.RequiresRecovery(new(installed, false, false, false, 0, null, failure, DateTimeOffset.UtcNow), true));
}
