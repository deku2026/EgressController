using EgressController.Mac;
using EgressController.Runtime;

namespace EgressController.MacHelper;

public interface IPrivilegedRuntimeEnvironment
{
    Task ValidateAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken);
    Task ApplyAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken);
    Task RestoreAsync(CancellationToken cancellationToken);
    MacHelperStatus Inspect();
    CoreConnectionSnapshot ReadConnections() => CoreConnectionSnapshots.Unavailable(DateTimeOffset.UtcNow);
}

/// <summary>Independent from Internet reachability, probes, telemetry and diagnostics.</summary>
public sealed class RuntimeController(IPrivilegedRuntimeEnvironment environment)
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    public async Task<MacHelperWireReply> ExecuteAsync(MacHelperWireRequest request, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool mutationStarted = false;
        try
        {
            if (request.Command is MacHelperCommand.Validate or MacHelperCommand.Apply)
            {
                if (request.Apply is null) throw new InvalidDataException("Missing apply plan.");
                await environment.ValidateAsync(request.Apply, cancellationToken).ConfigureAwait(false);
                if (request.Command == MacHelperCommand.Apply) { mutationStarted = true; await environment.ApplyAsync(request.Apply, cancellationToken).ConfigureAwait(false); }
            }
            else if (request.Command == MacHelperCommand.StopAndRestore) await environment.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            else if (request.Command is not (MacHelperCommand.Inspect or MacHelperCommand.Heartbeat)) throw new InvalidDataException("Unsupported helper command.");
            return new(request.SessionId, request.Sequence, true, "ok", "", SafeInspect(), SafeConnections());
        }
        catch (OperationCanceledException)
        {
            if (mutationStarted) await RecoverAsync().ConfigureAwait(false);
            return new(request.SessionId, request.Sequence, false, "canceled", "操作已取消。", SafeInspect());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            if (mutationStarted) await RecoverAsync().ConfigureAwait(false);
            // Exceptions can contain server credentials or private paths. Only fixed error codes cross IPC.
            return new(request.SessionId, request.Sequence, false, "operation-failed", "特权操作未完成；系统恢复状态见当前状态。", SafeInspect());
        }
        finally { _operations.Release(); }
    }
    public async Task RecoverAsync()
    {
        try { await environment.RestoreAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public async Task CheckCoreAsync(CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = SafeInspect();
            if (state.GenerationId is not null && (!state.Running || !state.TunTakenOver)) await RecoverAsync().ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }
    private MacHelperStatus SafeInspect()
    {
        try { return environment.Inspect(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { return new(true, false, false, false, 0, null, "status-unavailable", DateTimeOffset.UtcNow); }
    }
    private CoreConnectionSnapshot SafeConnections()
    {
        try
        {
            var snapshot = environment.ReadConnections();
            return CoreConnectionSnapshots.IsValid(snapshot, DateTimeOffset.UtcNow) ? snapshot with { Records = snapshot.Records.ToArray() }
                : CoreConnectionSnapshots.Unavailable(DateTimeOffset.UtcNow);
        }
        catch (Exception) { return CoreConnectionSnapshots.Unavailable(DateTimeOffset.UtcNow); }
    }
}
