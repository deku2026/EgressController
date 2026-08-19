using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EgressController.Windows.Process;

namespace EgressController.Windows.IntegrationTests;

/// <summary>
/// Loopback + self-process integration for Step 08 (no external network).
/// </summary>
public class ProcessIdentityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resolves_owning_pid_of_a_local_proxy_connection()
    {
        var resolver = new TcpOwnerSnapshotResolver();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var listenerEp = (IPEndPoint)listener.LocalEndpoint;

            using var client = new TcpClient();
            await client.ConnectAsync(listenerEp.Address, listenerEp.Port, Ct);
            await Task.Delay(200, Ct); // let the OS publish the tuple
            using var server = await listener.AcceptTcpClientAsync(Ct);

            var clientEp = (IPEndPoint)client.Client.LocalEndPoint!;

            // The client's row is the reversed (unordered) tuple: { client, listener }.
            uint? pid = resolver.ResolveOwner(clientEp, listenerEp, Ct);
            Assert.NotNull(pid);
            Assert.Equal((uint)Environment.ProcessId, pid);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Resolves_the_client_pid_when_listener_and_client_are_different_processes()
    {
        var resolver = new TcpOwnerSnapshotResolver();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var clientProcess = System.Diagnostics.Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", $"$c = New-Object System.Net.Sockets.TcpClient('127.0.0.1',{((IPEndPoint)listener.LocalEndpoint).Port}); Start-Sleep -Seconds 10" },
        });
        Assert.NotNull(clientProcess);

        try
        {
            var listenerEp = (IPEndPoint)listener.LocalEndpoint;
            using TcpClient server = await listener.AcceptTcpClientAsync(Ct);
            await Task.Delay(200, Ct);
            var clientEp = (IPEndPoint)server.Client.RemoteEndPoint!;

            uint? pid = resolver.ResolveOwner(clientEp, listenerEp, Ct);
            Assert.Equal((uint)clientProcess!.Id, pid);
        }
        finally
        {
            listener.Stop();
            if (!clientProcess!.HasExited)
            {
                try { clientProcess.Kill(entireProcessTree: true); } catch { }
            }
            await clientProcess.WaitForExitAsync(Ct);
        }
    }

    [Fact]
    public void Identity_resolver_returns_self_with_canonical_final_path()
    {
        var resolver = new WindowsProcessIdentityResolver(new ExecutablePathCanonicalizer());
        var identity = resolver.Resolve((uint)Environment.ProcessId);

        Assert.NotNull(identity);
        Assert.False(string.IsNullOrWhiteSpace(identity.ExePathObserved));
        Assert.NotNull(identity.ExePathFinal);          // canonicalization must succeed for self
        Assert.True(File.Exists(identity.ExePathFinal)); // final path is a real file
        Assert.True(identity.StartTimeUtc <= DateTime.UtcNow);
        Assert.True(identity.StartTimeUtc > DateTime.UtcNow.AddYears(-1));
    }
}
