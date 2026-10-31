using EgressController.Runtime;

namespace EgressController.Mac;

public static class MacRuntimeReadiness
{
    /// <summary>An explicit Connect may recover a confirmed core/TUN failure, never a failed status read.</summary>
    public static bool RequiresRecovery(MacHelperStatus status, bool hasAppliedGeneration)
    {
        if (!status.Installed || status.Failure == "status-unavailable") throw new RuntimeFailureException("helper.timeout");
        return hasAppliedGeneration && (!status.Running || !status.TunTakenOver || status.Failure is not null);
    }
}
