using EgressController.Core.Models;

namespace EgressController.Core.Protection;

public enum NetworkHealthCheck { CloudflareDoh, ProxyInternet }
public sealed record NetworkHealthResult(bool IsHealthy, string Detail);

/// <summary>Completed probes remain authoritative during rechecks. Live adapter changes
/// invalidate in-flight results; cached configuration bindings must never be observed here.</summary>
public sealed class NetworkHealthState
{
    private readonly object _gate = new();
    private int _generation;
    private AdapterSelection? _direct;
    private AdapterSelection? _proxy;
    private NetworkHealthResult? _doh;
    private NetworkHealthResult? _internet;

    public int Generation { get { lock (_gate) return _generation; } }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _doh = null;
            _internet = null;
        }
    }

    public int ObserveAdapters(NetworkEnvironmentSnapshot live)
    {
        lock (_gate)
        {
            if (_direct != live.DefaultAdapter || _proxy != live.ProxyAdapter) _generation++;
            if (_direct != live.DefaultAdapter) _doh = null;
            if (_proxy != live.ProxyAdapter) _internet = null;
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
            if (check == NetworkHealthCheck.CloudflareDoh) _doh = result;
            else _internet = result;
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
                if (_doh?.IsHealthy == false) return new(false, "Cloudflare DoH 不可用：" + _doh.Detail);
                if (_internet?.IsHealthy == false) return new(false, "Proxy-代理（非家宽）无法联网：" + _internet.Detail);
                if (_doh is null) return new(false, "等待 Cloudflare DoH 首次检测通过");
                if (_internet is null) return new(false, "等待 Proxy-代理（非家宽）联网检测通过");
                return new(true, "出口网络与 Cloudflare DoH 检测通过");
            }
        }
    }

    public string ProxyStatus
    {
        get
        {
            lock (_gate)
                return _proxy?.IsReady != true ? "Proxy-代理（非家宽）：网卡离线或没有 IP"
                    : _internet is null ? "Proxy-代理（非家宽）：等待联网检测"
                    : _internet.IsHealthy ? "Proxy-代理（非家宽）：联网检测通过"
                    : "Proxy-代理（非家宽）：联网失败，" + _internet.Detail;
        }
    }
}
