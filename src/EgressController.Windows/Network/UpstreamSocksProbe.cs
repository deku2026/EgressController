using System.Net;
using System.Net.Sockets;

namespace EgressController.Windows.Network;

public enum Socks5ProbeStatus
{
    Ready = 0,
    Offline = 1,
    NotSocks5 = 2,
    AuthenticationRequired = 3,
}

public sealed record Socks5ProbeResult
{
    public required Socks5ProbeStatus Status { get; init; }
    public required int Port { get; init; }
    public string Message { get; init; } = string.Empty;
    public byte? SelectedAuthenticationMethod { get; init; }
    public bool IsReady => Status == Socks5ProbeStatus.Ready;
}

/// <summary>Performs a loopback SOCKS5 greeting without using any process/global proxy settings.</summary>
public sealed class UpstreamSocksProbe(TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);

    public async Task<Socks5ProbeResult> ProbeAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
            };
            await client.ConnectAsync(IPAddress.Loopback, port, timeoutCts.Token).ConfigureAwait(false);
            await using NetworkStream stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, timeoutCts.Token).ConfigureAwait(false);
            byte[] response = new byte[2];
            await ReadExactlyAsync(stream, response, timeoutCts.Token).ConfigureAwait(false);

            if (response[0] != 0x05)
            {
                return new Socks5ProbeResult
                {
                    Status = Socks5ProbeStatus.NotSocks5,
                    Port = port,
                    Message = "监听端口没有返回 SOCKS5 版本 5。",
                };
            }

            return response[1] switch
            {
                0x00 => new Socks5ProbeResult
                {
                    Status = Socks5ProbeStatus.Ready,
                    Port = port,
                    SelectedAuthenticationMethod = response[1],
                    Message = "SOCKS5 无认证握手成功。",
                },
                0x02 => new Socks5ProbeResult
                {
                    Status = Socks5ProbeStatus.AuthenticationRequired,
                    Port = port,
                    SelectedAuthenticationMethod = response[1],
                    Message = "SOCKS5 要求用户名/密码认证；当前控制器不保存上游凭据。",
                },
                0xff => new Socks5ProbeResult
                {
                    Status = Socks5ProbeStatus.AuthenticationRequired,
                    Port = port,
                    SelectedAuthenticationMethod = response[1],
                    Message = "SOCKS5 拒绝当前无认证握手。",
                },
                _ => new Socks5ProbeResult
                {
                    Status = Socks5ProbeStatus.NotSocks5,
                    Port = port,
                    SelectedAuthenticationMethod = response[1],
                    Message = $"SOCKS5 返回了不支持的认证方法 0x{response[1]:X2}。",
                },
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Offline(port, "SOCKS5 探测超时。");
        }
        catch (SocketException ex)
        {
            return Offline(port, $"SOCKS5 端口不可达：{ex.SocketErrorCode}。");
        }
        catch (IOException ex)
        {
            return Offline(port, $"SOCKS5 握手失败：{ex.Message}");
        }
    }

    private static Socks5ProbeResult Offline(int port, string message)
        => new()
        {
            Status = Socks5ProbeStatus.Offline,
            Port = port,
            Message = message,
        };

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("SOCKS5 server 在返回完整 greeting 前关闭了连接。");
            offset += read;
        }
    }
}
