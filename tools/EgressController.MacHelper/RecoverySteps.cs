namespace EgressController.MacHelper;

public sealed record RecoveryOutcome(bool CoreStopped, bool DnsRestored);
public static class RecoverySteps
{
    public static async Task<bool> WaitForReleaseAsync(Func<bool> released, Func<Task>? delay = null)
    {
        delay ??= () => Task.Delay(100);
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try { if (released()) return true; } catch (Exception) { return false; }
            await delay().ConfigureAwait(false);
        }
        return false;
    }
    public static async Task<RecoveryOutcome> ExecuteAsync(Func<Task> stopOwnedCore, Func<Task> restoreDns)
    {
        bool core = true, dns = true;
        try { await stopOwnedCore().ConfigureAwait(false); } catch (Exception) { core = false; }
        // DNS recovery must run even if a process identity/termination read failed.
        try { await restoreDns().ConfigureAwait(false); } catch (Exception) { dns = false; }
        return new(core, dns);
    }
}
