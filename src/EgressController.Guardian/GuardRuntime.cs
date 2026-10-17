using System.Text.Json;
using EgressController.Core.Protection;

namespace EgressController.Guardian;

public interface IGuardFacts
{
    bool IsAlive(ProcessKey identity, string executable);
    string? InspectTun(ProcessKey core, string executable);
}

public sealed class GuardRuntime
{
    private readonly object _gate = new();
    private readonly IGuardStore _store;
    private readonly IGuardFacts _facts;
    private readonly ProcessProtection _protection;
    private readonly GuardLease _lease;
    private readonly HashSet<string> _selfPaths;
    private GuardPolicy? _policy;
    private string? _lastCheckpoint;
    private string? _infrastructureError;
    private GuardStatus _status = new(true, "主程序未运行", 0, 0, null, []);
    public GuardRuntime(IGuardStore store, IProcessControl processes, IGuardFacts facts,
        IEnumerable<string> selfPaths, TimeProvider? clock = null)
    {
        _store = store; _facts = facts;
        _protection = new(processes, clock);
        _lease = new(clock);
        _selfPaths = selfPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _policy = store.LoadPolicy(); // Corrupt/missing data never becomes a fabricated empty selection.
        var checkpoint = store.LoadCheckpoint();
        _protection.RestoreTracking(checkpoint.Processes, checkpoint.Events);
    }
    public GuardStatus Status { get { lock (_gate) return _status; } }
    public void SetInfrastructureError(string? error)
    {
        lock (_gate)
        {
            _infrastructureError = error;
            if (error is not null) _lease.Revoke();
        }
    }
    public void OpenSession(Guid session, ProcessKey main, string path, GuardPolicy policy)
    {
        lock (_gate) { SetPolicy(policy); Connect(session, main, path); }
    }
    public void UpdatePolicy(Guid session, long sequence, GuardPolicy policy)
    {
        lock (_gate)
        {
            if (!_lease.Advance(session, sequence)) throw new UnauthorizedAccessException("主程序会话/消息已过期。");
            SetPolicy(policy);
        }
    }
    public GuardStatus PrepareTransition(Guid session, long sequence)
    {
        lock (_gate)
        {
            if (!_lease.Advance(session, sequence)) throw new UnauthorizedAccessException("主程序会话/消息已过期。");
            return PrepareTransition();
        }
    }
    public void Connect(Guid session, ProcessKey main, string path)
    {
        lock (_gate) _lease.Connect(session, main, path);
    }
    public void Disconnect(Guid session)
    {
        lock (_gate) _lease.Disconnect(session);
    }
    public void SetPolicy(GuardPolicy policy)
    {
        lock (_gate)
        {
            policy = policy.Validate();
            if (_policy?.Revision == policy.Revision)
            {
                if (JsonSerializer.Serialize(_policy, GuardJson.Default.GuardPolicy) != JsonSerializer.Serialize(policy, GuardJson.Default.GuardPolicy))
                    throw new InvalidDataException("同一守护版本不能对应不同名单。");
                return;
            }
            _lease.Revoke();
            _store.SavePolicy(policy);
            _policy = policy;
        }
    }
    public bool Report(Guid session, long sequence, GuardHeartbeat heartbeat)
    {
        lock (_gate) return _lease.Report(session, sequence, heartbeat);
    }
    public GuardStatus PrepareTransition()
    {
        lock (_gate)
        {
            _lease.Revoke();
            return Tick();
        }
    }
    public void ClearHistory()
    {
        lock (_gate)
        {
            _protection.ClearHistory();
            Persist();
            _status = _status with { Events = [] };
        }
    }
    public GuardStatus Tick()
    {
        lock (_gate)
        {
            if (_policy is null)
                return _status = new(true, "尚未收到保护名单，不能确认保护范围", 0, 0, null, []);
            bool ready = false;
            string reason;
            try
            {
                bool mainAlive = _lease.Main is { } main && _facts.IsAlive(main, _lease.MainPath);
                string? tunError = _lease.Heartbeat is { TunReady: true, Core: { } core, CorePath: { } path }
                    ? _facts.InspectTun(core, path) : null;
                var readiness = _lease.Evaluate(_policy.Revision, mainAlive, tunError);
                ready = readiness.Ready;
                reason = readiness.Reason;
                if (_infrastructureError is not null) { ready = false; reason = _infrastructureError; }
            }
            catch (Exception exception) { reason = "无法核验主程序/TUN：" + exception.Message; }
            int failures;
            try
            {
                var excluded = _selfPaths.Concat(_policy.Exclusions).ToHashSet(StringComparer.OrdinalIgnoreCase);
                failures = _protection.Sweep(_policy.Executables, excluded, ready, "独立守护：" + reason, _policy.Directories);
                Persist();
            }
            catch (Exception exception)
            {
                failures = Math.Max(1, _protection.FailedCount);
                ready = false;
                _lease.Revoke();
                reason = "保护未完成：" + exception.Message;
            }
            if (failures > 0) reason = $"保护未完成：{failures} 项失败；{reason}";
            return _status = new(!ready || failures > 0, reason, _policy.Executables.Length,
                failures, _policy.Revision, _protection.Events.ToArray());
        }
    }
    private void Persist()
    {
        var snapshot = new GuardCheckpoint(_protection.CaptureTracking(), _protection.Events.ToArray());
        string serialized = JsonSerializer.Serialize(snapshot, GuardJson.Default.GuardCheckpoint);
        if (_lastCheckpoint == serialized) return;
        _store.SaveCheckpoint(snapshot);
        _lastCheckpoint = serialized;
    }
}
