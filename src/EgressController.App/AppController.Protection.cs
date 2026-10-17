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
    private volatile bool _routingDirty;
    private sealed record ApplicationRouteState(ApplicationRoutingCoverage Coverage, string InheritedKey);
    private ApplicationRouteState _activeRouting = new(ApplicationRoutingCoverage.Empty, "");
    private ApplicationRouteState _preparedRouting = new(ApplicationRoutingCoverage.Empty, "");
    private void ActivatePreparedRouting() => Volatile.Write(ref _activeRouting, _preparedRouting);

    private SingBoxApplicationRouteInput[] InheritedApplicationRoutes(EgressProfileDocument profile)
    {
        var covered = new ApplicationRoutingCoverage(profile.Applications.SelectMany(item => item.ExecutablePaths),
            profile.Applications.SelectMany(item => item.DirectoryRoots));
        return _processProtection.CaptureRoutingMembers()
            .Where(item => !covered.Contains(item.Path) && !_excludedPaths.Contains(item.Path))
            .SelectMany(item => profile.Applications.Where(selection =>
                selection.ExecutablePaths.Contains(item.Source.Path, StringComparer.OrdinalIgnoreCase)
                || selection.DirectoryRoots.Any(root => ProcessProtection.IsWithinDirectory(item.Source.Path, root)))
                .Select(selection => new SingBoxApplicationRouteInput([item.Path], selection.Target)))
            .ToArray();
    }
    private static string InheritedRoutingKey(IEnumerable<SingBoxApplicationRouteInput> routes)
        => string.Join("\n", routes.SelectMany(route => route.ExecutablePaths.Select(path => path + ":" + route.Target))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
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

    private void ValidateProtectionConflicts(EgressProfileDocument profile, IReadOnlyList<string> ownerPaths, bool updateExclusions = true)
    {
        var excluded = ApplicationPathAliases.Expand(ownerPaths.Concat(new[] { Environment.ProcessPath, _coreExecutable }.OfType<string>()),
                new ExecutablePathCanonicalizer())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (updateExclusions) _excludedPaths = excluded;
        ApplicationInventorySnapshot inventory = ApplicationInventorySnapshot.Create(_targets.All());
        string? conflict = profile.Applications.SelectMany(selection =>
                inventory.ExpandSelected([selection.DiscoveryKey]).Concat(selection.ExecutablePaths))
            .FirstOrDefault(excluded.Contains);
        conflict ??= excluded.FirstOrDefault(path => profile.Applications
            .SelectMany(selection => selection.DirectoryRoots).Any(root => ProcessProtection.IsWithinDirectory(path, root)));
        if (conflict is not null)
            throw new InvalidOperationException("勾选范围包含控制器、sing-box 或上游代理，请取消该选择：" + conflict);
    }

    private void InvalidateReadiness(string reason)
    {
        _networkHealth.Reset();
        _profileApplied = false;
        _protectionStatus = "保护中：" + reason;
    }

    private void BeforeTunChange()
    {
        if (!_shuttingDown && _tunInspector.GetStartError(_directSingBox.Status.State == "running") is { } conflict)
            throw new InvalidOperationException(conflict);
        // ShutdownAsync already received the guard ACK before cancelling the lifetime.
        // Dispose also disconnects the agent before releasing the owned core. Do not try
        // to reacquire that handoff using the now-cancelled startup token.
        _guardTransition = true;
        try
        {
            if (!_shuttingDown) _guardAgent?.PrepareAsync(_lifetimeCts.Token).GetAwaiter().GetResult();
            int failures = SweepProtection(new(false, "TUN 即将启动或重启，先终止所选应用"));
            if (failures > 0)
                throw new InvalidOperationException("部分所选进程无法终止，已取消本次 TUN 变更；请检查进程保护记录。");
        }
        catch
        {
            if (!_shuttingDown) _guardTransition = false;
            throw;
        }
    }

    private void OnTunStatusChanged(EgressController.SingBox.Runtime.SingBoxServiceStatus status)
    {
        if (status.State is EgressController.SingBox.Runtime.SingBoxServiceState.Starting
            or EgressController.SingBox.Runtime.SingBoxServiceState.Applying
            or EgressController.SingBox.Runtime.SingBoxServiceState.Stopping
            or EgressController.SingBox.Runtime.SingBoxServiceState.RollingBack)
        {
            InvalidateReadiness("TUN 正在变更");
            _tunInspector.Reset();
            return;
        }
        if (status.State != EgressController.SingBox.Runtime.SingBoxServiceState.Failed) return;
        InvalidateReadiness(status.ErrorMessage ?? "TUN 已退出");
        try { SweepProtection(new(false, status.ErrorMessage ?? "TUN 已退出")); }
        catch (Exception exception) { _protectionStatus = "保护未完成：" + exception.Message; }
    }

    private int SweepProtection(ProtectionReadiness readiness)
    {
        var directories = _profile.Applications.SelectMany(item => item.DirectoryRoots.Select(path =>
            new ProtectedDirectory(path, item.DisplayName ?? item.DiscoveryKey))).ToArray();
        int failures = _processProtection.Sweep(_protectedExecutables, _excludedPaths, readiness.Ready,
            "主程序网络保护：" + readiness.Reason, directories, Volatile.Read(ref _activeRouting).Coverage.Contains);
        if (InheritedRoutingKey(InheritedApplicationRoutes(_profile)) != Volatile.Read(ref _activeRouting).InheritedKey)
            _routingDirty = true;
        _protectionStatus = readiness.Ready ? readiness.Reason : "保护中：" + readiness.Reason;
        if (readiness.Ready && _processProtection.PendingRoutingCount > 0)
            _protectionStatus = $"保护中：{_processProtection.PendingRoutingCount} 个进程的分流规则尚未生效，已执行终止并等待配置更新";
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
                // Use fresh link/address state, never AdapterBindingCache's retained bindings.
                _networkHealth.ObserveEnvironment(_environmentResolver.Resolve(profile, _adapters),
                    profile.UpstreamPorts.Count > 0 ? profile.UpstreamPort : null);
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
                // Independent from the companion's main/TUN lease and tray badge.
                var readiness = ProtectionReadiness.Evaluate(IsTunRunning, _singBox.Status.ErrorMessage,
                    _profileApplied, _protectionError, _takeoverError, _networkHealth.Readiness);
                SweepProtection(readiness);
                if (_inventoryReady && _protectionError is null)
                    _tunSupervisor.Tick(IsTunRunning, IsUpdatingProfile, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                try { SweepProtection(new(false, "无法确认 TUN 或出口网络状态：" + exception.Message)); }
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
        _guardTransition = true;
        try
        {
            if (_guardAgent is not null) await _guardAgent.PrepareAsync().ConfigureAwait(false);
            if (SweepProtection(new(false, "主程序准备退出，先终止所选应用")) > 0)
                throw new InvalidOperationException("仍有进程未能终止，已取消退出。");
        }
        catch (Exception exception)
        {
            _guardTransition = false;
            _protectionStatus = "退出已取消：" + exception.Message;
            throw;
        }
        _shuttingDown = true;
        _lifetimeCts.Cancel();
        if (_protectionTask is not null) await _protectionTask.ConfigureAwait(false);
        try { await _tunSupervisor.Pending.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await StopTunAsync().ConfigureAwait(false);
    }
}
