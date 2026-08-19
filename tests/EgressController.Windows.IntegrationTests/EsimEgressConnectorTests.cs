using System.Net;
using System.Net.Sockets;
using EgressController.Core.Contracts;
using EgressController.Core.Models;
using EgressController.Windows.Network;

namespace EgressController.Windows.IntegrationTests;

/// <summary>
/// Fake-backed tests for the ESIM connect policy. Not touching the real network: verifies
/// the IPv4-first stagger (avoid the "bad IPv6 stalls 5s" bug) and that failure is fail-closed.
/// </summary>
public class EsimEgressConnectorTests
{
    private static NetworkAdapterInfo Esim()
        => new()
        {
            Identity = new NetworkAdapterIdentity(Guid.NewGuid(), "ESIM-WIFI"),
            Description = "test",
            Luid = 0,
            IfIndex = 13,
            Ipv6IfIndex = 13,
            IsUp = true,
            Addresses = Array.Empty<IPAddress>(),
            Gateways = Array.Empty<IPAddress>(),
            DnsServers = Array.Empty<IPAddress>(),
        };

    [Fact]
    public async Task IPv4_is_tried_before_IPv6_even_if_IPv6_came_first_from_dns()
    {
        var dns = new FakeDns
        {
            Result = [IPAddress.Parse("2001:4860:4860::8888"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")],
        };
        var sockets = new RecordingSockets(succeedOnIndex: 1); // second attempt (first IPv4) succeeds
        var connector = new EsimEgressConnector(dns, sockets) { ConnectTimeout = TimeSpan.FromSeconds(2) };

        var stream = await connector.ConnectAsync("test.example", 443, Esim(), CancellationToken.None);

        Assert.NotNull(stream);
        // IPv4 (8.8.8.8 and 1.1.1.1) must come before IPv6 even though DNS listed IPv6 first.
        Assert.Equal(AddressFamily.InterNetwork, sockets.Tried[0].AddressFamily);
        Assert.Equal(2, sockets.Tried.Count);
        Assert.Equal("8.8.8.8", sockets.Tried[0].ToString());
    }

    [Fact]
    public async Task all_addresses_fail_throws_EsimConnectException()
    {
        var dns = new FakeDns { Result = [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4")] };
        var sockets = new RecordingSockets(succeedOnIndex: int.MaxValue);
        var connector = new EsimEgressConnector(dns, sockets) { ConnectTimeout = TimeSpan.FromSeconds(2) };

        await Assert.ThrowsAsync<EsimConnectException>(
            () => connector.ConnectAsync("test.example", 443, Esim(), CancellationToken.None).AsTask());
    }

    private sealed class FakeDns : IEsimDnsResolver
    {
        public List<IPAddress> Result { get; set; } = [];
        public IReadOnlyList<IPAddress> Resolve(string host, int interfaceIndex, CancellationToken ct) => Result;
    }

    private static Socket ConnectedLoopbackSocket()
    {
        // NetworkStream rejects non-connected sockets; give the fake a genuinely connected pair.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndpoint);
        listener.AcceptSocket(); // server side held by GC root via listener until disposed
        return client;
    }

    private sealed class RecordingSockets(int succeedOnIndex) : IEsimSocketFactory, IDisposable
    {
        public List<IPAddress> Tried { get; } = [];
        private readonly int _succeedOnIndex = succeedOnIndex;
        private readonly List<Socket> _owned = [];

        public Socket Connect(IPAddress target, int port, NetworkAdapterInfo esim, CancellationToken ct)
        {
            Tried.Add(target);
            if (Tried.Count - 1 == _succeedOnIndex)
            {
                var s = ConnectedLoopbackSocket();
                _owned.Add(s);
                return s;
            }
            throw new SocketException(10061); // connection refused
        }

        public void Dispose()
        {
            foreach (var s in _owned) s.Dispose();
        }
    }
}