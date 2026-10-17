namespace EgressController.Core.Protection;

public readonly record struct ProcessKey(uint Pid, DateTime StartedAtUtc);
public sealed record GuardProcess(ProcessKey Key, uint ParentPid, string ExecutablePath, string? ObservedExecutablePath = null)
{
    public IEnumerable<string> Paths => ObservedExecutablePath is null
        ? [ExecutablePath] : new[] { ExecutablePath, ObservedExecutablePath }.Distinct(StringComparer.OrdinalIgnoreCase);
}
public sealed record ProcessInspectionFailure(uint Pid, string FileName, string Error, string? ExecutablePath = null);
public sealed record ProtectedExecutable(string Path, string Application);
public sealed record ProtectedDirectory(string Path, string Application);
public sealed record TrackedProtectionProcess(ProcessKey Identity, ProtectedExecutable Source);
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
public sealed class ProcessProtection(IProcessControl processes, TimeProvider? clock = null)
{
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromHours(24);
    public const int HistoryLimit = 200;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<ProcessKey, ProtectedExecutable> _tracked = new();
    private readonly Dictionary<string, ProtectedExecutable> _knownExecutables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProcessKey, string> _reportedFailures = new();
    private readonly HashSet<string> _inspectionReported = new(StringComparer.Ordinal);
    private readonly Queue<ProtectionEvent> _events = new();
    private readonly object _gate = new();
    private ProtectionEvent[] _eventSnapshot = [];
    public IReadOnlyList<ProtectionEvent> Events => Volatile.Read(ref _eventSnapshot);
    public int FailedCount { get; private set; }
    public int PendingRoutingCount { get; private set; }
    public InheritedProtectionExecutable[] CaptureRoutingMembers()
    {
        lock (_gate) return _knownExecutables.Select(item => new InheritedProtectionExecutable(item.Key, item.Value)).ToArray();
    }

    public TrackedProtectionProcess[] CaptureTracking()
    {
        lock (_gate) return _tracked.Select(item => new TrackedProtectionProcess(item.Key, item.Value)).ToArray();
    }

    public void RestoreTracking(IEnumerable<TrackedProtectionProcess> processes, IEnumerable<ProtectionEvent> events)
    {
        lock (_gate)
        {
            // Sweep validates each saved PID/creation time against a fresh process snapshot.
            foreach (var process in processes) _tracked[process.Identity] = process.Source;
            foreach (var item in events.OrderBy(item => item.At).TakeLast(HistoryLimit)) _events.Enqueue(item);
            PruneHistory();
            Volatile.Write(ref _eventSnapshot, _events.Reverse().ToArray());
        }
    }

    public static bool IsWithinDirectory(string path, string directory)
        => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void ClearHistory()
    {
        lock (_gate)
        {
            _events.Clear();
            Volatile.Write(ref _eventSnapshot, []);
        }
    }

    private void PruneHistory()
    {
        DateTimeOffset cutoff = _clock.GetUtcNow() - HistoryRetention;
        bool changed = false;
        while (_events.Count > 0 && (_events.Count > HistoryLimit || _events.Peek().At <= cutoff))
        {
            _events.Dequeue();
            changed = true;
        }
        if (changed) Volatile.Write(ref _eventSnapshot, _events.Reverse().ToArray());
    }

