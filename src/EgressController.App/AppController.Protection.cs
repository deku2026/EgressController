using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Launcher.Discovery;
using EgressController.SingBox.Api;
using EgressController.SingBox.Configuration;
using EgressController.Windows.Network;
using EgressController.Windows.Process;

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
    private volatile bool _allHealthy;
    private DateTimeOffset? _lastHealthyAt;
    private int _healthGeneration;
    private string? _probeError;
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

    private void MarkProbeFailure(string reason)
    {
        lock (_dohStateGate)
        {
            _allHealthy = false;
            _lastHealthyAt = null;
            _probeError = reason;
        }
    }

    private void InvalidateReadiness(string reason)
    {
        Interlocked.Increment(ref _healthGeneration);
        lock (_dohStateGate)
        {
            _allHealthy = false;
            _lastHealthyAt = null;
            _probeError = reason;
            _dohRouting = _dohRouting with { FailClosed = true };
        }
    }

    private IEnumerable<EgressRouteTarget> RequiredRoutes(EgressProfileDocument profile)
        => profile.Applications.Select(item => item.Target).Concat(profile.RuleSets.Select(item => item.Target))
            .Concat(profile.Domains.Select(item => item.Target));

    private IEnumerable<string?> RequiredAdapterIds(EgressProfileDocument profile)
        => RequiredRoutes(profile).Where(target => target.IsAdapter)
            .Select(target => target.AdapterId ?? profile.DefaultAdapterId)
            .Append(profile.DefaultAdapterId).Append(profile.EffectiveDnsAdapterId).Distinct();

    private string? RequiredNetworkError(EgressProfileDocument profile, NetworkEnvironmentSnapshot environment)
    {
        foreach (string? id in RequiredAdapterIds(profile))
        {
            if (id is null) return "尚未设置默认网卡";
            if (environment.Find(id)?.IsReady != true)
                return $"网卡未连接或没有 IP：{profile.Adapters.FirstOrDefault(adapter => adapter.Id == id)?.Name ?? id}";
        }
        return null;
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
                    ValidateProtectionConflicts(profile, ResolveUpstreamOwners(profile, token));
                    _protectionError = null;
                    networkError = RequiredNetworkError(profile, _environmentResolver.Resolve(profile, _adapters));
                }
                catch (Exception exception) { _protectionError = exception.Message; networkError = exception.Message; }
                ProtectionReadiness readiness;
                lock (_dohStateGate)
                    readiness = ProtectionReadiness.Evaluate(IsTunRunning,
                        _singBox.Status.ErrorMessage ?? _tunSupervisor.LastError, IsUpdatingProfile,
                        _protectionError ?? (IsTunRunning && !_profileApplied ? "配置尚未成功应用，正在自动恢复" : null), networkError, _allHealthy, _lastHealthyAt, DateTimeOffset.UtcNow, _probeError);
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

    private async Task<string?> ProbeRequiredOutboundsAsync(SingBoxApiClient api, CancellationToken token)
    {
        EgressProfileDocument profile = _profile;
        NetworkEnvironmentSnapshot environment = _environmentResolver.Resolve(profile, _adapters);
        if (RequiredNetworkError(profile, environment) is string error) return error;
        var tags = RequiredRoutes(profile).Select(target => target.IsAdapter
            ? EgressProfileCompiler.AdapterTag(Guid.Parse(target.AdapterId ?? profile.DefaultAdapterId!))
            : EgressProfileCompiler.SocksTag(target.Port ?? profile.UpstreamPort))
            .Append(EgressProfileCompiler.DnsDirectTag).Distinct().ToArray();
        string?[] failures = await Task.WhenAll(tags.Select(async tag =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var response = await api.ProbeOutboundAsync(tag, timeout.Token).ConfigureAwait(false);
                return response.Delay > 0 ? null : $"出口联网检测失败：{DescribeOutbound(tag)}";
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            { string failure = $"出口无法联网：{DescribeOutbound(tag)} · {DescribeDiagnosticsFailure(exception)}"; MarkProbeFailure(failure); return failure; }
        })).ConfigureAwait(false);
        return failures.FirstOrDefault(failure => failure is not null);
    }

    private string DescribeOutbound(string tag)
        => _profile.Adapters.FirstOrDefault(adapter => EgressProfileCompiler.AdapterTag(Guid.Parse(adapter.Id)) == tag)?.Name
            ?? (tag == EgressProfileCompiler.DnsDirectTag ? "DNS 网卡" : tag.Replace("clash-", "SOCKS5 端口 "));

    public Task<ControllerOperationResult> AddAdapterAsync(Guid id, string name)
        => UpdateProfileAsync(profile => profile.AddAdapter(id.ToString("D"), name), _lifetimeCts.Token);
    public Task<ControllerOperationResult> SetDefaultAdapterAsync(string id)
        => UpdateProfileAsync(profile => profile.SetDefaultAdapter(id), _lifetimeCts.Token);
    public Task<ControllerOperationResult> RemoveAdapterAsync(string id)
        => UpdateProfileAsync(profile => profile.RemoveAdapter(id), _lifetimeCts.Token);
    public Task<ControllerOperationResult> RenameAdapterAsync(string id, string name)
        => UpdateProfileAsync(profile => profile with { Adapters = profile.Adapters.Select(adapter => adapter.Id == id ? adapter with { Name = name } : adapter).ToArray() }, _lifetimeCts.Token);
    public Task<ControllerOperationResult> SetDnsAdapterAsync(string? id)
        => UpdateProfileAsync(profile => profile with { DnsAdapterId = id }, _lifetimeCts.Token);

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _lifetimeCts.Cancel();
        if (_protectionTask is not null) await _protectionTask.ConfigureAwait(false);
        try { await _tunSupervisor.Pending.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await StopTunAsync().ConfigureAwait(false);
    }
}
