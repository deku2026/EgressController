using EgressController.SingBox.Core;
using EgressController.State.SingBox;

namespace EgressController.SingBox.Runtime;

public sealed record SingBoxRuntimeCandidate
{
    public required string CoreVersion { get; init; }
    public required string CorePath { get; init; }
    public required string CoreSha256 { get; init; }
    public required string ConfigPath { get; init; }
    public required string ConfigSha256 { get; init; }
    public int ControllerPort { get; init; }
    public string ControllerSecret { get; init; } = string.Empty;

    public static SingBoxRuntimeCandidate From(
        SingBoxCoreCandidate core,
        string configPath,
        string configSha256,
        int controllerPort = 0,
        string controllerSecret = "")
    {
        ArgumentNullException.ThrowIfNull(core);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(configSha256))
            throw new ArgumentException("Checked config path and hash are required.");
        return new SingBoxRuntimeCandidate
        {
            CoreVersion = core.Version,
            CorePath = Path.GetFullPath(core.ExecutablePath),
            CoreSha256 = core.Sha256,
            ConfigPath = Path.GetFullPath(configPath),
            ConfigSha256 = configSha256.Trim().ToLowerInvariant(),
            ControllerPort = controllerPort,
            ControllerSecret = controllerSecret,
        };
    }

    internal SingBoxRuntimePointer ToPointer()
        => new()
        {
            Core = new SingBoxCorePointer
            {
                Version = CoreVersion,
                ExecutablePath = CorePath,
                Sha256 = CoreSha256,
                VerifiedAtUtc = DateTimeOffset.UtcNow,
            },
            ConfigPath = ConfigPath,
            ConfigSha256 = ConfigSha256,
            ControllerPort = ControllerPort,
            ControllerSecret = ControllerSecret,
            AppliedAtUtc = DateTimeOffset.UtcNow,
        };

    internal static SingBoxRuntimeCandidate FromPointer(SingBoxRuntimePointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        return new SingBoxRuntimeCandidate
        {
            CoreVersion = pointer.Core.Version,
            CorePath = pointer.Core.ExecutablePath,
            CoreSha256 = pointer.Core.Sha256,
            ConfigPath = pointer.ConfigPath,
            ConfigSha256 = pointer.ConfigSha256,
            ControllerPort = pointer.ControllerPort,
            ControllerSecret = pointer.ControllerSecret,
        };
    }
}

public enum SingBoxServiceState
{
    Stopped,
    Preparing,
    Starting,
    Stopping,
    Applying,
    RollingBack,
    Running,
    Failed,
}

public sealed record SingBoxServiceStatus(
    SingBoxServiceState State,
    int? ProcessId,
    string? ErrorCode,
    string? ErrorMessage,
    bool HasPendingApply);

