namespace EgressController.SingBox.Configuration;

/// <summary>Cache only a mode confirmed by PATCH and read-back. Normal successful
/// rechecks do not depend on another controller request or disturb active traffic.</summary>
public sealed class DohModeController
{
    private readonly object _gate = new();
    private string? _confirmed;
    private long _generation;

    public void Invalidate()
    {
        lock (_gate) { _generation++; _confirmed = null; }
    }

    public async Task ApplyAsync(DohRoutingDecision desired,
        Func<string, CancellationToken, Task> setAndVerify, CancellationToken token)
    {
        string mode = EgressDohConfiguration.ModeFor(desired);
        long generation;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_confirmed == mode) return;
            _confirmed = null; // Failed/canceled PATCH may have reached the core.
            generation = ++_generation;
        }
        await setAndVerify(mode, token).ConfigureAwait(false);
        lock (_gate)
            if (generation == _generation) _confirmed = mode;
    }
}
