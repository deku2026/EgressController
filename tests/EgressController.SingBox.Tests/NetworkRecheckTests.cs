using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Protection;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class NetworkRecheckTests
{
    [Theory]
    [InlineData(NetworkHealthCheck.DirectInternet, true, false)]
    [InlineData(NetworkHealthCheck.DirectInternet, false, true)]
    [InlineData(NetworkHealthCheck.ProxyInternet, true, false)]
    [InlineData(NetworkHealthCheck.ProxyInternet, false, true)]
    public async Task Pending_recheck_retains_previous_process_decision_then_applies_the_completed_result(
        NetworkHealthCheck check, bool previous, bool next)
    {
        var state = CreateState();
        state.Complete(state.Generation, check, new(previous, "previous"), CancellationToken.None);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processes = new FakeProcesses();
        var guard = new ProcessProtection(processes);
        int generation = state.Generation;
        async Task Recheck()
        {
            NetworkHealthResult result = await InternetHealthProbe.RunAsync(check, async (_, _) =>
                new SingBoxDelayResponse { Delay = await completion.Task ? 12 : -1 }, TestContext.Current.CancellationToken);
            Assert.True(state.Complete(generation, check, result, TestContext.Current.CancellationToken));
        }
        void Sweep()
        {
            var ready = ProtectionReadiness.Evaluate(true, null, true, null, null, state.Readiness);
            guard.Sweep([new(@"C:\mock\app.exe", "Mock app")], new HashSet<string>(), ready.Ready, ready.Reason);
        }
        var pending = Recheck();
        Assert.False(pending.IsCompleted);
        Assert.Equal(previous, state.Readiness.Ready);
        Sweep();
        int killedWhilePending = previous ? 0 : 1;
        Assert.Equal(killedWhilePending, processes.Terminations);
        completion.SetResult(next);
        await pending;
        Assert.Equal(next, state.Readiness.Ready);
        Sweep();
        Assert.Equal(killedWhilePending + (next ? 0 : 1), processes.Terminations);
    }

    private static NetworkHealthState CreateState()
    {
        var direct = new AdapterSelection
        {
            AdapterId = Guid.NewGuid(), Alias = "ESIM", Luid = 1, IfIndex = 1, Ipv6IfIndex = 1,
            IsUp = true, AddressState = AdapterAddressState.Ipv4Only, Ipv4BindAddress = IPAddress.Parse("192.0.2.10"),
        };
        var state = new NetworkHealthState();
        state.ObserveAdapters(new() { DefaultAdapter = direct, DnsAdapter = direct,
            ProxyAdapter = direct with { AdapterId = Guid.NewGuid(), Alias = "Proxy", Ipv4BindAddress = IPAddress.Parse("198.51.100.10") } });
        state.Complete(state.Generation, NetworkHealthCheck.DirectInternet, new(true, "healthy"), CancellationToken.None);
        state.Complete(state.Generation, NetworkHealthCheck.ProxyInternet, new(true, "healthy"), CancellationToken.None);
        return state;
    }

    private sealed class FakeProcesses : IProcessControl
    {
        public int Terminations { get; private set; }
        public IReadOnlyList<GuardProcess> Capture() => [new(new(1, DateTime.UnixEpoch), 0, @"C:\mock\app.exe")];
        public TerminationResult Terminate(GuardProcess process) { Terminations++; return new(true); }
    }
}
