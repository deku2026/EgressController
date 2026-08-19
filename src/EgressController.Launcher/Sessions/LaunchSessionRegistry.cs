using EgressController.Core.Models;

namespace EgressController.Launcher.Sessions;

/// <summary>
/// Concurrency-safe registry of live <see cref="LaunchSession"/>s and their pid→session backing,
/// so the Step 11 router can ask "does PID p belong to a managed (active-owned) session?" at
/// accept time. Every active member carries its own process StartTime identity so a reused child
/// PID cannot inherit an earlier process's Managed membership.
/// </summary>
public sealed class LaunchSessionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, LaunchSession> _sessions = new();
    private readonly Dictionary<uint, HashSet<Guid>> _pidToSessions = new();

    public void Register(LaunchSession session)
    {
        lock (_gate)
        {
            if (session.ActiveOwnedPids.Contains(session.RootPid)
                && !session.ActiveOwnedProcessStartTimes.ContainsKey(session.RootPid))
            {
                var identities = new Dictionary<uint, DateTime>(session.ActiveOwnedProcessStartTimes);
                identities[session.RootPid] = session.RootStartTimeUtc;
                session.ActiveOwnedProcessStartTimes = identities;
            }
            _sessions[session.SessionId] = session;
            AddMembership(session.SessionId, session.RootPid, session.ActiveOwnedPids);
        }
    }

    public void SetActiveOwnedPids(Guid sessionId, IReadOnlySet<uint> ownedPids)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return;
            foreach (uint p in session.ActiveOwnedPids)
                RemoveMembership(sessionId, p);
            session.ActiveOwnedPids = ownedPids.ToHashSet();
            session.ActiveOwnedProcessStartTimes = session.ActiveOwnedPids.ToDictionary(
                pid => pid,
                pid => session.ActiveOwnedProcessStartTimes.TryGetValue(pid, out DateTime started)
                    ? started
                    : pid == session.RootPid ? session.RootStartTimeUtc : DateTime.MinValue);
            foreach (uint p in session.ActiveOwnedPids)
                Add(sessionId, p);
        }
    }

    /// <summary>
    /// Replaces the process-tree candidates observed for a session. Candidates are diagnostic
    /// state only; routing membership is granted to the root and the separately verified
    /// <paramref name="ownedPids"/> set.
    /// </summary>
    public void SetCandidatePids(Guid sessionId, IReadOnlySet<uint> candidatePids)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
                session.CandidatePids = candidatePids.ToHashSet();
        }
    }

    /// <summary>
    /// Atomically promotes a just-observed descendant after accept-time ancestry and executable
    /// verification. This closes the startup window before the periodic reconciler sees a fast
    /// Electron/Chromium network-service child.
    /// </summary>
    public bool TrackOwnedProcess(Guid sessionId, ProcessIdentity identity)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out LaunchSession? session))
                return false;

            var candidates = session.CandidatePids.ToHashSet();
            candidates.Add(identity.Pid);
            session.CandidatePids = candidates;

            var owned = session.ActiveOwnedPids.ToHashSet();
            owned.Add(identity.Pid);
            session.ActiveOwnedPids = owned;
            var identities = new Dictionary<uint, DateTime>(session.ActiveOwnedProcessStartTimes);
            identities[identity.Pid] = identity.StartTimeUtc;
            session.ActiveOwnedProcessStartTimes = identities;
            Add(sessionId, identity.Pid);
            return true;
        }
    }

    /// <summary>
    /// Applies one process-tree/Job reconciliation without losing a process promoted concurrently
    /// by accept-time routing. Entries added after <paramref name="baselineOwned"/> was captured
    /// are merged into the result; stale entries present in the baseline are removed normally.
    /// </summary>
    public void ReconcileMembership(
        Guid sessionId,
        IReadOnlySet<uint> baselineCandidates,
        IReadOnlyDictionary<uint, DateTime> baselineOwned,
        IReadOnlySet<uint> candidatePids,
        IReadOnlyDictionary<uint, DateTime> ownedProcesses)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out LaunchSession? session))
                return;

            var mergedCandidates = candidatePids.ToHashSet();
            foreach (uint pid in session.CandidatePids)
                if (!baselineCandidates.Contains(pid))
                    mergedCandidates.Add(pid);

            var mergedOwned = new Dictionary<uint, DateTime>(ownedProcesses);
            foreach ((uint pid, DateTime started) in session.ActiveOwnedProcessStartTimes)
            {
                bool existedAtBaseline = baselineOwned.TryGetValue(pid, out DateTime baselineStarted)
                    && baselineStarted == started;
                if (!existedAtBaseline)
                    mergedOwned[pid] = started;
            }

            foreach (uint pid in session.ActiveOwnedPids)
                RemoveMembership(sessionId, pid);
            session.CandidatePids = mergedCandidates;
            session.ActiveOwnedPids = mergedOwned.Keys.ToHashSet();
            session.ActiveOwnedProcessStartTimes = mergedOwned;
            foreach (uint pid in session.ActiveOwnedPids)
                Add(sessionId, pid);
        }
    }

    public bool MarkRootExited(Guid sessionId, DateTime? observedAtUtc = null)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return false;

            session.RootExited = true;
            session.RootExitedAtUtc ??= observedAtUtc ?? DateTime.UtcNow;
            RemoveMembership(sessionId, session.RootPid);
            session.ActiveOwnedPids = session.ActiveOwnedPids
                .Where(pid => pid != session.RootPid)
                .ToHashSet();
            session.ActiveOwnedProcessStartTimes = session.ActiveOwnedProcessStartTimes
                .Where(pair => pair.Key != session.RootPid)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            return true;
        }
    }

    public LaunchSession? Get(Guid sessionId)
    {
        lock (_gate)
            return _sessions.TryGetValue(sessionId, out var s) ? s : null;
    }

    /// <summary>Session ids whose root or active-owned set contains <paramref name="pid"/>.</summary>
    public IReadOnlyList<Guid> SessionsForPid(uint pid)
    {
        lock (_gate)
            return _pidToSessions.TryGetValue(pid, out var set) ? set.ToList() : Array.Empty<Guid>();
    }

    public IReadOnlyList<LaunchSession> All()
    {
        lock (_gate)
            return _sessions.Values.ToList();
    }

    public void Unregister(Guid sessionId)
    {
        lock (_gate)
        {
            RemoveLocked(sessionId);
        }
    }

    public IReadOnlyList<Guid> UnregisterForTarget(string targetId)
    {
        lock (_gate)
        {
            Guid[] ids = _sessions.Values
                .Where(session => string.Equals(session.TargetId, targetId, StringComparison.Ordinal))
                .Select(session => session.SessionId)
                .ToArray();
            foreach (Guid id in ids)
                RemoveLocked(id);
            return ids;
        }
    }

    private void RemoveLocked(Guid sessionId)
    {
        if (!_sessions.Remove(sessionId, out var s))
            return;
        RemoveMembership(sessionId, s.RootPid);
        foreach (uint p in s.ActiveOwnedPids)
            RemoveMembership(sessionId, p);
    }

    private void AddMembership(Guid id, uint pid, IReadOnlySet<uint> owned)
    {
        Add(id, pid);
        foreach (uint p in owned)
            Add(id, p);
    }

    private void Add(Guid id, uint pid)
    {
        if (!_pidToSessions.TryGetValue(pid, out var set))
            _pidToSessions[pid] = set = new HashSet<Guid>();
        set.Add(id);
    }

    private void RemoveMembership(Guid id, uint pid)
    {
        if (_pidToSessions.TryGetValue(pid, out var set))
        {
            set.Remove(id);
            if (set.Count == 0)
                _pidToSessions.Remove(pid);
        }
    }
}
