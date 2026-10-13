using EgressController.Core.Models;

namespace EgressController.Core.Protection;

public enum NetworkHealthCheck { DirectInternet, ProxyInternet }
public sealed record NetworkHealthResult(bool IsHealthy, string Detail);

/// <summary>Completed probes remain authoritative during rechecks. Live adapter changes
/// invalidate in-flight results; cached configuration bindings must never be observed here.</summary>
public sealed class NetworkHealthState
{
    private readonly object _gate = new();
    private int _generation;
    private AdapterSelection? _direct;
    private AdapterSelection? _proxy;
    private NetworkHealthResult? _directInternet;
    private NetworkHealthResult? _proxyInternet;

    public int Generation { get { lock (_gate) return _generation; } }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _directInternet = null;
            _proxyInternet = null;
        }
    }

    public int ObserveAdapters(NetworkEnvironmentSnapshot live)
    {
        lock (_gate)
        {
            if (_direct != live.DefaultAdapter || _proxy != live.ProxyAdapter) _generation++;
            if (_direct != live.DefaultAdapter) _directInternet = null;
            if (_proxy != live.ProxyAdapter) _proxyInternet = null;
            _direct = live.DefaultAdapter;
            _proxy = live.ProxyAdapter;
            return _generation;
        }
    }

    public bool Complete(int generation, NetworkHealthCheck check, NetworkHealthResult result, CancellationToken token)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested || generation != _generation) return false;
            if (check == NetworkHealthCheck.DirectInternet) _directInternet = result;
            else _proxyInternet = result;
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
                if (_proxyInternet?.IsHealthy == false) return new(false, "Proxy-代理（非家宽）无法联网：" + _proxyInternet.Detail);
                if (_directInternet is null) return new(false, "等待 ESIM-家宽 联网检测通过");
                if (_proxyInternet is null) return new(false, "等待 Proxy-代理（非家宽）联网检测通过");
                return new(true, "两张出口网卡的联网检测均通过");
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
            AdapterSelection? adapter = direct ? _direct : _proxy;
            NetworkHealthResult? result = direct ? _directInternet : _proxyInternet;
            return adapter?.IsReady != true ? label + "：网卡离线或没有 IP"
                : result is null ? label + "：等待联网检测"
                : result.IsHealthy ? label + "：上次联网检测通过"
                : label + "：上次联网检测失败，" + result.Detail;
        }
    }
}
