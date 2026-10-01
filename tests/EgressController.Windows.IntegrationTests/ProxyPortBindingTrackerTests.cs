using System.Net;
using EgressController.Windows.Process;

namespace EgressController.Windows.IntegrationTests;

// Fake tables and process identities only; never opens a real socket or queries live PIDs.
public sealed class ProxyPortBindingTrackerTests
{
    private static readonly DateTime Started = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    [Fact]
    public void Multiple_ports_can_share_one_core_and_other_cores_are_deduplicated_by_path()
    {
        var source = new FakeResolver();
        source.Table[7890] = source.Table[7891] = [new(10, @"C:\Proxy\core.exe", Started)];
        source.Table[7892] = [new(20, @"C:\Proxy2\core.exe", Started)];
        var snapshot = new ProxyPortBindingTracker(source).Capture([7892, 7890, 7891], TestContext.Current.CancellationToken);
        Assert.All(snapshot.Ports, port => Assert.True(port.IsReady));
        Assert.Equal(new[] { 7890, 7891, 7892 }, snapshot.Ports.Select(port => port.Port));
        Assert.Equal(2, snapshot.OwnerPaths.Count);
        Assert.Equal(new uint[] { 10, 10, 20 }, snapshot.Ports.Select(port => port.Owners[0].ProcessId));
    }

    [Fact]
    public void Restart_keeps_loop_exemption_and_changes_fingerprint_even_if_pid_is_reused()
    {
        var source = new FakeResolver();
        const string path = @"C:\Proxy\core.exe";
        source.Table[7890] = [new(10, path, Started)];
        var tracker = new ProxyPortBindingTracker(source);
        var before = tracker.Capture([7890], TestContext.Current.CancellationToken);
        source.Table[7890] = [];
        var down = tracker.Capture([7890], TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(down.Ports).IsReady);
        Assert.Contains("未监听", down.Ports[0].Error);
        Assert.Equal(path, Assert.Single(down.OwnerPaths));
        source.Table[7890] = [new(10, path, Started.AddSeconds(3))];
        var restarted = tracker.Capture([7890], TestContext.Current.CancellationToken);
        Assert.True(restarted.Ports[0].IsReady);
        Assert.NotEqual(before.Fingerprint, restarted.Fingerprint);
        Assert.Equal(before.RoutingFingerprint, restarted.RoutingFingerprint);
        Assert.NotEqual(before.RoutingFingerprint, down.RoutingFingerprint);
        source.Table[7890] = [new(11, path, Started.AddSeconds(4))];
        Assert.NotEqual(restarted.Fingerprint, tracker.Capture([7890], TestContext.Current.CancellationToken).Fingerprint);
    }

    [Fact]
    public void Ambiguous_unresolved_and_failed_lookups_remain_not_ready_and_preserve_known_core()
    {
        var source = new FakeResolver();
        const string path = @"C:\Proxy\core.exe";
        source.Table[7890] = [new(10, path)];
        var tracker = new ProxyPortBindingTracker(source);
        tracker.Capture([7890], TestContext.Current.CancellationToken);
        source.Table[7890] = [new(10, path), new(11, @"C:\Other\core.exe")];
        var ambiguous = tracker.Capture([7890], TestContext.Current.CancellationToken);
        Assert.False(ambiguous.Ports[0].IsReady);
        Assert.Contains("多个监听进程", ambiguous.Ports[0].Error);
        source.Table[7890] = [new(12, null)];
        Assert.Contains("完整路径", tracker.Capture([7890], TestContext.Current.CancellationToken).Ports[0].Error);
        source.Failure = new UnauthorizedAccessException("mock denied");
        var failed = tracker.Capture([7890], TestContext.Current.CancellationToken);
        Assert.False(failed.Ports[0].IsReady);
        Assert.Contains("mock denied", failed.Ports[0].Error);
        Assert.Contains(path, failed.OwnerPaths);
    }

    [Fact]
    public void Changing_core_replaces_old_path_and_removing_port_removes_its_exemption()
    {
        var source = new FakeResolver();
        source.Table[7890] = [new(10, @"C:\Old\core.exe")];
        var tracker = new ProxyPortBindingTracker(source);
        tracker.Capture([7890], TestContext.Current.CancellationToken);
        source.Table[7890] = [new(20, @"C:\New\core.exe")];
        Assert.Equal(@"C:\New\core.exe", Assert.Single(tracker.Capture([7890], TestContext.Current.CancellationToken).OwnerPaths));
        var removed = tracker.Capture([], TestContext.Current.CancellationToken);
        Assert.Empty(removed.Ports); Assert.Empty(removed.OwnerPaths);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("192.0.2.2", false)]
    [InlineData("127.0.0.2", false)]
    public void Unrelated_interface_listener_cannot_own_the_loopback_proxy(string address, bool expected)
        => Assert.Equal(expected, TcpListenerOwnerResolver.AcceptsLocalProxy(IPAddress.Parse(address)));

    [Fact]
    public void Junction_launch_path_and_canonical_path_both_keep_exact_owner_routes()
    {
        var source = new FakeResolver();
        source.Table[7890] = [new(10, @"C:\Real\core.exe", Started, @"C:\Link\core.exe")];
        var snapshot = new ProxyPortBindingTracker(source).Capture([7890], TestContext.Current.CancellationToken);
        Assert.True(snapshot.Ports[0].IsReady);
        Assert.Equal(new[] { @"C:\Link\core.exe", @"C:\Real\core.exe" }, snapshot.OwnerPaths);
    }

    [Fact]
    public void Relative_paths_do_not_establish_proxy_identity()
        => Assert.False(new TcpListenerOwner(10, "core.exe").IsResolved);

    private sealed class FakeResolver : ITcpListenerOwnerResolver
    {
        public Dictionary<int, IReadOnlyList<TcpListenerOwner>> Table { get; } = new();
        public Exception? Failure { get; set; }
        public IReadOnlyList<TcpListenerOwner> Resolve(int port, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            return Table.GetValueOrDefault(port, []);
        }
    }
}
