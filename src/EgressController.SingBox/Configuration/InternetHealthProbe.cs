using EgressController.Core.Protection;
using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Configuration;

public static class InternetHealthProbe
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static async Task<NetworkHealthResult> RunAsync(
        NetworkHealthCheck check,
        int? defaultUpstreamPort,
        Func<string, CancellationToken, Task<SingBoxDelayResponse>> probe,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (check == NetworkHealthCheck.ProxyInternet && defaultUpstreamPort is not (>= 1 and <= ushort.MaxValue))
            return new(false, "未配置有效的默认代理端口");

        string outbound = check switch
        {
            NetworkHealthCheck.DirectInternet => EgressProfileCompiler.DnsDirectTag,
            NetworkHealthCheck.ProxyInternet => EgressProfileCompiler.SocksTag(defaultUpstreamPort!.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };
        TimeSpan budget = timeout ?? Timeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            // Residential traffic uses its bound direct exit; proxy traffic must traverse the
            // configured default SOCKS endpoint. A failed proxy probe must never fall back to direct.
            SingBoxDelayResponse response = await probe(outbound, deadline.Token)
                .WaitAsync(budget, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return response.Delay is >= 0
                ? new(true, $"HTTPS 探测通过（{response.Delay} ms）")
                : new(false, "未收到有效的联网探测结果");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(false, exception is OperationCanceledException or TimeoutException ? "联网探测超时" : exception.Message);
        }
    }
}
