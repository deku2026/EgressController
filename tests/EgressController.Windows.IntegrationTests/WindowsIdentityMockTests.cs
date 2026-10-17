using System.Net;
using EgressController.Windows.Process;
using Windows.Win32.NetworkManagement.IpHelper;

namespace EgressController.Windows.IntegrationTests;

// Only synthetic native rows and strings; never enumerates the OS TCP table or opens a process.
public sealed class WindowsIdentityMockTests
{
    [Theory]
    [InlineData(@"\\?\C:\Program Files\AI\app.exe", @"C:\Program Files\AI\app.exe")]
    [InlineData(@"\\?\UNC\server\share\app.exe", @"\\server\share\app.exe")]
    [InlineData(@"C:\Apps\app.exe", @"C:\Apps\app.exe")]
    public void Canonical_path_preserves_unc_identity(string input, string expected)
        => Assert.Equal(expected, ExecutablePathCanonicalizer.NormalizeFinalPath(input));

    [Fact]
    public void Ipv6_owner_matches_address_contents_and_rejects_server_orientation_or_wrong_scope()
    {
        var client = new IPEndPoint(IPAddress.Parse("fe80::123%7"), 12345);
        var server = new IPEndPoint(IPAddress.Parse("fe80::456%7"), 7890);
        var row = new MIB_TCP6ROW_OWNER_PID
        {
            dwState = MIB_TCP_STATE.MIB_TCP_STATE_ESTAB,
            dwLocalPort = TcpOwnerSnapshotResolver.ToPort((uint)client.Port),
            dwRemotePort = TcpOwnerSnapshotResolver.ToPort((uint)server.Port),
            dwLocalScopeId = 7, dwRemoteScopeId = 7
        };
        var clientBytes = client.Address.GetAddressBytes();
        var serverBytes = server.Address.GetAddressBytes();
        for (int i = 0; i < 16; i++)
        {
            row.ucLocalAddr[i] = clientBytes[i];
            row.ucRemoteAddr[i] = serverBytes[i];
        }
        Assert.True(TcpOwnerSnapshotResolver.MatchV6(row, client, server));
        Assert.False(TcpOwnerSnapshotResolver.MatchV6(row, server, client));
        row.dwLocalScopeId = 8;
        Assert.False(TcpOwnerSnapshotResolver.MatchV6(row, client, server));
    }
}
