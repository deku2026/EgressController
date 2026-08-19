namespace EgressController.Core.Models;

/// <summary>A tracking record for one launched session (plan §6 LaunchSession).</summary>
public sealed class LaunchSession
{
    public required Guid SessionId { get; init; }
    public required string TargetId { get; init; }
    public required uint RootPid { get; init; }
    public required DateTime RootStartTimeUtc { get; init; }
    public required DateTime StartedAtUtc { get; init; }

    /// <summary>Set by the root process Exited event or by reconciliation when the PID/start
    /// identity is no longer alive. Descendant ownership may remain until the next scan retires
    /// all verified child PIDs.</summary>
    public bool RootExited { get; set; }
    public DateTime? RootExitedAtUtc { get; set; }

    /// <summary>PIDs currently known to belong to this session (candidates).</summary>
    public IReadOnlySet<uint> CandidatePids { get; set; } = new HashSet<uint>();

    /// <summary>PIDs whose exe is inside OwnedRoots/OwnedExecutables (managed components).</summary>
    public IReadOnlySet<uint> ActiveOwnedPids { get; set; } = new HashSet<uint>();

    /// <summary>
    /// Start-time identity for every active owned PID. PID membership without this identity is
    /// insufficient because Windows can reuse a PID while a session is still alive.
    /// </summary>
    public IReadOnlyDictionary<uint, DateTime> ActiveOwnedProcessStartTimes { get; set; }
        = new Dictionary<uint, DateTime>();
}
