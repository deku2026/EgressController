using System.Net;
using System.Net.Sockets;
using EgressController.App;
using EgressController.Core.Contracts;
using EgressController.Core.Diagnostics;
using EgressController.Core.Models;
using EgressController.Core.Routing;

namespace EgressController.Windows.IntegrationTests;

public sealed class EsimFailClosedMonitorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Initially_offline_esim_starts_with_reject_gate_and_one_warning_event()
    {
        var adapters = new MutableAdapterService(isUp: false);
        await using var host = new RouterHost(
            adapters,
            esimMonitorInterval: TimeSpan.FromMilliseconds(30),
            localPort: 0);
        var warning = new TaskCompletionSource<EsimConnectivityChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        int warnings = 0;
        host.EsimConnectivityChanged += (_, args) =>
        {
            if (!args.IsOnline)
            {
                Interlocked.Increment(ref warnings);
                warning.TrySetResult(args);
            }
        };

        host.Start();
        EsimConnectivityChangedEventArgs initial = await warning.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.False(initial.IsOnline);
        Assert.True(host.RejectingAllConnections);

        using var rejected = new TcpClient();
        await rejected.ConnectAsync(IPAddress.Loopback, host.BoundPort, Ct);
        Assert.True(await IsClosedWithoutResponseAsync(rejected));
        await Task.Delay(120, Ct);
        Assert.Equal(1, Volatile.Read(ref warnings));
    }

    [Fact]
    public async Task Simulated_esim_drop_closes_first_rejects_all_and_recovers_once_online()
    {
        var adapters = new MutableAdapterService(isUp: true);
        await using var host = new RouterHost(
            adapters,
            esimMonitorInterval: TimeSpan.FromMilliseconds(30),
            localPort: 0);
        var offline = new TaskCompletionSource<EsimConnectivityChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var online = new TaskCompletionSource<EsimConnectivityChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        int offlineEvents = 0;
        host.EsimConnectivityChanged += (_, args) =>
        {
            if (args.IsOnline)
                online.TrySetResult(args);
            else
            {
                Interlocked.Increment(ref offlineEvents);
                offline.TrySetResult(args);
            }
        };

        host.Start();
        Assert.True(host.Started, host.LastMessage);
        Assert.False(host.RejectingAllConnections);

        using var active = new TcpClient();
        await active.ConnectAsync(IPAddress.Loopback, host.BoundPort, Ct);
        await WaitUntilAsync(() => host.ActiveConnections == 1);

        // This changes only the injected fake snapshot; it never touches a Windows adapter.
        adapters.SetOnline(false);
        EsimConnectivityChangedEventArgs dropped = await offline.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);

        Assert.False(dropped.IsOnline);
        Assert.Equal("ESIM-Test", dropped.AdapterName);
        Assert.Equal(1, dropped.ClosedConnections);
        Assert.True(host.RejectingAllConnections);
        Assert.StartsWith("REJECT", host.SystemProxySummary, StringComparison.Ordinal);
        Assert.Contains("拒绝所有新连接", host.LastMessage, StringComparison.Ordinal);
        Assert.Equal(0, host.ActiveConnections); // Event is deliberately raised after disconnect.
        Assert.True(await IsClosedWithoutResponseAsync(active));

        await Task.Delay(150, Ct);
        Assert.Equal(1, Volatile.Read(ref offlineEvents));

        using var rejected = new TcpClient();
        await rejected.ConnectAsync(IPAddress.Loopback, host.BoundPort, Ct);
        Assert.True(await IsClosedWithoutResponseAsync(rejected));

        adapters.SetOnline(true);
        EsimConnectivityChangedEventArgs restored = await online.Task.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.True(restored.IsOnline);
        Assert.False(host.RejectingAllConnections);
        Assert.Contains("已解除全局拒绝", host.LastMessage, StringComparison.Ordinal);

        using var acceptedAgain = new TcpClient();
        await acceptedAgain.ConnectAsync(IPAddress.Loopback, host.BoundPort, Ct);
        await WaitUntilAsync(() => host.ActiveConnections == 1);
    }

    [Fact]
    public async Task Close_all_and_clear_waits_for_connections_and_leaves_online_gate_open()
    {
        var adapters = new MutableAdapterService(isUp: true);
        await using var host = new RouterHost(
            adapters,
            esimMonitorInterval: TimeSpan.FromMilliseconds(30),
            localPort: 0);
        host.Start();
        Assert.True(host.Started, host.LastMessage);

        host.Log.Write(SampleEvent());
        Assert.Single(host.Log.Latest());

        using var active = new TcpClient();
        await active.ConnectAsync(IPAddress.Loopback, host.BoundPort, Ct);
        await WaitUntilAsync(() => host.ActiveConnections == 1);

        Assert.Equal(1, await host.CloseAllConnectionsAndClearLogAsync());
        Assert.Empty(host.Log.Latest());
        Assert.False(host.Log.Reader.TryRead(out _));
        Assert.Equal(0, host.ActiveConnections);
        Assert.False(host.RejectingAllConnections);

        using var acceptedAgain = new TcpClient();
        await acceptedAgain.ConnectAsync(IPAddress.Loopback, host.BoundPort, Ct);
        await WaitUntilAsync(() => host.ActiveConnections == 1);
    }

    private static ConnectionEvent SampleEvent()
        => new(
            DateTimeOffset.UtcNow,
            42,
            "sample.exe",
            @"C:\Apps\sample.exe",
            null,
            "example.test",
            443,
            Egress.UpstreamProxy,
            RouteReason.DefaultUpstream,
            null,
            null,
            "upstream",
            ConnectionStatus.Decided,
            0,
            TimeSpan.Zero);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Host condition was not reached.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task<bool> IsClosedWithoutResponseAsync(TcpClient client)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            byte[] one = new byte[1];
            return await client.GetStream().ReadAsync(one, timeout.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private sealed class MutableAdapterService(bool isUp) : INetworkAdapterService
    {
        private readonly object _gate = new();
        private bool _isUp = isUp;
        private static readonly Guid AdapterGuid = Guid.Parse("7F4EA112-9F29-46E3-8F80-06F3EB83D7ED");

        public void SetOnline(bool online)
        {
            lock (_gate)
                _isUp = online;
        }

        public IReadOnlyList<NetworkAdapterInfo> EnumerateAll() => [Snapshot()];

        public NetworkAdapterInfo? GetByGuid(Guid guid)
            => guid == AdapterGuid ? Snapshot() : null;

        public NetworkAdapterInfo? GetByIfIndex(int ifIndex)
            => ifIndex == 77 ? Snapshot() : null;

        private NetworkAdapterInfo Snapshot()
        {
            bool online;
            lock (_gate)
                online = _isUp;
            return new NetworkAdapterInfo
            {
                Identity = new NetworkAdapterIdentity(AdapterGuid, "ESIM-Test"),
                Description = "Simulated adapter",
                Luid = 77,
                IfIndex = 77,
                Ipv6IfIndex = 77,
                IsUp = online,
                Addresses = online ? [IPAddress.Parse("192.0.2.10")] : [],
                Gateways = online ? [IPAddress.Parse("192.0.2.1")] : [],
                DnsServers = online ? [IPAddress.Parse("192.0.2.53")] : [],
            };
        }
    }
}
