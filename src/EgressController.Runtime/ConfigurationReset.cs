using EgressController.Policy;

namespace EgressController.Runtime;

/// <summary>The user explicitly resets settings. No startup, discovery or resource deletion is part of this operation.</summary>
public static class ConfigurationReset
{
    public static async Task ExecuteAsync(MacPolicyDocument current,
        Func<CancellationToken, Task<bool>> stopAndRestore,
        Func<MacPolicyDocument, CancellationToken, Task> backupAndReset,
        CancellationToken cancellationToken = default)
    {
        var snapshot = PolicyYaml.Read(PolicyYaml.Write(current)).Document
            ?? throw new InvalidDataException("Cannot back up the current draft.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!await stopAndRestore(cancellationToken).ConfigureAwait(false)) throw new RuntimeFailureException("reset.restore");
        cancellationToken.ThrowIfCancellationRequested();
        await backupAndReset(snapshot, cancellationToken).ConfigureAwait(false);
    }
}
