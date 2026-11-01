using EgressController.Policy;

namespace EgressController.Runtime;

public sealed record ConfigurationResetResult(bool NetworkRestored);

/// <summary>The user explicitly resets settings. No startup, discovery or resource deletion is part of this operation.</summary>
public static class ConfigurationReset
{
    public static async Task<ConfigurationResetResult> ExecuteAsync(MacPolicyDocument current,
        Func<CancellationToken, Task<bool>> stopAndRestore,
        Func<MacPolicyDocument, CancellationToken, Task> backupAndReset,
        CancellationToken cancellationToken = default)
    {
        var snapshot = PolicyYaml.Read(PolicyYaml.Write(current)).Document
            ?? throw new InvalidDataException("Cannot back up the current draft.");
        cancellationToken.ThrowIfCancellationRequested();
        // A stale/unavailable helper must not veto an explicitly requested local reset.
        // Its privileged recovery journal is independent and is never deleted here.
        bool restored;
        try { restored = await stopAndRestore(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or System.Net.Sockets.SocketException)
        { restored = false; }
        cancellationToken.ThrowIfCancellationRequested();
        await backupAndReset(snapshot, cancellationToken).ConfigureAwait(false);
        return new(restored);
    }
}
