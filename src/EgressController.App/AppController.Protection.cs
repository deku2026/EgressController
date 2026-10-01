using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Launcher.Discovery;
using EgressController.Windows.Process;
using EgressController.SingBox.Configuration;

namespace EgressController.App;

public sealed partial class AppController
{
    private ProcessProtection _processProtection = null!;
    private ForcedTunSupervisor _tunSupervisor = null!;
    private Task? _protectionTask;
    private volatile bool _profileApplied;
    private EgressController.SingBox.Core.SingBoxCoreCandidate? _preparedCore;
    private volatile bool _shuttingDown;
    private volatile bool _inventoryReady;
    private readonly DohHealthState _dohHealth = new();
    private readonly DohModeController _dohModes = new();
    private string? _coreExecutable;
    private string? _protectionError;
    private ProtectedExecutable[] _protectedExecutables = [];
    private HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string _protectionStatus = "保护中：正在初始化，所选应用暂不能运行";

    public string ProtectionStatus => _protectionStatus;
    public IReadOnlyList<ProtectionEvent> ProtectionEvents => _processProtection.Events;

    private void InitializeProtection()
    {
        _processProtection = new ProcessProtection(new WindowsProcessControl());
        _tunSupervisor = new ForcedTunSupervisor(async token =>
        {
            ControllerOperationResult result = await StartTunAsync(token).ConfigureAwait(false);
            if (!result.Succeeded) throw new InvalidOperationException(result.Error);
        });
        _coreExecutable = _stateStore.LoadCurrent()?.ExecutablePath;
        _excludedPaths = new[] { Environment.ProcessPath, _coreExecutable }.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        RefreshProtectedInventory();
        _protectionTask = Task.Run(() => ProtectionLoopAsync(_lifetimeCts.Token));
    }

    private void RefreshProtectedInventory()
    {
        ApplicationInventorySnapshot inventory = ApplicationInventorySnapshot.Create(_targets.All());
        EgressProfileDocument profile = _profile;
        var result = new List<ProtectedExecutable>();
        foreach (EgressApplicationSelection selected in profile.Applications)
        {
            inventory.TryGet(selected.DiscoveryKey, out ApplicationInventoryEntry? entry);
            IEnumerable<string> paths = (entry?.ExecutablePaths ?? []).Concat(selected.ExecutablePaths);
            if (selected.DiscoveryKey.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
                paths = paths.Append(selected.DiscoveryKey[4..]);
            foreach (string path in paths.Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase))
                result.Add(new(path, entry?.DisplayName ?? selected.DisplayName ?? Path.GetFileNameWithoutExtension(path)));
        }
        _protectedExecutables = result.ToArray();
    }

    private void ValidateProtectionConflicts(EgressProfileDocument profile, IReadOnlyList<string> ownerPaths)
    {
        var excluded = ownerPaths.Concat(new[] { Environment.ProcessPath, _coreExecutable }.OfType<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _excludedPaths = excluded;
        ApplicationInventorySnapshot inventory = ApplicationInventorySnapshot.Create(_targets.All());
        string? conflict = profile.Applications.SelectMany(selection =>
                inventory.ExpandSelected([selection.DiscoveryKey]).Concat(selection.ExecutablePaths))
            .FirstOrDefault(excluded.Contains);
        if (conflict is not null)
            throw new InvalidOperationException("勾选范围包含控制器、sing-box 或上游代理，请取消该选择：" + conflict);
    }

    private void InvalidateReadiness(string reason)
    {
        lock (_dohStateGate)
        {
            _dohHealth.Invalidate(reason);
            _dohModes.Invalidate();
            _dohRouting = _dohRouting with { FailClosed = true };
        }
    }

    private static string? RequiredNetworkError(EgressProfileDocument profile, NetworkEnvironmentSnapshot environment)
    {
        if (profile.AdapterConfigurationError is string configurationError) return configurationError;
        return environment.DefaultAdapter.IsReady ? null : "ESIM-家宽 未连接或没有 IP";
    }

    private async Task ProtectionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                RefreshAdapters();
                EgressProfileDocument profile = _profile;
                string? networkError;
                try
                {
                    ValidateProtectionConflicts(profile, ResolveProxyBindings(profile, token).OwnerPaths);
                    _protectionError = null;
                    networkError = RequiredNetworkError(profile, _environmentResolver.Resolve(profile, _adapters));
                }
                catch (Exception exception) { _protectionError = exception.Message; networkError = exception.Message; }
                ProtectionReadiness readiness;
                lock (_dohStateGate)
                {
                    if ((!IsTunRunning || networkError is not null) && (_dohHealth.Snapshot.Ready || _dohHealth.Snapshot.Checking))
                        InvalidateReadiness(networkError ?? "TUN 未就绪，等待重新检测");
                    readiness = ProtectionReadiness.Evaluate(IsTunRunning,
                        _singBox.Status.ErrorMessage ?? _tunSupervisor.LastError, IsUpdatingProfile && !_profileApplied,
                        _protectionError ?? (IsTunRunning && !_profileApplied ? "配置尚未成功应用，正在自动恢复" : null),
                        networkError, _dohHealth.Snapshot);
                }
                _processProtection.Sweep(_protectedExecutables, _excludedPaths, readiness.Ready, readiness.Reason);
                _protectionStatus = readiness.Ready ? readiness.Reason : "保护中：" + readiness.Reason;
                if (_processProtection.FailedCount > 0)
                    _protectionStatus = $"保护未完成：{_processProtection.FailedCount} 个进程终止失败；{readiness.Reason}";
                if (_inventoryReady && _protectionError is null)
                    _tunSupervisor.Tick(IsTunRunning, IsUpdatingProfile, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                InvalidateReadiness("进程保护检查失败：" + exception.Message);
                _protectionStatus = "保护未完成：" + exception.Message;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    public Task<ControllerOperationResult> SetAdapterRolesAsync(string? directId, string? proxyId)
        => UpdateProfileAsync(profile => profile.SetAdapterRoles(directId, proxyId), _lifetimeCts.Token);

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _lifetimeCts.Cancel();
        if (_protectionTask is not null) await _protectionTask.ConfigureAwait(false);
        try { await _tunSupervisor.Pending.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await StopTunAsync().ConfigureAwait(false);
    }
}
