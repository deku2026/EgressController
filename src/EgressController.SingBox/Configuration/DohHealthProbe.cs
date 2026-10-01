using System.Diagnostics;
using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Configuration;

public static class DohHealthProbe
{
    public static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(8);

    // Both queries run through sing-box's dns-direct on ESIM-家宽. Await the complete
    // round before publishing failure: one failed endpoint cannot veto a healthy one.
    public static Task<DohProbeResult[]> RunAsync(
        Func<string, CancellationToken, Task<SingBoxDnsResponse>> query,
        CancellationToken cancellationToken, TimeSpan? endpointTimeout = null)
    {
        string nonce = Guid.NewGuid().ToString("N");
        return Task.WhenAll(EgressDohConfiguration.Endpoints.Select(async endpoint =>
        {
            long started = Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            TimeSpan budget = endpointTimeout ?? EndpointTimeout;
            timeout.CancelAfter(budget);
            try
            {
                SingBoxDnsResponse response = await query(endpoint.CreateProbeHost(nonce), timeout.Token)
                    .WaitAsync(budget, cancellationToken).ConfigureAwait(false);
                bool healthy = response.Status is 0 or 3;
                return new DohProbeResult(endpoint.Tag, healthy,
                    response.Status switch { 0 => "NOERROR", 3 => "NXDOMAIN（上游已返回）", _ => $"DNS 返回状态 {response.Status}" },
                    response.Status, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new DohProbeResult(endpoint.Tag, false,
                    exception is OperationCanceledException or TimeoutException ? "DoH 查询超时" : exception.Message,
                    LatencyMilliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }));
    }
}