    public int Sweep(IReadOnlyList<ProtectedExecutable> targets, IReadOnlySet<string> excludedPaths,
        bool ready, string reason, IReadOnlyList<ProtectedDirectory>? directories = null,
        Func<GuardProcess, bool>? canRun = null)
    {
        lock (_gate)
        {
            PruneHistory();
            var targetPaths = targets.GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            directories ??= [];
            foreach (ProcessKey key in _tracked.Keys.Where(key => !targetPaths.ContainsKey(_tracked[key].Path)
                && !directories.Any(root => IsWithinDirectory(_tracked[key].Path, root.Path))).ToArray()) _tracked.Remove(key);
            foreach (string path in _knownExecutables.Keys.Where(path => !targetPaths.ContainsKey(_knownExecutables[path].Path)
                && !directories.Any(root => IsWithinDirectory(_knownExecutables[path].Path, root.Path))).ToArray()) _knownExecutables.Remove(path);
            GuardProcess[] snapshot = processes.Capture().ToArray();
            ProcessInspectionFailure[] inspectionFailures = processes.InspectionFailures.ToArray();
            var unreadablePids = inspectionFailures.Select(failure => failure.Pid).ToHashSet();
            var byPid = snapshot.ToDictionary(process => process.Key.Pid);
            var current = snapshot.Select(process => process.Key).ToHashSet();
            // Absence from the readable snapshot is not evidence of exit. Keep an ambiguous
            // identity until the PID is absent from enumeration or a different creation time
            // is positively observed; never terminate an unreadable PID using a stale identity.
            bool ConfirmedGone(ProcessKey key) => !current.Contains(key)
                && (byPid.ContainsKey(key.Pid) || !unreadablePids.Contains(key.Pid));
            foreach (ProcessKey key in _tracked.Keys.Where(ConfirmedGone).ToArray()) _tracked.Remove(key);
            foreach (ProcessKey key in _reportedFailures.Keys.Where(ConfirmedGone).ToArray()) _reportedFailures.Remove(key);
            foreach (GuardProcess process in snapshot)
            {
                if (process.Paths.Any(excludedPaths.Contains)) continue;
                ProtectedExecutable? name = process.Paths.Select(path => targetPaths.GetValueOrDefault(path)).FirstOrDefault(item => item is not null);
                if (name is not null)
                    _tracked[process.Key] = name;
                else
                {
                    foreach (string path in process.Paths.Where(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                        if (directories.FirstOrDefault(root => IsWithinDirectory(path, root.Path)) is { } root)
                        {
                            _tracked[process.Key] = new(path, root.Application);
                            break;
                        }
                }
            }

            bool changed;
            do
            {
                changed = false;
                foreach (GuardProcess child in snapshot)
                {
                    if (_tracked.ContainsKey(child.Key) || child.Paths.Any(excludedPaths.Contains)
                        || !byPid.TryGetValue(child.ParentPid, out GuardProcess? parent)
                        || parent.Key.StartedAtUtc > child.Key.StartedAtUtc
                        || !_tracked.TryGetValue(parent.Key, out ProtectedExecutable? application)) continue;
                    _tracked[child.Key] = application;
                    changed = true;
                }
            } while (changed);

            foreach (GuardProcess process in snapshot)
                if (_tracked.TryGetValue(process.Key, out var source) && !process.Paths.Any(excludedPaths.Contains))
                    foreach (string path in process.Paths) _knownExecutables[path] = source;
            FailedCount = 0;
            PendingRoutingCount = 0;
            if (ready && canRun is null) return 0;
            var inaccessible = inspectionFailures.Where(failure => !ready && (_tracked.Keys.Any(key => key.Pid == failure.Pid)
                || (failure.ExecutablePath is { } path
                ? !excludedPaths.Contains(path) && (targetPaths.ContainsKey(path) || directories.Any(root => IsWithinDirectory(path, root.Path)))
                : targetPaths.Keys.Any(target => string.Equals(Path.GetFileName(target), failure.FileName, StringComparison.OrdinalIgnoreCase))))).ToArray();
            _inspectionReported.IntersectWith(inaccessible.Select(failure => $"{failure.Pid}:{failure.FileName}:{failure.Error}"));
            foreach (ProcessInspectionFailure failure in inaccessible)
            {
                FailedCount++;
                if (!_inspectionReported.Add($"{failure.Pid}:{failure.FileName}:{failure.Error}")) continue;
                _events.Enqueue(new(_clock.GetUtcNow(), failure.FileName, failure.ExecutablePath ?? "完整路径无法确认", failure.Pid,
                    false, reason, failure.Error));
            }
            // Newer descendants are usually youngest; each identity is separately validated by the adapter.
            foreach (GuardProcess process in snapshot.OrderByDescending(process => process.Key.StartedAtUtc))
            {
                if (!_tracked.TryGetValue(process.Key, out ProtectedExecutable? application) || process.Paths.Any(excludedPaths.Contains)) continue;
                if (ready && canRun!(process)) continue;
                if (ready) PendingRoutingCount++;
                string processReason = ready ? "此进程的分流规则尚未生效，等待配置更新" : reason;
                TerminationResult result;
                try { result = processes.Terminate(process); }
                catch (Exception exception) { result = new(false, exception.Message); }
                if (!result.Succeeded) FailedCount++;
                if (result.AlreadyExited) continue;
                string errorKey = processReason + "|" + result.Error;
                if (!result.Succeeded && _reportedFailures.GetValueOrDefault(process.Key) == errorKey) continue;
                if (result.Succeeded) _reportedFailures.Remove(process.Key);
                else _reportedFailures[process.Key] = errorKey;
                _events.Enqueue(new(_clock.GetUtcNow(), application.Application, process.ExecutablePath,
                    process.Key.Pid, result.Succeeded, processReason, result.Error));
            }
            PruneHistory();
            Volatile.Write(ref _eventSnapshot, _events.Reverse().ToArray());
            return FailedCount;
        }
    }
}
