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
    private readonly EgressController.Windows.Network.WindowsTunInspector _tunInspector = new();
    private readonly AdapterBindingCache _adapterBindings = new();
    private string? _takeoverError = "等待 TUN 接管";
    private string? _coreExecutable;
    private string? _protectionError;
    private ProtectedExecutable[] _protectedExecutables = [];
    private HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string _protectionStatus = "保护中：正在初始化，所选应用暂不能运行";

    public string ProtectionStatus => _protectionStatus;
    public IReadOnlyList<ProtectionEvent> ProtectionEvents => _processProtection.Events;

    public void ClearProtectionHistory() => _processProtection.ClearHistory();

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
        _singBox.StatusChanged += OnTunStatusChanged;
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
        _dohSelection.Invalidate();
        _profileApplied = false;
        _protectionStatus = "保护中：" + reason;
    }

    private void BeforeTunChange()
    {
        _profileApplied = false;
        int failures = SweepProtection(new(false, "TUN 即将启动或重启，先终止所选应用"));
        if (failures > 0)
            throw new InvalidOperationException("部分所选进程无法终止，已取消本次 TUN 变更；请检查进程保护记录。");
    }

    private void OnTunStatusChanged(EgressController.SingBox.Runtime.SingBoxServiceStatus status)
    {
        if (status.State != EgressController.SingBox.Runtime.SingBoxServiceState.Failed) return;
        InvalidateReadiness(status.ErrorMessage ?? "TUN 已退出");
        try { SweepProtection(new(false, status.ErrorMessage ?? "TUN 已退出")); }
        catch (Exception exception) { _protectionStatus = "保护未完成：" + exception.Message; }
    }

    private int SweepProtection(ProtectionReadiness readiness)
    {
        int failures = _processProtection.Sweep(_protectedExecutables, _excludedPaths, readiness.Ready, readiness.Reason);
        _protectionStatus = readiness.Ready ? readiness.Reason : "保护中：" + readiness.Reason;
        if (failures > 0)
            _protectionStatus = $"保护未完成：{failures} 个进程终止失败；{readiness.Reason}";
        return failures;
    }

    private async Task ProtectionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                RefreshAdapters();
                EgressProfileDocument profile = _profile;
                try
                {
                    ValidateProtectionConflicts(profile, ResolveProxyBindings(profile, token).OwnerPaths);
                    _protectionError = profile.AdapterConfigurationError;
                }
                catch (Exception exception) { _protectionError = exception.Message; }
                var observed = _singBox.Status;
                if (observed.State == EgressController.SingBox.Runtime.SingBoxServiceState.Running)
                {
                    var process = await _directSingBox.GetStatusAsync(token).ConfigureAwait(false);
                    string? error = process.State == "running" ? _tunInspector.GetError() : "sing-box 进程已退出";
                    // An observation spanning a restart must not fail the replacement TUN.
                    if (error is not null) _singBox.ReportTakeoverFailure(error, observed);
                }
                var readiness = ProtectionReadiness.Evaluate(IsTunRunning,
                    _singBox.Status.ErrorMessage ?? _tunSupervisor.LastError,
                    _profileApplied, _protectionError, _takeoverError);
                SweepProtection(readiness);
                if (_inventoryReady && _protectionError is null)
                    _tunSupervisor.Tick(IsTunRunning, IsUpdatingProfile, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                try { SweepProtection(new(false, "无法确认 TUN 接管状态：" + exception.Message)); }
                catch (Exception failure) { _protectionStatus = "保护未完成：" + failure.Message; }
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
