using EgressController.Core.Protection;
using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Configuration;

public static class InternetHealthProbe
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static async Task<NetworkHealthResult> RunAsync(
        NetworkHealthCheck check,
        Func<string, CancellationToken, Task<SingBoxDelayResponse>> probe,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        string outbound = check switch
        {
            NetworkHealthCheck.DirectInternet => EgressProfileCompiler.DnsDirectTag,
            NetworkHealthCheck.ProxyInternet => EgressProfileCompiler.ProxyDirectTag,
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };
        TimeSpan budget = timeout ?? Timeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            // sing-box owns this socket and binds it to the selected NIC, independently of DNS health.
            // Never substitute recovery-direct, the system route or a local SOCKS port.
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
