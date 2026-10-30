using EgressController.Mac;
using EgressController.MacHelper;

namespace EgressController.Client.Tests;

public sealed class HelperReviewTests
{
    [Fact]
    public async Task ApplyCancellationAlwaysUsesAnUncancelableRecoveryToken()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var environment = new FakeEnvironment { CancelDuringApply = cancellation };
        var response = await new RuntimeController(environment).ExecuteAsync(Request(MacHelperCommand.Apply), cancellation.Token);
        Assert.Equal("canceled", response.Code); Assert.False(response.Succeeded);
        Assert.Equal(1, environment.Validations); Assert.Equal(1, environment.Applications); Assert.Equal(1, environment.Restorations);
        Assert.False(environment.RecoveryToken.CanBeCanceled);
    }

    [Theory]
    [InlineData(MacHelperCommand.Inspect)]
    [InlineData(MacHelperCommand.Heartbeat)]
    public async Task StatusAndHeartbeatDoNotMutateOrRevalidateTheCore(MacHelperCommand command)
    {
        var environment = new FakeEnvironment();
        var request = new MacHelperWireRequest(2, Guid.NewGuid(), 1, command);
        var reply = await new RuntimeController(environment).ExecuteAsync(request, TestContext.Current.CancellationToken);
        Assert.True(reply.Succeeded); Assert.True(reply.Status.Running); Assert.True(reply.Status.TunTakenOver);
        Assert.Equal(0, environment.Validations); Assert.Equal(0, environment.Applications); Assert.Equal(0, environment.Restorations);
    }

    [Theory]
    [InlineData(MacHelperCommand.Apply)]
    [InlineData(MacHelperCommand.Validate)]
    public async Task MissingApplyPlanIsRejectedBeforeAnyAdapterMutation(MacHelperCommand command)
    {
        var environment = new FakeEnvironment();
        var reply = await new RuntimeController(environment).ExecuteAsync(new(2, Guid.NewGuid(), 1, command), TestContext.Current.CancellationToken);
        Assert.False(reply.Succeeded); Assert.Equal("operation-failed", reply.Code);
        Assert.Equal(0, environment.Validations); Assert.Equal(0, environment.Applications); Assert.Equal(0, environment.Restorations);
    }

    [Fact]
    public async Task ValidationExceptionsNeverExposeInjectedCredentialsToIpc()
    {
        var environment = new FakeEnvironment { ValidationError = new IOException("fixture-password=synthetic-secret; server=private.synthetic.invalid") };
        var reply = await new RuntimeController(environment).ExecuteAsync(Request(MacHelperCommand.Apply), TestContext.Current.CancellationToken);
        Assert.False(reply.Succeeded); Assert.Equal("operation-failed", reply.Code);
        Assert.DoesNotContain("synthetic-secret", reply.ToString()); Assert.DoesNotContain("private.synthetic.invalid", reply.ToString());
        Assert.Equal(0, environment.Applications); Assert.Equal(0, environment.Restorations);
    }

    [Theory]
    [InlineData(true, false, true, 1)]
    [InlineData(false, false, true, 1)]
    [InlineData(true, true, true, 0)]
    [InlineData(false, false, false, 0)]
    public async Task WatchdogRestoresOnlyAnActiveCoreOrTunFailure(bool running, bool takeover, bool hasGeneration, int expectedRestorations)
    {
        var environment = new FakeEnvironment
        {
            Running = running,
            Takeover = takeover,
            HasGeneration = hasGeneration,
            Failure = "Historical Internet probe failure is not a guardian input."
        };
        await new RuntimeController(environment).CheckCoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedRestorations, environment.Restorations); Assert.Equal(0, environment.Validations); Assert.Equal(0, environment.Applications);
        if (expectedRestorations > 0) Assert.False(environment.RecoveryToken.CanBeCanceled);
    }

    [Fact]
    public async Task StaleHelperReplyCannotRetainThePreviouslyHealthyStatus()
    {
        await using var client = new MacHelperClient(new StaleConnection());
        Assert.True((await client.InspectAsync(TestContext.Current.CancellationToken)).TunTakenOver);
        var stale = await client.InspectAsync(TestContext.Current.CancellationToken);
        Assert.False(stale.Running); Assert.False(stale.TunTakenOver); Assert.NotNull(stale.Failure);
    }

    private static MacHelperWireRequest Request(MacHelperCommand command) => new(2, Guid.NewGuid(), 1, command,
        new("fixture-generation", "/fixture/generation", "fixture-manifest", "/fixture/core", "fixture-core", "fixture-home", "fixture-nonhome"));

    private sealed class FakeEnvironment : IPrivilegedRuntimeEnvironment
    {
        public CancellationTokenSource? CancelDuringApply; public Exception? ValidationError;
        public bool Running = true, Takeover = true, HasGeneration = true; public string? Failure;
        public int Validations, Applications, Restorations; public CancellationToken RecoveryToken;
        public Task ValidateAsync(MacHelperApplyPlan plan, CancellationToken token)
        { Validations++; if (ValidationError is not null) throw ValidationError; return Task.CompletedTask; }
        public Task ApplyAsync(MacHelperApplyPlan plan, CancellationToken token)
        {
            Applications++;
            if (CancelDuringApply is not null) { CancelDuringApply.Cancel(); throw new OperationCanceledException(token); }
            return Task.CompletedTask;
        }
        public Task RestoreAsync(CancellationToken token) { Restorations++; RecoveryToken = token; return Task.CompletedTask; }
        public MacHelperStatus Inspect() => new(true, Running, Takeover, false, Running ? 42 : 0, HasGeneration ? "fixture-generation" : null, Failure, DateTimeOffset.UtcNow);
    }
    private sealed class StaleConnection : IMacHelperConnection
    {
        private int _requests;
        public Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken token)
        {
            _requests++;
            var status = new MacHelperStatus(true, true, true, false, 42, "fixture-generation", null,
                _requests == 1 ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddMinutes(-1));
            return Task.FromResult(new MacHelperWireReply(request.SessionId, request.Sequence, true, "ok", "", status));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
