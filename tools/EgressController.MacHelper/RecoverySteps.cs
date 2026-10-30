namespace EgressController.MacHelper;

public sealed record RecoveryOutcome(bool CoreStopped, bool DnsRestored);
public static class RecoverySteps
{
    public static async Task<RecoveryOutcome> ExecuteAsync(Func<Task> stopOwnedCore, Func<Task> restoreDns)
    {
        bool core = true, dns = true;
        try { await stopOwnedCore().ConfigureAwait(false); } catch (Exception) { core = false; }
        // DNS recovery must run even if a process identity/termination read failed.
        try { await restoreDns().ConfigureAwait(false); } catch (Exception) { dns = false; }
        return new(core, dns);
    }
}