public sealed record SingBoxApplyResult(
    bool Succeeded,
    bool RestoredLastGood,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record SingBoxProcessStatus(
    bool Succeeded,
    string State,
    int? ProcessId,
    int DroppedOutputCount,
    string? ErrorCode,
    string? ErrorMessage);

public interface ISingBoxProcessClient : IAsyncDisposable
{
    event Action<SingBoxOutputEvent>? Output;
    Task<SingBoxProcessStatus> StartAsync(
        SingBoxRuntimeCandidate candidate,
        bool restart,
        CancellationToken cancellationToken = default);
    Task<SingBoxProcessStatus> StopAsync(CancellationToken cancellationToken = default);
    Task<SingBoxProcessStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record SingBoxOutputEvent(string Source, string Line, int DroppedCount);

/// <summary>
/// Serializes Start/Stop/Apply and keeps pending/last-good runtime pointers. Preparation is
/// cancellable before the lifecycle lock, so Stop remains responsive while a core or SRS is
/// being downloaded.
/// </summary>
public sealed class SingBoxService : IAsyncDisposable
{
    private readonly ISingBoxProcessClient _processClient;
    private readonly SingBoxStateStore _stateStore;
    private readonly Func<SingBoxRuntimeCandidate, CancellationToken, Task<bool>> _healthCheck;
    private readonly Action? _beforeTunChange;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _operation;
    private SingBoxServiceStatus _status = new(SingBoxServiceState.Stopped, null, null, null, false);

    public SingBoxService(
        ISingBoxProcessClient processClient,
        SingBoxStateStore stateStore,
        Func<SingBoxRuntimeCandidate, CancellationToken, Task<bool>>? healthCheck = null,
        Action? beforeTunChange = null)
    {
        _processClient = processClient ?? throw new ArgumentNullException(nameof(processClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _healthCheck = healthCheck ?? ((_, _) => Task.FromResult(true));
        _beforeTunChange = beforeTunChange;
        _processClient.Output += OnOutput;
    }

    public event Action<SingBoxOutputEvent>? Output;
    public event Action<SingBoxServiceStatus>? StatusChanged;

    public SingBoxServiceStatus Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public Task<SingBoxApplyResult> StartAsync(
        Func<CancellationToken, Task<SingBoxRuntimeCandidate>> prepare,
        CancellationToken cancellationToken = default)
        => PrepareAndRunAsync(prepare, apply: false, cancellationToken);

    public Task<SingBoxApplyResult> ApplyAsync(
        Func<CancellationToken, Task<SingBoxRuntimeCandidate>> prepare,
        CancellationToken cancellationToken = default)
        => PrepareAndRunAsync(prepare, apply: true, cancellationToken);

    public async Task<SingBoxApplyResult> ApplyCandidateAsync(
        SingBoxRuntimeCandidate candidate,
        CancellationToken cancellationToken = default)
        => await ApplyPreparedAsync(candidate, apply: true, cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancelPreparation();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _beforeTunChange?.Invoke();
            SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Stopping, null, null, null, HasPending()));
            await _processClient.StopAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Stopped, null, null, null, HasPending()));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        SingBoxRuntimePointer? lastGood = _stateStore.LoadLastGoodRuntime();
        if (_stateStore.LoadPendingApply() is null)
            return false;
        if (lastGood is null)
        {
            _stateStore.ClearPendingApply();
            SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Stopped, null, "recovery.no-last-good", "没有可恢复的 last-good runtime。", false));
            return false;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _beforeTunChange?.Invoke();
            SetStatus(new SingBoxServiceStatus(SingBoxServiceState.RollingBack, null, null, null, true));
            SingBoxRuntimeCandidate candidate = SingBoxRuntimeCandidate.FromPointer(lastGood);
            SingBoxProcessStatus status = await _processClient.StartAsync(candidate, restart: true, cancellationToken).ConfigureAwait(false);
            if (!status.Succeeded || !await _healthCheck(candidate, cancellationToken).ConfigureAwait(false))
            {
                SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Failed, status.ProcessId, status.ErrorCode, status.ErrorMessage, true));
                return false;
            }
            _stateStore.SaveCurrentRuntime(lastGood);
            _stateStore.ClearPendingApply();
            SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Running, status.ProcessId, null, null, false));
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancelPreparation();
        try { await StopAsync().ConfigureAwait(false); } catch { }
        _processClient.Output -= OnOutput;
        await _processClient.DisposeAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private async Task<SingBoxApplyResult> PrepareAndRunAsync(
        Func<CancellationToken, Task<SingBoxRuntimeCandidate>> prepare,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _operation?.Cancel();
            _operation?.Dispose();
            _operation = operation;
        }
        // Preparing/validating a replacement does not stop the current TUN.
        bool keptRunning = Status.State == SingBoxServiceState.Running;
        if (!keptRunning)
            SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Preparing, null, null, null, HasPending()));
        try
        {
            SingBoxRuntimeCandidate candidate = await prepare(operation.Token).ConfigureAwait(false);
            return await ApplyPreparedAsync(candidate, apply, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return new SingBoxApplyResult(false, false, "operation.cancelled", "操作已取消。");
        }
        catch (Exception ex)
        {
            if (!keptRunning)
                SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Failed, null, "prepare.failed", ex.Message, HasPending()));
            return new SingBoxApplyResult(false, false, "prepare.failed", ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_operation, operation))
                    _operation = null;
            }
        }
    }

    private async Task<SingBoxApplyResult> ApplyPreparedAsync(
        SingBoxRuntimeCandidate candidate,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool restartIssued = false;
        try
        {
            SingBoxRuntimePointer? current = _stateStore.LoadCurrentRuntime();
            if (Status.State == SingBoxServiceState.Running && current is not null
                && string.Equals(current.ConfigSha256, candidate.ConfigSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Core.Sha256, candidate.CoreSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Core.ExecutablePath, candidate.CorePath, StringComparison.OrdinalIgnoreCase))
                return new(true, false, null, null);
            // Synchronous guard must complete before StartAsync(restart: true) can stop TUN.
            // If termination fails, leave the current core untouched, including no rollback.
            try { _beforeTunChange?.Invoke(); }
            catch (Exception exception)
            {
                return new(false, false, "protection.failed", exception.Message);
            }
            SingBoxRuntimePointer pending = candidate.ToPointer();
            _stateStore.SavePendingApply(new SingBoxPendingApply
            {
                Candidate = pending,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            SetStatus(new SingBoxServiceStatus(
                apply ? SingBoxServiceState.Applying : SingBoxServiceState.Starting,
                null,
                null,
                null,
                true));

            restartIssued = true;
            SingBoxProcessStatus started = await _processClient.StartAsync(candidate, restart: true, cancellationToken).ConfigureAwait(false);
            if (!started.Succeeded || started.State is not ("running" or "starting"))
                throw new SingBoxServiceException(started.ErrorCode ?? "process.start", started.ErrorMessage ?? "sing-box 进程启动失败。");

            bool healthy = await _healthCheck(candidate, cancellationToken).ConfigureAwait(false);
            SingBoxProcessStatus confirmed = await _processClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!healthy || !confirmed.Succeeded || confirmed.State != "running")
                throw new SingBoxServiceException("health.failed", "sing-box API 或 TUN 接管检查失败。");

            _stateStore.SaveCurrentRuntime(pending);
            _stateStore.SaveLastGoodRuntime(pending);
            _stateStore.SaveCurrent(pending.Core);
            _stateStore.SaveLastGood(pending.Core);
            _stateStore.ClearPendingApply();
            SetStatus(new SingBoxServiceStatus(SingBoxServiceState.Running, started.ProcessId, null, null, false));
            return new SingBoxApplyResult(true, false, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool restored = await TryRestoreLastGoodAsync().ConfigureAwait(false);
            SetStatus(new(restored ? SingBoxServiceState.Running : SingBoxServiceState.Failed,
                null, "operation.cancelled", "操作已取消。", !restored && HasPending()));
            return new SingBoxApplyResult(false, restored, "operation.cancelled", "操作已取消。");
        }
        catch (Exception ex)
        {
            // A failed state read/write before StartAsync has not touched the old core.
            if (!restartIssued)
            {
                if (Status.State != SingBoxServiceState.Running)
                    SetStatus(new(SingBoxServiceState.Failed, null, "apply.failed", ex.Message, false));
                return new(false, false, "apply.failed", ex.Message);
            }
            bool restored = await TryRestoreLastGoodAsync().ConfigureAwait(false);
            SetStatus(new SingBoxServiceStatus(
                restored ? SingBoxServiceState.Running : SingBoxServiceState.Failed,
                null,
                restored ? "apply.failed.restored" : "apply.failed",
                ex.Message,
                !restored && HasPending()));
            return new SingBoxApplyResult(false, restored, ex is SingBoxServiceException service ? service.Code : "apply.failed", ex.Message);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> TryRestoreLastGoodAsync()
    {
        SingBoxRuntimePointer? lastGood = _stateStore.LoadLastGoodRuntime();
        if (lastGood is null)
            return false;
        SetStatus(new SingBoxServiceStatus(SingBoxServiceState.RollingBack, null, null, null, true));
        try
        {
            SingBoxRuntimeCandidate candidate = SingBoxRuntimeCandidate.FromPointer(lastGood);
            SingBoxProcessStatus restored = await _processClient.StartAsync(
                candidate,
                restart: true,
                CancellationToken.None).ConfigureAwait(false);
            if (!restored.Succeeded || !await _healthCheck(candidate, CancellationToken.None).ConfigureAwait(false))
                return false;
            SingBoxProcessStatus confirmed = await _processClient.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            if (!confirmed.Succeeded || confirmed.State != "running") return false;
            _stateStore.SaveCurrentRuntime(lastGood);
            _stateStore.ClearPendingApply();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void CancelPreparation()
    {
        lock (_gate)
            _operation?.Cancel();
    }

    private bool HasPending() => _stateStore.LoadPendingApply() is not null;

    private void SetStatus(SingBoxServiceStatus status)
    {
        lock (_gate)
            _status = status;
        StatusChanged?.Invoke(status);
    }

    public void ReportTakeoverFailure(string error, SingBoxServiceStatus? observed = null)
    {
        SingBoxServiceStatus failed;
        lock (_gate)
        {
            if (_status.State != SingBoxServiceState.Running || (observed is not null && !ReferenceEquals(_status, observed))) return;
            failed = _status = _status with { State = SingBoxServiceState.Failed, ErrorCode = "tun.takeover", ErrorMessage = error };
        }
        StatusChanged?.Invoke(failed);
    }

    private void OnOutput(SingBoxOutputEvent output)
    {
        Output?.Invoke(output);
        if (!string.Equals(output.Source, "lifecycle", StringComparison.OrdinalIgnoreCase)
            || !output.Line.Contains("exited", StringComparison.OrdinalIgnoreCase))
            return;

        SingBoxServiceStatus current = Status;
        if (current.State == SingBoxServiceState.Running)
        {
            SetStatus(new SingBoxServiceStatus(
                SingBoxServiceState.Failed,
                current.ProcessId,
                "process.exited",
                output.Line,
                HasPending()));
        }
    }
}

public sealed class SingBoxServiceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
