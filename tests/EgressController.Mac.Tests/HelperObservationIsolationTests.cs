// SPDX-License-Identifier: MIT
using EgressController.Mac;
using EgressController.MacHelper;
using EgressController.Runtime;

namespace EgressController.Mac.Tests;

public sealed class HelperObservationIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalObservationFailureDoesNotFailHeartbeatOrRequestNetworkRestoration(bool throwFromObserver)
    {
        // Instantiate only the injected controller, never Program/NativeRuntimeEnvironment/installer.
        var environment = new EnvironmentFixture { ThrowFromObserver = throwFromObserver };
        var controller = new RuntimeController(environment);
        var reply = await controller.ExecuteAsync(new(2, Guid.NewGuid(), 1, MacHelperCommand.Heartbeat), TestContext.Current.CancellationToken);
        Assert.True(reply.Succeeded); Assert.True(reply.Status.TunTakenOver); Assert.True(reply.Status.Running);
        Assert.False(reply.Connections!.IsAvailable);
        Assert.Equal(0, environment.Restores); Assert.Equal(0, environment.Mutations);
        await controller.CheckCoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, environment.Restores); // Independent core health is still confirmed.
    }

    private sealed class EnvironmentFixture : IPrivilegedRuntimeEnvironment
    {
        public bool ThrowFromObserver;
        public int Restores, Mutations;
        public Task ValidateAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken) { Mutations++; return Task.CompletedTask; }
        public Task ApplyAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken) { Mutations++; return Task.CompletedTask; }
        public Task RestoreAsync(CancellationToken cancellationToken) { Restores++; return Task.CompletedTask; }
        public MacHelperStatus Inspect() => new(true, true, true, false, 1234, "generation", null, DateTimeOffset.UtcNow);
        public CoreConnectionSnapshot ReadConnections() => ThrowFromObserver ? throw new IOException("Synthetic observation error.") : new(null!, DateTimeOffset.UtcNow);
    }
}
