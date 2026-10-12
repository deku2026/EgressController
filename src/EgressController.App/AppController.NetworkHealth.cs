using EgressController.Core.Protection;
using EgressController.SingBox.Api;
using EgressController.SingBox.Configuration;

namespace EgressController.App;

public sealed partial class AppController
{
    public string ProxyInternetStatus => _networkHealth.ProxyStatus;

    private void StartDohMonitor()
    {
        StopDohMonitor(resetHealth: false);
        _dohMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken token = _dohMonitorCts.Token;
        _dohMonitorTask = Task.Run(() => Task.WhenAll(
            MonitorNetworkHealthAsync(EgressDohConfiguration.CheckInterval,
                async ct => { await RunDohHealthCheckAsync(ct, announce: false).ConfigureAwait(false); }, _dohProbeGate, token),
            MonitorNetworkHealthAsync(ProxyInternetProbe.CheckInterval, CheckProxyInternetAsync, null, token)), token);
    }

    private void StopDohMonitor(bool resetHealth = true)
    {
        CancellationTokenSource? cts = _dohMonitorCts;
        Task? task = _dohMonitorTask;
        _dohMonitorCts = null;
        _dohMonitorTask = null;
        cts?.Cancel();
        if (cts is not null)
        {
            if (task is null || task.IsCompleted) cts.Dispose();
            else _ = task.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                cts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        lock (_dohStateGate)
        {
            if (resetHealth) _networkHealth.Reset();
            _dohMonitorStatus = resetHealth ? "未运行" : "等待网络检测…";
            _dohLastCheckedAtUtc = null;
            _dohStatuses = CreateStoppedDohStatuses();
        }
    }

    private async Task MonitorNetworkHealthAsync(TimeSpan interval, Func<CancellationToken, Task> check,
        SemaphoreSlim? probeGate, CancellationToken token)
    {
        DateTimeOffset nextCheck = DateTimeOffset.MinValue;
        int generation = -1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Adapter recovery/configuration changes trigger a fresh check without waiting a minute.
                if (IsTunRunning && !IsUpdatingProfile
                    && (DateTimeOffset.UtcNow >= nextCheck || generation != _networkHealth.Generation)
                    && (probeGate is null || await probeGate.WaitAsync(0, token).ConfigureAwait(false)))
                {
                    generation = _networkHealth.Generation;
                    try { await check(token).ConfigureAwait(false); }
                    finally
                    {
                        probeGate?.Release();
                        nextCheck = DateTimeOffset.UtcNow + interval;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                SetMessage("网络检测失败：" + DescribeDiagnosticsFailure(exception));
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
    }

    private async Task CheckProxyInternetAsync(CancellationToken token)
    {
        int generation = _networkHealth.Generation;
        NetworkHealthResult result;
        try
        {
            // Deliberately resolve live state, not the bindings retained for sing-box configuration.
            bool ready = _environmentResolver.Resolve(_profile, _adapters).ProxyAdapter.IsReady;
            if (!ready) result = new(false, "网卡未连接或没有 IP");
            else
            {
                using SingBoxApiClient api = CreateApiClient();
                result = await ProxyInternetProbe.RunAsync(api.ProbeOutboundAsync, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { result = new(false, DescribeDiagnosticsFailure(exception)); }
        _networkHealth.Complete(generation, NetworkHealthCheck.ProxyInternet, result, token);
    }

    private async Task<ControllerOperationResult> RunDohHealthCheckAsync(CancellationToken cancellationToken, bool announce)
    {
        if (!IsTunRunning || IsUpdatingProfile) return ControllerOperationResult.Failure("TUN 或配置正在变化，请稍后检测。");
        int generation = _networkHealth.Generation;
        bool dnsReady = ResolveCurrentDnsReady();
        SingBoxDohEndpointDefinition endpoint = EgressDohConfiguration.Endpoints.Single();
        lock (_dohStateGate)
        {
            _dohMonitorStatus = "Cloudflare DoH 检测中，沿用上次检测结果";
            DohStatusSnapshot? previous = _dohStatuses.FirstOrDefault();
            _dohStatuses = [DohStatusSnapshot.Create(endpoint, dnsReady, dnsReady, previous?.IsHealthy,
                "检测中…", "上次通过则继续运行；上次失败则继续关闭所选应用。",
                previous?.LastCheckedAtUtc, previous?.LatencyMilliseconds)];
        }

        DohProbeResult probe;
        try
        {
            using SingBoxApiClient api = CreateApiClient();
            probe = dnsReady
                ? (await DohHealthProbe.RunAsync((host, ct) => api.QueryDnsAsync(host, "A", ct), cancellationToken).ConfigureAwait(false)).Single()
                : new(endpoint.Tag, false, "ESIM-家宽 未连接或没有 IP");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_dohStateGate)
            {
                if (generation == _networkHealth.Generation)
                {
                    _dohMonitorStatus = "检测已取消，沿用上次检测结果";
                    _dohStatuses = _dohStatuses.Select(status => status with { State = "检测已取消" }).ToArray();
                }
            }
            throw;
        }
        catch (Exception exception) { probe = new(endpoint.Tag, false, DescribeDiagnosticsFailure(exception)); }

        lock (_dohStateGate)
        {
            if (!_networkHealth.Complete(generation, NetworkHealthCheck.CloudflareDoh,
                new(probe.IsHealthy, probe.Detail ?? "未收到有效的 DoH 响应"), cancellationToken))
                return ControllerOperationResult.Failure("检测已取消或网络配置已变化，丢弃过期的 DoH 结果。");
            _dohLastCheckedAtUtc = DateTimeOffset.UtcNow;
            _dohStatuses = [DohStatusSnapshot.Create(endpoint, dnsReady, dnsReady, probe.IsHealthy,
                probe.IsHealthy ? "正常 · 当前" : "失败", probe.Detail ?? "未收到有效的 DoH 响应",
                _dohLastCheckedAtUtc, probe.LatencyMilliseconds)];
            _dohMonitorStatus = probe.IsHealthy ? "Cloudflare DoH 检测通过"
                : "Cloudflare DoH 不可用，持续关闭所选应用：" + probe.Detail;
        }
        if (announce) SetMessage(DohMonitorStatus);
        return probe.IsHealthy ? ControllerOperationResult.Success() : ControllerOperationResult.Failure(DohMonitorStatus);
    }

    private bool ResolveCurrentDnsReady()
    {
        try { return _environmentResolver.Resolve(_profile, _adapters).IsDnsReady; }
        catch { return false; }
    }

    private static IReadOnlyList<DohStatusSnapshot> CreateStoppedDohStatuses()
        => EgressDohConfiguration.Endpoints.Select(endpoint => DohStatusSnapshot.Create(
            endpoint, false, false, null, "未检测", "等待 TUN 运行并完成检测。")).ToArray();
}
