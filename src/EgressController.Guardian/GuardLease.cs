using EgressController.Core.Protection;

namespace EgressController.Guardian;

/// <summary>Monotonic, session-scoped lease. A heartbeat is never an Internet health check.</summary>
public sealed class GuardLease(TimeProvider? clock = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Guid _session;
    private long _sequence;
    private long _observedAt;
    private bool _connected;
    private GuardHeartbeat? _heartbeat;
    public ProcessKey? Main { get; private set; }
    public string MainPath { get; private set; } = "";
    public GuardHeartbeat? Heartbeat => _heartbeat;

    public void Connect(Guid session, ProcessKey main, string path)
    {
        if (session == Guid.Empty) throw new InvalidDataException("守护会话不能为空。");
        _session = session;
        Main = main;
        MainPath = path;
        _sequence = 0;
        _heartbeat = null;
        _connected = true;
    }

    public bool Report(Guid session, long sequence, GuardHeartbeat heartbeat)
    {
        if (!Advance(session, sequence)) return false;
        _heartbeat = heartbeat;
        _observedAt = _clock.GetTimestamp();
        return true;
    }

    public void Disconnect(Guid session)
    {
        if (session != _session) return;
        _connected = false;
        _heartbeat = null;
    }

    public bool Matches(Guid session) => _connected && session == _session;
    public bool Advance(Guid session, long sequence)
    {
        if (!Matches(session) || sequence <= _sequence) return false;
        _sequence = sequence;
        return true;
    }

    public void Revoke() => _heartbeat = null;

    public ProtectionReadiness Evaluate(string revision, bool mainAlive, string? tunError)
    {
        if (!_connected || !mainAlive) return new(false, "主程序未运行或通信已断开");
        if (_heartbeat is null) return new(false, "等待主程序确认 TUN 接管");
        if (_clock.GetElapsedTime(_observedAt) >= Timeout) return new(false, "主程序状态更新超时");
        if (_heartbeat.PolicyRevision != revision) return new(false, "保护名单尚未同步到当前 TUN 会话");
        if (!_heartbeat.TunReady || _heartbeat.Core is null || string.IsNullOrWhiteSpace(_heartbeat.CorePath))
            return new(false, _heartbeat.Reason ?? "TUN 未就绪");
        if (tunError is not null) return new(false, tunError);
        return new(true, "主程序在线，TUN 已接管");
    }
}
