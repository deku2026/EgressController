using EgressController.Runtime;

namespace EgressController.Mac;

public static class MacRuntimeReadiness
{
    // A read failure can clear the displayed Running flag, but must not erase the need
    // to recover a session that may have changed DNS/routes. Unused/untrusted helpers
    // do not trap a client that has never attempted takeover in an exit loop.
    public static bool RequiresExitRecovery(bool sessionMayOwnNetwork, MacHelperStatus status)
        => sessionMayOwnNetwork || status.Installed && (status.Running || status.TunTakenOver || !status.DnsRestored);

    /// <summary>An explicit Connect may recover a confirmed core/TUN failure, never a failed status read.</summary>
    public static bool RequiresRecovery(MacHelperStatus status, bool hasAppliedGeneration)
    {
        if (!status.Installed || status.Failure == "status-unavailable") throw new RuntimeFailureException("helper.timeout");
        return hasAppliedGeneration && (!status.Running || !status.TunTakenOver || status.Failure is not null);
    }
}
