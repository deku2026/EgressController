using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Protection;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.Core.Tests;

public sealed class NetworkHealthStateTests
{
    [Fact]
    public void Startup_requires_both_completed_checks_even_with_confirmed_tun()
    {
        var state = new NetworkHealthState();
        int generation = state.ObserveEnvironment(Environment(), 7890);
        Assert.False(Ready(state).Ready);
        Assert.True(state.Complete(generation, NetworkHealthCheck.DirectInternet, new(true, "HTTPS OK"), CancellationToken.None));
        Assert.False(Ready(state).Ready);
        Assert.True(state.Complete(generation, NetworkHealthCheck.ProxyInternet, new(true, "HTTPS OK"), CancellationToken.None));
        Assert.True(Ready(state).Ready);
    }

    [Theory]
    [InlineData(NetworkHealthCheck.DirectInternet)]
    [InlineData(NetworkHealthCheck.ProxyInternet)]
    public void Completed_failure_closes_every_selected_instance_and_recovery_stops_termination(NetworkHealthCheck check)
    {
        var state = Healthy();
        var processes = new FakeProcesses();
        var guard = new ProcessProtection(processes);
        ProtectedExecutable[] targets = [new(@"C:\Apps\a.exe", "A")];
        HashSet<string> exclusions = [];
        void Sweep()
        {
            var readiness = Ready(state);
            guard.Sweep(targets, exclusions, readiness.Ready, readiness.Reason);
        }
        Sweep();
        Assert.Empty(processes.Killed);
        state.Complete(state.Generation, check, new(false, "mock offline"), CancellationToken.None);
        Sweep();
        Assert.Equal(new uint[] { 1, 2 }, processes.Killed.Order().ToArray());
        Assert.All(guard.Events, item => Assert.Contains("mock offline", item.Reason));
        Sweep(); // Failure remains authoritative while the next check is pending.
        Assert.Equal(4, processes.Killed.Count);
        state.Complete(state.Generation, check, new(true, "recovered"), CancellationToken.None);
        Sweep();
        Assert.Equal(4, processes.Killed.Count);
    }

