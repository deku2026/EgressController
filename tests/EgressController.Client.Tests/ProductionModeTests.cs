using System.Buffers.Binary;
using EgressController.Desktop;
using EgressController.Mac;
using EgressController.MacHelper;

namespace EgressController.Client.Tests;

public sealed class ProductionModeTests
{
    [Fact]
    public void ProductionIsDefaultAndOnlyExplicitFlagsEnterPreview()
    {
        Assert.Equal(StartupMode.Production, StartupArguments.Resolve([]).Mode);
        Assert.Equal(StartupMode.Preview, StartupArguments.Resolve(["--preview"]).Mode);
        Assert.Equal(StartupMode.MockSmoke, StartupArguments.Resolve(["--mock-smoke"]).Mode);
        Assert.Equal("apps", StartupArguments.Resolve(["--preview", "--scenario", "apps"]).Scenario);
    }
    [Theory]
    [InlineData("--live")]
    [InlineData("--unknown")]
    [InlineData("--scenario")]
    public void InvalidArgumentsCannotFallThroughIntoProduction(string argument)
        => Assert.Throws<ArgumentException>(() => StartupArguments.Resolve([argument]));
    [Fact]
    public void ConflictingModesAndProductionScenariosAreRejected()
    {
        Assert.Throws<ArgumentException>(() => StartupArguments.Resolve(["--preview", "--mock-smoke"]));
        Assert.Throws<ArgumentException>(() => StartupArguments.Resolve(["--scenario", "apps"]));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DnsAndCoreRestorationAreAttemptedIndependently(bool coreFails, bool dnsFails)
    {
        bool stopped = false, restored = false;
        var outcome = await RecoverySteps.ExecuteAsync(() => { stopped = true; if (coreFails) throw new IOException(); return Task.CompletedTask; },
            () => { restored = true; if (dnsFails) throw new IOException(); return Task.CompletedTask; });
        Assert.True(stopped); Assert.True(restored); Assert.Equal(!coreFails, outcome.CoreStopped); Assert.Equal(!dnsFails, outcome.DnsRestored);
    }
    [Fact]
    public async Task InvalidNewConfigurationDoesNotStopAnExistingRuntime()
    {
        var fake = new FakeEnvironment { ValidationFails = true };
        var controller = new RuntimeController(fake);
        var response = await controller.ExecuteAsync(Request(MacHelperCommand.Apply), TestContext.Current.CancellationToken);
        Assert.False(response.Succeeded); Assert.Equal(0, fake.Applies); Assert.Equal(0, fake.Restorations);
    }
    [Fact]
    public async Task FailedMutationAndClientLossRestoreWithoutInternetOrTelemetryInputs()
    {
        var fake = new FakeEnvironment { ApplyFails = true };
        var controller = new RuntimeController(fake);
        Assert.False((await controller.ExecuteAsync(Request(MacHelperCommand.Apply), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(1, fake.Applies); Assert.Equal(1, fake.Restorations);
        await controller.RecoverAsync(); Assert.Equal(2, fake.Restorations);
    }
    [Fact]
    public async Task OversizedOrTruncatedIpcFramesAreRejectedWithoutAnyAdapter()
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, 1_048_577);
        await Assert.ThrowsAsync<InvalidDataException>(() => EgressController.MacHelper.Program.ReadFrameAsync(new MemoryStream(header), TestContext.Current.CancellationToken));
        BinaryPrimitives.WriteUInt32BigEndian(header, 5);
        await Assert.ThrowsAsync<EndOfStreamException>(() => EgressController.MacHelper.Program.ReadFrameAsync(new MemoryStream(header), TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData("proxy.port-in-use")]
    [InlineData("tun.in-use")]
    [InlineData("interface.unavailable")]
    [InlineData("core.permission")]
    [InlineData("tun.timeout")]
    public async Task PhysicalStartupFailureSurvivesIpcWithoutRawExceptionText(string code)
    {
        var fake = new FakeEnvironment { FailureCode = code };
        var reply = await new RuntimeController(fake).ExecuteAsync(Request(MacHelperCommand.Apply), TestContext.Current.CancellationToken);
        Assert.False(reply.Succeeded); Assert.Equal(code, reply.Code);
        Assert.Equal(EgressController.Runtime.RuntimeFailures.Message(code), reply.Message);
        Assert.Equal(1, fake.Restorations);
    }
    private static MacHelperWireRequest Request(MacHelperCommand command) => new(2, Guid.NewGuid(), 1, command,
        new("fixture-generation", "/fixture/staged", "fixture-hash", "/fixture/core", "fixture-core-hash", "fixture-home", "fixture-other"));
    private sealed class FakeEnvironment : IPrivilegedRuntimeEnvironment
    {
        public bool ValidationFails { get; set; }
        public bool ApplyFails { get; set; }
        public string? FailureCode { get; set; }
        public int Applies { get; private set; }
        public int Restorations { get; private set; }
        public Task ValidateAsync(MacHelperApplyPlan plan, CancellationToken token) { if (ValidationFails) throw new InvalidDataException(); return Task.CompletedTask; }
        public Task ApplyAsync(MacHelperApplyPlan plan, CancellationToken token) { Applies++; if (FailureCode is { } code) throw new EgressController.Runtime.RuntimeFailureException(code); if (ApplyFails) throw new IOException(); return Task.CompletedTask; }
        public Task RestoreAsync(CancellationToken token) { Restorations++; return Task.CompletedTask; }
        public MacHelperStatus Inspect() => new(true, true, true, false, 42, "fixture", null, DateTimeOffset.UtcNow);
    }
}
