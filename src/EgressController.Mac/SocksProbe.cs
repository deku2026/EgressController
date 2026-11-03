using System.Net.Sockets;
using System.Runtime.InteropServices;
using EgressController.Core.Protection;

namespace EgressController.Mac;

public sealed record SocksStatus(int Port, bool Listening, bool Socks5, string OwnerPath, string Summary);
public sealed partial class SocksProbe
{
    public async Task<SocksStatus> CheckAsync(int port, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        bool listening = false;
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", port, timeout.Token).ConfigureAwait(false); listening = true;
            var stream = client.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token).ConfigureAwait(false);
            byte[] result = new byte[2]; await stream.ReadExactlyAsync(result, timeout.Token).ConfigureAwait(false);
            bool socks = result[0] == 5 && result[1] == 0;
            string owner = await OwnerAsync(port, token).ConfigureAwait(false);
            bool surge = owner.Contains("/Surge.app/", StringComparison.OrdinalIgnoreCase);
            return new(port, true, socks && !surge, owner, surge ? "Surge 自身端口不可作为上游" : socks ? "SOCKS5 握手正常" : "不是免认证 SOCKS5 端口");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !token.IsCancellationRequested)
        { return new(port, listening, false, "", listening ? "SOCKS5 握手失败或超时" : "端口未监听"); }
    }
    private static async Task<string> OwnerAsync(int port, CancellationToken token)
    {
        try
        {
            string result = await MacCommand.ReadAsync("/usr/sbin/lsof", ["-nP", $"-iTCP:{port}", "-sTCP:LISTEN", "-Fp"], token).ConfigureAwait(false);
            int[] pids = result.Split('\n').Where(s => s.StartsWith('p')).Select(s => int.TryParse(s.AsSpan(1), out int id) ? id : 0).Where(p => p > 0).Distinct().ToArray();
            var owners = pids.Select(PathForPid).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            return owners.Length == 1 ? MacDiscovery.Canonical(owners[0]) : "";
        }
        catch (IOException) { return ""; }
    }
    private static string PathForPid(int pid)
    {
        byte[] bytes = new byte[4096]; int count = ProcPidPath(pid, bytes, (uint)bytes.Length);
        return count > 0 ? System.Text.Encoding.UTF8.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0) is int end && end >= 0 ? end : count) : "";
    }
    [LibraryImport("libproc", EntryPoint = "proc_pidpath")] private static partial int ProcPidPath(int pid, [Out] byte[] buffer, uint size);
}
