using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Core.Diagnostics;
using EgressController.Core.Models;
using EgressController.Core.Routing;
using EgressController.Diagnostics;
using EgressController.Launcher.Discovery;
using EgressController.Launcher.Ownership;
using EgressController.Launcher.Sessions;
using EgressController.Proxy.Server;
using EgressController.Rules.Catalog;
using EgressController.Rules.Parsing;
using EgressController.Rules.Stores;
using EgressController.State.Storage;
using EgressController.Transport.Upstream;
using EgressController.Windows.Network;
using EgressController.Windows.Process;
using EgressController.Windows.SystemProxy;

namespace EgressController.App;

/// <summary>
/// Composition root for the routing data plane and its desktop control surface. The host owns
/// the live matcher, discovered launch targets, sessions, connection-source resolver and the
/// explicit System Proxy transaction; UI view-models only call these typed operations.
/// </summary>
public sealed class RouterHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly INetworkAdapterService _adapterService;
    private readonly TimeSpan _esimMonitorInterval;
    private readonly WindowsLaunchTargetScanner _targetScanner = new();
    private readonly TcpOwnerSnapshotResolver _ownerResolver = new();
    private readonly WindowsProcessIdentityResolver _processIdentity =
        new(new ExecutablePathCanonicalizer());
    private readonly ProxyStateStore _proxyStateStore;
    private readonly UpstreamRemoteFetcher _remoteFetcher;
    private readonly MetaRulesCatalogUpdater _remoteCatalogUpdater;
    private readonly RuleSnapshotManager _remoteSnapshotManager;
    private readonly RuleCacheStore _ruleCacheStore;
    private readonly SemaphoreSlim _rulesUpdateGate = new(1, 1);
    private readonly CancellationTokenSource _rulesLifetimeCts = new();
    private readonly Dictionary<string, IReadOnlyList<CompiledDomainRule>> _selectedRuleSets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _activeRuleBodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _managedOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LaunchTarget> _manualTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (System.Diagnostics.Process Process, EventHandler Handler)> _sessionRootProcesses = new();
    private readonly Dictionary<Guid, WindowsProcessJob> _sessionJobs = new();

    private LocalProxyServer? _proxy;
    private CancellationTokenSource? _reconcileCts;
    private Task? _reconcileTask;
    private Task? _esimMonitorTask;
    private SystemProxyState? _previousProxy;
    private ProxyStateRecord? _lastProxyRecord;
    private SystemProxyWatcher? _proxyWatcher;
    private string? _catalogDirectory;
    private NetworkAdapterInfo? _selectedEsim;
    private Guid? _selectedEsimGuid;
    private string? _selectedEsimName;
    private bool _esimUnavailable;
    private int _connectionClearOperations;
    private bool _catalogIsRemote;
    private string _catalogMessage = "尚未获取 MetaCubeX 规则 catalog。";
    private DateTimeOffset? _catalogUpdatedAtUtc;
    private Task? _rulesBackgroundTask;

    public RouterHost(int localPort = 18080, string upstreamHost = "127.0.0.1", int upstreamPort = 7890)
        : this(new WindowsNetworkAdapterService(), TimeSpan.FromSeconds(1), localPort, upstreamHost, upstreamPort)
    {
    }

    internal RouterHost(
        INetworkAdapterService adapterService,
        TimeSpan esimMonitorInterval,
        int localPort = 18080,
        string upstreamHost = "127.0.0.1",
        int upstreamPort = 7890,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(adapterService);
        if (esimMonitorInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(esimMonitorInterval));

        _adapterService = adapterService;
        _esimMonitorInterval = esimMonitorInterval;
        LocalPort = localPort;
        UpstreamHost = upstreamHost;
        UpstreamPort = upstreamPort;
        string dataDir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgressController");
        _proxyStateStore = new ProxyStateStore(dataDir);
        _remoteFetcher = new UpstreamRemoteFetcher(upstreamHost, upstreamPort);
        _remoteCatalogUpdater = new MetaRulesCatalogUpdater(_remoteFetcher);
        _remoteSnapshotManager = new RuleSnapshotManager(_remoteFetcher);
        _ruleCacheStore = new RuleCacheStore(Path.Combine(dataDir, "Rules"));
        _lastProxyRecord = _proxyStateStore.Load();
        LoadCachedRules();
        RefreshAdapters();
        if (_lastProxyRecord is { Active: true })
            LastMessage = "发现上一次会话可能未正常退出；请在概览页选择恢复旧 System Proxy。";
    }

    public int LocalPort { get; }
    public int BoundPort => _proxy?.BoundPort ?? LocalPort;
    public string UpstreamHost { get; }
    public int UpstreamPort { get; }

    public DomainSetStore Domains { get; } = new();
    public ConnectionLog Log { get; } = new();
    public SystemProxyManager SystemProxy { get; } = new();
    public LaunchTargetRegistry Targets { get; } = new();
    public LaunchSessionRegistry Sessions { get; } = new();

    public IReadOnlyList<NetworkAdapterInfo> Adapters { get; private set; } = Array.Empty<NetworkAdapterInfo>();
    public RuleCatalog? Catalog { get; private set; }
    public string CatalogDirectory => _catalogDirectory
        ?? (Catalog is null ? "(尚未获取规则 catalog)" : "MetaCubeX/meta-rules-dat · 本地缓存");
    public string CatalogCommit => Catalog?.Snapshot.CommitSha ?? string.Empty;
    public string CatalogMessage => _catalogMessage;
    public bool CatalogIsRemote => _catalogIsRemote;
    public DateTimeOffset? CatalogUpdatedAtUtc => _catalogUpdatedAtUtc;
    public IReadOnlyList<string> SelectedRuleNames
    {
        get
        {
            lock (_gate)
                return _selectedRuleSets.Keys.ToArray();
        }
    }
    public IReadOnlyList<string> ManualDomains => Domains.ManualDomains;

    public Task StartRemoteRulesRefresh()
    {
        lock (_gate)
        {
            if (_rulesBackgroundTask is { IsCompleted: false })
                return _rulesBackgroundTask;
            _rulesBackgroundTask = RefreshRemoteRulesAsync(_rulesLifetimeCts.Token);
            return _rulesBackgroundTask;
        }
    }

    public IEsimEgressConnector EsimConnector { get; private set; } = null!;
    public NetworkAdapterInfo? Esim { get; private set; }
    public IUpstreamProxyConnector Upstream { get; private set; } = null!;
    public RoutingEngine Engine { get; private set; } = null!;
    public ComposedRouteSource RouteSource { get; private set; } = null!;
    public bool Started { get; private set; }
    public bool RoutingEnabled { get; private set; }
    public string LastMessage { get; private set; } = string.Empty;
    public int ActiveConnections => _proxy?.ActiveConnections ?? 0;
    public bool RejectingAllConnections
    {
        get
        {
            lock (_gate)
                return _esimUnavailable;
        }
    }

    public event EventHandler<EsimConnectivityChangedEventArgs>? EsimConnectivityChanged;

    public bool HasStaleProxy
    {
        get
        {
            if (_lastProxyRecord is not { Active: true, Previous: not null, Ours: not null })
                return false;
            return SystemProxy.IsEquivalent(SystemProxy.Snapshot(), _lastProxyRecord.Ours);
        }
    }

    public void Start()
    {
        EsimConnectivityChangedEventArgs? initialConnectivity = null;
        lock (_gate)
        {
            if (Started)
                return;

            try
            {
                RefreshAdapters();
                Esim = _selectedEsim ?? SelectDefaultEsim(Adapters);
                if (Esim is not null)
                {
                    _selectedEsim = Esim;
                    _selectedEsimGuid = Esim.Identity.Guid;
                    _selectedEsimName = Esim.Identity.NameSnapshot;
                }
                EsimConnector = new EsimEgressConnector(new EsimDnsResolver(), new EsimSocketFactory())
                {
                    ConnectTimeout = TimeSpan.FromSeconds(12),
                };
                Upstream = new UpstreamHttpProxyConnector(UpstreamHost, UpstreamPort)
                {
                    ConnectTimeout = TimeSpan.FromSeconds(6),
                };
                Engine = new RoutingEngine(() => Domains.GetMatcher());
                RouteSource = new ComposedRouteSource(Engine, Esim, EsimConnector, Upstream);

                var sourceResolver = new ManagedConnectionSourceResolver(
                    _ownerResolver, _processIdentity, Sessions, Targets,
                    isSessionJobMember: IsSessionJobMember);
                _proxy = new LocalProxyServer(RouteSource, LocalPort, Log, sourceResolver);
                _esimUnavailable = Esim?.IsUp != true;
                if (_esimUnavailable)
                {
                    _proxy.SetRejectAll(true);
                    initialConnectivity = NewConnectivityEvent(isOnline: false, closedConnections: 0);
                }
                _proxy.Start();
                _proxyWatcher = SystemProxy.Watch(OnProxyChanged);
                Started = true;
                LastMessage = _esimUnavailable
                    ? OfflineMessage(0)
                    : $"本地路由已启动：127.0.0.1:{_proxy.BoundPort}。系统代理仍需通过按钮显式接管。";

                _reconcileCts = new CancellationTokenSource();
                _reconcileTask = Task.Run(() => ReconcileSessionsAsync(_reconcileCts.Token));
                _esimMonitorTask = Task.Run(() => MonitorEsimConnectivityAsync(_reconcileCts.Token));
            }
            catch (Exception ex)
            {
                _proxyWatcher?.Dispose();
                _proxyWatcher = null;
                _reconcileCts?.Cancel();
                _reconcileCts?.Dispose();
                _reconcileCts = null;
                if (_proxy is not null)
                {
                    try { _proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                    _proxy = null;
                }
                Started = false;
                _esimUnavailable = false;
                LastMessage = "本地路由启动失败：" + ex.Message;
                initialConnectivity = null;
            }
        }

        if (initialConnectivity is not null)
            RaiseEsimConnectivityChanged(initialConnectivity);
    }

    public IReadOnlyList<NetworkAdapterInfo> RefreshAdapters()
    {
        try
        {
            IReadOnlyList<NetworkAdapterInfo> adapters = _adapterService.EnumerateAll();
            lock (_gate)
            {
                Adapters = adapters;
                if (_selectedEsimGuid is Guid guid)
                    _selectedEsim = adapters.FirstOrDefault(a => a.Identity.Guid == guid);
                return Adapters;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                LastMessage = "扫描网卡失败：" + ex.Message;
                Adapters = Array.Empty<NetworkAdapterInfo>();
                _selectedEsim = null;
                return Adapters;
            }
        }
    }

    public void SelectEsim(Guid guid)
    {
        NetworkAdapterInfo? adapter = Adapters.FirstOrDefault(a => a.Identity.Guid == guid);
        if (adapter is null)
            return;
        lock (_gate)
        {
            _selectedEsim = adapter;
            _selectedEsimGuid = adapter.Identity.Guid;
            _selectedEsimName = adapter.Identity.NameSnapshot;
            Esim = adapter;
            RouteSource?.UpdateAdapter(adapter);
            LastMessage = $"ESIM 出口已选择：{adapter.Identity.NameSnapshot}";
        }

        if (Started)
            _ = ApplyEsimSnapshotAsync(adapter, adapter.Identity.Guid, CancellationToken.None);
    }

    public IReadOnlyList<LaunchTarget> ScanTargets()
    {
        IReadOnlyList<LaunchTarget> scanned = _targetScanner.Scan();
        var discovered = scanned.ToList();
        var discoveredKeys = discovered
            .Select(target => target.DiscoveryKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (LaunchTarget manual in _manualTargets.Values)
        {
            // A manual path may become discoverable through App Paths/ARP/Program Files later.
            // Keep the scanner's richer metadata in that case, otherwise preserve the explicit
            // user target across every active rescan.
            if (discoveredKeys.Add(manual.DiscoveryKey))
                discovered.Add(manual);
        }

        IReadOnlyList<LaunchTarget> previous = Targets.All();
        var oldManaged = previous.ToDictionary(t => t.DiscoveryKey, t => t.Managed, StringComparer.Ordinal);
        foreach (LaunchSession session in Sessions.All())
        {
            LaunchTarget? oldTarget = previous.FirstOrDefault(target => target.Id == session.TargetId);
            if (oldTarget is null || !discoveredKeys.Contains(oldTarget.DiscoveryKey))
                RetireSession(session.SessionId);
        }
        Targets.Clear();
        foreach (LaunchTarget target in discovered)
        {
            if (oldManaged.TryGetValue(target.DiscoveryKey, out bool managed))
                target.Managed = managed;
            else if (_managedOverrides.TryGetValue(target.DiscoveryKey, out managed))
                target.Managed = managed;
            Targets.Add(target);
        }
        LastMessage = $"已扫描 {discovered.Count} 个本地 Windows 应用。";
        return discovered;
    }

    public LaunchTarget AddExecutable(string path, string? displayName = null)
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(full))
            throw new FileNotFoundException("找不到可执行文件。", full);
        if (!Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("这里只接受 Windows .exe 的完整路径。", nameof(path));

        string root = Path.GetDirectoryName(full) ?? string.Empty;
        var target = new LaunchTarget
        {
            Id = "manual-exe:" + full.ToLowerInvariant(),
            Name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(full) : displayName.Trim(),
            Kind = LaunchKind.DirectExe,
            Command = full,
            CanonicalExecutable = full,
            OwnedRoots = new[] { root },
            // The selected executable's directory is the ownership boundary. Include every
            // descendant EXE so helper processes in app/bin/resources subdirectories can be
            // classified without granting ESIM to unrelated child processes.
            OwnedExecutables = ExecutableInventory.Collect(new[] { root }, full),
            Managed = false,
            IconPath = full,
            Source = "手动添加",
        };
        _manualTargets[target.DiscoveryKey] = target;
        if (Targets.Add(target))
            return target;

        // The path may already be present from App Paths/ARP/Program Files. Return the live
        // catalog entry so the caller cannot create a row whose target ID is not registered.
        return Targets.All().First(existing => existing.DiscoveryKey == target.DiscoveryKey);
    }

    public bool SetTargetManaged(string id, bool managed)
    {
        LaunchTarget? target = Targets.Get(id);
        if (target is null)
            return false;
        _managedOverrides[target.DiscoveryKey] = managed;
        bool changed = Targets.SetManaged(id, managed);
        if (changed && !managed)
        {
            foreach (Guid sessionId in Sessions.UnregisterForTarget(id))
            {
                ReleaseRootWatcher(sessionId);
                ReleaseSessionJob(sessionId);
            }
        }
        return changed;
    }

    public string LaunchTarget(string id)
    {
        LaunchTarget target = Targets.Get(id)
            ?? throw new InvalidOperationException("目标已从扫描结果中消失，请重新扫描。");
        if (target.ResolutionUnsupported)
            throw new InvalidOperationException("该 wrapper/shortcut 尚未解析为确定的可执行根，不能建立 Managed 会话。");

        if (target.Managed && !Started)
            Start();
        if (target.Managed && (!Started || _proxy is null))
            throw new InvalidOperationException("本地 Router 未启动，不能建立 Managed 会话。");

        int localPort = _proxy?.BoundPort ?? LocalPort;
        IReadOnlyDictionary<string, string>? environment = target.Managed
            ? WindowsLaunchService.LocalProxyEnvironment(localPort)
            : null;
        var launcher = new WindowsLaunchService();
        LaunchSession? preparedSession = null;
        LaunchSession session;
        try
        {
            session = target.Managed
                ? launcher.StartManagedTracked(
                    target,
                    environment!,
                    (prepared, job) =>
                    {
                        preparedSession = prepared;
                        Sessions.Register(prepared);
                        if (job is not null)
                            RegisterSessionJob(prepared.SessionId, job);
                        AttachRootWatcher(prepared);
                    })
                : launcher.Start(target);
        }
        catch
        {
            if (preparedSession is not null)
                RetireSession(preparedSession.SessionId);
            throw;
        }
        if (target.Managed)
        {
            LastMessage = target.Kind == LaunchKind.PackagedAumid
                ? launcher.DirectExecutableStarted
                    ? launcher.ChromiumProxyArgumentsApplied
                        ? $"已启动并纳入 Managed：{target.Name} (PID {session.RootPid})；已接管 Electron/Chromium、WebView2 与环境代理。"
                        : $"已启动并纳入 Managed：{target.Name} (PID {session.RootPid})；manifest EXE 已注入 WebView2 与环境代理，并保留 System Proxy。"
                    : $"已启动并纳入 Managed：{target.Name} (PID {session.RootPid})；该 MSIX 依赖 System Proxy。"
                : launcher.DirectExecutableStarted
                    ? launcher.ChromiumProxyArgumentsApplied
                        ? $"已启动并纳入 Managed：{target.Name} (PID {session.RootPid})；已接管 Electron/Chromium、WebView2 与环境代理。"
                        : $"已启动并纳入 Managed：{target.Name} (PID {session.RootPid})；HTTP(S)/ALL_PROXY 与 WebView2 → 127.0.0.1:{localPort}。"
                    : $"已启动并纳入 Managed：{target.Name} (PID {session.RootPid})；该目标依赖 System Proxy。";
        }
        else
        {
            LastMessage = $"已启动普通进程：{target.Name} (PID {session.RootPid})，未纳入 Managed。";
        }
        return LastMessage;
    }

    public int CloseAllConnections()
    {
        int count = _proxy?.CloseAllConnections() ?? 0;
        LastMessage = count == 0
            ? "当前没有活动连接。"
            : $"已请求关闭全部活动连接：{count} 个。";
        return count;
    }

    /// <summary>
    /// UI operation for "close all": temporarily rejects reconnect attempts, closes the current
    /// sockets, waits for their final log writes, clears the complete log, then reopens the gate
    /// only when the eSIM monitor is not holding the application in fail-closed mode.
    /// </summary>
    public async Task<int> CloseAllConnectionsAndClearLogAsync()
    {
        LocalProxyServer? proxy;
        int count;
        lock (_gate)
        {
            proxy = _proxy;
            _connectionClearOperations++;
            count = proxy?.SetRejectAll(true) ?? 0;
        }

        try
        {
            if (proxy is not null)
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (proxy.ActiveConnections > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(25).ConfigureAwait(false);
            }
            Log.Clear();
        }
        finally
        {
            lock (_gate)
            {
                _connectionClearOperations--;
                if (_connectionClearOperations == 0
                    && !_esimUnavailable
                    && ReferenceEquals(proxy, _proxy))
                    proxy?.SetRejectAll(false);
            }
        }

        lock (_gate)
        {
            LastMessage = _esimUnavailable
                ? OfflineMessage(count)
                : count == 0
                    ? "连接日志已清空；当前没有活动连接。"
                    : $"已关闭 {count} 个活动连接并清空连接日志。";
        }
        return count;
    }

    /// <summary>Starts an explicit, serialized refresh of the official MetaCubeX snapshot.</summary>
    public async Task<bool> RefreshRemoteRulesAsync(CancellationToken cancellationToken = default)
    {
        await _rulesUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateCatalogMessage("正在通过上游代理获取 MetaCubeX/meta-rules-dat catalog…");
            CatalogUpdateResult update = await _remoteCatalogUpdater.FetchLatestAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!update.Succeeded || update.Catalog is null)
            {
                UpdateCatalogMessage("规则更新失败：" + update.Error);
                return false;
            }

            string[] selectedNames;
            lock (_gate)
                selectedNames = _selectedRuleSets.Keys.ToArray();

            MigrationResult migration = await _remoteSnapshotManager.ActivateAsync(
                selectedNames,
                update.Catalog,
                cancellationToken).ConfigureAwait(false);
            if (!migration.Succeeded || migration.Activated is null)
            {
                UpdateCatalogMessage($"catalog 已获取，但当前已选规则无法整体迁移：{migration.Error}");
                return false;
            }

            // Persist the candidate before publishing it in memory. If disk fails, routing keeps
            // its previous active snapshot and the next refresh can retry.
            _ruleCacheStore.SaveCatalog(update.Catalog.Snapshot);
            _ruleCacheStore.PublishActive(update.Catalog.Snapshot, migration.DownloadedBodies);

            lock (_gate)
            {
                Catalog = update.Catalog;
                _catalogDirectory = null;
                _catalogIsRemote = true;
                _selectedRuleSets.Clear();
                foreach ((string name, IReadOnlyList<CompiledDomainRule> rules) in migration.RuleSets)
                    _selectedRuleSets[name] = rules;
                _activeRuleBodies.Clear();
                foreach ((string name, byte[] body) in migration.DownloadedBodies)
                    _activeRuleBodies[name] = body;
                _catalogUpdatedAtUtc = DateTimeOffset.UtcNow;
                Domains.ReplaceSelectedSets(_selectedRuleSets);
                _catalogMessage = $"已更新 MetaCubeX：{Catalog.Count} 个规则，commit {Catalog.Snapshot.CommitSha[..12]}。";
                LastMessage = _catalogMessage;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            UpdateCatalogMessage("规则更新已取消；继续使用当前规则。");
            throw;
        }
        catch (Exception ex)
        {
            UpdateCatalogMessage("规则更新失败：" + ex.Message);
            return false;
        }
        finally
        {
            _rulesUpdateGate.Release();
        }
    }

    /// <summary>
    /// Compatibility entry point for the optional local-directory mode. Production startup no
    /// longer searches the developer's machine; only EGRESS_RULES_ROOT may opt into this path.
    /// </summary>
    public bool RefreshCatalog()
    {
        string? directory = FindRuleDirectory();
        return directory is not null && ImportLocalRules(directory, out _);
    }

    public bool ImportLocalRules(string directory, out string error)
    {
        error = string.Empty;
        try
        {
            string geositeDirectory = ResolveGeositeDirectory(directory);
            LocalCatalogCandidate candidate = BuildLocalCatalog(geositeDirectory);

            _rulesUpdateGate.Wait();
            try
            {
                IReadOnlyDictionary<string, IReadOnlyList<CompiledDomainRule>> selected =
                    ReparseSelectedLocalRules(candidate, out error);
                if (error.Length != 0)
                    return false;

                lock (_gate)
                {
                    Catalog = candidate.Catalog;
                    _catalogDirectory = candidate.Directory;
                    _catalogIsRemote = false;
                    _catalogUpdatedAtUtc = DateTimeOffset.UtcNow;
                    _selectedRuleSets.Clear();
                    foreach ((string name, IReadOnlyList<CompiledDomainRule> rules) in selected)
                        _selectedRuleSets[name] = rules;
                    _activeRuleBodies.Clear();
                    Domains.ReplaceSelectedSets(_selectedRuleSets);
                    _catalogMessage = $"已扫描本地规则：{Catalog.Count} 个规则（内容指纹 {Catalog.Snapshot.CommitSha[6..]}）。";
                    LastMessage = _catalogMessage;
                }
                return true;
            }
            finally
            {
                _rulesUpdateGate.Release();
            }
        }
        catch (Exception ex)
        {
            error = "扫描本地规则失败：" + ex.Message;
            UpdateCatalogMessage(error);
            return false;
        }
    }

    public async Task<(bool Succeeded, string Error)> SetRuleSetAsync(
        string name,
        bool selected,
        CancellationToken cancellationToken = default)
    {
        await _rulesUpdateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuleCatalog? catalog;
            string? localDirectory;
            Dictionary<string, IReadOnlyList<CompiledDomainRule>> current;
            Dictionary<string, byte[]> currentBodies;
            lock (_gate)
            {
                catalog = Catalog;
                localDirectory = _catalogDirectory;
                current = new Dictionary<string, IReadOnlyList<CompiledDomainRule>>(_selectedRuleSets, StringComparer.OrdinalIgnoreCase);
                currentBodies = new Dictionary<string, byte[]>(_activeRuleBodies, StringComparer.OrdinalIgnoreCase);
            }

            if (catalog is null || !catalog.TryGet(name, out RuleCatalogEntry? entry) || entry is null)
                return (false, "规则 catalog 尚未就绪。");

            if (!selected)
            {
                current.Remove(entry.Name);
                currentBodies.Remove(entry.Name);
                if (localDirectory is null)
                {
                    if (!TryPublishRemoteSelection(catalog.Snapshot, currentBodies, out string publishError))
                        return (false, publishError);
                }
                ApplySelectedRules(catalog, localDirectory, current, currentBodies);
                return (true, string.Empty);
            }

            current[entry.Name] = Array.Empty<CompiledDomainRule>();
            if (localDirectory is not null)
            {
                if (!TryReadAndParseLocalRule(localDirectory, entry, out IReadOnlyList<CompiledDomainRule>? rules, out byte[]? body, out string error))
                    return (false, error);
                current[entry.Name] = rules!;
                currentBodies[entry.Name] = body!;
                ApplySelectedRules(catalog, localDirectory, current, currentBodies);
                return (true, string.Empty);
            }

            MigrationResult migration = await _remoteSnapshotManager.ActivateAsync(
                current.Keys.ToArray(),
                catalog,
                cancellationToken).ConfigureAwait(false);
            if (!migration.Succeeded || migration.Activated is null)
                return (false, migration.Error ?? "规则下载失败。");

            if (!TryPublishRemoteSelection(catalog.Snapshot, migration.DownloadedBodies, out string migrationError))
                return (false, migrationError);

            lock (_gate)
            {
                _selectedRuleSets.Clear();
                foreach ((string selectedName, IReadOnlyList<CompiledDomainRule> rules) in migration.RuleSets)
                    _selectedRuleSets[selectedName] = rules;
                _activeRuleBodies.Clear();
                foreach ((string selectedName, byte[] body) in migration.DownloadedBodies)
                    _activeRuleBodies[selectedName] = body;
                Domains.ReplaceSelectedSets(_selectedRuleSets);
                LastMessage = $"规则已{(selected ? "加载" : "移除")}：{entry.Name}。";
            }
            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, "规则操作失败：" + ex.Message);
        }
        finally
        {
            _rulesUpdateGate.Release();
        }
    }

    public bool SetRuleSet(string name, bool selected, out string error)
    {
        (bool succeeded, string message) = SetRuleSetAsync(name, selected).GetAwaiter().GetResult();
        error = message;
        return succeeded;
    }

    private void LoadCachedRules()
    {
        RuleCatalog? cachedCatalog = null;
        if (_ruleCacheStore.TryLoadCatalog(out RuleCatalog? catalog, out string? catalogError) && catalog is not null)
        {
            cachedCatalog = catalog;
            lock (_gate)
            {
                Catalog = catalog;
                _catalogDirectory = null;
                _catalogIsRemote = true;
                _catalogUpdatedAtUtc = DateTimeOffset.UtcNow;
                _catalogMessage = $"已加载本地规则缓存：{catalog.Count} 个规则，commit {catalog.Snapshot.CommitSha[..Math.Min(12, catalog.Snapshot.CommitSha.Length)]}。";
            }
        }
        else if (!string.IsNullOrWhiteSpace(catalogError))
        {
            UpdateCatalogMessage(catalogError);
        }

        if (!_ruleCacheStore.TryLoadActive(out CachedActiveRules? active, out string? activeError)
            || active is null)
        {
            if (!string.IsNullOrWhiteSpace(activeError))
                UpdateCatalogMessage(activeError);
            return;
        }

        if (cachedCatalog is null
            || !string.Equals(active.Manifest.CommitSha, cachedCatalog.Snapshot.CommitSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(active.Manifest.TreeSha, cachedCatalog.Snapshot.TreeSha, StringComparison.OrdinalIgnoreCase))
        {
            UpdateCatalogMessage("规则缓存的 catalog 与活动快照不是同一 commit；已拒绝恢复，等待重新更新。");
            return;
        }

        foreach (string name in active.Manifest.SelectedNames)
        {
            if (!cachedCatalog.TryGet(name, out _))
            {
                UpdateCatalogMessage($"活动规则缓存包含 catalog 中不存在的规则：{name}；已拒绝恢复。");
                return;
            }
        }

        var parsed = new Dictionary<string, IReadOnlyList<CompiledDomainRule>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, byte[] body) in active.Bodies)
        {
            if (!TryParseRuleBody(name, body, out IReadOnlyList<CompiledDomainRule>? rules, out string error))
            {
                UpdateCatalogMessage("活动规则缓存无效：" + error);
                return;
            }
            parsed[name] = rules!;
        }

        lock (_gate)
        {
            _selectedRuleSets.Clear();
            foreach ((string name, IReadOnlyList<CompiledDomainRule> rules) in parsed)
                _selectedRuleSets[name] = rules;
            _activeRuleBodies.Clear();
            foreach ((string name, byte[] body) in active.Bodies)
                _activeRuleBodies[name] = body;
            Domains.ReplaceSelectedSets(_selectedRuleSets);
        }
    }

    private void ApplySelectedRules(
        RuleCatalog catalog,
        string? localDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<CompiledDomainRule>> selected,
        IReadOnlyDictionary<string, byte[]> bodies)
    {
        lock (_gate)
        {
            Catalog = catalog;
            _catalogDirectory = localDirectory;
            _catalogIsRemote = localDirectory is null;
            _selectedRuleSets.Clear();
            foreach ((string name, IReadOnlyList<CompiledDomainRule> rules) in selected)
                _selectedRuleSets[name] = rules;
            _activeRuleBodies.Clear();
            foreach ((string name, byte[] body) in bodies)
                _activeRuleBodies[name] = body;
            Domains.ReplaceSelectedSets(_selectedRuleSets);
        }
    }

    private bool TryPublishRemoteSelection(
        RuleCatalogSnapshot snapshot,
        IReadOnlyDictionary<string, byte[]> bodies,
        out string error)
    {
        try
        {
            _ruleCacheStore.SaveCatalog(snapshot);
            _ruleCacheStore.PublishActive(snapshot, bodies);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "保存规则缓存失败：" + ex.Message;
            return false;
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<CompiledDomainRule>> ReparseSelectedLocalRules(
        LocalCatalogCandidate candidate,
        out string error)
    {
        string[] selectedNames;
        lock (_gate)
            selectedNames = _selectedRuleSets.Keys.ToArray();

        var parsed = new Dictionary<string, IReadOnlyList<CompiledDomainRule>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in selectedNames)
        {
            if (!candidate.Catalog.TryGet(name, out RuleCatalogEntry? entry) || entry is null)
            {
                error = $"新目录缺少当前已选择的规则：{name}。旧规则保持不变。";
                return parsed;
            }
            if (!TryReadAndParseLocalRule(candidate.Directory, entry, out IReadOnlyList<CompiledDomainRule>? rules, out _, out error))
                return parsed;
            parsed[name] = rules!;
        }

        error = string.Empty;
        return parsed;
    }

    private static LocalCatalogCandidate BuildLocalCatalog(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.list", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("目录中没有顶层 *.list 文件。");

        var entries = new List<RuleCatalogEntry>(files.Length);
        foreach (string file in files)
        {
            string fileName = Path.GetFileName(file);
            string name = Path.GetFileNameWithoutExtension(fileName);
            string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            entries.Add(new RuleCatalogEntry(name, "geo/geosite/" + fileName, sha));
        }

        string fingerprintInput = string.Join('\n', entries.Select(entry => entry.Name + "\0" + entry.BlobSha));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant()[..16];
        var catalog = new RuleCatalog(new RuleCatalogSnapshot("local-" + fingerprint, fingerprint, entries));
        return new LocalCatalogCandidate(directory, catalog);
    }

    private static string ResolveGeositeDirectory(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("规则目录不能为空。", nameof(input));

        string full = Path.GetFullPath(input.Trim().Trim('"'));
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);

        if (Directory.EnumerateFiles(full, "*.list", SearchOption.TopDirectoryOnly).Any())
            return full;

        string nested = Path.Combine(full, "geo", "geosite");
        if (Directory.Exists(nested)
            && Directory.EnumerateFiles(nested, "*.list", SearchOption.TopDirectoryOnly).Any())
            return nested;

        if (string.Equals(Path.GetFileName(full), "geo", StringComparison.OrdinalIgnoreCase))
        {
            nested = Path.Combine(full, "geosite");
            if (Directory.Exists(nested)
                && Directory.EnumerateFiles(nested, "*.list", SearchOption.TopDirectoryOnly).Any())
                return nested;
        }

        throw new InvalidDataException("未找到 geo\\geosite 下的顶层 *.list 文件。");
    }

    private static bool TryReadAndParseLocalRule(
        string directory,
        RuleCatalogEntry entry,
        out IReadOnlyList<CompiledDomainRule>? rules,
        out byte[]? body,
        out string error)
    {
        rules = null;
        body = null;
        error = string.Empty;
        try
        {
            string path = Path.Combine(directory, entry.Name + ".list");
            body = File.ReadAllBytes(path);
            string sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            if (!string.Equals(sha, entry.BlobSha, StringComparison.OrdinalIgnoreCase))
            {
                error = $"规则文件在扫描后发生变化：{entry.Name}，请重新扫描。";
                return false;
            }
            if (!TryParseRuleBody(entry.Name, body, out rules, out error))
                return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            error = $"读取规则 {entry.Name} 失败：{ex.Message}";
            return false;
        }
    }

    private static bool TryParseRuleBody(
        string name,
        byte[] body,
        out IReadOnlyList<CompiledDomainRule>? rules,
        out string error)
    {
        rules = null;
        error = string.Empty;
        try
        {
            string text = new UTF8Encoding(false, true).GetString(body);
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text[1..];
            if (!StrictDomainListParser.TryParse(text.Split('\n'), name, out rules, out RuleListParseFailure? failure))
            {
                error = $"{name} 第 {failure!.LineNumber} 行语法不支持：{failure.LineText}";
                return false;
            }
            return true;
        }
        catch (DecoderFallbackException ex)
        {
            error = $"{name} 不是有效 UTF-8：{ex.Message}";
            return false;
        }
    }

    private void UpdateCatalogMessage(string message)
    {
        lock (_gate)
        {
            _catalogMessage = message;
            LastMessage = message;
        }
    }

    private sealed record LocalCatalogCandidate(string Directory, RuleCatalog Catalog);

    public void AddManualDomain(string host)
    {
        if (!StrictDomainListParser.TryCompileSingle(host, out _))
            throw new ArgumentException("不是有效的域名。", nameof(host));
        Domains.AddManual(host);
        LastMessage = "已添加 ESIM 域名：" + host.Trim().ToLowerInvariant();
    }

    public void RemoveManualDomain(string host)
    {
        Domains.RemoveManual(host);
        LastMessage = "已移除 ESIM 域名：" + host.Trim().ToLowerInvariant();
    }

    public bool AcquireSystemProxy()
    {
        if (!Started)
            Start();
        if (!Started)
            return false;

        SystemProxyState current = SystemProxy.Snapshot();
        SystemProxyState ours = SystemProxyState.Ours(LocalPort);
        if (SystemProxy.IsEquivalent(current, ours))
        {
            RoutingEnabled = true;
            _previousProxy ??= _lastProxyRecord?.Previous ?? SystemProxyState.Off;
            PersistProxyState(active: true);
            LastMessage = "System Proxy 已由 EgressController 接管。";
            return true;
        }

        if (current.AutoDetect || !string.IsNullOrWhiteSpace(current.AutoConfigUrl))
        {
            LastMessage = "检测到 PAC/WPAD，V1 不自动覆盖。请先关闭自动代理配置。";
            return false;
        }

        bool upstreamProxy = current.Enabled
            && SystemProxyStateComparer.ServersEquivalent(current.Server, $"http={UpstreamHost}:{UpstreamPort}");
        if (current.Enabled && !upstreamProxy)
        {
            LastMessage = $"检测到其他 System Proxy（{current.Server}），为避免覆盖用户状态，本次拒绝接管。";
            return false;
        }

        _previousProxy = current;
        Guid sessionId = Guid.NewGuid();
        try
        {
            _proxyStateStore.Save(new ProxyStateRecord(sessionId, current, ours, true, DateTimeOffset.UtcNow));
            SystemProxy.Apply(ours);
            if (!SystemProxy.IsEquivalent(SystemProxy.Snapshot(), ours))
                throw new InvalidOperationException("System Proxy read-back 与期望状态不一致。");
            _lastProxyRecord = new ProxyStateRecord(sessionId, current, ours, true, DateTimeOffset.UtcNow);
            RoutingEnabled = true;
            LastMessage = "System Proxy 已接管：HTTP/HTTPS → 127.0.0.1:" + LocalPort;
            return true;
        }
        catch (Exception ex)
        {
            try { SystemProxy.Apply(current); } catch { /* best effort rollback */ }
            try { _proxyStateStore.Save(new ProxyStateRecord(sessionId, current, ours, false, DateTimeOffset.UtcNow)); } catch { }
            LastMessage = "接管 System Proxy 失败：" + ex.Message;
            return false;
        }
    }

    public bool RestoreStaleProxy()
    {
        if (_lastProxyRecord is not { Active: true, Previous: not null, Ours: not null } record
            || !SystemProxy.IsEquivalent(SystemProxy.Snapshot(), record.Ours))
        {
            LastMessage = "当前 System Proxy 已被其他状态取代，不覆盖它。";
            return false;
        }

        try
        {
            SystemProxy.Apply(record.Previous);
            _proxyStateStore.Save(record with { Active = false, TimestampUtc = DateTimeOffset.UtcNow });
            _lastProxyRecord = record with { Active = false };
            LastMessage = "已恢复上一次接管前的 System Proxy。";
            return true;
        }
        catch (Exception ex)
        {
            LastMessage = "恢复 System Proxy 失败：" + ex.Message;
            return false;
        }
    }

    public void StopRouting()
    {
        if (!RoutingEnabled)
            return;

        RoutingEnabled = false;
        SystemProxyState current = SystemProxy.Snapshot();
        if (_previousProxy is not null && SystemProxy.IsEquivalent(current, SystemProxyState.Ours(LocalPort)))
        {
            try { SystemProxy.Apply(_previousProxy); } catch (Exception ex) { LastMessage = "恢复 System Proxy 失败：" + ex.Message; }
        }
        else if (!SystemProxy.IsEquivalent(current, SystemProxyState.Ours(LocalPort)))
        {
            LastMessage = "System Proxy 已被外部修改，退出时未覆盖当前状态。";
        }

        try
        {
            _proxyStateStore.Save(new ProxyStateRecord(
                _lastProxyRecord?.SessionId ?? Guid.Empty,
                _previousProxy,
                SystemProxyState.Ours(LocalPort),
                false,
                DateTimeOffset.UtcNow));
        }
        catch { }
    }

    public string EsimSummary
        => Esim is null
            ? _selectedEsimName is null
                ? "未选择 ESIM 网卡"
                : $"{_selectedEsimName} · 离线 / 未找到"
            : $"{Esim.Identity.NameSnapshot} · {(Esim.IsUp ? "在线" : "离线")} · ifIndex {Esim.IfIndex} · {string.Join(", ", Esim.Addresses.Take(2))}";

    public string RouterSummary
        => Started ? $"127.0.0.1:{(_proxy?.BoundPort ?? LocalPort)}" : "未启动";

    public string SystemProxySummary
    {
        get
        {
            if (RejectingAllConnections)
                return "REJECT · ESIM 离线";
            SystemProxyState state = SystemProxy.Snapshot();
            if (RoutingEnabled && SystemProxy.IsEquivalent(state, SystemProxyState.Ours(LocalPort)))
                return "Owned · 127.0.0.1:" + LocalPort;
            if (HasStaleProxy)
                return "Stale · 上次会话未正常退出";
            if (!state.Enabled)
                return "Off";
            if (state.AutoDetect || !string.IsNullOrWhiteSpace(state.AutoConfigUrl))
                return "Conflict · PAC/WPAD";
            return "External · " + (state.Server ?? "enabled");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rulesLifetimeCts.Cancel();
        if (_rulesBackgroundTask is not null)
        {
            try { await _rulesBackgroundTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { /* rule update failure is already reflected in CatalogMessage */ }
        }
        _rulesLifetimeCts.Dispose();

        StopRouting();
        _reconcileCts?.Cancel();
        if (_reconcileTask is not null)
        {
            try { await _reconcileTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (_esimMonitorTask is not null)
        {
            try { await _esimMonitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _reconcileCts?.Dispose();
        _reconcileCts = null;
        _reconcileTask = null;
        _esimMonitorTask = null;

        _proxyWatcher?.Dispose();
        _proxyWatcher = null;

        if (_proxy is not null)
        {
            await _proxy.DisposeAsync().ConfigureAwait(false);
            _proxy = null;
        }
        foreach (LaunchSession session in Sessions.All())
            RetireSession(session.SessionId);
        Started = false;
        _esimUnavailable = false;
        _remoteFetcher.Dispose();
        _rulesUpdateGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnProxyChanged(SystemProxyState state)
    {
        lock (_gate)
        {
            if (!RoutingEnabled)
                return;
            SystemProxyState ours = SystemProxyState.Ours(LocalPort);
            if (SystemProxy.IsEquivalent(state, ours))
                return;

            RoutingEnabled = false;
            if (_lastProxyRecord is { Active: true } record)
            {
                _lastProxyRecord = record with { Active = false, TimestampUtc = DateTimeOffset.UtcNow };
                try { _proxyStateStore.Save(_lastProxyRecord); } catch { /* state cleanup is best effort */ }
            }
            LastMessage = "检测到外部修改了 System Proxy；已停止声称 Owned，请在概览页检查当前状态。";
        }
    }

    private async Task ReconcileSessionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tree = WindowsProcessTreeSnapshot.Capture();
                foreach (LaunchSession session in Sessions.All())
                {
                    if (ReconcileSession(session, tree))
                        RetireSession(session.SessionId);
                }
            }
            catch
            {
                // A process snapshot is advisory. Accept-time routing remains fail-closed.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task MonitorEsimConnectivityAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<NetworkAdapterInfo> adapters;
                try { adapters = _adapterService.EnumerateAll(); }
                catch { adapters = Array.Empty<NetworkAdapterInfo>(); }

                Guid? monitoredGuid;
                NetworkAdapterInfo? snapshot;
                lock (_gate)
                {
                    Adapters = adapters;
                    monitoredGuid = _selectedEsimGuid;
                    snapshot = monitoredGuid is Guid guid
                        ? adapters.FirstOrDefault(item => item.Identity.Guid == guid)
                        : SelectDefaultEsim(adapters);
                    if (monitoredGuid is null && snapshot is not null)
                    {
                        monitoredGuid = snapshot.Identity.Guid;
                        _selectedEsimGuid = monitoredGuid;
                        _selectedEsimName = snapshot.Identity.NameSnapshot;
                    }
                }

                await ApplyEsimSnapshotAsync(snapshot, monitoredGuid, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Adapter probing is safety-critical but non-fatal. An unreadable selected
                // adapter is treated as unavailable by the next successful/empty snapshot.
            }

            try { await Task.Delay(_esimMonitorInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ApplyEsimSnapshotAsync(
        NetworkAdapterInfo? snapshot,
        Guid? monitoredGuid,
        CancellationToken cancellationToken)
    {
        EsimConnectivityChangedEventArgs? transition = null;
        LocalProxyServer? proxy = null;

        lock (_gate)
        {
            if (!Started || _proxy is null || _selectedEsimGuid != monitoredGuid)
                return;

            proxy = _proxy;
            _selectedEsim = snapshot;
            Esim = snapshot;
            if (snapshot is not null)
            {
                _selectedEsimName = snapshot.Identity.NameSnapshot;
                RouteSource.UpdateAdapter(snapshot);
            }

            bool isOnline = snapshot?.IsUp == true;
            if (isOnline == !_esimUnavailable)
                return;

            _esimUnavailable = !isOnline;
            if (isOnline)
            {
                if (_connectionClearOperations == 0)
                    proxy.SetRejectAll(false);
                LastMessage = $"ESIM 已恢复：{_selectedEsimName}；已解除全局拒绝。";
                transition = NewConnectivityEvent(isOnline: true, closedConnections: 0);
            }
            else
            {
                int closed = proxy.SetRejectAll(true);
                LastMessage = OfflineMessage(closed);
                transition = NewConnectivityEvent(isOnline: false, closedConnections: closed);
            }
        }

        if (transition is null)
            return;

        if (!transition.IsOnline && proxy is not null)
        {
            // The reject gate and socket closes happen synchronously above. Wait briefly for
            // handlers to leave the active set so the UI warning is observably last.
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (proxy.ActiveConnections > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        RaiseEsimConnectivityChanged(transition);
    }

    private EsimConnectivityChangedEventArgs NewConnectivityEvent(bool isOnline, int closedConnections)
        => new()
        {
            IsOnline = isOnline,
            AdapterName = _selectedEsimName ?? "ESIM",
            ClosedConnections = closedConnections,
            DetectedAtUtc = DateTimeOffset.UtcNow,
        };

    private string OfflineMessage(int closedConnections)
        => $"ESIM 已离线：{_selectedEsimName ?? "未找到选定网卡"}；已关闭 {closedConnections} 个连接，并拒绝所有新连接。";

    private void RaiseEsimConnectivityChanged(EsimConnectivityChangedEventArgs args)
    {
        try { EsimConnectivityChanged?.Invoke(this, args); }
        catch
        {
            // A UI notification failure must never reopen the fail-closed data-plane gate.
        }
    }

    private void PersistProxyState(bool active)
    {
        try
        {
            _proxyStateStore.Save(new ProxyStateRecord(
                _lastProxyRecord?.SessionId ?? Guid.NewGuid(),
                _previousProxy,
                SystemProxyState.Ours(LocalPort),
                active,
                DateTimeOffset.UtcNow));
        }
        catch { }
    }

    private void AttachRootWatcher(LaunchSession session)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(checked((int)session.RootPid));
            EventHandler handler = (_, _) => OnRootProcessExited(session.SessionId);
            process.Exited += handler;
            lock (_gate)
                _sessionRootProcesses[session.SessionId] = (process, handler);
            process.EnableRaisingEvents = true;

            if (process.HasExited)
                OnRootProcessExited(session.SessionId);
        }
        catch
        {
            // The process may have exited between Start and GetProcessById. Mark it ended and
            // let reconciliation decide whether verified children still keep the session alive.
            OnRootProcessExited(session.SessionId);
        }
    }

    private void OnRootProcessExited(Guid sessionId)
    {
        Sessions.MarkRootExited(sessionId);
        ReleaseRootWatcher(sessionId);
        try
        {
            WindowsProcessTreeSnapshot tree = WindowsProcessTreeSnapshot.Capture();
            LaunchSession? session = Sessions.Get(sessionId);
            if (session is not null && ReconcileSession(session, tree))
                RetireSession(sessionId);
        }
        catch
        {
            // The periodic reconciler remains the fallback when a process snapshot races exit.
        }
    }

    private bool ReconcileSession(LaunchSession session, WindowsProcessTreeSnapshot tree)
    {
        LaunchTarget? target = Targets.Get(session.TargetId);
        if (target is null)
            return true;

        IReadOnlySet<uint> baselineCandidates = session.CandidatePids.ToHashSet();
        IReadOnlyDictionary<uint, DateTime> baselineOwned =
            new Dictionary<uint, DateTime>(session.ActiveOwnedProcessStartTimes);

        ProcessIdentity? rootIdentity = _processIdentity.Resolve(session.RootPid);
        bool rootAlive = rootIdentity?.StartTimeUtc == session.RootStartTimeUtc;
        if (!rootAlive)
            Sessions.MarkRootExited(session.SessionId);

        var candidates = new HashSet<uint>();
        if (rootAlive)
            candidates.UnionWith(tree.DescendantsOf(session.RootPid));
        candidates.UnionWith(SnapshotSessionJob(session.SessionId));

        // A verified member is also a lineage anchor. This preserves descendants when Windows
        // reparents them after the original root exits, while start-time checks prevent PID reuse.
        foreach ((uint pid, DateTime started) in baselineOwned)
        {
            ProcessIdentity? anchor = _processIdentity.Resolve(pid);
            if (anchor?.StartTimeUtc == started)
                candidates.UnionWith(tree.DescendantsOf(pid));
        }

        var owned = new Dictionary<uint, DateTime>();
        if (rootAlive)
        {
            candidates.Add(session.RootPid);
            owned[session.RootPid] = session.RootStartTimeUtc;
        }
        foreach (uint pid in candidates)
        {
            if (pid == session.RootPid)
                continue;
            ProcessIdentity? identity = _processIdentity.Resolve(pid);
            if (identity?.ExePathFinal is not null
                && identity.StartTimeUtc >= session.RootStartTimeUtc
                && OwnedRootMatcher.IsScannedExecutable(identity.ExePathFinal, target))
                owned[pid] = identity.StartTimeUtc;
        }
        Sessions.ReconcileMembership(
            session.SessionId,
            baselineCandidates,
            baselineOwned,
            candidates,
            owned);

        LaunchSession? current = Sessions.Get(session.SessionId);
        return current is { RootExited: true, ActiveOwnedPids.Count: 0 };
    }

    private void RetireSession(Guid sessionId)
    {
        Sessions.Unregister(sessionId);
        ReleaseRootWatcher(sessionId);
        ReleaseSessionJob(sessionId);
    }

    private void RegisterSessionJob(Guid sessionId, WindowsProcessJob job)
    {
        WindowsProcessJob? previous = null;
        lock (_gate)
        {
            if (_sessionJobs.Remove(sessionId, out WindowsProcessJob? existing))
                previous = existing;
            _sessionJobs[sessionId] = job;
        }
        previous?.Dispose();
    }

    private IReadOnlySet<uint> SnapshotSessionJob(Guid sessionId)
    {
        WindowsProcessJob? job;
        lock (_gate)
            _sessionJobs.TryGetValue(sessionId, out job);
        if (job is null)
            return new HashSet<uint>();
        try { return job.SnapshotProcessIds(); }
        catch { return new HashSet<uint>(); }
    }

    private bool IsSessionJobMember(Guid sessionId, uint processId)
        => SnapshotSessionJob(sessionId).Contains(processId);

    private void ReleaseSessionJob(Guid sessionId)
    {
        WindowsProcessJob? job = null;
        lock (_gate)
        {
            if (_sessionJobs.Remove(sessionId, out WindowsProcessJob? removed))
                job = removed;
        }
        job?.Dispose();
    }

    private void ReleaseRootWatcher(Guid sessionId)
    {
        (System.Diagnostics.Process Process, EventHandler Handler)? watcher = null;
        lock (_gate)
        {
            if (_sessionRootProcesses.Remove(sessionId, out var removed))
                watcher = removed;
        }
        if (watcher is { } value)
        {
            try { value.Process.Exited -= value.Handler; } catch { }
            try { value.Process.Dispose(); } catch { }
        }
    }

    private static NetworkAdapterInfo? SelectDefaultEsim(IReadOnlyList<NetworkAdapterInfo> adapters)
        => adapters.FirstOrDefault(a => a.Identity.NameSnapshot.Contains("ESIM", StringComparison.OrdinalIgnoreCase))
           ?? adapters.FirstOrDefault(a => a.Identity.NameSnapshot.Contains("热点", StringComparison.OrdinalIgnoreCase));

    private static string? FindRuleDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("EGRESS_RULES_ROOT");
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        try
        {
            return ResolveGeositeDirectory(configured);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Accept-time PID → ProcessIdentity → managed-session resolver.</summary>
internal sealed class ManagedConnectionSourceResolver : IProxySourceResolver
{
    private readonly IConnectionOwnerResolver _owner;
    private readonly IProcessIdentityResolver _identity;
    private readonly LaunchSessionRegistry _sessions;
    private readonly LaunchTargetRegistry _targets;
    private readonly Func<uint, uint, bool> _isCurrentDescendant;
    private readonly Func<Guid, uint, bool> _isSessionJobMember;

    public ManagedConnectionSourceResolver(
        IConnectionOwnerResolver owner,
        IProcessIdentityResolver identity,
        LaunchSessionRegistry sessions,
        LaunchTargetRegistry targets,
        Func<uint, uint, bool>? isCurrentDescendant = null,
        Func<Guid, uint, bool>? isSessionJobMember = null)
    {
        _owner = owner;
        _identity = identity;
        _sessions = sessions;
        _targets = targets;
        _isCurrentDescendant = isCurrentDescendant ?? IsCurrentDescendant;
        _isSessionJobMember = isSessionJobMember ?? ((_, _) => false);
    }

    public ProxySource? Resolve(System.Net.IPEndPoint clientLocal, System.Net.IPEndPoint listenerLocal, CancellationToken cancellationToken)
    {
        uint? pid = _owner.ResolveOwner(clientLocal, listenerLocal, cancellationToken);
        if (pid is null)
            return null;

        ProcessIdentity? process = _identity.Resolve(pid.Value);
        if (process is null)
            return new ProxySource(pid, string.Empty, null, null, null);

        foreach (LaunchSession session in _sessions.All())
        {
            LaunchTarget? target = _targets.Get(session.TargetId);
            if (target is null)
                continue;

            bool isRoot = session.RootPid == process.Pid
                && session.RootStartTimeUtc == process.StartTimeUtc;
            bool isOwnedComponent = session.ActiveOwnedPids.Contains(process.Pid)
                && session.CandidatePids.Contains(process.Pid)
                && session.ActiveOwnedProcessStartTimes.TryGetValue(process.Pid, out DateTime ownedStarted)
                && ownedStarted == process.StartTimeUtc
                && process.ExePathFinal is not null
                && OwnedRootMatcher.IsScannedExecutable(process.ExePathFinal, target);
            if (!isRoot
                && !isOwnedComponent
                && process.ExePathFinal is not null
                && process.StartTimeUtc >= session.RootStartTimeUtc
                && OwnedRootMatcher.IsScannedExecutable(process.ExePathFinal, target))
            {
                bool verifiedCandidate = _isSessionJobMember(session.SessionId, process.Pid);
                if (!verifiedCandidate)
                {
                    // Do not trust a raw parent PID after reuse. Any ancestry anchor must still
                    // have the exact start-time identity recorded for this launch session.
                    foreach ((uint anchorPid, DateTime anchorStarted) in session.ActiveOwnedProcessStartTimes)
                    {
                        if (anchorPid == process.Pid)
                            continue;
                        ProcessIdentity? anchor = _identity.Resolve(anchorPid);
                        if (anchor?.StartTimeUtc == anchorStarted
                            && _isCurrentDescendant(anchorPid, process.Pid))
                        {
                            verifiedCandidate = true;
                            break;
                        }
                    }
                }
                if (verifiedCandidate)
                    isOwnedComponent = _sessions.TrackOwnedProcess(session.SessionId, process);
            }
            if (isRoot || isOwnedComponent)
            {
                return new ProxySource(
                    process.Pid,
                    Path.GetFileName(process.ExePathFinal ?? process.ExePathObserved),
                    process.ExePathFinal,
                    session.SessionId.ToString("D"),
                    target.Name);
            }
        }

        return new ProxySource(
            process.Pid,
            Path.GetFileName(process.ExePathFinal ?? process.ExePathObserved),
            process.ExePathFinal,
            null,
            null);
    }

    private static bool IsCurrentDescendant(uint rootPid, uint processPid)
        => WindowsProcessTreeSnapshot.Capture().DescendantsOf(rootPid).Contains(processPid);
}

/// <summary>Bridges a RoutingEngine decision to a concrete ESIM/upstream connect target.</summary>
public sealed class ComposedRouteSource(
    RoutingEngine engine,
    NetworkAdapterInfo? esim,
    IEsimEgressConnector esimConnector,
    IUpstreamProxyConnector upstream) : IProxyRouteSource
{
    private NetworkAdapterInfo? _esim = esim;
    private readonly IConnectTarget _upstream = new UpstreamConnectTarget(upstream);

    public void UpdateAdapter(NetworkAdapterInfo adapter)
        => _esim = adapter;

    public ProxyRoute? Resolve(string host, int port, ProxySource? source = null)
    {
        RouteDecision decision = engine.Decide(host, source?.SessionId);
        if (decision.Egress != Egress.Esim)
            return new ProxyRoute(decision, _upstream);

        IConnectTarget target = _esim is null
            ? new DeadTarget()
            : new EsimConnectTarget(esimConnector, _esim);
        return new ProxyRoute(decision, target);
    }

    private sealed class EsimConnectTarget(IEsimEgressConnector esim, NetworkAdapterInfo adapter) : IConnectTarget
    {
        public string Description => $"ESIM:{adapter.Identity.NameSnapshot}";
        public ValueTask<Stream> ConnectTunnelAsync(string host, int port, CancellationToken ct) => esim.ConnectAsync(host, port, adapter, ct);
        public ValueTask<Stream> OpenNextHopAsync(string host, int port, CancellationToken ct) => esim.ConnectAsync(host, port, adapter, ct);
    }

    private sealed class UpstreamConnectTarget(IUpstreamProxyConnector upstream) : IConnectTarget
    {
        public string Description => $"upstream {upstream.Endpoint}";
        public ValueTask<Stream> ConnectTunnelAsync(string host, int port, CancellationToken ct) => upstream.ConnectTunnelAsync(host, port, ct);
        public ValueTask<Stream> OpenNextHopAsync(string host, int port, CancellationToken ct) => upstream.OpenNextHopAsync(ct);
    }

    private sealed class DeadTarget : IConnectTarget
    {
        public string Description => "ESIM(no adapter)";
        public ValueTask<Stream> ConnectTunnelAsync(string host, int port, CancellationToken ct)
            => throw new EsimConnectException(host, port, new IOException("no ESIM adapter"));
        public ValueTask<Stream> OpenNextHopAsync(string host, int port, CancellationToken ct)
            => throw new IOException("no ESIM adapter");
    }
}
