using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.SingBox.Api;
using EgressController.SingBox.Configuration;

namespace EgressController.App;

public sealed partial class AppController
{
    public string DirectInternetStatus => _networkHealth.DirectStatus;
    public string ProxyInternetStatus => _networkHealth.ProxyStatus;

    private void StartNetworkMonitor()
    {
        StopNetworkMonitor(resetHealth: false);
        EgressProfileDocument profile = _profile;
        _networkHealth.ObserveEnvironment(_environmentResolver.Resolve(profile, _adapters),
            profile.UpstreamPorts.Count > 0 ? profile.UpstreamPort : null);
        _networkMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken token = _networkMonitorCts.Token;
        _networkMonitorTask = Task.Run(() => MonitorNetworkHealthAsync(token), token);
    }

    private void StopNetworkMonitor(bool resetHealth = true)
    {
        CancellationTokenSource? cts = _networkMonitorCts;
        Task? task = _networkMonitorTask;
        _networkMonitorCts = null;
        _networkMonitorTask = null;
        cts?.Cancel();
        if (cts is not null)
        {
            if (task is null || task.IsCompleted) cts.Dispose();
            else _ = task.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                cts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        lock (_networkStateGate)
        {
            if (resetHealth) _networkHealth.Reset();
            _networkMonitorStatus = resetHealth ? "未运行" : "等待联网检测…";
            _networkLastCheckedAtUtc = null;
        }
    }

    private async Task MonitorNetworkHealthAsync(CancellationToken token)
    {
        DateTimeOffset nextCheck = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (IsTunRunning && !IsUpdatingProfile && DateTimeOffset.UtcNow >= nextCheck
                    && await _networkProbeGate.WaitAsync(0, token).ConfigureAwait(false))
                {
                    // Both exits share a 30-second cadence, measured from the start of a round.
                    // The gate stays held through retries, so rounds never overlap.
                    nextCheck = DateTimeOffset.UtcNow + InternetHealthProbe.CheckInterval;
                    try { await RunInternetHealthCheckAsync(token, announce: false).ConfigureAwait(false); }
                    finally { _networkProbeGate.Release(); }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                ReportFailure("联网检测", "network.monitor", "联网检测失败：" + DescribeDiagnosticsFailure(exception), exception);
                try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<ControllerOperationResult> RunInternetHealthCheckAsync(CancellationToken token, bool announce)
    {
        if (!IsTunRunning || IsUpdatingProfile || !_profileApplied)
            return Failure("TUN 或配置正在变化，请稍后检测。");
        int generation = _networkHealth.Generation;
        EgressProfileDocument profile = _profile;
        int? defaultUpstreamPort = profile.UpstreamPorts.Count > 0 ? profile.UpstreamPort : null;
        lock (_networkStateGate)
            _networkMonitorStatus = defaultUpstreamPort is int port
                ? $"家宽与默认代理端口 {port} 联网检测中，沿用上次完成的结果"
                : "家宽联网检测中；未配置代理端口，不进行代理联网检测";

        bool accepted;
        try
        {
            // Use live availability; cached bindings are only for preserving sing-box configuration.
            NetworkEnvironmentSnapshot live = _environmentResolver.Resolve(profile, _adapters);
            using SingBoxApiClient api = CreateApiClient();
            async Task<bool> CheckAsync(NetworkHealthCheck check, bool ready)
            {
                NetworkHealthResult result = ready
                    ? await InternetHealthProbe.RunWithRetriesAsync(check, defaultUpstreamPort, api.ProbeOutboundAsync, token,
                        detail =>
                        {
                            if (_networkHealth.ReportProgress(generation, check, detail, token))
                                Diagnostics.Info("联网复测 · " + check, detail);
                        }).ConfigureAwait(false)
                    : new(false, "网卡未连接或没有 IP");
                // Publish only success or three consecutive failures, independently for each exit.
                bool applied = _networkHealth.Complete(generation, check, result, token);
                if (applied && !result.IsHealthy)
                    Diagnostics.Report("联网检测 · " + check, "network.failed", result.Detail);
                return applied;
            }
            bool[] results = await Task.WhenAll(
                CheckAsync(NetworkHealthCheck.DirectInternet, live.DefaultAdapter.IsReady),
                defaultUpstreamPort is null ? Task.FromResult(true)
                    : CheckAsync(NetworkHealthCheck.ProxyInternet, live.ProxyAdapter.IsReady)).ConfigureAwait(false);
            accepted = results.All(result => result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_networkStateGate)
                if (generation == _networkHealth.Generation)
                    _networkMonitorStatus = "检测已取消，沿用上次完成的结果";
            throw;
        }
        catch (Exception exception)
        {
            // Setup/coordination errors are not three measured failures. Preserve the last verdict.
            string error = "联网检测未完成，沿用上次完成的结果：" + DescribeDiagnosticsFailure(exception);
            lock (_networkStateGate)
                if (generation == _networkHealth.Generation) _networkMonitorStatus = error;
            return Failure(error, exception);
        }

        finally { _networkHealth.ClearProgress(generation); }

        ProtectionReadiness network;
        lock (_networkStateGate)
        {
            if (!accepted || token.IsCancellationRequested || generation != _networkHealth.Generation)
                return Failure("检测已取消或网络配置已变化，丢弃过期的联网结果。");
            network = _networkHealth.Readiness;
            _networkLastCheckedAtUtc = DateTimeOffset.UtcNow;
            _networkMonitorStatus = network.Ready ? network.Reason
                : "持续关闭所选应用：" + network.Reason;
        }
        if (announce) SetMessage(NetworkMonitorStatus);
        return network.Ready ? ControllerOperationResult.Success() : Failure(network.Reason);
    }
}
