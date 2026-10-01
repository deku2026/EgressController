namespace EgressController.Core.Protection;

public readonly record struct DohCheckTicket(long Generation, long Round);
public sealed record DohHealthSnapshot(bool Ready, bool Checking, string Reason);

/// <summary>A pending check never erases a confirmed result. Only a completed round,
/// an explicit invalidation, or an overdue check may change readiness.</summary>
public sealed class DohHealthState(TimeProvider? clock = null)
{
    public static readonly TimeSpan CheckDeadline = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MonitorDeadline = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private long _generation, _round;
    private bool _ready, _checking;
    private string _reason = "等待 ESIM-家宽 的首次 DoH 检测";
    private DateTimeOffset? _deadline;

    public void Invalidate(string reason)
    {
        lock (_gate)
        {
            _generation++;
            _ready = _checking = false;
            _deadline = null;
            _reason = reason;
        }
    }

    public DohCheckTicket BeginCheck()
    {
        lock (_gate)
        {
            Expire();
            _round++;
            _checking = true;
            _deadline = _clock.GetUtcNow() + CheckDeadline;
            return new(_generation, _round);
        }
    }

    public bool IsCurrent(DohCheckTicket ticket)
    {
        lock (_gate)
        {
            Expire();
            return _checking && ticket.Generation == _generation && ticket.Round == _round;
        }
    }

    public bool RecordFailure(DohCheckTicket ticket, string reason)
    {
        lock (_gate)
        {
            if (!IsCurrent(ticket)) return false;
            _ready = false;
            _reason = reason;
            return true;
        }
    }

    public void Cancel(DohCheckTicket ticket)
    {
        lock (_gate)
        {
            if (!IsCurrent(ticket)) return;
            _checking = false;
            _deadline = _clock.GetUtcNow() + MonitorDeadline;
        }
    }

    public bool Complete(DohCheckTicket ticket, bool ready, string? error = null)
    {
        lock (_gate)
        {
            if (!IsCurrent(ticket)) return false;
            _ready = ready;
            _checking = false;
            _reason = ready ? "网络已就绪，可以打开所选应用" : error ?? "ESIM-家宽 的两个 DoH 均失败";
            _deadline = _clock.GetUtcNow() + MonitorDeadline;
            return true;
        }
    }

    public DohHealthSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                Expire();
                return new(_ready, _checking, _ready && _checking
                    ? "网络已就绪，正在复检（保留上次结果）" : _reason);
            }
        }
    }

    private void Expire()
    {
        if (_deadline is not { } deadline || _clock.GetUtcNow() < deadline) return;
        _generation++;
        _ready = _checking = false;
        _deadline = null;
        _reason = "DoH 检测超时：未在期限内完成，等待重新检测";
    }
}
