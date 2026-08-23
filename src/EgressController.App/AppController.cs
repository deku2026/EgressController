using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using EgressController.App.Services;
using EgressController.Core.Contracts;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Launcher.Discovery;
using EgressController.Launcher.Sessions;
using EgressController.Rules.Artifacts;
using EgressController.Rules.Catalog;
using EgressController.SingBox.Api;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Cli;
using EgressController.SingBox.Configuration;
using EgressController.SingBox.Core;
using EgressController.SingBox.Runtime;
using EgressController.State.Profile;
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
/// diagnostics streams, Windows discovery and ordinary (non-proxy-injected) process launches.
/// </summary>
public sealed class AppController : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly string _dataRoot;
    private readonly EgressProfileStore _profileStore;
    private readonly SingBoxStateStore _stateStore;
    private readonly INetworkAdapterService _adapterService;
    private readonly NetworkEnvironmentResolver _environmentResolver = new();
    private readonly WindowsLaunchTargetScanner _targetScanner = new();
    private readonly LaunchTargetRegistry _targets = new();
    private readonly Dictionary<string, LaunchTarget> _manualTargets = new(StringComparer.Ordinal);
    private readonly LaunchSessionRegistry _sessions = new();
    private readonly TcpListenerOwnerResolver _ownerResolver = new();
    private readonly UpstreamSocksProbe _upstreamProbe = new();
    private readonly WindowsProcessIdentityResolver _processIdentity =
        new(new ExecutablePathCanonicalizer());
    private readonly EgressProfileCompiler _compiler = new();
    private readonly DirectSingBoxProcessClient _directSingBox = new();
    private readonly SingBoxService _singBox;
    private readonly ConnectionHistoryStore _connectionHistory = new();
    private readonly BoundedLogStore _logs = new();
    private readonly CancellationTokenSource _lifetimeCts = new();

    private Socks5RemoteFetcher _remoteFetcher = null!;
    private HttpClient _releaseHttpClient = null!;
    private RuleCatalogService _catalogService = null!;
    private RuleArtifactStore _artifactStore = null!;
    private SingBoxCoreManager _coreManager = null!;
    private Task? _diagnosticsTask;
    private CancellationTokenSource? _diagnosticsCts;
    private EgressProfileDocument _profile;
    private IReadOnlyList<NetworkAdapterInfo> _adapters = Array.Empty<NetworkAdapterInfo>();
    private string _lastMessage = "就绪。";
    private long _trafficUp;
    private long _trafficDown;

    public AppController(string? dataRoot = null)
    {
        _dataRoot = Path.GetFullPath(dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EgressController"));
        Directory.CreateDirectory(_dataRoot);
        _profileStore = new EgressProfileStore(_dataRoot);
        _stateStore = new SingBoxStateStore(_dataRoot);
        _adapterService = new WindowsNetworkAdapterService();
        _profile = LoadProfile();
        ConfigureControlPlane(_profile.UpstreamPort);
        LoadCachedCatalog();
        RefreshAdapters();

        _singBox = new SingBoxService(_directSingBox, _stateStore);
        _singBox.Output += OnSingBoxOutput;
    }

    public string DataRoot => _dataRoot;
    public EgressProfileDocument Profile => _profile;
    public SingBoxService SingBox => _singBox;
    public ConnectionHistoryStore ConnectionHistory => _connectionHistory;
    public BoundedLogStore Logs => _logs;
    public LaunchSessionRegistry Sessions => _sessions;
    public IReadOnlyList<NetworkAdapterInfo> Adapters => _adapters;
    public IReadOnlyList<LaunchTarget> Targets => _targets.All();
    public SingBoxRuleCatalog? Catalog => _catalogService.Current;
    public string CatalogDirectory => Path.GetDirectoryName(_catalogService.CatalogPath) ?? _dataRoot;
    public string CatalogCommit => Catalog?.Snapshot.CommitSha ?? string.Empty;
    public IReadOnlyList<string> SelectedRuleNames => _profile.EsimRuleSets;
    public IReadOnlyList<string> ManualDomains => _profile.EsimDomains;
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
            SetMessage("扫描网卡失败：" + exception.Message);
            return _adapters;
        }
    }

    public IReadOnlyList<LaunchTarget> ScanTargets()
    {
        IReadOnlyList<LaunchTarget> scanned = _targetScanner.Scan();
        var discovered = scanned.ToList();
        var discoveredKeys = discovered.Select(target => target.DiscoveryKey).ToHashSet(StringComparer.Ordinal);
        foreach (LaunchTarget manual in _manualTargets.Values)
        {
            if (discoveredKeys.Add(manual.DiscoveryKey))
                discovered.Add(manual);
        }

        _targets.Clear();
        foreach (LaunchTarget target in discovered)
        {
            target.EsimSelected = _profile.EsimApplications.Any(selection =>
                string.Equals(selection.DiscoveryKey, target.DiscoveryKey, StringComparison.Ordinal));
            _targets.Add(target);
        }
        SetMessage($"已扫描 {discovered.Count} 个 Windows 应用。");
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
            OwnedExecutables = ExecutableInventory.Collect(new[] { root }, full),
            EsimSelected = false,
            IconPath = full,
            Source = "手动添加",
        };
        _manualTargets[target.DiscoveryKey] = target;
        if (_targets.Add(target))
            return target;
        return _targets.All().First(existing => existing.DiscoveryKey == target.DiscoveryKey);
    }

    public async Task<ControllerOperationResult> SetApplicationsEsimAsync(
        IEnumerable<LaunchTarget> targets,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        string[] keys = targets
            .Where(target => target.CanRoute)
            .Select(target => target.DiscoveryKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return await UpdateProfileAsync(
            current =>
            {
                var selected = current.EsimApplications.ToDictionary(item => item.DiscoveryKey, StringComparer.Ordinal);
                foreach (string key in keys)
                {
                    if (enabled)
                    {
                        LaunchTarget? target = _targets.All().FirstOrDefault(item => item.DiscoveryKey == key);
                        selected[key] = new EgressApplicationSelection
                        {
                            DiscoveryKey = key,
                            ManualExecutablePath = target?.Source == "手动添加" ? target.CanonicalExecutable : null,
                        };
                    }
                    else
                    {
                        selected.Remove(key);
                    }
                }
                return current with { EsimApplications = selected.Values.ToArray() };
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControllerOperationResult> SetRuleSetAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ControllerOperationResult.Failure("规则集名称不能为空。");
        string normalized = name.Trim().ToLowerInvariant();
        if (enabled)
        {
            SingBoxRuleCatalog? catalog = Catalog;
            if (catalog is null || !catalog.TryGet(normalized, out _))
                return ControllerOperationResult.Failure("规则 catalog 尚未就绪，请先更新规则。");
            RuleArtifactResult artifact = await _artifactStore.EnsureAsync(
                catalog.Snapshot,
                normalized,
                cancellationToken).ConfigureAwait(false);
            if (!artifact.Succeeded)
                return ControllerOperationResult.Failure(artifact.Error ?? "SRS 下载失败。");
        }

        return await UpdateProfileAsync(
            current => current with
            {
                EsimRuleSets = enabled
                    ? current.EsimRuleSets.Append(normalized).ToArray()
                    : current.EsimRuleSets.Where(item => !string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)).ToArray(),
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControllerOperationResult> SetRuleSetsAsync(
        IEnumerable<string> names,
        bool enabled,
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
                return ControllerOperationResult.Failure("规则 catalog 尚未就绪，请先更新规则。");

            string[] missing = normalized
                .Where(name => !catalog.TryGet(name, out _))
                .ToArray();
            if (missing.Length > 0)
                return ControllerOperationResult.Failure("规则 catalog 缺少：" + string.Join(", ", missing));

            RuleArtifactBatchResult artifacts = await _artifactStore.EnsureManyAsync(
                catalog.Snapshot,
                normalized,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!artifacts.Succeeded)
            {
                string details = string.Join(
                    "; ",
                    artifacts.Failures.Select(item => item.Key + ": " + item.Value));
                return ControllerOperationResult.Failure("SRS 批量下载失败：" + details);
            }
        }

        return await UpdateProfileAsync(
            current => current with
            {
                EsimRuleSets = enabled
                    ? current.EsimRuleSets
                        .Concat(normalized)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : current.EsimRuleSets
                        .Where(item => !normalized.Contains(item, StringComparer.OrdinalIgnoreCase))
                        .ToArray(),
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ControllerOperationResult> AddManualDomainAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        string normalized = EgressProfileDocument.NormalizeDomain(domain);
        return UpdateProfileAsync(
            current => current with { EsimDomains = current.EsimDomains.Append(normalized).ToArray() },
            cancellationToken);
    }

    public Task<ControllerOperationResult> RemoveManualDomainAsync(
        string domain,
        CancellationToken cancellationToken = default)
        => UpdateProfileAsync(
            current => current with
            {
                EsimDomains = current.EsimDomains
                    .Where(item => !string.Equals(item, domain, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
            },
            cancellationToken);

    public Task<ControllerOperationResult> SetAdaptersAsync(
        Guid? primary,
        Guid? esim,
        CancellationToken cancellationToken = default)
        => UpdateProfileAsync(
            current => current with
            {
                PrimaryAdapterId = primary?.ToString("D"),
                EsimAdapterId = esim?.ToString("D"),
            },
            cancellationToken);

    public Task<ControllerOperationResult> SetUpstreamPortAsync(
        int port,
        CancellationToken cancellationToken = default)
        => UpdateProfileAsync(current => current with { UpstreamPort = port }, cancellationToken);

    public async Task<SingBoxCatalogUpdateResult> RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        SingBoxCatalogUpdateResult result = await _catalogService.UpdateAsync(cancellationToken).ConfigureAwait(false);
        SetMessage(result.Succeeded
            ? $"已更新 MetaCubeX sing catalog：{result.Catalog!.Count} 个 SRS，commit {result.Catalog.Snapshot.CommitSha[..12]}。"
            : "规则更新失败：" + result.Error);
        return result;
    }

    public async Task<ControllerOperationResult> ToggleTunAsync(CancellationToken cancellationToken = default)
        => IsTunRunning ? await StopTunAsync(cancellationToken).ConfigureAwait(false) : await StartTunAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ControllerOperationResult> StartTunAsync(CancellationToken cancellationToken = default)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsTunRunning)
                return ControllerOperationResult.Success();
            SingBoxApplyResult result = await _singBox.StartAsync(PrepareRuntimeAsync, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                SetMessage("TUN 启动失败：" + result.ErrorMessage);
                return ControllerOperationResult.Failure(result.ErrorMessage ?? "TUN 启动失败。");
            }

            SetMessage("sing-box TUN 已启动。");
            return ControllerOperationResult.Success();
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public async Task<ControllerOperationResult> StopTunAsync(CancellationToken cancellationToken = default)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _singBox.StopAsync(cancellationToken).ConfigureAwait(false);
            SetMessage("sing-box TUN 已停止。");
            return ControllerOperationResult.Success();
        }
        catch (Exception exception)
        {
            SetMessage("停止 TUN 失败：" + exception.Message);
            return ControllerOperationResult.Failure(exception.Message);
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
            return ControllerOperationResult.Failure("关闭连接失败：" + exception.Message);
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
            return ControllerOperationResult.Failure("清理 DNS 缓存失败：" + exception.Message);
        }
    }

    public string LaunchTarget(string id)
    {
        LaunchTarget target = _targets.Get(id)
            ?? throw new InvalidOperationException("目标已从扫描结果中消失，请重新扫描。");
        if (!target.CanLaunch)
            throw new InvalidOperationException("该目标没有可安全启动的已解析 EXE。");

        LaunchSession session = new WindowsLaunchService().StartPlain(target);
        _sessions.Register(session);
        SetMessage($"已发送启动请求：{target.Name} (启动器 PID {session.RootPid})；网络规则由 sing-box 按 EXE 路径决定。");
        return LastMessage;
    }

    public string GetTargetStatus(string targetId)
    {
        LaunchSession[] sessions = _sessions.All()
            .Where(session => string.Equals(session.TargetId, targetId, StringComparison.Ordinal))
            .ToArray();
        if (sessions.Length == 0)
            return string.Empty;
        LaunchSession? live = sessions.FirstOrDefault(IsLiveRoot);
        if (live is not null)
            return $"运行中 · PID {live.RootPid}";

        LaunchTarget? target = _targets.Get(targetId);
        uint? ownedPid = target is null ? null : FindLiveTargetProcess(target);
        if (ownedPid is not null)
            return $"运行中 · PID {ownedPid.Value}（目标进程）";

        foreach (LaunchSession session in sessions)
            _sessions.MarkRootExited(session.SessionId);
        return "未运行";
    }

    private bool IsLiveRoot(LaunchSession session)
    {
        ProcessIdentity? identity = _processIdentity.Resolve(session.RootPid);
        return identity is not null && identity.StartTimeUtc == session.RootStartTimeUtc;
    }

    private uint? FindLiveTargetProcess(LaunchTarget target)
    {
        HashSet<string> executablePaths = target.OwnedExecutables
            .Append(target.CanonicalExecutable)
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (executablePaths.Count == 0)
            return null;

        HashSet<string> processNames = executablePaths
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string processName in processNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }

            foreach (Process process in processes)
            {
                using (process)
                {
                    ProcessIdentity? identity = _processIdentity.Resolve(checked((uint)process.Id));
                    if (identity?.ExePathFinal is not null && executablePaths.Contains(identity.ExePathFinal))
                        return identity.Pid;
                }
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();
        StopDiagnostics();
        _singBox.Output -= OnSingBoxOutput;
        try { await _singBox.DisposeAsync().ConfigureAwait(false); } catch { }
        _remoteFetcher.Dispose();
        _releaseHttpClient.Dispose();
        _lifetimeCts.Dispose();
        _configurationGate.Dispose();
    }

    private async Task<SingBoxRuntimeCandidate> PrepareRuntimeAsync(CancellationToken cancellationToken)
    {
        EgressProfileDocument profile = _profile.NormalizeAndValidate();
        Socks5ProbeResult upstream = await _upstreamProbe.ProbeAsync(profile.UpstreamPort, cancellationToken).ConfigureAwait(false);
        if (!upstream.IsReady)
            throw new ControllerPreparationException("upstream.offline", upstream.Message);

        RefreshAdapters();
        NetworkEnvironmentSnapshot environment = _environmentResolver.Resolve(profile, _adapters);
        string[] ownerPaths = ResolveUpstreamOwners(profile.UpstreamPort, cancellationToken);
        string[] applicationPaths = ResolveApplicationPaths(profile);
        IReadOnlyList<SingBoxRuleSetInput> ruleSets = await EnsureRuleSetsAsync(profile, cancellationToken).ConfigureAwait(false);
        SingBoxCoreCandidate core = await _coreManager.PrepareAsync(profile.Core, cancellationToken).ConfigureAwait(false);

        string runtimeDirectory = Path.Combine(_dataRoot, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string configPath = Path.Combine(runtimeDirectory, "config.json");
        EgressProfileCompilationResult compiled = _compiler.Compile(new EgressProfileCompileInput
        {
            Profile = profile,
            Environment = environment,
            ApplicationExecutablePaths = applicationPaths,
            UpstreamOwnerPaths = ownerPaths,
            SelfExecutablePaths = [Environment.ProcessPath ?? string.Empty],
            RuleSets = ruleSets,
        });
        EgressProfileCompiler.WriteNext(configPath, compiled);
        return SingBoxRuntimeCandidate.From(core, configPath, compiled.Sha256);
    }

    private async Task<IReadOnlyList<SingBoxRuleSetInput>> EnsureRuleSetsAsync(
        EgressProfileDocument profile,
        CancellationToken cancellationToken)
    {
        if (profile.EsimRuleSets.Count == 0)
            return Array.Empty<SingBoxRuleSetInput>();
        SingBoxRuleCatalog catalog = Catalog
            ?? throw new ControllerPreparationException("rules.catalog", "已选择 SRS，但本地没有可用的 sing catalog。");
        RuleArtifactBatchResult result = await _artifactStore.EnsureManyAsync(
            catalog.Snapshot,
            profile.EsimRuleSets,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new ControllerPreparationException("rules.download", string.Join("；", result.Failures.Values));
        return profile.EsimRuleSets
            .Select(name => new SingBoxRuleSetInput(name, result.Paths[name]))
            .ToArray();
    }

    private string[] ResolveUpstreamOwners(int port, CancellationToken cancellationToken)
    {
        IReadOnlyList<TcpListenerOwner> owners = _ownerResolver.Resolve(port, cancellationToken);
        if (owners.Count == 0)
            throw new ControllerPreparationException("upstream.owner", $"没有找到 127.0.0.1:{port} 的 SOCKS5 监听进程。");
        if (owners.Any(owner => !owner.IsResolved))
            throw new ControllerPreparationException("upstream.owner.identity", "SOCKS5 监听进程存在，但无法解析其最终 EXE 路径。");
        return owners.Select(owner => owner.CanonicalExecutablePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string[] ResolveApplicationPaths(EgressProfileDocument profile)
    {
        var paths = new List<string>();
        foreach (EgressApplicationSelection selection in profile.EsimApplications)
        {
            LaunchTarget? target = _targets.All().FirstOrDefault(item => item.DiscoveryKey == selection.DiscoveryKey);
            if (target is null)
                throw new ControllerPreparationException("application.missing", $"找不到已选择的应用：{selection.DiscoveryKey}。");
            if (!target.CanRoute)
                throw new ControllerPreparationException("application.unresolved", $"应用没有可用于 process_path 的 EXE：{target.Name}。");
            paths.AddRange(target.OwnedExecutables);
            if (target.OwnedExecutables.Count == 0 && target.CanonicalExecutable is not null)
                paths.Add(target.CanonicalExecutable);
        }
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<bool> HealthCheckAsync(SingBoxRuntimeCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.ControllerPort is < 1 or > ushort.MaxValue || string.IsNullOrWhiteSpace(candidate.ControllerSecret))
            return false;
        using var api = new SingBoxApiClient(new Uri($"http://127.0.0.1:{candidate.ControllerPort}"), candidate.ControllerSecret);
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                SingBoxVersionResponse version = await api.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                return version.Version.StartsWith("sing-box ", StringComparison.OrdinalIgnoreCase);
            }
            catch (SingBoxApiException exception) when (exception.StatusCode is null)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        return false;
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
            EgressProfileDocument next;
            try
            {
                next = update(previous).NormalizeAndValidate();
            }
            catch (Exception exception)
            {
                return ControllerOperationResult.Failure(exception.Message);
            }

            bool portChanged = next.UpstreamPort != previous.UpstreamPort;
            try
            {
                _profile = next;
                if (portChanged)
                    ConfigureControlPlane(next.UpstreamPort);
                _profileStore.Save(next);
            }
            catch (Exception exception)
            {
                _profile = previous;
                if (portChanged)
                    ConfigureControlPlane(previous.UpstreamPort);
                return ControllerOperationResult.Failure("保存 Profile 失败：" + exception.Message);
            }

            if (!IsTunRunning)
            {
                SetMessage("配置已保存；启动 TUN 后生效。");
                return ControllerOperationResult.Success();
            }

            SingBoxApplyResult applied = await _singBox.ApplyAsync(PrepareRuntimeAsync, cancellationToken).ConfigureAwait(false);
            if (applied.Succeeded)
            {
                SetMessage("配置已校验并应用，sing-box 已重启。");
                return ControllerOperationResult.Success();
            }

            _profile = previous;
            try
            {
                if (portChanged)
                    ConfigureControlPlane(previous.UpstreamPort);
                _profileStore.Save(previous);
            }
            catch { }
            return ControllerOperationResult.Failure(applied.ErrorMessage ?? "配置应用失败，已回滚。");
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    private void ConfigureControlPlane(int upstreamPort)
    {
        _remoteFetcher?.Dispose();
        _releaseHttpClient?.Dispose();
        _releaseHttpClient = Socks5HttpClientFactory.Create(upstreamPort);
        _remoteFetcher = new Socks5RemoteFetcher("127.0.0.1", upstreamPort);
        _catalogService = new RuleCatalogService(_remoteFetcher, Path.Combine(_dataRoot, "rules"));
        _artifactStore = new RuleArtifactStore(Path.Combine(_dataRoot, "rules"), _remoteFetcher);
        _coreManager = new SingBoxCoreManager(
            _dataRoot,
            new SingBoxReleaseClient(_releaseHttpClient),
            new SingBoxCli(),
            _stateStore);
    }

    private void LoadCachedCatalog()
    {
        _catalogService.LoadCached(out string? error);
        if (!string.IsNullOrWhiteSpace(error))
            SetMessage(error);
    }

    private EgressProfileDocument LoadProfile()
    {
        try
        {
            return _profileStore.Load();
        }
        catch (Exception exception)
        {
            _lastMessage = "Profile 读取失败，已使用默认配置：" + exception.Message;
            return EgressProfileDocument.Default;
        }
    }

    private void StartDiagnostics(ControllerEndpoint? endpoint)
    {
        StopDiagnostics();
        if (endpoint is null)
            return;
        _diagnosticsCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken token = _diagnosticsCts.Token;
        _diagnosticsTask = Task.Run(() => DiagnosticsLoopAsync(endpoint, token), token);
    }

    private void StopDiagnostics()
    {
        _diagnosticsCts?.Cancel();
        _diagnosticsCts?.Dispose();
        _diagnosticsCts = null;
        _diagnosticsTask = null;
    }

    private async Task DiagnosticsLoopAsync(ControllerEndpoint endpoint, CancellationToken cancellationToken)
    {
        TimeSpan backoff = TimeSpan.FromMilliseconds(250);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var api = new SingBoxApiClient(endpoint.Uri, endpoint.Secret);
                SingBoxConnectionsResponse snapshot = await api.GetConnectionsAsync(cancellationToken).ConfigureAwait(false);
                ApplyConnectionSnapshot(snapshot);
                using ClientWebSocketBundle sockets = await ClientWebSocketBundle.ConnectAsync(api, cancellationToken).ConfigureAwait(false);
                using var socketLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task connections = ConsumeConnectionsAsync(sockets.Connections, socketLifetime.Token);
                Task traffic = ConsumeTrafficAsync(sockets.Traffic, socketLifetime.Token);
                Task logs = ConsumeLogsAsync(sockets.Logs, socketLifetime.Token);
                await Task.WhenAny(connections, traffic, logs).ConfigureAwait(false);
                socketLifetime.Cancel();
                try { await Task.WhenAll(connections, traffic, logs).ConfigureAwait(false); } catch { }
                backoff = TimeSpan.FromMilliseconds(250);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logs.Append("controller", "error", "sing-box API 连接中断：" + exception.Message);
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5_000));
            }
        }
    }

    private async Task ConsumeConnectionsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? message = await SingBoxApiClient.ReceiveTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return;
            ApplyConnectionSnapshot(SingBoxApiClient.ParseConnectionsMessage(message));
        }
    }

    private async Task ConsumeTrafficAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? message = await SingBoxApiClient.ReceiveTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return;
            SingBoxTrafficEvent traffic = SingBoxApiClient.ParseTrafficMessage(message);
            Interlocked.Add(ref _trafficUp, traffic.Up);
            Interlocked.Add(ref _trafficDown, traffic.Down);
        }
    }

    private async Task ConsumeLogsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? message = await SingBoxApiClient.ReceiveTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return;
            SingBoxLogEvent log = SingBoxApiClient.ParseLogMessage(message);
            _logs.Append("sing-box", log.Type, log.Payload);
        }
    }

    private void ApplyConnectionSnapshot(SingBoxConnectionsResponse snapshot)
    {
        Interlocked.Exchange(ref _trafficUp, snapshot.UploadTotal);
        Interlocked.Exchange(ref _trafficDown, snapshot.DownloadTotal);
        _connectionHistory.ApplySnapshot(snapshot.Connections.Select(ToObservation));
    }

    private static ConnectionObservation ToObservation(SingBoxConnection connection)
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
            StartedAtUtc = connection.Start ?? DateTimeOffset.UtcNow,
            Chains = connection.Chains.ToArray(),
            Rule = connection.Rule,
            RulePayload = connection.RulePayload,
            Outbound = connection.Chains.LastOrDefault(),
        };

    private SingBoxApiClient CreateApiClient()
        => throw new InvalidOperationException("当前最小 sing-box 配置未启用 Clash API。");

    private void OnSingBoxOutput(SingBoxOutputEvent output)
        => _logs.Append(output.Source, "output", output.Line);

    private void SetMessage(string message)
    {
        lock (_gate)
            _lastMessage = message;
    }

    private sealed class ClientWebSocketBundle : IDisposable
    {
        private ClientWebSocketBundle(ClientWebSocket connections, ClientWebSocket traffic, ClientWebSocket logs)
        {
            Connections = connections;
            Traffic = traffic;
            Logs = logs;
        }

        public ClientWebSocket Connections { get; }
        public ClientWebSocket Traffic { get; }
        public ClientWebSocket Logs { get; }

        public static async Task<ClientWebSocketBundle> ConnectAsync(SingBoxApiClient api, CancellationToken cancellationToken)
        {
            ClientWebSocket? connections = null;
            ClientWebSocket? traffic = null;
            ClientWebSocket? logs = null;
            try
            {
                connections = await api.ConnectConnectionsWebSocketAsync(500, cancellationToken).ConfigureAwait(false);
                traffic = await api.ConnectTrafficWebSocketAsync(cancellationToken).ConfigureAwait(false);
                logs = await api.ConnectLogsWebSocketAsync("info", cancellationToken).ConfigureAwait(false);
                return new ClientWebSocketBundle(connections, traffic, logs);
            }
            catch
            {
                connections?.Dispose();
                traffic?.Dispose();
                logs?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Connections.Dispose();
            Traffic.Dispose();
            Logs.Dispose();
        }
    }
}

internal sealed class ControllerPreparationException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
