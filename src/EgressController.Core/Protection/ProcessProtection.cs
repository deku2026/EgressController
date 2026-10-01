namespace EgressController.Core.Protection;

public readonly record struct ProcessKey(uint Pid, DateTime StartedAtUtc);
public sealed record GuardProcess(ProcessKey Key, uint ParentPid, string ExecutablePath);
public sealed record ProcessInspectionFailure(uint Pid, string FileName, string Error);
public sealed record ProtectedExecutable(string Path, string Application);
public sealed record TerminationResult(bool Succeeded, string? Error = null, bool AlreadyExited = false);
public sealed record ProtectionEvent(DateTimeOffset At, string Application, string Path, uint Pid,
    bool Succeeded, string Reason, string? Error);

/// <summary>The only boundary permitted to enumerate or terminate real processes.</summary>
public interface IProcessControl
{
    IReadOnlyList<GuardProcess> Capture();
    IReadOnlyList<ProcessInspectionFailure> InspectionFailures => [];
    TerminationResult Terminate(GuardProcess process);
}

/// <summary>Exact paths seed the tree; PID plus creation time identify every termination.</summary>
public sealed class ProcessProtection(IProcessControl processes)
{
    private readonly Dictionary<ProcessKey, ProtectedExecutable> _tracked = new();
    private readonly Dictionary<ProcessKey, string> _reportedFailures = new();
    private readonly HashSet<string> _inspectionReported = new(StringComparer.Ordinal);
    private readonly Queue<ProtectionEvent> _events = new();
    private readonly object _gate = new();
    private ProtectionEvent[] _eventSnapshot = [];
    public IReadOnlyList<ProtectionEvent> Events => Volatile.Read(ref _eventSnapshot);
    public int FailedCount { get; private set; }

    public void Sweep(IReadOnlyList<ProtectedExecutable> targets, IReadOnlySet<string> excludedPaths,
        bool ready, string reason)
    {
        lock (_gate)
        {
            var targetPaths = targets.GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (ProcessKey key in _tracked.Keys.Where(key => !targetPaths.ContainsKey(_tracked[key].Path)).ToArray()) _tracked.Remove(key);
            GuardProcess[] snapshot = processes.Capture().ToArray();
            var byPid = snapshot.ToDictionary(process => process.Key.Pid);
            var current = snapshot.Select(process => process.Key).ToHashSet();
            foreach (ProcessKey key in _tracked.Keys.Where(key => !current.Contains(key)).ToArray()) _tracked.Remove(key);
            foreach (ProcessKey key in _reportedFailures.Keys.Where(key => !current.Contains(key)).ToArray()) _reportedFailures.Remove(key);
            foreach (GuardProcess process in snapshot)
                if (!excludedPaths.Contains(process.ExecutablePath) && targetPaths.TryGetValue(process.ExecutablePath, out ProtectedExecutable? name))
                    _tracked[process.Key] = name;

            bool changed;
            do
            {
                changed = false;
                foreach (GuardProcess child in snapshot)
                {
                    if (_tracked.ContainsKey(child.Key) || excludedPaths.Contains(child.ExecutablePath)
                        || !byPid.TryGetValue(child.ParentPid, out GuardProcess? parent)
                        || parent.Key.StartedAtUtc > child.Key.StartedAtUtc
                        || !_tracked.TryGetValue(parent.Key, out ProtectedExecutable? application)) continue;
                    _tracked[child.Key] = application;
                    changed = true;
                }
            } while (changed);

            FailedCount = 0;
            if (ready) return;
            var inaccessible = processes.InspectionFailures.Where(failure => targetPaths.Keys.Any(path =>
                string.Equals(Path.GetFileName(path), failure.FileName, StringComparison.OrdinalIgnoreCase))).ToArray();
            _inspectionReported.IntersectWith(inaccessible.Select(failure => $"{failure.Pid}:{failure.FileName}:{failure.Error}"));
            foreach (ProcessInspectionFailure failure in inaccessible)
            {
                FailedCount++;
                if (!_inspectionReported.Add($"{failure.Pid}:{failure.FileName}:{failure.Error}")) continue;
                _events.Enqueue(new(DateTimeOffset.UtcNow, failure.FileName, "完整路径无法确认", failure.Pid,
                    false, reason, failure.Error));
            }
            // Newer descendants are usually youngest; each identity is separately validated by the adapter.
            foreach (GuardProcess process in snapshot.OrderByDescending(process => process.Key.StartedAtUtc))
            {
                if (!_tracked.TryGetValue(process.Key, out ProtectedExecutable? application) || excludedPaths.Contains(process.ExecutablePath)) continue;
                TerminationResult result;
                try { result = processes.Terminate(process); }
                catch (Exception exception) { result = new(false, exception.Message); }
                if (!result.Succeeded) FailedCount++;
                if (result.AlreadyExited) continue;
                string errorKey = reason + "|" + result.Error;
                if (!result.Succeeded && _reportedFailures.GetValueOrDefault(process.Key) == errorKey) continue;
                if (result.Succeeded) _reportedFailures.Remove(process.Key);
                else _reportedFailures[process.Key] = errorKey;
                _events.Enqueue(new(DateTimeOffset.UtcNow, application.Application, process.ExecutablePath,
                    process.Key.Pid, result.Succeeded, reason, result.Error));
            }
            while (_events.Count > 200) _events.Dequeue();
            Volatile.Write(ref _eventSnapshot, _events.Reverse().ToArray());
        }
    }
}
