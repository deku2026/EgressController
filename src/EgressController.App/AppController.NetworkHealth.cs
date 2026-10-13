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
                    // Both exits share one one-minute cadence, measured from the start of a round.
                    nextCheck = DateTimeOffset.UtcNow + InternetHealthProbe.CheckInterval;
                    try { await RunInternetHealthCheckAsync(token, announce: false).ConfigureAwait(false); }
                    finally { _networkProbeGate.Release(); }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                SetMessage("联网检测失败：" + DescribeDiagnosticsFailure(exception));
                try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<ControllerOperationResult> RunInternetHealthCheckAsync(CancellationToken token, bool announce)
    {
        if (!IsTunRunning || IsUpdatingProfile || !_profileApplied)
            return ControllerOperationResult.Failure("TUN 或配置正在变化，请稍后检测。");
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
                    ? await InternetHealthProbe.RunAsync(check, defaultUpstreamPort, api.ProbeOutboundAsync, token).ConfigureAwait(false)
                    : new(false, "网卡未连接或没有 IP");
                // Each completed verdict takes effect without waiting for the other adapter.
                return _networkHealth.Complete(generation, check, result, token);
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
            var result = new NetworkHealthResult(false, DescribeDiagnosticsFailure(exception));
            bool direct = _networkHealth.Complete(generation, NetworkHealthCheck.DirectInternet, result, token);
            bool proxy = defaultUpstreamPort is null
                || _networkHealth.Complete(generation, NetworkHealthCheck.ProxyInternet, result, token);
            accepted = direct && proxy;
        }

        ProtectionReadiness network;
        lock (_networkStateGate)
        {
            if (!accepted || token.IsCancellationRequested || generation != _networkHealth.Generation)
                return ControllerOperationResult.Failure("检测已取消或网络配置已变化，丢弃过期的联网结果。");
            network = _networkHealth.Readiness;
            _networkLastCheckedAtUtc = DateTimeOffset.UtcNow;
            _networkMonitorStatus = network.Ready ? network.Reason
                : "持续关闭所选应用：" + network.Reason;
        }
        if (announce) SetMessage(NetworkMonitorStatus);
        return network.Ready ? ControllerOperationResult.Success() : ControllerOperationResult.Failure(network.Reason);
    }
}
