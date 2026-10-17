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
            NetworkHealthResult result = await InternetHealthProbe.RunWithRetriesAsync(check, 1080, async (_, _) =>
                new SingBoxDelayResponse { Delay = await completion.Task ? 12 : -1 }, TestContext.Current.CancellationToken,
                    delay: (_, _) => Task.CompletedTask);
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

    [Theory]
    [InlineData(NetworkHealthCheck.DirectInternet, false)]
    [InlineData(NetworkHealthCheck.DirectInternet, true)]
    [InlineData(NetworkHealthCheck.ProxyInternet, false)]
    [InlineData(NetworkHealthCheck.ProxyInternet, true)]
    public async Task First_two_failures_keep_processes_running_and_only_a_third_failure_kills(NetworkHealthCheck check, bool recoverOnThird)
    {
        var state = CreateState();
        var processes = new FakeProcesses();
        var guard = new ProcessProtection(processes);
        var waits = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var entered = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        int calls = 0, delays = 0, generation = state.Generation;
        async Task Recheck()
        {
            var result = await InternetHealthProbe.RunWithRetriesAsync(check, 1080, (_, _) =>
                Task.FromResult(new SingBoxDelayResponse { Delay = ++calls == 3 && recoverOnThird ? 10 : -1 }),
                TestContext.Current.CancellationToken, detail => state.ReportProgress(generation, check, detail, CancellationToken.None),
                async (interval, token) =>
                {
                    Assert.Equal(TimeSpan.FromSeconds(5), interval);
                    int index = delays++;
                    entered[index].SetResult();
                    await waits[index].Task.WaitAsync(token);
                });
            Assert.True(state.Complete(generation, check, result, CancellationToken.None));
        }
        void Sweep()
        {
            var ready = ProtectionReadiness.Evaluate(true, null, true, null, null, state.Readiness);
            guard.Sweep([new(@"C:\mock\app.exe", "Mock app")], new HashSet<string>(), ready.Ready, ready.Reason);
        }
        var pending = Recheck();
        for (int index = 0; index < 2; index++)
        {
            await entered[index].Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(index + 1, calls);
            Assert.True(state.Readiness.Ready);
            Assert.Contains($"{index + 1}/3", check == NetworkHealthCheck.DirectInternet ? state.DirectStatus : state.ProxyStatus);
            Sweep();
            Assert.Equal(0, processes.Terminations);
            waits[index].SetResult();
        }
        await pending;
        Assert.Equal(3, calls);
        Sweep();
        Assert.Equal(recoverOnThird ? 0 : 1, processes.Terminations);
        if (!recoverOnThird) Assert.Contains("连续 3 次", state.Readiness.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reset_during_retry_discards_both_late_success_and_late_failure(bool recover)
    {
        var state = CreateState();
        int generation = state.Generation, calls = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = InternetHealthProbe.RunWithRetriesAsync(NetworkHealthCheck.ProxyInternet, 1080, (_, _) =>
            Task.FromResult(new SingBoxDelayResponse { Delay = ++calls > 1 && recover ? 1 : -1 }),
            TestContext.Current.CancellationToken, detail => state.ReportProgress(generation, NetworkHealthCheck.ProxyInternet, detail, CancellationToken.None),
            async (_, token) => { waiting.TrySetResult(); await resume.Task.WaitAsync(token); });
        await waiting.Task.WaitAsync(TestContext.Current.CancellationToken);
        state.Reset();
        state.Complete(state.Generation, NetworkHealthCheck.DirectInternet, new(true, "fresh direct"), CancellationToken.None);
        state.Complete(state.Generation, NetworkHealthCheck.ProxyInternet, new(true, "fresh proxy"), CancellationToken.None);
        resume.SetResult();
        Assert.False(state.Complete(generation, NetworkHealthCheck.ProxyInternet, await pending, CancellationToken.None));
        Assert.True(state.Readiness.Ready);
        Assert.Contains("fresh proxy", state.ProxyStatus);
    }

    [Fact]
    public async Task Confirmed_failure_is_published_without_waiting_for_the_other_exit()
    {
        var state = CreateState();
        int generation = state.Generation;
        var never = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        async Task Check(NetworkHealthCheck check)
        {
            var result = await InternetHealthProbe.RunWithRetriesAsync(check, 1080, (_, _) =>
                check == NetworkHealthCheck.DirectInternet ? Task.FromResult(new SingBoxDelayResponse { Delay = -1 }) : never.Task,
                canceled.Token, delay: (_, _) => Task.CompletedTask);
            state.Complete(generation, check, result, canceled.Token);
        }
        Task proxy = Check(NetworkHealthCheck.ProxyInternet);
        await Check(NetworkHealthCheck.DirectInternet);
        Assert.False(proxy.IsCompleted);
        Assert.False(state.Readiness.Ready);
        Assert.Contains("连续 3 次", state.Readiness.Reason);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proxy);
    }

    private static NetworkHealthState CreateState()
    {
        var direct = new AdapterSelection
        {
            AdapterId = Guid.NewGuid(), Alias = "ESIM", Luid = 1, IfIndex = 1, Ipv6IfIndex = 1,
            IsUp = true, AddressState = AdapterAddressState.Ipv4Only, Ipv4BindAddress = IPAddress.Parse("192.0.2.10"),
        };
        var state = new NetworkHealthState();
        state.ObserveEnvironment(new() { DefaultAdapter = direct, DnsAdapter = direct,
            ProxyAdapter = direct with { AdapterId = Guid.NewGuid(), Alias = "Proxy", Ipv4BindAddress = IPAddress.Parse("198.51.100.10") } }, 1080);
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
