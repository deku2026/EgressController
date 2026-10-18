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

/// <summary>Only saved EXE paths define membership. ParentPid never expands protection.</summary>
public sealed class ProcessProtection(IProcessControl processes, TimeProvider? clock = null)
{
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromHours(24);
    public const int HistoryLimit = 200;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<ProcessKey, ProtectedExecutable> _tracked = new();
    private readonly Dictionary<ProcessKey, string> _reportedFailures = new();
    private readonly HashSet<string> _inspectionReported = new(StringComparer.Ordinal);
    private readonly Queue<ProtectionEvent> _events = new();
    private readonly object _gate = new();
    private ProtectionEvent[] _eventSnapshot = [];
    public IReadOnlyList<ProtectionEvent> Events => Volatile.Read(ref _eventSnapshot);
    public int FailedCount { get; private set; }
    public TrackedProtectionProcess[] CaptureTracking()
    {
        lock (_gate) return _tracked.Select(item => new TrackedProtectionProcess(item.Key, item.Value)).ToArray();
    }

    public void RestoreTracking(IEnumerable<TrackedProtectionProcess> processes, IEnumerable<ProtectionEvent> events)
    {
        lock (_gate)
        {
            // Legacy checkpoints may contain OS descendants. Never restore those as membership.
            // A fresh exact-path snapshot is authoritative; persisted records are history only.
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
        bool ready, string reason)
    {
        lock (_gate)
        {
            PruneHistory();
            var selected = targets.GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            GuardProcess[] snapshot = processes.Capture().ToArray();
            var failures = processes.InspectionFailures.ToArray();
            var unreadable = failures.Select(item => item.Pid).ToHashSet();
            var byPid = snapshot.ToDictionary(item => item.Key.Pid);
            bool Retain(ProcessKey key) => selected.ContainsKey(_tracked[key].Path)
                && (byPid.TryGetValue(key.Pid, out var process) ? process.Key == key
                    && process.Paths.Any(path => selected.ContainsKey(path) && !excludedPaths.Contains(path))
                    : unreadable.Contains(key.Pid));
            foreach (var key in _tracked.Keys.Where(key => !Retain(key)).ToArray()) _tracked.Remove(key);
            foreach (var key in _reportedFailures.Keys.Where(key => !_tracked.ContainsKey(key)).ToArray()) _reportedFailures.Remove(key);
            foreach (var process in snapshot)
            {
                if (process.Paths.Any(excludedPaths.Contains)) continue;
                var source = process.Paths.Select(path => selected.GetValueOrDefault(path)).FirstOrDefault(item => item is not null);
                if (source is not null) _tracked[process.Key] = source;
            }
            FailedCount = 0;
            if (ready)
            {
                _inspectionReported.Clear();
                _reportedFailures.Clear();
                return 0;
            }
            var inaccessible = failures.Where(failure => _tracked.Keys.Any(key => key.Pid == failure.Pid)
                || (failure.ExecutablePath is { } path
                    ? selected.ContainsKey(path) && !excludedPaths.Contains(path)
                    : selected.Keys.Any(path => Path.GetFileName(path).Equals(failure.FileName, StringComparison.OrdinalIgnoreCase)))).ToArray();
            _inspectionReported.IntersectWith(inaccessible.Select(failure => $"{failure.Pid}:{failure.Error}"));
            foreach (var failure in inaccessible)
            {
                FailedCount++;
                if (_inspectionReported.Add($"{failure.Pid}:{failure.Error}"))
                    _events.Enqueue(new(_clock.GetUtcNow(), failure.FileName, failure.ExecutablePath ?? "完整路径无法确认",
                        failure.Pid, false, reason, failure.Error));
            }
            foreach (var process in snapshot)
            {
                if (!_tracked.TryGetValue(process.Key, out var source) || process.Paths.Any(excludedPaths.Contains)) continue;
                TerminationResult result;
                try { result = processes.Terminate(process); }
                catch (Exception error) { result = new(false, error.Message); }
                if (!result.Succeeded) FailedCount++;
                if (result.AlreadyExited) continue;
                string errorKey = reason + "|" + result.Error;
                if (!result.Succeeded && _reportedFailures.GetValueOrDefault(process.Key) == errorKey) continue;
                if (result.Succeeded) _reportedFailures.Remove(process.Key);
                else _reportedFailures[process.Key] = errorKey;
                _events.Enqueue(new(_clock.GetUtcNow(), source.Application, process.ExecutablePath,
                    process.Key.Pid, result.Succeeded, reason, result.Error));
            }
            PruneHistory();
            Volatile.Write(ref _eventSnapshot, _events.Reverse().ToArray());
            return FailedCount;
        }
    }
}