    [Theory]
    [InlineData(false, "down")]
    [InlineData(false, "no-ip")]
    [InlineData(false, "missing")]
    [InlineData(true, "down")]
    [InlineData(true, "no-ip")]
    [InlineData(true, "missing")]
    public void Either_live_adapter_failure_overrides_cached_healthy_bindings(bool proxy, string failure)
    {
        var environment = Environment();
        var state = Healthy();
        var cache = new AdapterBindingCache();
        cache.Resolve(environment);
        var adapter = proxy ? environment.ProxyAdapter : environment.DefaultAdapter;
        var offline = failure switch
        {
            "down" => adapter with { IsUp = false },
            "no-ip" => adapter with { Ipv4BindAddress = null, AddressState = AdapterAddressState.NoAddress },
            _ => adapter with { Alias = "网卡未连接", IsUp = false, Ipv4BindAddress = null, Luid = 0 },
        };
        var live = proxy ? environment with { ProxyAdapter = offline }
            : environment with { DefaultAdapter = offline, DnsAdapter = offline };
        var retained = cache.Resolve(live);
        Assert.True(retained.ProxyAdapter.IsReady);
        Assert.True(retained.DefaultAdapter.IsReady);
        int stale = state.Generation;
        state.ObserveEnvironment(live, 7890);
        Assert.False(Ready(state).Ready);
        Assert.Contains(proxy ? "非家宽" : "家宽", Ready(state).Reason);
        Assert.False(state.Complete(stale, proxy ? NetworkHealthCheck.ProxyInternet : NetworkHealthCheck.DirectInternet,
            new(true, "late success"), CancellationToken.None));
        state.ObserveEnvironment(environment, 7890); // Same IP on reconnect still needs a new completed result.
        Assert.False(Ready(state).Ready);
        state.Complete(state.Generation, proxy ? NetworkHealthCheck.ProxyInternet : NetworkHealthCheck.DirectInternet,
            new(true, "reconnected"), CancellationToken.None);
        Assert.True(Ready(state).Ready);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Canceled_results_do_not_replace_the_last_completed_result(bool healthy)
    {
        var state = Healthy();
        state.Complete(state.Generation, NetworkHealthCheck.DirectInternet, new(healthy, "previous"), CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.False(state.Complete(state.Generation, NetworkHealthCheck.DirectInternet, new(!healthy, "canceled"), canceled.Token));
        Assert.Equal(healthy, Ready(state).Ready);
    }

    [Fact]
    public void Configuration_reset_rejects_old_success_and_failure_and_requires_new_checks()
    {
        var state = Healthy();
        int stale = state.Generation;
        state.Reset();
        Assert.False(state.Complete(stale, NetworkHealthCheck.DirectInternet, new(true, "old success"), CancellationToken.None));
        Assert.False(state.Complete(stale, NetworkHealthCheck.ProxyInternet, new(false, "old failure"), CancellationToken.None));
        Assert.False(Ready(state).Ready);
        CompleteHealthy(state);
        Assert.True(Ready(state).Ready);
    }

    [Fact]
    public void Replacing_or_readdressing_an_online_adapter_requires_a_new_probe()
    {
        var state = Healthy();
        var environment = Environment();
        var replaced = environment with { ProxyAdapter = environment.ProxyAdapter with { AdapterId = Guid.NewGuid() } };
        state.ObserveEnvironment(replaced, 7890);
        Assert.False(Ready(state).Ready);
        CompleteHealthy(state);
        state.ObserveEnvironment(replaced with { ProxyAdapter = replaced.ProxyAdapter with { Ipv4BindAddress = IPAddress.Parse("198.51.100.22") } }, 7890);
        Assert.False(Ready(state).Ready);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changing_default_proxy_discards_old_port_results_and_requires_new_proxy_success(bool oldHealthy)
    {
        var state = Healthy();
        int oldGeneration = state.Generation;
        var completion = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = InternetHealthProbe.RunAsync(NetworkHealthCheck.ProxyInternet, 7890, (outbound, _) =>
        {
            Assert.Equal("upstream-7890", outbound);
            return completion.Task;
        }, TestContext.Current.CancellationToken);

        state.ObserveEnvironment(Environment(), 1080);
        Assert.False(Ready(state).Ready);
        Assert.Contains("1080", state.ProxyStatus);
        Assert.Contains("通过", state.DirectStatus); // Unchanged residential result remains valid.
        completion.SetResult(new() { Delay = oldHealthy ? 12 : -1 });
        Assert.False(state.Complete(oldGeneration, NetworkHealthCheck.ProxyInternet, await pending, CancellationToken.None));
        Assert.False(Ready(state).Ready);

        NetworkHealthResult fresh = await InternetHealthProbe.RunAsync(NetworkHealthCheck.ProxyInternet, 1080, (outbound, _) =>
        {
            Assert.Equal("upstream-1080", outbound);
            return Task.FromResult(new SingBoxDelayResponse { Delay = 20 });
        }, TestContext.Current.CancellationToken);
        Assert.True(state.Complete(state.Generation, NetworkHealthCheck.ProxyInternet, fresh, CancellationToken.None));
        Assert.True(Ready(state).Ready);
    }

    [Fact]
    public void Direct_only_mode_requires_residential_health_and_both_live_adapters_without_a_proxy_result()
    {
        var state = new NetworkHealthState();
        var environment = Environment();
        state.ObserveEnvironment(environment, null);
        Assert.False(Ready(state).Ready);
        state.Complete(state.Generation, NetworkHealthCheck.DirectInternet, new(true, "HTTPS OK"), CancellationToken.None);
        Assert.True(Ready(state).Ready);
        Assert.Contains("未配置代理端口", state.ProxyStatus);
        Assert.False(state.Complete(state.Generation, NetworkHealthCheck.ProxyInternet, new(false, "unused"), CancellationToken.None));
        Assert.True(Ready(state).Ready);

        state.ObserveEnvironment(environment with { ProxyAdapter = environment.ProxyAdapter with { IsUp = false } }, null);
        Assert.False(Ready(state).Ready);
    }

    [Fact]
    public void Removing_and_readding_default_proxy_cannot_reuse_its_old_health()
    {
        var state = Healthy();
        int oldGeneration = state.Generation;
        state.ObserveEnvironment(Environment(), null);
        Assert.True(Ready(state).Ready);
        state.ObserveEnvironment(Environment(), 7890);
        Assert.False(Ready(state).Ready);
        Assert.False(state.Complete(oldGeneration, NetworkHealthCheck.ProxyInternet, new(true, "old result"), CancellationToken.None));
        state.Complete(state.Generation, NetworkHealthCheck.ProxyInternet, new(true, "new result"), CancellationToken.None);
        Assert.True(Ready(state).Ready);
    }

    [Fact]
    public void Retry_progress_and_clearing_it_never_override_health_or_a_new_generation()
    {
        var state = Healthy();
        int generation = state.Generation;
        Assert.True(state.ReportProgress(generation, NetworkHealthCheck.DirectInternet, "1/3 failure; retrying", CancellationToken.None));
        Assert.True(Ready(state).Ready);
        Assert.Contains("1/3", state.DirectStatus);
        Assert.False(ProtectionReadiness.Evaluate(false, "TUN exited", true, null, null, state.Readiness).Ready);
        state.ClearProgress(generation);
        Assert.True(Ready(state).Ready);
        Assert.DoesNotContain("1/3", state.DirectStatus);
        state.Reset();
        Assert.False(state.ReportProgress(generation, NetworkHealthCheck.DirectInternet, "stale", CancellationToken.None));
        state.ReportProgress(state.Generation, NetworkHealthCheck.DirectInternet, "fresh", CancellationToken.None);
        state.ClearProgress(generation);
        Assert.Contains("fresh", state.DirectStatus);
        Assert.False(Ready(state).Ready); // No success confirmed since reset.
    }

    private static ProtectionReadiness Ready(NetworkHealthState state)
        => ProtectionReadiness.Evaluate(true, null, true, null, null, state.Readiness);
    private static NetworkHealthState Healthy()
    {
        var state = new NetworkHealthState();
        state.ObserveEnvironment(Environment(), 7890);
        CompleteHealthy(state);
        return state;
    }
    private static void CompleteHealthy(NetworkHealthState state)
    {
        state.Complete(state.Generation, NetworkHealthCheck.DirectInternet, new(true, "OK"), CancellationToken.None);
        state.Complete(state.Generation, NetworkHealthCheck.ProxyInternet, new(true, "OK"), CancellationToken.None);
    }
    private static NetworkEnvironmentSnapshot Environment()
    {
        var direct = Adapter("11111111-1111-1111-1111-111111111111", "ESIM", "192.0.2.10");
        var proxy = Adapter("22222222-2222-2222-2222-222222222222", "Proxy", "198.51.100.10");
        return new() { DefaultAdapter = direct, DnsAdapter = direct, ProxyAdapter = proxy };
    }
    private static AdapterSelection Adapter(string id, string name, string ip) => new()
    {
        AdapterId = Guid.Parse(id), Alias = name, Luid = 1, IfIndex = 1, Ipv6IfIndex = 1, IsUp = true,
        Ipv4BindAddress = IPAddress.Parse(ip), AddressState = AdapterAddressState.Ipv4Only,
    };
    private sealed class FakeProcesses : IProcessControl
    {
        public List<uint> Killed { get; } = [];
        public IReadOnlyList<GuardProcess> Capture() => [
            new(new(1, DateTime.UnixEpoch), 0, @"C:\Apps\a.exe"),
            new(new(2, DateTime.UnixEpoch), 0, @"C:\Apps\a.exe"),
            new(new(3, DateTime.UnixEpoch), 0, @"C:\Other\b.exe")];
        public TerminationResult Terminate(GuardProcess process) { Killed.Add(process.Key.Pid); return new(true); }
    }
}
