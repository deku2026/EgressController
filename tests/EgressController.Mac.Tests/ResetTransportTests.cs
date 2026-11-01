using EgressController.Mac;
using EgressController.MacHelper;
using EgressController.Runtime;
namespace EgressController.Mac.Tests;
public sealed class ResetTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelperDeadlineDoesNotMasqueradeAsUserCancellationAndCannotVetoLocalReset(bool callerCancels)
    {
        using var cancel = new CancellationTokenSource();
        await using var client = new MacHelperClient(new StalledConnection(), requestTimeout: TimeSpan.FromMilliseconds(20));
        if (callerCancels) cancel.Cancel();
        bool saved = false;
        var operation = ConfigurationReset.ExecuteAsync(new(), async token => (await client.StopAndRestoreAsync(token)).Succeeded,
            (_, _) => { saved = true; return Task.CompletedTask; }, cancel.Token);
        if (callerCancels) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.False(saved); }
        else { Assert.False((await operation).NetworkRestored); Assert.True(saved); Assert.Equal("helper.timeout", client.TransportFailureCode); }
        Assert.False(client.Status.Running); Assert.False(client.Status.DnsRestored);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetDispatchIsAuthenticatedMutationSeparateFromApplyAndFailureIsRedacted(bool fail)
    {
        var environment = new ResetEnvironment { Fail = fail }; var controller = new RuntimeController(environment);
        var reply = await controller.ExecuteAsync(new(2, Guid.NewGuid(), 1, MacHelperCommand.ResetAndRestore), TestContext.Current.CancellationToken);
        Assert.Equal(!fail, reply.Succeeded); Assert.Equal(1, environment.Resets); Assert.Equal(0, environment.Applies);
        Assert.Equal(!fail, reply.Status.DnsRestored); Assert.DoesNotContain("private", reply.Message);
    }
    private sealed class StalledConnection : IMacHelperConnection
    {
        public async Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ResetEnvironment : IPrivilegedRuntimeEnvironment
    {
        public bool Fail; public int Resets, Applies; private bool _restored;
        public Task ValidateAsync(MacHelperApplyPlan plan, CancellationToken token) { Applies++; return Task.CompletedTask; }
        public Task ApplyAsync(MacHelperApplyPlan plan, CancellationToken token) { Applies++; return Task.CompletedTask; }
        public Task RestoreAsync(CancellationToken token) => throw new InvalidOperationException("Reset dispatch must use explicit cleanup operation");
        public Task ResetAsync(CancellationToken token)
        { Resets++; if (Fail) throw new IOException("private recovery journal failure"); _restored = true; return Task.CompletedTask; }
        public MacHelperStatus Inspect() => new(true, false, false, _restored, 0, null, null, DateTimeOffset.UtcNow);
    }
}
