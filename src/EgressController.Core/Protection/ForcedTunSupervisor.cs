namespace EgressController.Core.Protection;

/// <summary>One-second callers may retry, but never overlap an in-flight startup.</summary>
public sealed class ForcedTunSupervisor(Func<CancellationToken, Task> start)
{
    private Task? _attempt;
    public string? LastError { get; private set; }
    public Task Pending => _attempt ?? Task.CompletedTask;

    public void Tick(bool running, bool configurationBusy, CancellationToken cancellationToken)
    {
        if (running || configurationBusy || cancellationToken.IsCancellationRequested || _attempt?.IsCompleted == false) return;
        _attempt = Task.Run(async () =>
        {
            try { await start(cancellationToken).ConfigureAwait(false); LastError = null; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) { LastError = exception.Message; }
        }, cancellationToken);
    }
}
