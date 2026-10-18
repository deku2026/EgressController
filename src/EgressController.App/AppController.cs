using System.Net;
using System.Net.Sockets;
using System.Text;
using EgressController.App.Services;
using EgressController.Core.Contracts;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Diagnostics;
using EgressController.Launcher.Discovery;
using EgressController.Rules.Artifacts;
using EgressController.Rules.Catalog;
using EgressController.SingBox.Api;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Cli;
using EgressController.SingBox.Configuration;
using EgressController.SingBox.Core;
using EgressController.SingBox.Runtime;
using EgressController.State.Profile;
using EgressController.State.Quota;
using EgressController.State.SingBox;
using EgressController.Transport.Upstream;
using EgressController.Windows.Network;
using EgressController.Windows.Process;

namespace EgressController.App;

public sealed record ControllerOperationResult(bool Succeeded, string? Error = null)
{
    public static ControllerOperationResult Success() => new(true);
    public static ControllerOperationResult Failure(string error) => new(false, error);
}

public sealed record ControllerEndpoint(int Port, string Secret)
{
    public Uri Uri => new($"http://127.0.0.1:{Port}");
}

/// <summary>
/// Thin composition root for the new data plane. It owns Profile edits, sing-box lifecycle,
/// diagnostics streams and Windows discovery. Routing never depends on an application launch
/// button; sing-box resolves the owning process for each new connection.
/// </summary>
public sealed partial class AppController : IAppController, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly string _dataRoot;
    private readonly string _rulesetRoot;
    private readonly EgressProfileStore _profileStore;
    private readonly EgressQuotaStore _quotaStore;
    private readonly SingBoxStateStore _stateStore;
    private readonly INetworkAdapterService _adapterService;
    private readonly NetworkEnvironmentResolver _environmentResolver = new();
    private readonly WindowsLaunchTargetScanner _targetScanner = new();
    private readonly LaunchTargetRegistry _targets = new();
    private readonly ProxyPortBindingTracker _portTracker = new(new TcpListenerOwnerResolver());
    private ProxyPortSnapshot _proxyBindings = ProxyPortSnapshot.Empty;
    public ProxyPortSnapshot ProxyBindings => Volatile.Read(ref _proxyBindings);
    private readonly EgressProfileCompiler _compiler = new();
    private readonly DirectSingBoxProcessClient _directSingBox = new();
    private readonly SingBoxService _singBox;
    private readonly ConnectionHistoryStore _connectionHistory = new();
    private readonly BoundedLogStore _logs = new();
    public ApplicationVisualCatalog Visuals { get; } = new();
    private readonly TrafficTimeline _trafficTimeline = new();
    private bool _connectionRatesValid;
    public TrafficSnapshot TrafficSnapshot => _trafficTimeline.Snapshot();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _diagnosticsStateGate = new();
    private readonly object _runtimeStateGate = new();
    private readonly object _networkStateGate = new();
    private readonly SemaphoreSlim _networkProbeGate = new(1, 1);
    private readonly Dictionary<string, ConnectionRateSample> _connectionRateSamples = new(StringComparer.Ordinal);

    private HttpRemoteFetcher _remoteFetcher = null!;
    private HttpClient _releaseHttpClient = null!;
    private RuleCatalogService _catalogService = null!;
    private RuleArtifactStore _artifactStore = null!;
    private SingBoxCoreManager _coreManager = null!;
    private Task? _diagnosticsTask;
    private CancellationTokenSource? _diagnosticsCts;
    private Task? _runtimeMonitorTask;
    private CancellationTokenSource? _runtimeMonitorCts;
    private Task? _networkMonitorTask;
    private CancellationTokenSource? _networkMonitorCts;
    private string _runtimeFingerprint = string.Empty;
    private EgressProfileDocument _profile;
    private IReadOnlyList<NetworkAdapterInfo> _adapters = Array.Empty<NetworkAdapterInfo>();
    private string _lastMessage = "就绪。";
    private long _trafficUp;
    private long _trafficDown;
    private DateTimeOffset? _connectionsUpdatedAtUtc;
    private DateTimeOffset? _trafficUpdatedAtUtc;
    private string _connectionMonitorStatus = "未启动";
    private string _trafficMonitorStatus = "未启动";
    private int _diagnosticsGeneration;
    private readonly NetworkHealthState _networkHealth = new();
    private string _networkMonitorStatus = "未启动";
    private DateTimeOffset? _networkLastCheckedAtUtc;

    public AppController(string? dataRoot = null, string? rulesetRoot = null)
    {
        EgressController.Core.Protection.RuntimeSafety.RequireLiveOperations();
        string applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        ApplicationStoragePaths storage = ApplicationStoragePaths.Resolve(applicationDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), WindowsPackageIdentity.IsPackaged());
        _dataRoot = Path.GetFullPath(dataRoot ?? storage.Data);
        _rulesetRoot = Path.GetFullPath(rulesetRoot ?? storage.Rulesets);
        Diagnostics = new DiagnosticJournal(Path.Combine(_dataRoot, "logs", "protection.log"), shouldPersist: ProtectionLogPolicy.IsRelevant);
        Diagnostics.Info("启动", "EgressController 启动；数据目录：" + _dataRoot);
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(Path.Combine(_dataRoot, "logs"));
        Directory.CreateDirectory(Path.Combine(_dataRoot, "runtime"));
        Directory.CreateDirectory(_rulesetRoot);
        _profileStore = new EgressProfileStore(_dataRoot);
        _quotaStore = new EgressQuotaStore(_dataRoot);
        _stateStore = new SingBoxStateStore(_dataRoot);
        _adapterService = new WindowsNetworkAdapterService();
        _profile = ApplicationPathAliases.Expand(LoadProfile(), new ExecutablePathCanonicalizer());
        ConfigureControlPlane();
        RefreshAdapters();
        _profile = _profile.NormalizeAndValidate();
        _profileStore.Save(_profile);

        _singBox = new SingBoxService(_directSingBox, _stateStore, HealthCheckAsync, BeforeTunChange,
            () => { if (!_shuttingDown) _guardTransition = false; });
        _singBox.Output += OnSingBoxOutput;
        _singBox.FailureReported += (stage, error) => Diagnostics.Report("内核生命周期 · " + stage,
            error is SingBoxServiceException service ? service.Code : "core." + stage, error.Message, error);
        InitializeProtection();
        InitializeGuardian();
    }

    public string DataRoot => _dataRoot;
    public bool IsPreview => false;
    public string RulesetRoot => _rulesetRoot;
    public string LogPath => Diagnostics.LogPath;
    public EgressProfileDocument Profile => _profile;
    public bool IsUpdatingProfile => _configurationGate.CurrentCount == 0;
    public EgressQuotaSnapshot Quota => _quotaStore.Load();
    public SingBoxService SingBox => _singBox;
    public ConnectionHistoryStore ConnectionHistory => _connectionHistory;
    public BoundedLogStore Logs => _logs;
    public IReadOnlyList<NetworkAdapterInfo> Adapters => _adapters;
    public IReadOnlyList<LaunchTarget> Targets => _targets.All();
    public SingBoxRuleCatalog? Catalog => _catalogService.Current;
    public string CatalogDirectory => Path.GetDirectoryName(_catalogService.CatalogPath) ?? _dataRoot;
    public string CatalogCommit => Catalog?.Snapshot.CommitSha ?? string.Empty;
    public IReadOnlyList<string> SelectedRuleNames => _profile.RuleSets.Select(route => route.Name).ToArray();
    public IReadOnlyList<string> ManualDomains => _profile.Domains.Select(route => route.Name).ToArray();
    public string LastMessage => _lastMessage;
    public bool IsTunRunning => _singBox.Status.State == SingBoxServiceState.Running;
    public string TunStatus => _singBox.Status.State switch
    {
        SingBoxServiceState.Running => "运行中",
        SingBoxServiceState.Preparing or SingBoxServiceState.Applying or SingBoxServiceState.Starting => "应用中…",
        SingBoxServiceState.Stopping => "停止中…",
        SingBoxServiceState.Failed => "失败",
        _ => "已停止",
    };
    public long TrafficUp => Interlocked.Read(ref _trafficUp);
    public long TrafficDown => Interlocked.Read(ref _trafficDown);
    public DateTimeOffset? ConnectionsUpdatedAtUtc
    {
        get
        {
            lock (_diagnosticsStateGate)
                return _connectionsUpdatedAtUtc;
        }
    }
    public DateTimeOffset? TrafficUpdatedAtUtc
    {
        get
        {
            lock (_diagnosticsStateGate)
                return _trafficUpdatedAtUtc;
        }
    }
    public string DiagnosticsStatus
    {
        get
        {
            lock (_gate)
                return $"连接：{_connectionMonitorStatus} · 流量：{_trafficMonitorStatus}";
        }
    }

    public string NetworkMonitorStatus
    {
        get
        {
            lock (_networkStateGate)
                return _networkMonitorStatus;
        }
    }

    public DateTimeOffset? NetworkLastCheckedAtUtc
    {
        get
        {
            lock (_networkStateGate)
                return _networkLastCheckedAtUtc;
        }
    }


    public string UpstreamSummary => $"127.0.0.1:{_profile.UpstreamPort} · SOCKS5";

    public IReadOnlyList<NetworkAdapterInfo> RefreshAdapters()
    {
        try
        {
            _adapters = _adapterService.EnumerateAll();
            return _adapters;
        }
        catch (Exception exception)
        {
            _adapters = Array.Empty<NetworkAdapterInfo>();
            ReportFailure("网卡扫描", "adapter.enumerate", "扫描网卡失败：" + exception.Message, exception);
            return _adapters;
        }
    }

    public IReadOnlyList<string> DiscoveryNotices => _targetScanner.Notices;

    public IReadOnlyList<LaunchTarget> ScanTargets()
    {
        IReadOnlyList<LaunchTarget> scanned = _targetScanner.Scan();
        var discovered = scanned.ToList();

        _configurationGate.Wait(_lifetimeCts.Token);
        try
        {
            string previousSelection = ApplicationRoutingKey(_profile);
            discovered = ApplicationCatalogProjection.RestoreSavedTargets(discovered, _profile.Applications).ToList();
            _profile = _profile with { Applications = ApplicationCatalogProjection.Reconcile(_profile.Applications, discovered) };
            foreach (EgressApplicationSelection selected in _profile.Applications
                .Where(selected => discovered.All(target => !target.SelectionKeys.Contains(selected.DiscoveryKey))))
            {
                discovered.Add(new LaunchTarget
                {
                    Id = selected.DiscoveryKey, SavedDiscoveryKey = selected.DiscoveryKey,
                    Name = selected.DisplayName ?? selected.DiscoveryKey,
                    CanonicalExecutable = selected.ExecutablePaths.FirstOrDefault(),
                    Kind = selected.DiscoveryKey.StartsWith("cli:", StringComparison.OrdinalIgnoreCase) ? LaunchKind.CliNative : LaunchKind.DirectExe,
                    Source = "缓存记录（当前未发现）",
                    OwnedExecutables = selected.ExecutablePaths,
                    OwnedRoots = selected.DirectoryRoots,
                    ResolutionUnsupported = true,
                });
            }
            _targets.Clear();
            foreach (LaunchTarget target in discovered)
            {
                target.RouteSelected = _profile.Applications.Any(selection => target.SelectionKeys.Contains(selection.DiscoveryKey));
                _targets.Add(target);
            }
            ApplicationInventorySnapshot inventory = ApplicationInventorySnapshot.Create(discovered);
            _profile = _profile with
            {
                Applications = _profile.Applications.Select(selection => inventory.TryGet(selection.DiscoveryKey, out var entry) && entry is not null
                    ? selection with { DisplayName = entry.DisplayName, ExecutablePaths = entry.ExecutablePaths.Concat(selection.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        DirectoryRoots = discovered.Where(target => target.DiscoveryKey == selection.DiscoveryKey).SelectMany(target => target.OwnedRoots)
                            .Concat(selection.DirectoryRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() }
                    : selection).ToArray(),
            };
            _profile = ApplicationPathAliases.Expand(_profile, new ExecutablePathCanonicalizer());
            _profileStore.Save(_profile);
            RefreshProtectedInventory();
            if (previousSelection != ApplicationRoutingKey(_profile)) _routingDirty = true;
            Visuals.Update(discovered, _profile);
            _inventoryReady = true;
        }
        finally { _configurationGate.Release(); }
        SetMessage($"已扫描 {discovered.Count} 个浏览器、AI 应用与 CLI。" + (_targetScanner.Notices.Count == 0 ? "" : "\n" + string.Join("\n", _targetScanner.Notices)));
        return discovered;
    }

    public async Task<ControllerOperationResult> SetApplicationsRouteAsync(
        IEnumerable<LaunchTarget> targets,
        bool enabled,
        EgressRouteTarget? route = null,
        IReadOnlyDictionary<string, EgressRouteTarget>? routes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        string[] keys = targets
            .Where(target => !enabled || target.CanRoute)
            .Select(target => target.DiscoveryKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await UpdateProfileAsync(
            current =>
            {
                var selected = current.Applications.ToDictionary(item => item.DiscoveryKey, StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    if (enabled)
                    {
                        var target = _targets.All().First(item => item.DiscoveryKey == key);
                        var saved = selected.Values.Where(item => target.SelectionKeys.Contains(item.DiscoveryKey)).ToArray();
                        foreach (string alias in target.SelectionKeys) selected.Remove(alias);
                        selected[key] = ApplicationCatalogProjection.Merge(target, saved,
                            route ?? routes?.GetValueOrDefault(key) ?? saved.FirstOrDefault()?.Target ?? EgressRouteTarget.DefaultAdapter);
                    }
                    else
                    {
                        var target = _targets.All().FirstOrDefault(item => item.DiscoveryKey == key);
                        foreach (string alias in target?.SelectionKeys ?? [key]) selected.Remove(alias);
                    }
                }
                return current with { Applications = selected.Values.ToArray() };
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControllerOperationResult> SetRuleSetAsync(
        string name,
        bool enabled,
        EgressRouteTarget? route = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Failure("规则集名称不能为空。");
        string normalized = name.Trim().ToLowerInvariant();
        if (enabled)
        {
            SingBoxRuleCatalog? catalog = Catalog;
            if (catalog is null || !catalog.TryGet(normalized, out _))
                return Failure("规则 catalog 尚未就绪，请先更新规则。");
            RuleArtifactResult artifact = await _artifactStore.EnsureAsync(
                catalog.Snapshot,
                normalized,
                cancellationToken).ConfigureAwait(false);
            if (!artifact.Succeeded)
                return Failure(artifact.Error ?? "SRS 下载失败。");
        }

        return await UpdateProfileAsync(
            current => current with
            {
                RuleSets = UpdateNamedRoutes(current.RuleSets, [normalized], enabled, route),
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControllerOperationResult> SetRuleSetsAsync(
        IEnumerable<string> names,
        bool enabled,
        EgressRouteTarget? route = null,
        IReadOnlyDictionary<string, EgressRouteTarget>? routes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] normalized = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
            return ControllerOperationResult.Success();

        if (enabled)
        {
            SingBoxRuleCatalog? catalog = Catalog;
            if (catalog is null)
                return Failure("规则 catalog 尚未就绪，请先更新规则。");

            string[] missing = normalized
                .Where(name => !catalog.TryGet(name, out _))
                .ToArray();
            if (missing.Length > 0)
                return Failure("规则 catalog 缺少：" + string.Join(", ", missing));

            RuleArtifactBatchResult artifacts = await _artifactStore.EnsureManyAsync(
                catalog.Snapshot,
                normalized,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!artifacts.Succeeded)
            {
                string details = string.Join(
                    "; ",
                    artifacts.Failures.Select(item => item.Key + ": " + item.Value));
                return Failure("SRS 批量下载失败：" + details);
            }
        }

        return await UpdateProfileAsync(
            current => current with
            {
                RuleSets = UpdateNamedRoutes(current.RuleSets, normalized, enabled, route, routes),
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ControllerOperationResult> AddManualDomainAsync(
        string domain,
        EgressRouteTarget? route = null,
        CancellationToken cancellationToken = default)
    {
        string normalized = EgressProfileDocument.NormalizeDomain(domain);
        return UpdateProfileAsync(
            current => current with { Domains = UpdateNamedRoutes(current.Domains, [normalized], true, route) },
            cancellationToken);
    }

    public Task<ControllerOperationResult> RemoveManualDomainAsync(
        string domain,
        CancellationToken cancellationToken = default)
        => UpdateProfileAsync(
            current => current with
            {
                Domains = current.Domains
                    .Where(item => !string.Equals(item.Name, domain, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
            },
            cancellationToken);

    public Task<ControllerOperationResult> AddUpstreamPortAsync(
        int port,
        CancellationToken cancellationToken = default)
        => UpdateProfileAsync(current => current.AddPort(port), cancellationToken);

    public Task<ControllerOperationResult> SetDefaultUpstreamPortAsync(int port, CancellationToken cancellationToken = default)
        => UpdateProfileAsync(current => current.SetDefaultPort(port), cancellationToken);

    public Task<ControllerOperationResult> RemoveUpstreamPortAsync(int port, CancellationToken cancellationToken = default)
        => UpdateProfileAsync(current => current.RemovePort(port), cancellationToken);

    private static EgressNamedRoute[] UpdateNamedRoutes(
        IReadOnlyList<EgressNamedRoute> current, IEnumerable<string> names, bool enabled, EgressRouteTarget? target,
        IReadOnlyDictionary<string, EgressRouteTarget>? targets = null)
    {
        var routes = current.ToDictionary(route => route.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            if (enabled)
                routes[name] = new EgressNamedRoute
                {
                    Name = name,
                    Target = target ?? targets?.GetValueOrDefault(name)
                        ?? routes.GetValueOrDefault(name)?.Target ?? EgressRouteTarget.DefaultAdapter,
                };
            else
                routes.Remove(name);
        }
        return routes.Values.ToArray();
    }

    public void ConfigureQuota(decimal totalGigabytes, decimal remainingGigabytes)
    {
        const decimal bytesPerGigabyte = 1024m * 1024m * 1024m;
        if (totalGigabytes < 0 || remainingGigabytes < 0 || remainingGigabytes > totalGigabytes)
            throw new ArgumentOutOfRangeException(nameof(remainingGigabytes), "当前剩余量必须在 0 和套餐总量之间。");
        if (totalGigabytes * bytesPerGigabyte > long.MaxValue
            || remainingGigabytes * bytesPerGigabyte > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(totalGigabytes), "套餐流量过大。");
        }

        long totalBytes = checked((long)Math.Round(totalGigabytes * bytesPerGigabyte, MidpointRounding.AwayFromZero));
        long remainingBytes = checked((long)Math.Round(remainingGigabytes * bytesPerGigabyte, MidpointRounding.AwayFromZero));
        _quotaStore.Configure(totalBytes, remainingBytes);
        SetMessage("默认网卡流量套餐已保存，本地统计已重置。");
    }

    public void ClearQuotaUsage()
    {
        _quotaStore.ClearUsage();
        SetMessage("已清空本地 默认网卡流量统计。");
    }

    public async Task<SingBoxCatalogUpdateResult> RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        SingBoxCatalogUpdateResult result = await _catalogService.UpdateAsync(cancellationToken).ConfigureAwait(false);
        SetMessage(result.Succeeded
            ? $"已更新 MetaCubeX sing catalog：{result.Catalog!.Count} 个 SRS，commit {result.Catalog.Snapshot.CommitSha[..12]}。"
            : "规则更新失败：" + result.Error);
        if (!result.Succeeded) Diagnostics.Report("规则更新", "catalog.update", result.Error ?? "规则更新失败");
        return result;
    }

    public async Task<ControllerOperationResult> StartTunAsync(CancellationToken cancellationToken = default)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsTunRunning || _shuttingDown)
                return ControllerOperationResult.Success();
            InvalidateReadiness("正在启动 TUN");
            _profileApplied = false;

            SingBoxApplyResult result = await _singBox.StartAsync(PrepareRuntimeAsync, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                if (IsTunRunning)
                {
                    StartDiagnostics(LoadControllerEndpoint());
                    StartRuntimeMonitor();
                    StartNetworkMonitor();
                }
                return ReportFailure("TUN 启动", result.ErrorCode ?? "tun.start",
                    "TUN 启动失败：" + (result.ErrorMessage ?? "未知错误"));
            }

            StartDiagnostics(LoadControllerEndpoint());
            StartRuntimeMonitor();
            StartNetworkMonitor();
            _profileApplied = true;
            _routingDirty = false;
            SetMessage("sing-box TUN 已启动。");
            return ControllerOperationResult.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return ReportFailure("TUN 启动", "tun.start.unhandled", error.Message, error); }
        finally
        {
            _configurationGate.Release();
        }
    }

    private async Task<ControllerOperationResult> StopTunAsync(CancellationToken cancellationToken = default)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopNetworkMonitor();
            await _singBox.StopAsync(cancellationToken).ConfigureAwait(false);
            StopRuntimeMonitor();
            StopDiagnostics();
            SetMessage("sing-box TUN 已停止。");
            return ControllerOperationResult.Success();
        }
        catch (Exception exception)
        {
            return Failure("停止 TUN 失败：" + exception.Message, exception);
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task<ControllerOperationResult> CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using SingBoxApiClient api = CreateApiClient();
            await api.CloseAllConnectionsAsync(cancellationToken).ConfigureAwait(false);
            SetMessage("已请求 sing-box 关闭全部活动连接。");
            return ControllerOperationResult.Success();
        }
        catch (Exception exception)
        {
            return Failure("关闭连接失败：" + exception.Message, exception);
        }
    }

    public async Task<ControllerOperationResult> CloseConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using SingBoxApiClient api = CreateApiClient();
            await api.CloseConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
            SetMessage("已请求 sing-box 关闭选中连接。");
            return ControllerOperationResult.Success();
        }
        catch (Exception exception)
        {
            return Failure("关闭连接失败：" + exception.Message, exception);
        }
    }

    public void ClearConnectionHistory()
    {
        _connectionHistory.ClearClosed();
        SetMessage("已清空连接历史；不会影响 sing-box 当前活动连接。");
    }

    public async Task<SingBoxDnsResponse> QueryDnsAsync(
        string host,
        string recordType = "A",
        CancellationToken cancellationToken = default)
    {
        using SingBoxApiClient api = CreateApiClient();
        return await api.QueryDnsAsync(host, recordType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControllerOperationResult> CheckInternetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsTunRunning)
            return Failure("TUN 未运行，暂不能检测联网。");
        if (!await _networkProbeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Failure("联网检测正在进行中。");

        try
        {
            return await RunInternetHealthCheckAsync(cancellationToken, announce: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("联网检测已取消。");
        }
        catch (Exception exception)
        {
            return Failure("联网检测失败：" + DescribeDiagnosticsFailure(exception), exception);
        }
        finally
        {
            _networkProbeGate.Release();
        }
    }

    public async Task<ControllerOperationResult> FlushDnsCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using SingBoxApiClient api = CreateApiClient();
            await api.FlushDnsCacheAsync(cancellationToken).ConfigureAwait(false);
            return ControllerOperationResult.Success();
        }
        catch (Exception exception)
        {
            return Failure("清理 DNS 缓存失败：" + exception.Message, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;
        _lifetimeCts.Cancel();
        Task[] background = new[] { _networkMonitorTask, _runtimeMonitorTask, _diagnosticsTask }.OfType<Task>().ToArray();
        if (_protectionTask is not null) { try { await _protectionTask.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        if (_tunSupervisor is not null) { try { await _tunSupervisor.Pending.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        StopNetworkMonitor();
        StopRuntimeMonitor();
        StopDiagnostics();
        try { await Task.WhenAll(background).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _singBox.Output -= OnSingBoxOutput;
        if (_guardAgent is not null) await _guardAgent.DisposeAsync().ConfigureAwait(false);
        try { await _singBox.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { ReportFailure("退出清理", "core.dispose", error.Message, error); }
        _remoteFetcher.Dispose();
        _releaseHttpClient.Dispose();
        Visuals.Dispose();
        Diagnostics.Info("退出", "EgressController stopped.");
        _lifetimeCts.Dispose();
        _configurationGate.Dispose();
    }

    private Task<SingBoxRuntimeCandidate> PrepareRuntimeAsync(CancellationToken cancellationToken)
        => PrepareRuntimeAsync(_profile, cancellationToken);

    private async Task<SingBoxRuntimeCandidate> PrepareRuntimeAsync(EgressProfileDocument desiredProfile, CancellationToken cancellationToken)
    {
        EgressProfileDocument profile = desiredProfile.NormalizeAndValidate();

        RefreshAdapters();
        // Only migrate persisted intent. Interface discovery must never choose either role.
        string? conflict = _tunInspector.GetStartError(_directSingBox.Status.State == "running");
        if (conflict is not null) throw new ControllerPreparationException("tun.conflict", conflict);
        NetworkEnvironmentSnapshot environment = _adapterBindings.Resolve(_environmentResolver.Resolve(profile, _adapters), _adapters);
        ProxyPortSnapshot proxyBindings = ResolveProxyBindings(profile, cancellationToken);
        IReadOnlyList<string> ownerPaths = proxyBindings.OwnerPaths;
        ValidateProtectionConflicts(profile, ownerPaths, updateExclusions: false);
        ApplicationInventorySnapshot inventory = ApplicationInventorySnapshot.Create(_targets.All());
        IReadOnlyList<SingBoxApplicationRouteInput> applicationRoutes = ResolveApplicationRoutes(profile, inventory);
        IReadOnlyList<SingBoxRuleSetInput> ruleSets = await EnsureRuleSetsAsync(profile, cancellationToken).ConfigureAwait(false);
        SingBoxCoreCandidate core = _preparedCore ??= await _coreManager.PrepareAsync(profile.Core, cancellationToken).ConfigureAwait(false);
        _coreExecutable = core.ExecutablePath;
        // Downloads may outlive a proxy restart; compile one consistent fresh snapshot.
        environment = _adapterBindings.Resolve(_environmentResolver.Resolve(profile, _adapters), _adapters);
        proxyBindings = ResolveProxyBindings(profile, cancellationToken);
        ownerPaths = proxyBindings.OwnerPaths;
        ValidateProtectionConflicts(profile, ownerPaths, updateExclusions: false);
        ControllerEndpoint? existingEndpoint = IsTunRunning ? LoadControllerEndpoint() : null;
        ControllerEndpoint endpoint = existingEndpoint is not null && !profile.UpstreamPorts.Contains(existingEndpoint.Port)
            ? existingEndpoint : CreateControllerEndpoint(profile.UpstreamPorts);

        string runtimeDirectory = Path.Combine(_dataRoot, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        EgressProfileCompilationResult compiled = _compiler.Compile(new EgressProfileCompileInput
        {
            Profile = profile,
            Environment = environment,
            ApplicationRoutes = applicationRoutes,
            UpstreamOwnerPaths = ownerPaths,
            UnreadyUpstreamPorts = proxyBindings.Ports.Where(binding => !binding.IsReady).Select(binding => binding.Port).ToArray(),
            SelfExecutablePaths = [Environment.ProcessPath ?? string.Empty, core.ExecutablePath],
            RuleSets = ruleSets,
            ControllerPort = endpoint.Port,
            ControllerSecret = endpoint.Secret,
        });
        // Keep each candidate immutable.  SingBoxService can then restart the last-good
        // candidate if process start or the API health check fails after this candidate was
        // prepared; overwriting one shared config.json would make rollback impossible.
        string configPath = Path.Combine(runtimeDirectory, $"config-{compiled.Sha256}.json");
        EgressProfileCompiler.WriteNext(configPath, compiled);
        await _coreManager.CheckConfigAsync(core, configPath, cancellationToken).ConfigureAwait(false);
        SetRuntimeFingerprint(environment, proxyBindings);
        return SingBoxRuntimeCandidate.From(core, configPath, compiled.Sha256, endpoint.Port, endpoint.Secret);
    }

    private async Task<IReadOnlyList<SingBoxRuleSetInput>> EnsureRuleSetsAsync(
        EgressProfileDocument profile,
        CancellationToken cancellationToken)
    {
        if (profile.RuleSets.Count == 0)
            return Array.Empty<SingBoxRuleSetInput>();
        SingBoxRuleCatalog catalog = Catalog
            ?? throw new ControllerPreparationException("rules.catalog", "已选择 SRS，但本地没有可用的 sing catalog。");
        RuleArtifactBatchResult result = await _artifactStore.EnsureManyAsync(
            catalog.Snapshot,
            profile.RuleSets.Select(route => route.Name),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ControllerPreparationException("rules.download", string.Join("；", result.Failures.Values));
        return profile.RuleSets.Select(route => route.Name)
            .Select(name => new SingBoxRuleSetInput(name, result.Paths[name]))
            .ToArray();
    }

    private ProxyPortSnapshot ResolveProxyBindings(EgressProfileDocument profile, CancellationToken cancellationToken)
    {
        ProxyPortSnapshot snapshot = _portTracker.Capture(profile.UpstreamPorts, cancellationToken);
        Volatile.Write(ref _proxyBindings, snapshot);
        return snapshot;
    }

    internal static string ApplicationRoutingKey(EgressProfileDocument profile)
        => string.Join("\n", profile.Applications.OrderBy(item => item.DiscoveryKey, StringComparer.Ordinal)
            .Select(item => item.DiscoveryKey + ":" + item.Target + ":"
                + string.Join("|", item.ExecutablePaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))));

    private static IReadOnlyList<SingBoxApplicationRouteInput> ResolveApplicationRoutes(
        EgressProfileDocument profile, ApplicationInventorySnapshot inventory)
    {
        var routes = new List<SingBoxApplicationRouteInput>();
        foreach (EgressApplicationSelection selection in profile.Applications)
        {
            if (!inventory.TryGet(selection.DiscoveryKey, out ApplicationInventoryEntry? target) || target is null)
                throw new ControllerPreparationException("application.missing", $"找不到已选择的应用：{selection.DiscoveryKey}。");
            if (!target.CanRoute)
                throw new ControllerPreparationException("application.unresolved", $"应用没有可用于分流匹配的 EXE：{target.DisplayName}。");
            routes.Add(new SingBoxApplicationRouteInput(
                target.ExecutablePaths.Concat(selection.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                selection.Target));
        }
        return routes;
    }

    private async Task<bool> HealthCheckAsync(SingBoxRuntimeCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.ControllerPort is < 1 or > ushort.MaxValue || string.IsNullOrWhiteSpace(candidate.ControllerSecret))
            throw new SingBoxServiceException("controller.invalid", "控制 API 端口或凭据缺失，不能确认 TUN 接管。");
        using var api = new SingBoxApiClient(new Uri($"http://127.0.0.1:{candidate.ControllerPort}"), candidate.ControllerSecret);
        try
        {
            return await SingBoxStartupCheck.WaitAsync(async token =>
            {
                SingBoxVersionResponse version = await api.GetVersionAsync(token).ConfigureAwait(false);
                _takeoverError = !version.Version.StartsWith("sing-box ", StringComparison.OrdinalIgnoreCase)
                    ? $"控制端口 {candidate.ControllerPort} 返回的不是 sing-box，可能被其他程序占用。"
                    : _tunInspector.GetError();
                return _takeoverError;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SingBoxServiceException error)
        {
            _takeoverError = error.Message;
            ReportFailure("TUN 接管检查", error.Code, error.Message, error);
            throw;
        }
    }

    private async Task<ControllerOperationResult> UpdateProfileAsync(
        Func<EgressProfileDocument, EgressProfileDocument> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EgressProfileDocument previous = _profile;
            bool previouslyApplied = _profileApplied;
            EgressProfileDocument next;
            try
            {
                next = ApplicationPathAliases.Expand(update(previous).NormalizeAndValidate(), new ExecutablePathCanonicalizer());
                if (EgressProfileStore.HasSameContent(next, previous)) return ControllerOperationResult.Success();
                ValidateProtectionConflicts(next, ResolveProxyBindings(next, cancellationToken).OwnerPaths, updateExclusions: false);
            }
            catch (Exception exception) { return Failure(exception.Message, exception); }

            void Commit(EgressProfileDocument profile)
            {
                _profileStore.Save(profile);
                _profile = profile;
                RefreshProtectedInventory();
            }

            if (!IsTunRunning)
            {
                try
                {
                    Commit(next);
                    _profileApplied = false;
                    if (_guardAgent is not null) await _guardAgent.SyncAsync(cancellationToken).ConfigureAwait(false);
                    SetMessage("配置已保存，正在等待 TUN 自动启动。");
                    return ControllerOperationResult.Success();
                }
                catch (Exception exception) { return Failure("保存配置或同步守护失败：" + exception.Message, exception); }
            }

            string previousRuntimeFingerprint = GetRuntimeFingerprint();
            void RestorePrevious()
            {
                // Restore enforcement even if the settings disk has become unwritable.
                _profile = previous;
                RefreshProtectedInventory();
                _profileStore.Save(previous);
            }
            Diagnostics.Info("TUN 配置变更", "用户保存了网卡、端口或分流设置");
            SingBoxApplyResult applied = await PreparedRuntimeUpdate.ApplyAsync(_singBox,
                token => PrepareRuntimeAsync(next, token), () => Commit(next), RestorePrevious,
                cancellationToken).ConfigureAwait(false);
            if (applied.Succeeded)
            {
                _profileApplied = true;
                _routingDirty = false;
                if (applied.CoreChanged)
                {
                    StartDiagnostics(LoadControllerEndpoint());
                    StartNetworkMonitor();
                }
                SetMessage("配置已校验并应用。");
            }
            else
            {
                SetRuntimeFingerprint(previousRuntimeFingerprint);
                _profileApplied = IsTunRunning && previouslyApplied;
                if (applied.CoreChanged && IsTunRunning)
                {
                    StartDiagnostics(LoadControllerEndpoint());
                    StartNetworkMonitor();
                }
            }
            try
            {
                if (_guardAgent is not null) await _guardAgent.SyncAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) { return Failure("守护同步失败：" + exception.Message, exception); }
            return applied.Succeeded ? ControllerOperationResult.Success()
                : Failure(applied.ErrorMessage ?? "配置应用失败，已保留旧配置。");
        }
        finally { _configurationGate.Release(); }
    }

    private void ConfigureControlPlane()
    {
        _remoteFetcher?.Dispose();
        _releaseHttpClient?.Dispose();
        _releaseHttpClient = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(8) })
            { Timeout = TimeSpan.FromSeconds(30) };
        _releaseHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EgressController/1.0");
        _remoteFetcher = new HttpRemoteFetcher(_releaseHttpClient);
        string rulesDirectory = Path.Combine(_rulesetRoot, "rules");
        _catalogService = new RuleCatalogService(_remoteFetcher, rulesDirectory);
        _artifactStore = new RuleArtifactStore(rulesDirectory, _remoteFetcher);
        _coreManager = new SingBoxCoreManager(
            _rulesetRoot,
            new SingBoxReleaseClient(_releaseHttpClient),
            new SingBoxCli(),
            _stateStore);
        LoadCachedCatalog();
    }

    private void LoadCachedCatalog()
    {
        _catalogService.LoadCached(out string? error);
        if (!string.IsNullOrWhiteSpace(error))
            ReportFailure("规则目录", "catalog.cache", error);
    }

    private EgressProfileDocument LoadProfile()
    {
        try
        {
            return _profileStore.Load();
        }
        catch (Exception exception)
        {
            // A fabricated empty profile would erase the independent guardian's last
            // selection. Leave both files intact and never establish a ready heartbeat.
            throw new InvalidDataException("Profile 读取失败，原文件及守护名单已保留。请修复配置后重新打开：" + exception.Message, exception);
        }
    }

    private void StartDiagnostics(ControllerEndpoint? endpoint)
    {
        StopDiagnostics();
        if (endpoint is null)
        {
            SetMonitorStatuses("未启用", "未启用");
            return;
        }

        int generation = Interlocked.Increment(ref _diagnosticsGeneration);
        _diagnosticsCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken token = _diagnosticsCts.Token;
        SetMonitorStatuses("连接中…", "连接中…");
        _diagnosticsTask = Task.Run(() => DiagnosticsLoopAsync(endpoint, generation, token), token);
    }

    private void StopDiagnostics()
    {
        Interlocked.Increment(ref _diagnosticsGeneration);
        CancellationTokenSource? diagnosticsCts = _diagnosticsCts;
        Task? diagnosticsTask = _diagnosticsTask;
        _diagnosticsCts = null;
        _diagnosticsTask = null;

        diagnosticsCts?.Cancel();
        if (diagnosticsCts is not null)
        {
            if (diagnosticsTask is null || diagnosticsTask.IsCompleted)
            {
                diagnosticsCts.Dispose();
            }
            else
            {
                _ = diagnosticsTask.ContinueWith(
                    static (completedTask, state) =>
                    {
                        _ = completedTask.Exception;
                        ((CancellationTokenSource)state!).Dispose();
                    },
                    diagnosticsCts,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        lock (_diagnosticsStateGate)
        {
            _connectionRateSamples.Clear();
            _connectionHistory.ApplySnapshot(Array.Empty<ConnectionObservation>());
            Interlocked.Exchange(ref _trafficUp, 0);
            Interlocked.Exchange(ref _trafficDown, 0);
            _connectionsUpdatedAtUtc = null;
            _trafficUpdatedAtUtc = null;
            _trafficTimeline.Reset();
            _connectionRatesValid = false;
        }
        SetMonitorStatuses("未运行", "未运行");
    }

    private void StartRuntimeMonitor()
    {
        StopRuntimeMonitor();
        _runtimeMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken token = _runtimeMonitorCts.Token;
        _runtimeMonitorTask = Task.Run(() => RuntimeMonitorLoopAsync(token), token);
    }

    private void StopRuntimeMonitor()
    {
        CancellationTokenSource? cts = _runtimeMonitorCts;
        Task? task = _runtimeMonitorTask;
        _runtimeMonitorCts = null;
        _runtimeMonitorTask = null;
        cts?.Cancel();
        if (cts is not null)
        {
            if (task is null || task.IsCompleted)
                cts.Dispose();
            else
                _ = task.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    cts,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }
    }

    private async Task RuntimeMonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                if (!IsTunRunning)
                    continue;

                RefreshAdapters();
                EgressProfileDocument profile = _profile.NormalizeAndValidate();
                NetworkEnvironmentSnapshot environment = _adapterBindings.Resolve(_environmentResolver.Resolve(profile, _adapters), _adapters);
                ProxyPortSnapshot proxyBindings = ResolveProxyBindings(profile, cancellationToken);
                string fingerprint = BuildRuntimeFingerprint(environment, proxyBindings);
                bool changed;
                lock (_runtimeStateGate)
                    changed = _runtimeFingerprint.Length > 0 && !string.Equals(_runtimeFingerprint, fingerprint, StringComparison.Ordinal);
                if (!changed && _profileApplied && !_routingDirty)
                    continue;

                await ApplyRuntimeChangeAsync(_routingDirty ? "显式扫描更新了已保存 EXE 清单"
                    : !_profileApplied ? "等待当前配置生效" : "网卡绑定或代理端口身份发生变化", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                string detail = DescribeDiagnosticsFailure(exception);
                ReportFailure("出口变化检测", "runtime.monitor", "网络状态检测失败：" + detail, exception);
            }
        }
    }

    private async Task ApplyRuntimeChangeAsync(string cause, CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsTunRunning)
                return;

            bool previouslyApplied = _profileApplied;
            string previousRuntimeFingerprint = GetRuntimeFingerprint();
            Diagnostics.Info("TUN 配置变更", cause);
            SingBoxApplyResult applied = await _singBox.ApplyAsync(PrepareRuntimeAsync, cancellationToken).ConfigureAwait(false);
            if (applied.Succeeded)
            {
                _profileApplied = true;
                _routingDirty = false;
                if (applied.CoreChanged)
                {
                    StartDiagnostics(LoadControllerEndpoint());
                    StartNetworkMonitor();
                }
                SetMessage("检测到网卡或上游代理进程变化，配置已重新校验并应用。");
            }
            else
            {
                SetRuntimeFingerprint(previousRuntimeFingerprint);
                _profileApplied = IsTunRunning && previouslyApplied;
                if (applied.CoreChanged && IsTunRunning) StartNetworkMonitor();
                ReportFailure("出口配置更新", applied.ErrorCode ?? "runtime.apply",
                    "网络状态变化后的配置应用失败，已保留当前配置：" + (applied.ErrorMessage ?? "未知错误"));
            }
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private void SetRuntimeFingerprint(NetworkEnvironmentSnapshot environment, ProxyPortSnapshot proxyBindings)
    {
        SetRuntimeFingerprint(BuildRuntimeFingerprint(environment, proxyBindings));
    }

    private string GetRuntimeFingerprint()
    {
        lock (_runtimeStateGate)
            return _runtimeFingerprint;
    }

    private void SetRuntimeFingerprint(string fingerprint)
    {
        lock (_runtimeStateGate)
            _runtimeFingerprint = fingerprint;
    }

    private static string BuildRuntimeFingerprint(
        NetworkEnvironmentSnapshot environment,
        ProxyPortSnapshot proxyBindings)
    {
        static string AdapterFingerprint(AdapterSelection adapter)
            => string.Join(
                ":",
                adapter.AdapterId,
                adapter.Alias,
                adapter.IsUp,
                adapter.Ipv4BindAddress,
                adapter.Ipv6BindAddress,
                adapter.AddressState);

        return string.Join(
            "|",
            environment.AllAdapters
                .OrderBy(adapter => adapter.AdapterId).Select(AdapterFingerprint)
                .Append(proxyBindings.RoutingFingerprint));
    }

    private async Task DiagnosticsLoopAsync(ControllerEndpoint endpoint, int generation, CancellationToken token)
    {
        using var api = new SingBoxApiClient(endpoint.Uri, endpoint.Secret);
        var sampler = new ConnectionSnapshotSampler(api.GetConnectionsAsync);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sample = await sampler.SampleAsync(token).ConfigureAwait(false);
                if (sample.Snapshot is { } snapshot)
                {
                    ApplyTrafficRate(new SingBoxTrafficEvent { Up = sample.UploadRate, Down = sample.DownloadRate }, generation, sample.HasRate);
                    ApplyConnectionSnapshot(snapshot, generation);
                    SetConnectionMonitorConnected(generation);
                    SetTrafficMonitorConnected(generation);
                }
                else
                {
                    ResetTrafficRate(generation);
                    // Display sampling has no authority over TUN or either protection decision.
                    SetConnectionMonitorStatus("统计暂不可用：" + DescribeDiagnosticsFailure(sample.Error!), generation);
                    SetTrafficMonitorStatus("等待下一次统计", generation);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                ResetTrafficRate(generation);
                SetConnectionMonitorStatus("统计显示失败：" + DescribeDiagnosticsFailure(error), generation);
                try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    private void ApplyConnectionSnapshot(SingBoxConnectionsResponse snapshot, int generation)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        long observedStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var observations = new List<ConnectionObservation>(snapshot.Connections.Count);
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        long adapterDelta = 0;
        lock (_diagnosticsStateGate)
        {
            if (generation != Volatile.Read(ref _diagnosticsGeneration))
                return;

            foreach (SingBoxConnection connection in snapshot.Connections)
            {
                if (string.IsNullOrWhiteSpace(connection.Id))
                    continue;
                currentIds.Add(connection.Id);
                _connectionRateSamples.TryGetValue(connection.Id, out ConnectionRateSample? previous);
                double elapsedSeconds = previous is null
                    ? 0
                    : System.Diagnostics.Stopwatch.GetElapsedTime(previous.ObservedStamp, observedStamp).TotalSeconds;
                long uploadRate = CalculateRate(connection.Upload, previous?.Upload, elapsedSeconds);
                long downloadRate = CalculateRate(connection.Download, previous?.Download, elapsedSeconds);
                DateTimeOffset startedAtUtc = connection.Start ?? previous?.StartedAtUtc ?? observedAtUtc;
                observations.Add(ToObservation(connection, observedAtUtc, startedAtUtc, uploadRate, downloadRate) with
                { HasRate = _connectionRatesValid && previous is not null && elapsedSeconds is > 0 and <= 3
                    && previous.Upload >= 0 && previous.Download >= 0
                    && previous.StartedAtUtc == startedAtUtc && connection.Upload >= previous.Upload && connection.Download >= previous.Download });
                string? outbound = connection.Chains.FirstOrDefault();
                if (Guid.TryParse(_profile.DefaultAdapterId, out Guid quotaAdapter)
                    && string.Equals(outbound, EgressProfileCompiler.AdapterTag(quotaAdapter), StringComparison.OrdinalIgnoreCase))
                {
                    adapterDelta = SafeAdd(adapterDelta, previous is null
                        ? SafeAdd(Math.Max(0, connection.Upload), Math.Max(0, connection.Download))
                        : SafeAdd(SafeDelta(connection.Upload, previous.Upload), SafeDelta(connection.Download, previous.Download)));
                }
                _connectionRateSamples[connection.Id] = new ConnectionRateSample(
                    observedAtUtc, observedStamp,
                    connection.Upload,
                    connection.Download,
                    startedAtUtc,
                    outbound);
            }

            foreach (string id in _connectionRateSamples.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                _connectionRateSamples.Remove(id);

            Interlocked.Exchange(ref _trafficUp, Math.Max(0, snapshot.UploadTotal));
            Interlocked.Exchange(ref _trafficDown, Math.Max(0, snapshot.DownloadTotal));
            _connectionHistory.ApplySnapshot(observations, observedAtUtc);
            _connectionsUpdatedAtUtc = observedAtUtc;
            _connectionRatesValid = true;
        }
        if (adapterDelta > 0)
            _quotaStore.AddUsage(adapterDelta);
    }

    private static long CalculateRate(long current, long? previous, double elapsedSeconds)
    {
        if (previous is null || previous < 0 || current < 0 || elapsedSeconds <= 0 || current <= previous.Value)
            return 0;
        double rate = ((double)current - previous.Value) / elapsedSeconds;
        return rate >= long.MaxValue ? long.MaxValue : (long)Math.Round(rate);
    }

    private static long SafeDelta(long current, long previous)
        => previous >= 0 && current > previous ? current - previous : 0;

    private static long SafeAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static ConnectionObservation ToObservation(
        SingBoxConnection connection,
        DateTimeOffset observedAtUtc,
        DateTimeOffset startedAtUtc,
        long uploadRate,
        long downloadRate)
        => new()
        {
            Id = connection.Id,
            Network = connection.Metadata.Network,
            Type = connection.Metadata.Type,
            SourceIp = connection.Metadata.SourceIp,
            DestinationIp = connection.Metadata.DestinationIp,
            SourcePort = connection.Metadata.SourcePort,
            DestinationPort = connection.Metadata.DestinationPort,
            Host = connection.Metadata.Host,
            DnsMode = connection.Metadata.DnsMode,
            ProcessPath = string.IsNullOrWhiteSpace(connection.Metadata.ProcessPath) ? null : connection.Metadata.ProcessPath,
            Upload = connection.Upload,
            Download = connection.Download,
            UploadRate = uploadRate,
            DownloadRate = downloadRate,
            StartedAtUtc = startedAtUtc,
            LastSeenAtUtc = observedAtUtc,
            Chains = connection.Chains.ToArray(),
            Rule = connection.Rule,
            RulePayload = connection.RulePayload,
            Outbound = connection.Chains.FirstOrDefault(),
        };

    private ControllerEndpoint? LoadControllerEndpoint()
    {
        try
        {
            SingBoxRuntimePointer? runtime = _stateStore.LoadCurrentRuntime();
            return runtime is { ControllerPort: >= 1 and <= ushort.MaxValue }
                && !string.IsNullOrWhiteSpace(runtime.ControllerSecret)
                ? new ControllerEndpoint(runtime.ControllerPort, runtime.ControllerSecret)
                : null;
        }
        catch (Exception error)
        {
            ReportFailure("控制 API 配置", "controller.read", "无法读取内核控制接口配置：" + error.Message, error);
            return null;
        }
    }

    private static ControllerEndpoint CreateControllerEndpoint(IReadOnlyList<int> upstreamPorts)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!upstreamPorts.Contains(port))
                return new ControllerEndpoint(port, EgressProfileCompiler.CreateControllerSecret());
        }
        throw new ControllerPreparationException("controller.port", "无法分配与上游端口分离的本地 API 端口。");
    }

    private SingBoxApiClient CreateApiClient()
    {
        ControllerEndpoint endpoint = LoadControllerEndpoint()
            ?? throw new InvalidOperationException("当前 sing-box runtime 没有可用的 Clash API 配置。");
        return new SingBoxApiClient(endpoint.Uri, endpoint.Secret);
    }

    private void ApplyTrafficRate(SingBoxTrafficEvent traffic, int generation, bool available)
    {
        lock (_diagnosticsStateGate)
        {
            if (generation != Volatile.Read(ref _diagnosticsGeneration))
                return;
            _trafficUpdatedAtUtc = DateTimeOffset.UtcNow;
            _trafficTimeline.Observe(available ? traffic.Up : null, available ? traffic.Down : null);
        }
    }

    private void ResetTrafficRate(int generation)
    {
        lock (_diagnosticsStateGate)
        {
            if (generation != Volatile.Read(ref _diagnosticsGeneration))
                return;
            _connectionRatesValid = false;
            _trafficTimeline.Unavailable();
        }
    }

    private void OnSingBoxOutput(SingBoxOutputEvent output)
    {
        string level = CoreLogClassifier.Classify(output.Source, output.Line);
        string message = DiagnosticJournal.Redact(output.Line);
        _logs.Append(output.Source, level, message);
        // Routine core output remains bounded in memory; it is not a protection event.
        // Actual core exits/startup failures are recorded by the lifecycle owner.
    }

    private void SetConnectionMonitorConnected(int generation)
    {
        lock (_gate)
        {
            if (generation != Volatile.Read(ref _diagnosticsGeneration))
                return;
            _connectionMonitorStatus = "已连接";
        }
    }

    private void SetTrafficMonitorConnected(int generation)
    {
        lock (_gate)
        {
            if (generation != Volatile.Read(ref _diagnosticsGeneration))
                return;
            _trafficMonitorStatus = "已连接";
        }
    }

    private static string DescribeDiagnosticsFailure(Exception exception)
    {
        string detail = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length == 0)
            detail = exception.GetType().Name;
        return detail.Length <= 180 ? detail : detail[..180] + "…";
    }

    private void SetConnectionMonitorStatus(string status, int generation)
    {
        lock (_gate)
        {
            if (generation != Volatile.Read(ref _diagnosticsGeneration))
                return;
            _connectionMonitorStatus = status;
        }
    }

    private void SetTrafficMonitorStatus(string status, int generation)
    {
        lock (_gate)
        {
            if (generation != Volatile.Read(ref _diagnosticsGeneration))
                return;
            _trafficMonitorStatus = status;
        }
    }

    private void SetMonitorStatuses(string connectionStatus, string trafficStatus)
    {
        lock (_gate)
        {
            _connectionMonitorStatus = connectionStatus;
            _trafficMonitorStatus = trafficStatus;
        }
    }

    private void SetMessage(string message)
    {
        lock (_gate)
            _lastMessage = message;
        Diagnostics.Info("主程序", message);
    }

}

internal sealed record ConnectionRateSample(
    DateTimeOffset ObservedAtUtc,
    long ObservedStamp,
    long Upload,
    long Download,
    DateTimeOffset StartedAtUtc,
    string? Outbound);

internal sealed class ControllerPreparationException(string code, string message)
    : SingBoxServiceException(code, message);
