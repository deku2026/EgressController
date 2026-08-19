namespace EgressController.Core.Models;

/// <summary>
/// Identity of a process at a point in time (plan §6 ProcessIdentity). StartTimeUtc guards
/// against PID reuse. ExePathFinal is the canonical (GetFinalPathNameByHandle) path used by the
/// OwnedRoot matcher; it's null when canonicalization could not be performed (then the process
/// is NEVER treated as managed — §1.6).
/// </summary>
public sealed record ProcessIdentity(
    uint Pid,
    DateTime StartTimeUtc,
    string ExePathObserved,
    string? ExePathFinal);