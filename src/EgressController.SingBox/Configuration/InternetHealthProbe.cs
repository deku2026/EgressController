using EgressController.Core.Protection;
using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Configuration;

public static class InternetHealthProbe
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    public const int MaxAttempts = 3;

    /// <summary>Only a completed sequence of three failures is an outage. Intermediate failures
    /// are progress, not health verdicts; any success ends the sequence immediately.</summary>
    public static async Task<NetworkHealthResult> RunWithRetriesAsync(
        NetworkHealthCheck check,
        int? defaultUpstreamPort,
        Func<string, CancellationToken, Task<SingBoxDelayResponse>> probe,
        CancellationToken cancellationToken,
        Action<string>? progress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? timeout = null)
    {
        delay ??= static (interval, token) => Task.Delay(interval, token);
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke($"正在进行第 {attempt}/{MaxAttempts} 次联网检测");
            NetworkHealthResult result = await RunAsync(check, defaultUpstreamPort, probe, cancellationToken, timeout).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsHealthy)
                return result with { Detail = result.Detail + $"（第 {attempt}/{MaxAttempts} 次检测通过）" };
            if (attempt == MaxAttempts)
                return new(false, $"连续 {MaxAttempts} 次联网检测失败：{result.Detail}");

            progress?.Invoke($"第 {attempt}/{MaxAttempts} 次检测失败，{RetryInterval.TotalSeconds:0} 秒后复测；沿用上次完成的结论。{result.Detail}");
            await delay(RetryInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("No network probe was performed.");
    }

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
                ? new(true, $"HTTPS 探测通过（{response.Delay} ms）", response.Delay)
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
