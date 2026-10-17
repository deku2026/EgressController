using EgressController.Core.Models;

namespace EgressController.Core.Protection;

public enum NetworkHealthCheck { DirectInternet, ProxyInternet }
public sealed record NetworkHealthResult(bool IsHealthy, string Detail);

/// <summary>Completed probes remain authoritative during rechecks. Live adapter or default proxy changes
/// invalidate in-flight results; cached configuration bindings must never be observed here.</summary>
public sealed class NetworkHealthState
{
    private readonly object _gate = new();
    private int _generation;
    private AdapterSelection? _direct;
    private AdapterSelection? _proxy;
    private int? _defaultUpstreamPort;
    private NetworkHealthResult? _directInternet;
    private NetworkHealthResult? _proxyInternet;
    private string? _directProgress;
    private string? _proxyProgress;

    public int Generation { get { lock (_gate) return _generation; } }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _directInternet = null;
            _proxyInternet = null;
            _directProgress = _proxyProgress = null;
        }
    }

    public int ObserveEnvironment(NetworkEnvironmentSnapshot live, int? defaultUpstreamPort)
    {
        lock (_gate)
        {
            bool proxyChanged = _proxy != live.ProxyAdapter || _defaultUpstreamPort != defaultUpstreamPort;
            if (_direct != live.DefaultAdapter || proxyChanged)
            {
                _generation++;
                _directProgress = _proxyProgress = null;
            }
            if (_direct != live.DefaultAdapter) _directInternet = null;
            if (proxyChanged) _proxyInternet = null;
            _direct = live.DefaultAdapter;
            _proxy = live.ProxyAdapter;
            _defaultUpstreamPort = defaultUpstreamPort;
            return _generation;
        }
    }

    public bool ReportProgress(int generation, NetworkHealthCheck check, string detail, CancellationToken token)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested || generation != _generation) return false;
            if (check == NetworkHealthCheck.ProxyInternet && _defaultUpstreamPort is null) return false;
            if (check == NetworkHealthCheck.DirectInternet) _directProgress = detail;
            else _proxyProgress = detail;
            return true;
        }
    }

    public void ClearProgress(int generation)
    {
        lock (_gate)
            if (generation == _generation) _directProgress = _proxyProgress = null;
    }

    public bool Complete(int generation, NetworkHealthCheck check, NetworkHealthResult result, CancellationToken token)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested || generation != _generation) return false;
            if (check == NetworkHealthCheck.ProxyInternet && _defaultUpstreamPort is null) return false;
            if (check == NetworkHealthCheck.DirectInternet) { _directInternet = result; _directProgress = null; }
            else { _proxyInternet = result; _proxyProgress = null; }
            return true;
        }
    }

    public ProtectionReadiness Readiness
    {
        get
        {
            lock (_gate)
            {
                if (_direct?.IsReady != true) return new(false, "ESIM-家宽 已掉线、未连接或没有 IP");
                if (_proxy?.IsReady != true) return new(false, "Proxy-代理（非家宽）已掉线、未连接或没有 IP");
                if (_directInternet?.IsHealthy == false) return new(false, "ESIM-家宽 无法联网：" + _directInternet.Detail);
                if (_defaultUpstreamPort is int failedPort && _proxyInternet?.IsHealthy == false)
                    return new(false, $"Proxy-代理（非家宽）默认代理端口 {failedPort} 无法联网：" + _proxyInternet.Detail);
                if (_directInternet is null) return new(false, "等待 ESIM-家宽 联网检测通过");
                if (_defaultUpstreamPort is int pendingPort && _proxyInternet is null)
                    return new(false, $"等待 Proxy-代理（非家宽）默认代理端口 {pendingPort} 联网检测通过");
                return new(true, _defaultUpstreamPort is null
                    ? "家宽联网检测通过；未配置代理端口，不进行代理联网检测"
                    : "家宽与默认代理的联网检测均通过");
            }
        }
    }

    public string DirectStatus => GetStatus(NetworkHealthCheck.DirectInternet);
    public string ProxyStatus => GetStatus(NetworkHealthCheck.ProxyInternet);

    private string GetStatus(NetworkHealthCheck check)
    {
        lock (_gate)
        {
            bool direct = check == NetworkHealthCheck.DirectInternet;
            string label = direct ? "ESIM-家宽" : "Proxy-代理（非家宽）";
            if (!direct && _defaultUpstreamPort is int port) label += $" · 默认端口 {port}";
            AdapterSelection? adapter = direct ? _direct : _proxy;
            NetworkHealthResult? result = direct ? _directInternet : _proxyInternet;
            string? progress = direct ? _directProgress : _proxyProgress;
            return adapter?.IsReady != true ? label + "：网卡离线或没有 IP"
                : !direct && _defaultUpstreamPort is null ? label + "：未配置代理端口，不进行代理联网检测"
                : progress is not null ? label + "：" + progress
                : result is null ? label + "：等待联网检测"
                : result.IsHealthy ? label + "：上次联网检测通过，" + result.Detail
                : label + "：上次联网检测失败，" + result.Detail;
        }
    }
}
