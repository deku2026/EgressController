namespace EgressController.SingBox.Configuration;

/// <summary>Only a changed resolver selection can request a checked configuration replacement.
/// The caller serializes configuration changes; revisions reject late probe results.</summary>
public sealed class DohSelectionController
{
    private readonly object _gate = new();
    private int _generation;
    private DohRoutingDecision _current = DohRoutingDecision.Default;
    public int Generation { get { lock (_gate) return _generation; } }
    public DohRoutingDecision Current { get { lock (_gate) return _current; } }
    public void Invalidate() { lock (_gate) _generation++; }
    public void Reset() { lock (_gate) { _generation++; _current = DohRoutingDecision.Default; } }

    public async Task<string?> ApplyAsync(int generation, DohRoutingDecision desired,
        Func<DohRoutingDecision, CancellationToken, Task<string?>> apply, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (generation != _generation) return "配置已变化，丢弃过期的 DoH 结果。";
            if (desired == _current) return null;
        }
        string? error = await apply(desired, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation != _generation) return "配置已变化，丢弃过期的 DoH 结果。";
            if (error is null) _current = desired;
        }
        return error;
    }
}
