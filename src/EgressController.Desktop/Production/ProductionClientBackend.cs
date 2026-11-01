using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using EgressController.Desktop.Services;
using EgressController.Mac;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Desktop.Production;

/// <summary>Real service composition. Constructed only after default-production argument dispatch, never by a preview.</summary>
public sealed class ProductionClientBackend : IClientBackend
{
    private readonly LiveOperationGate _gate;
    private readonly NativeMacPlatform _platform;
    private readonly NativeMacPrivileges _native;
    private readonly MacApplicationCatalog _catalog;
    private readonly MacIconCache _icons;
    private readonly MacHelperClient _helper;
    private readonly IValidatedResourceCache _cache;
    private readonly DraftPolicyStore _drafts;
    private readonly WorkspaceResetMaintenance _resetMaintenance;
    private bool _resetCleanupPending;
    private bool ResetCleanupPending => _resetCleanupPending || _resetMaintenance.Pending;
    private readonly ConfigurationCoordinator _coordinator;
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly string _root, _staging;
    private readonly ILifecycleLog _log;
    private readonly ILifecycleLog _networkLog;
    private readonly OperationDiagnostics _diagnostics;
    private ConnectionAttemptStage? _lastLoggedStage;
    private string? _lastRuntimeObservation;
    public void RecordEvent(string kind, string detail) => _diagnostics.Record(kind, detail);
    public void RecordFailure(string operation, Exception error) => _diagnostics.Failure(operation, error);
    private MacRuntimeLaunch? _launch;
    private bool _disposed;
    private bool _recoveryRequired;
    private int _savedResourcePort = 17890;
    private readonly ConnectionObservationMapper _observations = new();
    private IProgress<ClientOperationProgress>? _progress;
    private ConnectionAttemptStage _preparationStage = ConnectionAttemptStage.Resources;
    public bool IsPreview => false;
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public string LogDirectory { get; }
    public void SetProgressSink(IProgress<ClientOperationProgress>? progress) => _progress = progress;
    private void Report(ConnectionAttemptStage stage, string detail)
    {
        if (_lastLoggedStage != stage) { _lastLoggedStage = stage; _diagnostics.Record("connection.stage", stage + " · " + detail); }
        try { _progress?.Report(new(stage, detail)); } catch (Exception) { }
    }
    private IProgress<PreparationProgress> ResourceProgress() => new InlineProgress<PreparationProgress>(item =>
        Report(_preparationStage, ResourceLabel(item.ResourceId) + " · " + item.Message));
    private static string ResourceLabel(string id) => id.StartsWith("meta-raw-geosite:cn", StringComparison.Ordinal) ? "中国域名规则"
        : id.StartsWith("meta-raw-geoip:cn", StringComparison.Ordinal) ? "中国 IP 规则"
        : id.StartsWith("xray", StringComparison.Ordinal) ? "Xray 内核"
        : id.StartsWith("subscription", StringComparison.Ordinal) || id.StartsWith("provider", StringComparison.Ordinal) ? "节点订阅"
        : "规则目录与资源";
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    public ProductionClientBackend()
    {
        _gate = LiveOperationGate.ForExplicitLiveUse();
        _platform = new(_gate); _native = new(_gate);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _root = Path.Combine(home, "Library", "Application Support", "EgressController");
        _staging = Path.Combine(_root, "helper-staging");
        LogDirectory = Path.Combine(home, "Library", "Logs", "EgressController");
        _networkLog = new PrivateLifecycleFileLog(LogDirectory, 512 * 1024, "network-diagnostics.log");
        _log = new PrivateLifecycleFileLog(LogDirectory); _diagnostics = new(_log);
        _diagnostics.Record("client.start", $"version={typeof(ProductionClientBackend).Assembly.GetName().Version}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        _catalog = new(new PhysicalMacCatalogFileSystem(_gate, _platform), _platform, MacApplicationCatalog.StandardRoots(home));
        _icons = new(_platform); _helper = new(_gate, log: _log); _cache = new PrivateDiskResourceCache(Path.Combine(_root, "resource-cache"));
        _drafts = new(Path.Combine(_root, "configuration")); _resetMaintenance = new(_root);
        var runtime = new MacXrayCoreRuntime(_helper, _staging, _ => Task.FromResult(_launch ?? throw new InvalidOperationException("No validated runtime launch is prepared.")));
        _coordinator = new(runtime, new AtomicGenerationStore(Path.Combine(_root, "runtime")), _log);
    }

    public async Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default)
    {
        _gate.Require("load production workspace");
        var document = await _drafts.LoadAsync(cancellationToken).ConfigureAwait(false);
        _savedResourcePort = document?.Defaults.ResourceProxyPort ?? 17890;
        MacNetworkInterface[] interfaces = []; IReadOnlyList<ClientApplication> applications = [];
        var failures = new List<string>();
        try { interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { _diagnostics.Failure("workspace.interfaces", error); failures.Add("无法读取网卡信息，请重新载入。已保存的配置保留。"); }
        try { applications = await DiscoverApplicationsAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { _diagnostics.Failure("workspace.applications", error); failures.Add("无法读取应用目录，请重新载入。已保存的应用清单保留。"); }
        return new(document ?? new(), interfaces.Where(Physical).Select(item => new ClientInterface(item.ServiceId, item.BsdName,
            item.DisplayName, item.IsActive ? item.BsdName + " · 已连接" : item.BsdName + " · 未连接", item.IsActive)).ToArray(),
            applications, document is not null, await ReadStatusAsync(cancellationToken).ConfigureAwait(false), failures.Count == 0 ? null : string.Join("\n", failures));
    }

    public async Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default)
    {
        var found = await _catalog.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return found.Applications.Select(MapApplication).OrderByDescending(item => item.Product is "openai" or "claude" or "cursor" or "codex")
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public async Task<ClientApplication> BuildInventoryAsync(ClientApplication application, IReadOnlyList<string> savedPaths, CancellationToken cancellationToken = default)
    {
        _gate.Require("scan explicitly selected application");
        var metadata = await Task.Run(() => _platform.ReadBundle(application.BundlePath), cancellationToken).ConfigureAwait(false);
        string canonical = await Task.Run(() => _platform.Canonicalize(metadata.BundlePath), cancellationToken).ConfigureAwait(false);
        string? primary = metadata.PrimaryExecutablePath is { } executable
            ? await Task.Run(() => _platform.Canonicalize(executable), cancellationToken).ConfigureAwait(false) : null;
        var model = new MacApplication(application.Id, metadata.BundleIdentifier, metadata.DisplayName, canonical, primary, [], false, []);
        var inventory = await _catalog.BuildInventoryAsync(model, savedPaths, cancellationToken).ConfigureAwait(false);
        return application with { BundlePath = canonical, BundleIdentifier = metadata.BundleIdentifier, PrimaryExecutable = primary,
            ExecutablePaths = inventory.ExecutablePaths, InventoryComplete = inventory.IsComplete,
            InventoryError = inventory.IsComplete ? null : "部分目录无法读取或超出安全扫描范围；原有固定路径已保留。" };
    }
    public async Task<byte[]?> LoadIconAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        try { return await _icons.GetPngAsync(bundlePath, 96, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public async Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        var catalog = await Resources(_savedResourcePort).LoadRuleCatalogAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return catalog.Entries.GroupBy(item => item.Name, StringComparer.Ordinal).Select(group =>
            new ClientRuleCatalogEntry(SafeId(group.Key), group.Key, group.Any(item => item.Kind == RuleDataKind.Ip) && group.Any(item => item.Kind != RuleDataKind.Ip)
                ? "域名 + IP 集合" : group.First().Kind == RuleDataKind.Ip ? "IP/CIDR 集合" : "域名集合",
                group.Select(item => item.Id.Replace(':', '/') + ".yaml").ToArray())).ToArray();
    }
    public async Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument current, CancellationToken cancellationToken = default)
    {
        string sourceId = current.Subscriptions.FirstOrDefault(item => item.Url == url.AbsoluteUri)?.Id
            ?? (current.Subscriptions.Count == 1 ? current.Subscriptions[0].Id : "subscription-" + Digest(url.AbsoluteUri)[..24]);
        var source = new SubscriptionSource { Id = sourceId, Name = "节点订阅", Url = url.AbsoluteUri };
        // Capture subscription-only direct egress before awaiting. No helper/core/port readiness is consulted.
        string interfaceId = current.Interfaces.NonHomeId;
        var interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false);
        var selected = interfaces.SingleOrDefault(item => item.ServiceId == interfaceId && item.IsActive && Physical(item))
            ?? throw new RuntimeFailureException("subscription.interface");
        if (!DnsEndpoint.IsValid(current.Dns.HttpsUrl))
            throw new RuntimeFailureException("subscription.dns");
        var binder = new MacPhysicalSocketBinder(_native);
        var imported = await new DirectSubscriptionResources(binder, new BoundDohResolver(binder, current.Dns.HttpsUrl), _cache,
            new(interfaceId, selected.BsdName), ResourceProgress()).FetchAsync(source, cancellationToken).ConfigureAwait(false);
        if (!imported.IsSuccess) throw new InvalidDataException("订阅结构无效，保留原有节点与选择。");
        var merged = SubscriptionReconciler.Merge(current, sourceId, imported);
        merged.Subscriptions.RemoveAll(item => item.Id == sourceId); merged.Subscriptions.Add(source); return merged;
    }
    public async Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        _preparationStage = ConnectionAttemptStage.Resources;
        Report(ConnectionAttemptStage.Resources, "检查已验证缓存，通过已启动的 Xray 非家宽分组节点准备缺失资源。");
        var result = await Resources(document.Defaults.ResourceProxyPort).PrepareRulesAsync(document.RuleSets.Where(item => item.Enabled).SelectMany(item => item.Sources), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(result.Bindings, result.Assets, result.Sources);
    }
    public async Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding, CancellationToken cancellationToken = default)
    {
        await CheckInterfaceIdentityAsync(binding, cancellationToken).ConfigureAwait(false);
        var core = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
        var interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false);
        var options = new XrayCompileOptions { Interfaces = interfaces.Where(Physical).Select(item => new InterfaceBinding(item.ServiceId, item.BsdName)).ToList() };
        return await new ObservatoryProbes(new XrayObservatoryProcess(() => _gate.Require("run isolated outbound probe without TUN"), Path.Combine(_root, "probes")), _networkLog)
            .ProbeAsync(document, options, core.ExecutablePath, cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<GroupProbe>> ProbeMembersAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyList<RequiredGroupProbe> requests, CancellationToken cancellationToken = default)
    {
        await CheckInterfaceIdentityAsync(options.Interfaces.Single(item => item.StableId == document.Interfaces.NonHomeId), cancellationToken).ConfigureAwait(false);
        var core = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
        return await new ObservatoryProbes(new XrayObservatoryProcess(() => _gate.Require("probe explicit group members without TUN"), Path.Combine(_root, "probes")), _networkLog)
            .ProbeAsync(document, options, core.ExecutablePath, cancellationToken, requested: requests).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<GroupProbe>> ProbeMembersWithProgressAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyList<RequiredGroupProbe> requests, IProgress<GroupProbe> progress, CancellationToken cancellationToken = default)
    {
        await CheckInterfaceIdentityAsync(options.Interfaces.Single(item => item.StableId == document.Interfaces.NonHomeId), cancellationToken).ConfigureAwait(false);
        var core = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => new ObservatoryProbes(new XrayObservatoryProcess(() => _gate.Require("probe explicit group members without TUN"), Path.Combine(_root, "probes")), _networkLog)
            .ProbeAsync(document, options, core.ExecutablePath, cancellationToken, requested: requests, progress: progress), cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<GroupProbe>> PrepareRequiredProbesAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
    {
        if (!RequiredGroupProbes.NeedsProbes(document, options)) return options.Probes.ToArray();
        var binding = options.Interfaces.Single(item => item.StableId == document.Interfaces.NonHomeId);
        await CheckInterfaceIdentityAsync(binding, cancellationToken).ConfigureAwait(false);
        _preparationStage = ConnectionAttemptStage.Resources;
        Report(ConnectionAttemptStage.Resources, "TUN 已建立，正在检查所选自动分组；出口绑定失败只影响该连接，不会自动换卡。");
        var core = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
        return await new ObservatoryProbes(new XrayObservatoryProcess(() => _gate.Require("probe required outbound groups without TUN"), Path.Combine(_root, "probes")), _networkLog)
            .ProbeAsync(document, options, core.ExecutablePath, cancellationToken, requiredOnly: true).ConfigureAwait(false);
    }
    public async Task BeginConnectionAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
    {
        _lastLoggedStage = null;
        _diagnostics.Record("connection.begin", $"explicit user request; resource-port={document.Defaults.ResourceProxyPort}");
        Report(ConnectionAttemptStage.Core, "验证内置 Xray；不需要下载内核。");
        var core = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
        // TUN creation does not depend on physical link/IP availability. The helper verifies
        // configured service identities; Xray binds each outbound only when it opens a socket.
        _launch = new(core.ExecutablePath, core.Sha256, document.Interfaces.HomeId, document.Interfaces.NonHomeId);
        await EnsureHelperAsync(core, cancellationToken).ConfigureAwait(false);
        if (ResetCleanupPending && !await FinishResetCleanupAsync(cancellationToken).ConfigureAwait(false)) throw new RuntimeFailureException("recovery.failed");
        var current = await _helper.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (MacRuntimeReadiness.RequiresRecovery(current, _coordinator.Applied is not null)
            && !await _coordinator.StopAsync("用户重试连接：助手已确认核心或 TUN 不可用", cancellationToken).ConfigureAwait(false))
            throw new RuntimeFailureException("recovery.failed");
        Report(ConnectionAttemptStage.Bootstrap, $"建立 TUN 与 Xray SOCKS5 127.0.0.1:{document.Defaults.ResourceProxyPort}；规则未就绪前暂停普通流量。");
        _recoveryRequired = true;
        await _coordinator.BeginResourceBootstrapAsync(document, options, cancellationToken).ConfigureAwait(false);
    }
    public Task PrepareResourceProxyAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
        => _coordinator.CompleteResourceBootstrapAsync(document, options, cancellationToken);
    public async Task<ClientConnectionResources> PrepareConnectionResourcesAsync(MacPolicyDocument document, XrayCompileOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = await Resources(document.Defaults.ResourceProxyPort).PrepareRulesAsync(document.RuleSets.Where(rule => rule.Enabled).SelectMany(rule => rule.Sources),
                PreparationCachePolicy.CacheOnly, cancellationToken).ConfigureAwait(false);
            return new(new(cached.Bindings, cached.Assets, cached.Sources), options.Probes);
        }
        catch (ResourceCacheMissException) { /* Probe only when an actual download is needed. */ }
        var group = document.Groups.Single(group => group.Id == document.Defaults.Global.TargetId);
        var requests = GroupGraph.Leaves(group.Id, document).Where(id => document.Nodes.Any(node => node.Id == id && node.IsSupported))
            .Select(id => new RequiredGroupProbe(id, group.TestUrl)).ToArray();
        var observed = await ProbeMembersAsync(document, options, requests, cancellationToken).ConfigureAwait(false);
        options.Probes = options.Probes.Where(old => !observed.Any(probe => probe.NodeId == old.NodeId && probe.TestUrl == old.TestUrl)).Concat(observed).ToList();
        options.Now = Now;
        options.ResourceNodeId = GroupGraph.ResourceNode(document, options.Probes, options.Now) ?? throw new RuntimeFailureException("resources.no-proxy");
        await PrepareResourceProxyAsync(document, options, cancellationToken).ConfigureAwait(false);
        return new(await PrepareResourcesAsync(document, cancellationToken).ConfigureAwait(false), options.Probes);
    }
    public async Task EndConnectionAsync(bool applied)
    {
        try { await _coordinator.EndResourceBootstrapAsync(applied).ConfigureAwait(false); _diagnostics.Record("connection.end", $"applied={applied}; preparation/recovery finished"); }
        catch (Exception error) { _diagnostics.Failure("connection.recovery", error); throw; }
    }
    public async Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default)
    {
        await _apply.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = PolicyYaml.Read(PolicyYaml.Write(document)).Document ?? throw new InvalidDataException("策略结构无效。");
            Report(ConnectionAttemptStage.Validating, "检查策略、固定进程清单和生成的规则文件。");
            var compiled = XrayConfigCompiler.Compile(snapshot, options);
            if (!compiled.IsSuccess) return new(ApplyDisposition.Rejected, null, compiled.Diagnostics);
            var diagnostics = RuntimeAssetValidator.Validate(compiled.Json!, assets);
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return new(ApplyDisposition.Rejected, null, diagnostics);
            _preparationStage = ConnectionAttemptStage.Core;
            Report(ConnectionAttemptStage.Core, "验证安装包内置 Xray 与本地核心缓存。");
            var core = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
            _launch = new(core.ExecutablePath, core.Sha256, snapshot.Interfaces.HomeId, snapshot.Interfaces.NonHomeId);
            await EnsureHelperAsync(core, cancellationToken).ConfigureAwait(false);
            if (ResetCleanupPending && !await FinishResetCleanupAsync(cancellationToken).ConfigureAwait(false)) throw new RuntimeFailureException("recovery.failed");
            await _drafts.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            Report(ConnectionAttemptStage.Connecting, "校验助手配置，等待 Xray、实际 TUN 路由和 DNS 接管确认。");
            _recoveryRequired = true;
            var result = await _coordinator.ApplyAsync(snapshot, options, assets, "用户应用生产策略", cancellationToken).ConfigureAwait(false);
            if (result.Succeeded) _savedResourcePort = snapshot.Defaults.ResourceProxyPort;
            if (result.Succeeded && result.GenerationId is { } appliedGeneration)
            { try { _observations.RememberApplied(appliedGeneration, snapshot); } catch (Exception) { /* Observation labels cannot change a successful core apply. */ } }
            if (result.Disposition is ApplyDisposition.Applied or ApplyDisposition.Unchanged)
                Report(ConnectionAttemptStage.Ready, "运行配置已应用，网络接管状态已确认。");
            return result;
        }
        finally { _apply.Release(); }
    }
    public async Task<ApplyResult> UpdateGroupsAsync(MacPolicyDocument document, XrayCompileOptions options,
        IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default)
    {
        await _apply.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_coordinator.Applied is null) return new(ApplyDisposition.Rejected, null, [new("routing.stopped", "连接已停止，分组选择保留在草稿中。")]);
            var result = await _coordinator.ApplyAsync(document, options, assets, "分组成员或自动选择策略更新", cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && result.GenerationId is { } generation) _observations.RememberApplied(generation, document);
            return result;
        }
        finally { _apply.Release(); }
    }
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        bool restored = await _coordinator.StopAsync("用户停止网络接管", cancellationToken).ConfigureAwait(false);
        if (restored && ResetCleanupPending) restored = await FinishResetCleanupAsync(cancellationToken).ConfigureAwait(false);
        if (restored) _recoveryRequired = false;
        return restored;
    }
    public async Task<InterfaceConnectivityResult> CheckHomeConnectivityAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        var interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false);
        var home = interfaces.SingleOrDefault(item => Physical(item) && item.ServiceId == document.Interfaces.HomeId);
        var binding = new InterfaceBinding(document.Interfaces.HomeId, home?.BsdName ?? "");
        _diagnostics.Record("home.check.begin", $"service={binding.StableId}; interface={binding.BsdName}; active={home?.IsActive == true}; addresses={home?.Addresses.Length ?? 0}; direct=true; tun=false");
        string? failure = home is null ? "interface.bind" : InterfaceConnectivity.ReadinessFailure(home.IsActive, home.Addresses);
        InterfaceConnectivityResult result;
        try
        {
            var binder = new MacPhysicalSocketBinder(_native);
            result = failure is not null ? new(false, binding.StableId, binding.BsdName, "", failure, 0)
                : await new InterfaceConnectivity(binder, new BoundDohResolver(binder, document.Dns.HttpsUrl)).CheckAsync(binding, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException)
        {
            _diagnostics.Record("home.check.end", $"service={binding.StableId}; interface={binding.BsdName}; available=False; code={(error is OperationCanceledException ? "cancelled" : NetworkFailure.Code(error))}");
            throw;
        }
        _diagnostics.Record("home.check.end", $"service={result.InterfaceId}; interface={result.BsdName}; endpoint={result.Endpoint}; available={result.Available}; code={result.FailureCode}; ms={result.Milliseconds:F0}");
        return result;
    }
    public async Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (ResetCleanupPending) return new(false, false, _helper.Status.Installed, "旧配置清理尚未完成。修复助手并恢复网络后将完成后台清理。");
            if (!File.Exists(MacHelperEndpoint.Installed.ExpectedHelperPath))
                return _recoveryRequired || _coordinator.Applied is not null
                    ? new(false, false, false, "后台助手丢失，原有接管恢复尚未确认。请修复助手并恢复网络。")
                    : new(false, true, false, "首次连接时请求安装特权助手；当前未接管系统网络。");
            var state = await _helper.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (MacRuntimeReadiness.RequiresExitRecovery(false, state)) _recoveryRequired = true;
            bool healthy = state.Installed && state.Running && state.TunTakenOver && state.Failure is null;
            bool restored = state.Installed && !state.Running && !state.TunTakenOver && state.DnsRestored && state.Failure is null;
            string observation = $"installed={state.Installed}; running={state.Running}; tun={state.TunTakenOver}; dns-restored={state.DnsRestored}; failure={state.Failure}";
            if (_lastRuntimeObservation != observation) { _lastRuntimeObservation = observation; _diagnostics.Record("helper.state", observation); }
            return new(healthy, healthy || restored, state.Installed,
                healthy ? "Xray 与双栈 TUN 接管已确认。" : restored ? "已停止接管，系统 DNS 已恢复。"
                    : "后台助手或网络恢复尚未确认，可以修复助手后重试；本地设置仍可重置。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        { return new(false, false, false, "后台助手不可用，无法确认当前网络状态。可以修复助手后重试。"); }
    }
    public async Task<ConfigurationResetResult> ResetConfigurationAsync(MacPolicyDocument current, CancellationToken cancellationToken = default)
    {
        await _apply.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await ConfigurationReset.ExecuteAsync(current, token =>
                !_recoveryRequired && _coordinator.Applied is null && !File.Exists(MacHelperEndpoint.Installed.ExpectedHelperPath)
                    ? Task.FromResult(true)
                    : !_helper.Status.Installed ? Task.FromResult(false)
                    : _coordinator.StopAsync("用户确认重置配置：停止接管并恢复网络", token), async (snapshot, token) =>
                    {
                        await _drafts.ResetAsync(snapshot, token).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
            // The empty draft is committed. Cleanup failures must not make the UI resurrect the old draft.
            _resetCleanupPending = true;
            try
            {
                await Task.Run(_resetMaintenance.Begin, CancellationToken.None).ConfigureAwait(false);
                await _coordinator.ForgetConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _diagnostics.Failure("configuration.local-cleanup", error); result = new(false); }
            if (result.NetworkRestored) result = new(await FinishResetCleanupAsync(CancellationToken.None).ConfigureAwait(false));
            // Keep an owned session's recovery obligation and launch context until confirmed.
            if (result.NetworkRestored) { _recoveryRequired = false; _launch = null; }
            _savedResourcePort = new MacPolicyDocument().Defaults.ResourceProxyPort;
            _lastLoggedStage = null;
            _diagnostics.Record("configuration.reset", $"network-restored={result.NetworkRestored}; draft backed up; empty setup saved; generated local cleanup pending={ResetCleanupPending}; privileged recovery journal retained until restoration");
            return result;
        }
        finally { _apply.Release(); }
    }
    public async Task<bool> RepairNetworkAsync(CancellationToken cancellationToken = default)
    {
        await _apply.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _diagnostics.Record("network.repair", "explicit user request: authorize current helper identity and restore; never start TUN");
            var core = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
            await EnsureHelperAsync(core, cancellationToken).ConfigureAwait(false);
            bool restored = await _coordinator.StopAsync("用户修复后台助手并恢复系统网络", cancellationToken).ConfigureAwait(false);
            if (restored && ResetCleanupPending) restored = await FinishResetCleanupAsync(cancellationToken).ConfigureAwait(false);
            if (restored) { _recoveryRequired = false; _launch = null; }
            return restored;
        }
        finally { _apply.Release(); }
    }
    private async Task<bool> FinishResetCleanupAsync(CancellationToken token)
    {
        try
        {
            if (File.Exists(MacHelperEndpoint.Installed.ExpectedHelperPath) || File.Exists(MacHelperPaths.Installation))
            {
                var reply = await _helper.ResetAndRestoreAsync(token).ConfigureAwait(false);
                if (!reply.Succeeded || reply.Status.Running || reply.Status.TunTakenOver || !reply.Status.DnsRestored) return false;
            }
            await Task.Run(_resetMaintenance.Begin, token).ConfigureAwait(false);
            await _coordinator.ForgetConfigurationAsync(token).ConfigureAwait(false);
            await Task.Run(_resetMaintenance.Complete, token).ConfigureAwait(false);
            _resetCleanupPending = false;
            _diagnostics.Record("configuration.reset-cleanup", "old workspace generations and privileged generated data cleared; backup and logs retained");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        { _diagnostics.Failure("configuration.reset-cleanup", error); return false; }
    }
    public Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => Task.Run(() => _drafts.SaveAsync(document, cancellationToken), cancellationToken);
    public Task<ConnectionWorkspaceSnapshot> ReadConnectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_observations.Map(_helper.ConnectionSnapshot, Now));
    }

    public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default)
    {
        _gate.Require("open lifecycle log directory"); cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(LogDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var source in new[] { (MacHelperPaths.ServiceLog, "helper-service.log"), (Path.Combine(MacHelperPaths.ServiceLogDirectory, "network-errors.log"), "network-errors.log") })
        {
            try
            {
                using var handle = _native.OpenOwnedRead(source.Item1, 0, 256 * 1024);
                using var stream = new FileStream(handle, FileAccess.Read);
                string temporary = Path.Combine(LogDirectory, ".helper-log-" + Guid.NewGuid().ToString("N"));
                try
                {
                    using (var copy = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite })) stream.CopyTo(copy);
                    File.Move(temporary, Path.Combine(LogDirectory, source.Item2), overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                _diagnostics.Record("helper.service-log.copied", source.Item2);
            }
            catch (Exception error) { _diagnostics.Failure("helper.service-log", error); }
        }
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add(LogDirectory);
        using var child = Process.Start(start); return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        // Even a stopped core may leave a recovery journal / DNS pending after an earlier failure.
        if (MacRuntimeReadiness.RequiresExitRecovery(_recoveryRequired || _coordinator.Applied is not null, _helper.Status) && !await StopAsync().ConfigureAwait(false))
            throw new IOException("Network restoration was not confirmed; keep the authenticated helper connection for retry.");
        await _helper.DisposeAsync().ConfigureAwait(false); _disposed = true;
    }

    private async Task EnsureHelperAsync(PreparedCoreRelease core, CancellationToken cancellationToken)
    {
        Report(ConnectionAttemptStage.Authorization, "检查受信助手与核心版本；需要安装时会出现 macOS 管理员授权框。");
        var status = await _helper.InspectAsync(cancellationToken).ConfigureAwait(false);
        string gui = Environment.ProcessPath ?? throw new IOException("无法定位当前客户端。");
        if (Path.GetFileName(gui) != "EgressController.Desktop") throw new RuntimeFailureException("helper.missing");
        string helper = Path.Combine(AppContext.BaseDirectory, "helper", "EgressController.MacHelper");
        string library = Path.Combine(AppContext.BaseDirectory, "helper", "libEgressMacNative.dylib");
        if (!File.Exists(helper) || !File.Exists(library)) throw new RuntimeFailureException("helper.missing");
        string helperHash = await FileHashAsync(helper, cancellationToken).ConfigureAwait(false);
        string nativeHash = await FileHashAsync(library, cancellationToken).ConfigureAwait(false);
        if (status.Installed && status.InstalledCoreSha256 == core.Sha256
            && File.Exists(MacHelperEndpoint.Installed.ExpectedHelperPath) && File.Exists(MacHelperPaths.NativeLibrary)
            && await FileHashAsync(MacHelperEndpoint.Installed.ExpectedHelperPath, cancellationToken).ConfigureAwait(false) == helperHash
            && await FileHashAsync(MacHelperPaths.NativeLibrary, cancellationToken).ConfigureAwait(false) == nativeHash) return;
        var installation = new MacHelperInstallation(2, _native.GetUserId(), _native.GetGroupId(), gui, await FileHashAsync(gui, cancellationToken).ConfigureAwait(false),
            _staging, core.Sha256, helperHash, nativeHash);
        _diagnostics.Record("helper.install.identity", $"gui={gui}; helper-sha256={installation.HelperSha256}; native-sha256={installation.NativeLibrarySha256}; core-sha256={core.Sha256}");
        await new MacHelperInstaller(_gate, log: _log).InstallAsync(new(helper, library, core.ExecutablePath, installation), cancellationToken).ConfigureAwait(false);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                var fresh = await _helper.InspectAsync(readiness.Token).ConfigureAwait(false);
                if (fresh.Installed && fresh.InstalledCoreSha256 == core.Sha256) { _diagnostics.Record("helper.ready", "authenticated helper and pinned core confirmed"); return; }
                await Task.Delay(200, readiness.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string code = _helper.TransportFailureCode ?? "helper.timeout";
            _diagnostics.Record("helper.readiness.timeout", "15s; authenticated-reply=" + _helper.Status.Installed + "; last-transport-code=" + code);
            throw new RuntimeFailureException(code);
        }
    }
    private XrayResourceTransport ResourceTransport() => new(async (port, token) =>
    {
        var state = await _helper.InspectAsync(token).ConfigureAwait(false);
        var applied = _coordinator.Applied;
        if (!state.Running || !state.TunTakenOver || state.Failure is not null || applied is null)
            throw new RuntimeFailureException("proxy.not-ready");
        using var config = System.Text.Json.JsonDocument.Parse(applied.XrayJson);
        if (!config.RootElement.GetProperty("inbounds").EnumerateArray().Any(item => item.GetProperty("tag").GetString() == "resources"
            && item.GetProperty("port").GetInt32() == port)) throw new RuntimeFailureException("proxy.not-ready");
    });
    private ProductionResources Resources(int port) => new(ResourceTransport(), _cache,
        _ => ValueTask.FromResult(ResourceExitSnapshot.Xray(port)), ResourceProgress());
    private Task<PreparedCoreRelease> PrepareCoreAsync(CancellationToken token) => new XrayCoreReleaseManager(ResourceTransport(), _cache,
        Path.Combine(_root, "cores"), _ => ValueTask.FromResult(ResourceExitSnapshot.Xray(17890)))
        .PrepareBundledAsync(Path.Combine(AppContext.BaseDirectory, "core", "Xray-macos-arm64-v8a.zip"), token);
    private async Task CheckInterfaceIdentityAsync(InterfaceBinding binding, CancellationToken cancellationToken)
    {
        var interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false);
        if (!interfaces.Any(item => Physical(item) && item.ServiceId == binding.StableId && item.BsdName == binding.BsdName))
            throw new RuntimeFailureException("interface.identity");
    }
    private static bool Physical(MacNetworkInterface item) => !item.BsdName.StartsWith("utun", StringComparison.Ordinal) && item.BsdName != "lo0";
    private static ClientApplication MapApplication(MacApplication item)
    {
        string product = item.BundleIdentifier switch { "com.openai.chat" or "com.openai.chatgpt" => "openai", "com.openai.codex" => "codex",
            "com.anthropic.claudefordesktop" => "claude", "com.todesktop.230313mzl4w4u92" => "cursor", "com.google.Chrome" => "chrome", "com.apple.Safari" => "safari",
            "com.microsoft.VSCode" => "vscode", "notion.id" => "notion", "com.tinyspeck.slackmacgap" => "slack", "com.spotify.client" => "spotify", "ru.keepcoder.Telegram" or "org.telegram.desktop" => "telegram", "com.hnc.Discord" => "discord", _ => "app" };
        return new("app-" + Digest(item.Id)[..24], item.DisplayName, product, "macOS App", item.BundlePath, item.PrimaryExecutablePath, item.ExecutablePaths, false, item.BundleIdentifier);
    }
    private static string SafeId(string value) => value.Length <= 100 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? value : "catalog-" + Digest(value)[..24];
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static async Task<string> FileHashAsync(string path, CancellationToken cancellationToken)
    { await using var file = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)); }
}
