using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Diagnostics;
using EgressController.Guardian;
using EgressController.Rules.Catalog;
using EgressController.State.Quota;
using EgressController.Windows.Process;

namespace EgressController.App.Preview;

/// <summary>All commands mutate in-memory fixtures. No native scanner, process, service, HTTP or disk store.</summary>
public sealed class PreviewController : IAppController
{
    private static readonly Guid HomeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProxyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly List<LaunchTarget> _targets = [];
    private readonly List<ProtectionEvent> _events = [];
    private string _scenario;
    private int _sample;
    private readonly PreviewClock _clock = new();
    private readonly TrafficTimeline _traffic;
    public TrafficSnapshot TrafficSnapshot => _traffic.Snapshot();
    public void AdvanceSample()
    {
        _sample++; _clock.Advance();
        if (!IsTunRunning) return;
        if (_scenario == "telemetry" && _sample >= 24 || _sample is >= 32 and <= 36) _traffic.Unavailable();
        else _traffic.Observe(SimulatedUploadRate, SimulatedDownloadRate);
    }
    public PreviewController(string scenario = "ready")
    {
        RuntimeSafety.EnableMockOnly();
        _scenario = scenario;
        _traffic = new(_clock);
        Diagnostics = new(@"C:\EgressPreview\logs\protection.log", _ => { });
        Adapters = [Adapter(HomeId, "Ethernet", "USB 家宽", "192.0.2.10", 7), Adapter(ProxyId, "Wi-Fi", "无线网络", "198.51.100.10", 12)];
        Profile = new EgressProfileDocument { DefaultAdapterId = HomeId.ToString(), ProxyAdapterId = ProxyId.ToString(),
            UpstreamPorts = [7890, 7891], UpstreamPort = 7890 }.NormalizeAndValidate();
        string[] names = ["ChatGPT", "Claude", "Cursor", "Google Chrome", "Microsoft Edge", "Windsurf", "Firefox", "LM Studio"];
        foreach (string name in names)
        {
            string path = @"C:\Applications\" + name + @"\" + name.Replace(" ", "") + ".exe";
            _targets.Add(new() { Id = "preview:" + name, Name = name, Kind = LaunchKind.DirectExe,
                Command = path, CanonicalExecutable = path, OwnedExecutables = [path, Path.Combine(Path.GetDirectoryName(path)!, "tools", "helper.exe")],
                OwnedRoots = [Path.GetDirectoryName(path)!], Source = "UI 预览数据" });
        }
        Profile = Profile with { Applications = _targets.Take(3).Select(target => new EgressApplicationSelection
        { DiscoveryKey = target.DiscoveryKey, DisplayName = target.Name, ExecutablePaths = target.OwnedExecutables,
            DirectoryRoots = target.OwnedRoots, Target = EgressRouteTarget.DefaultAdapter }).ToArray(),
            Domains = [new() { Name = "openai.com", Target = EgressRouteTarget.DefaultAdapter },
                new() { Name = "github.com", Target = EgressRouteTarget.ForPort(7890) }],
            RuleSets = [new() { Name = "google", Target = EgressRouteTarget.ForPort(7890) }] };
        Catalog = new(new(new string('a', 40), new string('b', 40), new[] { "google", "youtube", "openai", "anthropic", "github", "telegram", "twitter" }
            .Select(name => new SingBoxRuleCatalogEntry(name, "geosite/" + name + ".srs", new string('c', 40), 2048)).ToArray()));
        Quota = new(500L * 1024 * 1024 * 1024, 380L * 1024 * 1024 * 1024, 62L * 1024 * 1024 * 1024, DateTimeOffset.UtcNow);
        Guard = new(new PreviewGuardClient(this), Diagnostics, Path.Combine(Diagnostics.DirectoryPath, "guardian"));
        if (scenario != "empty")
        {
            string[] hosts = ["api.openai.com", "claude.ai", "github.com", "www.google.com", "cdn.cursor.sh", "api.anthropic.com", "accounts.google.com", "docs.github.com"];
            ConnectionHistory.ApplySnapshot(hosts.Select((host, index) => new ConnectionObservation
            {
                Id = "preview-" + index, Host = host, ProcessPath = _targets[index % 4].CanonicalExecutable,
                ProcessId = (uint)(14200 + index), DestinationIp = "203.0.113." + (index + 10), DestinationPort = "443",
                SourceIp = "172.19.0.1", SourcePort = (51000 + index).ToString(), Network = "tcp", Type = "TUN", DnsMode = "normal",
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-index - 1), LastSeenAtUtc = DateTimeOffset.UtcNow,
                Upload = (index + 1) * 11400, Download = (index + 1) * 234500, UploadRate = 4600 + index * 1700,
                DownloadRate = 12800 + index * 2100, Outbound = index % 3 == 0 ? "ESIM-家宽" : "默认代理",
                Chains = [index % 3 == 0 ? "ESIM-家宽" : "默认代理"], Rule = "ProcessPath", RulePayload = _targets[index % 4].Name,
            }));
            _events.Add(new(DateTimeOffset.UtcNow.AddMinutes(-18), "ChatGPT", _targets[0].CanonicalExecutable!, 14200,
                true, "启动保护：TUN 尚未接管", null));
            Diagnostics.Info("TUN 启动", "自动启动受管理内核");
            Diagnostics.Info("TUN 状态", "接管检查通过");
            Diagnostics.Info("保护状态", "联网检测通过，允许所选应用运行");
        }
        else { Profile = Profile with { Applications = [], Domains = [], RuleSets = [] }; _targets.Clear(); }
        if (scenario == "offline") Diagnostics.Report("保护状态", "network.failed", "ESIM-家宽连续三次联网检测失败，正在保护所选应用。", new IOException("模拟出口不可达"));
        if (scenario is "starting" or "failed") { ConnectionHistory.ApplySnapshot([]); ConnectionHistory.ClearClosed(); }
        if (scenario == "failed") Diagnostics.Report("TUN 启动", "tun.conflict", LastTunFailure);
        if (scenario == "starting") Diagnostics.Info("TUN 启动", "正在准备内核，所选应用暂不能运行");
    }
    public bool IsPreview => true;
    public bool IsTunRunning => _scenario is not ("starting" or "failed");
    public bool IsUpdatingProfile => false;
    public ProtectionReadiness Protection => new(_scenario is not ("offline" or "starting" or "failed"), _scenario switch
    { "offline" => "ESIM-家宽连续三次联网检测失败", "starting" => "等待 TUN 接管", "failed" => LastTunFailure, _ => "TUN 已接管，网络检查通过" });
    public string TunStatus => IsTunRunning ? "运行中" : "正在启动";
    public string ProtectionStatus => Protection.Ready ? "保护已就绪，可以打开所选应用" : "正在保护：" + Protection.Reason;
    public string LastTunFailure => _scenario == "failed" ? "TUN 启动失败：检测到另一个活动隧道 Mihomo（模拟）。请关闭该隧道后等待自动重试。" : "";
    public string LastMessage { get; private set; } = "所有变更只作用于模拟数据";
    public string UpstreamSummary => $"127.0.0.1:{Profile.UpstreamPort} · SOCKS5";
    public EgressProfileDocument Profile { get; private set; }
    public IReadOnlyList<NetworkAdapterInfo> Adapters { get; }
    public ProxyPortSnapshot ProxyBindings => new(Profile.UpstreamPorts.Select(port => new ProxyPortBinding(port,
        [new(16800, @"C:\Applications\Proxy\mihomo.exe", DateTime.UnixEpoch)], null)).ToArray(), [@"C:\Applications\Proxy\mihomo.exe"]);
    public GuardManagementViewModel Guard { get; }
    public string GuardianConnectionStatus => "守护已连接";
    public int? DirectDelayMilliseconds => IsTunRunning && _scenario != "offline" ? 18 : null;
    public int? ProxyDelayMilliseconds => IsTunRunning ? 126 : null;
    public string DirectInternetStatus => _scenario switch { "starting" or "failed" => "等待 TUN 接管后检测", "offline" => "ESIM-家宽：连续三次检测失败", _ => "ESIM-家宽：HTTPS 探测通过（18 ms）" };
    public string ProxyInternetStatus => !IsTunRunning ? "等待 TUN 接管后检测" : "Proxy-代理：HTTPS 探测通过（126 ms）";
    public string NetworkMonitorStatus => _scenario switch { "starting" => "等待 TUN 接管", "failed" => LastTunFailure, "offline" => "家宽出口不可达", _ => "两个出口检测通过" };
    public DateTimeOffset? NetworkLastCheckedAtUtc => !IsTunRunning ? null : DateTimeOffset.UtcNow.AddSeconds(-8);
    public string DiagnosticsStatus => _scenario == "telemetry" ? "统计暂不可用：模拟 API 超时，等待下一次采样" : "统计更新正常";
    public ConnectionHistoryStore ConnectionHistory { get; } = new();
    public DateTimeOffset? ConnectionsUpdatedAtUtc => DateTimeOffset.UtcNow;
    public DateTimeOffset? TrafficUpdatedAtUtc => TrafficSnapshot.ObservedAtUtc;
    public long TrafficUp => _scenario is "empty" or "starting" or "failed" ? 0 : 178 * 1024 * 1024;
    public long TrafficDown => _scenario is "empty" or "starting" or "failed" ? 0 : 551 * 1024 * 1024;
    private long SimulatedUploadRate => _scenario is "ready" or "telemetry" ? (long)((104 + 28 * Math.Sin(_sample * .28) + (_sample % 17 < 3 ? 70 : 0)) * 1024) : 0;
    private long SimulatedDownloadRate => _scenario is "ready" or "telemetry" ? (long)((1320 + 310 * Math.Sin(_sample * .22) + (_sample % 23 < 5 ? 400 : 0)) * 1024) : 0;
    public EgressQuotaSnapshot Quota { get; private set; }
    public DiagnosticJournal Diagnostics { get; }
    public BoundedLogStore Logs { get; } = new();
    public string LogPath => Diagnostics.LogPath;
    public IReadOnlyList<ProtectionEvent> ProtectionEvents => _events.ToArray();
    public SingBoxRuleCatalog? Catalog { get; }
    public string CatalogDirectory => "预览规则库";
    public string CatalogCommit => "preview";
    public IReadOnlyList<string> SelectedRuleNames => Profile.RuleSets.Select(item => item.Name).ToArray();
    public IReadOnlyList<string> ManualDomains => Profile.Domains.Select(item => item.Name).ToArray();
    public IReadOnlyList<NetworkAdapterInfo> RefreshAdapters() => Adapters;
    public IReadOnlyList<LaunchTarget> ScanTargets() => _targets;
    private Task<ControllerOperationResult> Save(EgressProfileDocument value) { Profile = value.NormalizeAndValidate(); LastMessage = "预览配置已更新"; return Task.FromResult(ControllerOperationResult.Success()); }
    public Task<ControllerOperationResult> SetAdapterRolesAsync(string? directId, string? proxyId) => Save(Profile.SetAdapterRoles(directId, proxyId));
    public Task<ControllerOperationResult> SetApplicationsRouteAsync(IEnumerable<LaunchTarget> targets, bool enabled, EgressRouteTarget? route = null,
        IReadOnlyDictionary<string, EgressRouteTarget>? routes = null, CancellationToken cancellationToken = default)
    {
        var next = Profile.Applications.ToDictionary(item => item.DiscoveryKey);
        foreach (var target in targets)
            if (!enabled) next.Remove(target.DiscoveryKey);
            else next[target.DiscoveryKey] = EgressController.Launcher.Discovery.ApplicationInventorySnapshot.Create([target])
                .CaptureSelection(target.DiscoveryKey, route ?? routes?.GetValueOrDefault(target.DiscoveryKey) ?? EgressRouteTarget.DefaultAdapter,
                    next.GetValueOrDefault(target.DiscoveryKey), target.OwnedRoots);
        return Save(Profile with { Applications = next.Values.ToArray() });
    }
    public Task<ControllerOperationResult> SetRuleSetAsync(string name, bool enabled, EgressRouteTarget? route = null, CancellationToken cancellationToken = default)
        => SetRuleSetsAsync([name], enabled, route, cancellationToken: cancellationToken);
    public Task<ControllerOperationResult> SetRuleSetsAsync(IEnumerable<string> names, bool enabled, EgressRouteTarget? route = null,
        IReadOnlyDictionary<string, EgressRouteTarget>? routes = null, CancellationToken cancellationToken = default)
    {
        var next = Profile.RuleSets.ToDictionary(item => item.Name);
        foreach (string name in names) if (!enabled) next.Remove(name); else next[name] = new() { Name = name, Target = route ?? routes?.GetValueOrDefault(name) ?? EgressRouteTarget.DefaultAdapter };
        return Save(Profile with { RuleSets = next.Values.ToArray() });
    }
    public Task<ControllerOperationResult> AddManualDomainAsync(string domain, EgressRouteTarget? route = null, CancellationToken cancellationToken = default)
        => Save(Profile with { Domains = Profile.Domains.Where(item => item.Name != domain).Append(new EgressNamedRoute { Name = EgressProfileDocument.NormalizeDomain(domain), Target = route ?? EgressRouteTarget.DefaultAdapter }).ToArray() });
    public Task<ControllerOperationResult> RemoveManualDomainAsync(string domain, CancellationToken cancellationToken = default) => Save(Profile with { Domains = Profile.Domains.Where(item => item.Name != domain).ToArray() });
    public Task<ControllerOperationResult> AddUpstreamPortAsync(int port, CancellationToken cancellationToken = default) => Save(Profile.AddPort(port));
    public Task<ControllerOperationResult> RemoveUpstreamPortAsync(int port, CancellationToken cancellationToken = default) => Save(Profile.RemovePort(port));
    public Task<ControllerOperationResult> SetDefaultUpstreamPortAsync(int port, CancellationToken cancellationToken = default) => Save(Profile.SetDefaultPort(port));
    public Task<ControllerOperationResult> CheckInternetHealthAsync(CancellationToken cancellationToken = default) { if (!IsTunRunning) return Task.FromResult(new ControllerOperationResult(false, "TUN 未运行，暂不能检测联网。")); _scenario = "ready"; return Task.FromResult(ControllerOperationResult.Success()); }
    public Task<ControllerOperationResult> CloseAllConnectionsAsync(CancellationToken cancellationToken = default) { ConnectionHistory.ApplySnapshot([]); return Task.FromResult(ControllerOperationResult.Success()); }
    public Task<ControllerOperationResult> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken = default) { ConnectionHistory.MarkClosed(connectionId); return Task.FromResult(ControllerOperationResult.Success()); }
    public Task<SingBoxCatalogUpdateResult> RefreshCatalogAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SingBoxCatalogUpdateResult(true, Catalog, null));
    public void ClearConnectionHistory() => ConnectionHistory.ClearClosed();
    public void ClearProtectionHistory() => _events.Clear();
    public void ClearQuotaUsage() => Quota = Quota with { UsedBytes = 0 };
    public void ConfigureQuota(decimal totalGigabytes, decimal remainingGigabytes)
    {
        if (totalGigabytes < 0 || remainingGigabytes < 0 || remainingGigabytes > totalGigabytes) throw new ArgumentOutOfRangeException(nameof(totalGigabytes), "额度不能为负，剩余额度须在总额范围内。");
        Quota = new(checked((long)Math.Round(totalGigabytes * 1073741824, MidpointRounding.AwayFromZero)), checked((long)Math.Round(remainingGigabytes * 1073741824, MidpointRounding.AwayFromZero)), 0, DateTimeOffset.UtcNow);
    }
    public ControllerOperationResult ReportFailure(string stage, string code, string message, Exception? error = null) { Diagnostics.Report(stage, code, message, error); return new(false, message); }
    private static NetworkAdapterInfo Adapter(Guid id, string name, string description, string address, int index) => new()
    { Identity = new(id, name), Description = description, Luid = (ulong)index, IfIndex = index, Ipv6IfIndex = index,
        IsUp = true, Addresses = [IPAddress.Parse(address)], Gateways = [], DnsServers = [], InterfaceType = 6 };

    private sealed class PreviewClock : TimeProvider
    {
        private long _seconds;
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow.AddSeconds(-60);
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        public override DateTimeOffset GetUtcNow() => _start.AddSeconds(_seconds);
        public void Advance() => _seconds++;
    }

    private sealed class PreviewGuardClient(PreviewController controller) : IGuardManagementClient
    {
        private bool _running = controller._scenario != "guard-stopped", _installed = true, _automatic = true;
        public Task<GuardPanelSnapshot> ReadAsync(CancellationToken token = default) => Task.FromResult(new GuardPanelSnapshot(
            new("EgressController.Guard.Preview", !_installed ? GuardServiceState.Missing : _running ? GuardServiceState.Running : GuardServiceState.Stopped,
                _automatic, _running ? 14800u : 0u, @"C:\Program Files\EgressController\Guard\guard-service.exe", "LocalSystem"),
            _running ? new(!controller.IsTunRunning || controller._scenario == "guard-main-absent", controller._scenario == "guard-main-absent" ? "主程序未运行，持续保护最近保存的 EXE 清单" : controller.IsTunRunning ? "主程序在线，TUN 已接管" : "等待主程序完成 TUN 接管",
                controller.Profile.Applications.SelectMany(app => app.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).Count(), 0, "preview", controller._scenario == "guard-records" ? [
                    new(DateTimeOffset.UtcNow.AddMinutes(-8), "ChatGPT", @"C:\Applications\ChatGPT\ChatGPT.exe", 14200, true, "独立守护：主程序已退出", null),
                    new(DateTimeOffset.UtcNow.AddMinutes(-12), "Claude", @"C:\Applications\Claude\tools\helper.exe", 14600, false, "独立守护：TUN 尚未接管", "模拟：无法确认该实例已退出")
                ] : []) : null, !_running, controller._scenario == "guard-unavailable" ? "模拟：守护状态读取失败，请刷新或查看服务日志" : null));
        public Task ExecuteAsync(GuardAction action, CancellationToken token = default)
        {
            switch (action)
            {
                case GuardAction.Install: _installed = _running = true; break;
                case GuardAction.Start: _running = true; break;
                case GuardAction.Stop: case GuardAction.ForceStop: _running = false; break;
                case GuardAction.StopAndDisable: _running = _automatic = false; break;
                case GuardAction.EnableAutoStart: _automatic = true; break;
                case GuardAction.DisableAutoStart: _automatic = false; break;
                case GuardAction.Uninstall: _installed = _running = _automatic = false; break;
            }
            return Task.CompletedTask;
        }
        public Task ClearHistoryAsync(CancellationToken token = default) => Task.CompletedTask;
    }
}
