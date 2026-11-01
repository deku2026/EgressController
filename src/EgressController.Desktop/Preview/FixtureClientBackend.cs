using System.Text;
using EgressController.Desktop.Services;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Desktop.Preview;

/// <summary>Explicit preview/test adapter. Production composition never creates this type.</summary>
public sealed class FixtureClientBackend : IClientBackend
{
    private readonly ConfigurationCoordinator _coordinator;
    private readonly FixtureResourceTransport _transport = new();
    private readonly ResourcePreparation _preparation;
    private readonly Dictionary<string, GeoRuleSet> _sources = new(StringComparer.Ordinal);
    private readonly string _fileKey = Guid.NewGuid().ToString("N");
    private string? _assetKey;
    private ClientPreparedResources _prepared = new([], new Dictionary<string, byte[]>(), new Dictionary<string, GeoRuleSet>());
    private bool _running = true;
    public bool IsPreview => true;
    public DateTimeOffset Now => PreviewFixtures.ObservationTime;
    public string LogDirectory => PreviewEnvironment.SessionDirectory;
    public MockCoreRuntime Core { get; } = new();
    public BoundedLifecycleLog Lifecycle { get; } = new();
    public string? SavedPath { get; private set; }
    public int RequestCount => _transport.RequestCount;
    public bool SimulateHomeFailure { get; set; }
    public string HomeFailureCode { get; set; } = "timeout";
    public TaskCompletionSource? HomeCheckGate { get; set; }
    public Exception? HomeCheckError { get; set; }
    public int HomeChecks { get; private set; }
    public bool SimulateSubscriptionDnsFailure { get; set; }
    public async Task<InterfaceConnectivityResult> CheckHomeConnectivityAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        HomeChecks++; cancellationToken.ThrowIfCancellationRequested();
        if (HomeCheckGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (HomeCheckError is { } error) throw error;
        return new(!SimulateHomeFailure, document.Interfaces.HomeId, "en5", "cloudflare-204", SimulateHomeFailure ? HomeFailureCode : "", 42);
    }
    public bool SimulateHelperUnavailable { get; set; }
    public bool SimulateRepairFailure { get; set; }
    public int RepairCount { get; private set; }
    public FixtureClientBackend()
    {
        PreviewEnvironment.RequireIsolated();
        SetSubscriptionPayload(PreviewFixtures.SubscriptionYaml);
        _preparation = new(_transport, new InMemoryResourceCache());
        _coordinator = new(Core, new InMemoryGenerationStore(), Lifecycle);
    }
    public void SetSubscriptionPayload(string yaml) => _transport.Add(new Uri(PreviewFixtures.SubscriptionUrl), Encoding.UTF8.GetBytes(yaml));
    public string? LoadIssue { get; set; }
    public Exception? LoadFailure { get; set; }
    public TaskCompletionSource? LoadGate { get; set; }
    public int LoadCalls { get; private set; }
    public bool LongLabels { get; set; }
    public MacPolicyDocument? WorkspaceDocument { get; set; }
    public IReadOnlyList<ClientInterface>? InterfaceOverride { get; set; }
    public int SaveCalls { get; private set; }
    public async Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); LoadCalls++;
        if (LoadGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (LoadFailure is { } error) throw error;
        return new ClientBootstrap(WorkspaceDocument ?? PreviewFixtures.CreateDocument(),
            InterfaceOverride ?? [new("fixture-ethernet", "en5", (LongLabels ? new string('W', 150) : "USB 2.5G Ethernet"), "家宽 · 有线连接", true), new("fixture-wifi", "en0", "Wi-Fi", "非家宽 · 无线连接", true), new("fixture-usb", "en9", "USB LAN", "未连接 · 示例接口", false)],
            Catalog(), true, new(_running, true, true, "隔离示例运行"), LoadIssue);
    }
    private static IReadOnlyList<ClientApplication> Catalog() => PreviewFixtures.InstalledApplications.Select(app => new ClientApplication(app.Id, app.Name, app.Id == "codex" ? "codex" : app.Product, app.Category, app.BundlePath, app.PrimaryExecutable, [], false, "example.preview." + app.Id)).ToArray();
    public Task<IReadOnlyList<ClientApplication>> DiscoverApplicationsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Catalog());
    public Task<ClientApplication> BuildInventoryAsync(ClientApplication application, IReadOnlyList<string> savedPaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fixture = PreviewFixtures.InstalledApplications.FirstOrDefault(app => app.BundlePath == application.BundlePath);
        if (fixture is null) return Task.FromResult(application with { InventoryError = "该应用没有可用的示例清单。", InventoryComplete = false });
        var paths = savedPaths.Concat(fixture.ExecutablePaths).Distinct(StringComparer.Ordinal).ToList();
        if (savedPaths.Count > 0)
        {
            string helper = $"{fixture.BundlePath}/Contents/Frameworks/{fixture.Name} Helper (Network).app/Contents/MacOS/{fixture.Name} Helper (Network)";
            if (!paths.Contains(helper, StringComparer.Ordinal)) paths.Add(helper);
        }
        return Task.FromResult(application with { PrimaryExecutable = fixture.PrimaryExecutable, ExecutablePaths = paths, InventoryComplete = true, InventoryError = null });
    }
    public Task<byte[]?> LoadIconAsync(string bundlePath, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
    public Task<ConnectionWorkspaceSnapshot> ReadConnectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PreviewConnectionFixtures.Create());
    }
    public Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ClientRuleCatalogEntry> entries = new[] { ("openai", "OpenAI", "AI 服务 · 域名集合"), ("google", "Google", "域名 + IP · 一个条目统一管理"), ("youtube", "YouTube", "流媒体 · 域名 + IP"), ("github", "GitHub", "开发工具 · 域名集合"), ("netflix", "Netflix", "流媒体 · 域名集合"), ("apple", "Apple", "系统服务 · 域名集合") }
            .Select(item => new ClientRuleCatalogEntry(item.Item1, item.Item2, item.Item3, PreviewFixtures.Rule(item.Item1, item.Item2, ExitChoice.NonHome).Sources)).ToArray();
        return Task.FromResult(entries);
    }
    public async Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument current, CancellationToken cancellationToken = default)
    {
        if (url.AbsoluteUri != PreviewFixtures.SubscriptionUrl) throw new IOException("隔离预览仅支持示例订阅 URL；没有发送网络请求。");
        if (SimulateSubscriptionDnsFailure) throw new ResourcePreparationException("subscription-fixture", "subscription-direct:" + current.Interfaces.NonHomeId, 443,
            PreparationStage.Downloading, true, transportFailure: new(ResourceTransportStage.Dns, "timeout"), bsdName: "en0");
        ClashImportResult? parsed = null;
        var bytes = await _preparation.PrepareAsync(new ResourceDescriptor { Id = "fixture-subscription", Url = url, Exit = ResourceExitSnapshot.SystemDefault, CachePolicy = PreparationCachePolicy.Refresh }, payload => { parsed = ClashSubscriptionImporter.Import(Encoding.UTF8.GetString(payload), "fixture-subscription"); return parsed.IsSuccess; }, cancellationToken);
        parsed = ClashSubscriptionImporter.Import(Encoding.UTF8.GetString(bytes), "fixture-subscription");
        if (!parsed.IsSuccess) throw new IOException("订阅结构无效，原有选择与配置已保留。");
        var result = SubscriptionReconciler.Merge(current, "fixture-subscription", parsed);
        if (!result.Subscriptions.Any(source => source.Id == "fixture-subscription")) result.Subscriptions.Add(new() { Id = "fixture-subscription", Name = "我的示例订阅", Url = url.AbsoluteUri });
        return result;
    }
    public Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] sources = document.RuleSets.Where(rule => rule.Enabled).SelectMany(rule => rule.Sources).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (string source in sources)
        {
            if (_sources.ContainsKey(source) || !PreviewFixtures.RuleSourceYaml.TryGetValue(source, out string? yaml)) continue;
            string tag = "catalog-" + source.Replace('/', '-').Replace(".yaml", "", StringComparison.Ordinal);
            var set = GeoRuleCompiler.Parse(yaml, tag, source.StartsWith("geoip/", StringComparison.Ordinal) ? RuleDataKind.Ip : RuleDataKind.Domain);
            if (!set.IsSuccess) throw new IOException("示例规则资源未通过语义验证。");
            _sources.Add(source, set);
        }
        string key = string.Join("|", sources);
        if (_assetKey == key) return Task.FromResult(_prepared);
        var sets = sources.Where(_sources.ContainsKey).Select(source => _sources[source]).ToList();
        sets.Add(new() { Tag = "cn", Domains = [new(DomainMatchKind.Suffix, "fixture-china.example.invalid")], IpCidrs = ["198.51.100.0/24"] });
        var dat = GeoRuleCompiler.Compile(sets);
        var bindings = sources.Where(_sources.ContainsKey).Select(source => new RuleSourceBinding(source, _sources[source].Tag, _sources[source].Domains.Count > 0 ? "geosite.dat" : null, _sources[source].IpCidrs.Count > 0 ? "geoip.dat" : null)).ToArray();
        _prepared = new(bindings, new Dictionary<string, byte[]> { ["geosite.dat"] = dat.GeoSite, ["geoip.dat"] = dat.GeoIp }, _sources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        _assetKey = key;
        return Task.FromResult(_prepared);
    }
    public Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding, CancellationToken cancellationToken = default)
    {
        int index = 0; var probes = new List<GroupProbe>();
        foreach (var node in document.Nodes.Where(node => node.IsSupported))
        {
            double latency = new[] { 24d, 58d, 126d }[index++ % 3];
            foreach (string url in document.Groups.Select(group => group.TestUrl).Distinct(StringComparer.Ordinal)) probes.Add(new(node.Id, true, latency, Now, url));
        }
        return Task.FromResult<IReadOnlyList<GroupProbe>>(probes);
    }
    public async Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default)
    {
        var result = await _coordinator.ApplyAsync(document, options, assets, "用户应用隔离预览配置", cancellationToken);
        if (result.Succeeded) _running = true;
        else if (_coordinator.Applied is null) _running = false;
        return result;
    }
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        if (SimulateHelperUnavailable) return false;
        bool stopped = await _coordinator.StopAsync("用户停止隔离预览", cancellationToken);
        if (stopped) _running = false;
        return stopped;
    }
    public Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(SimulateHelperUnavailable
        ? new ClientRuntimeState(false, false, false, "模拟助手身份已过期，等待修复。")
        : new ClientRuntimeState(_running, true, true, _running ? "隔离模拟已就绪" : "模拟已手动停止"));
    public Task<bool> RepairNetworkAsync(CancellationToken cancellationToken = default)
    {
        RepairCount++;
        if (SimulateRepairFailure) return Task.FromResult(false);
        SimulateHelperUnavailable = false;
        return StopAsync(cancellationToken);
    }
    public Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); SaveCalls++; SavedPath = PrivatePreviewFiles.WriteText($"preview-policy-{_fileKey}.yaml", PolicyYaml.Write(document)); return Task.CompletedTask;
    }
    public string? ResetBackupPath { get; private set; }
    public Task<ConfigurationResetResult> ResetConfigurationAsync(MacPolicyDocument current, CancellationToken cancellationToken = default)
        => ConfigurationReset.ExecuteAsync(current, StopAsync, async (snapshot, token) =>
        {
            ResetBackupPath = PrivatePreviewFiles.WriteText($"preview-policy-before-reset-{_fileKey}.yaml", PolicyYaml.Write(snapshot));
            await SaveDraftAsync(new(), token);
        }, cancellationToken);
    public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default)
    {
        PrivatePreviewFiles.WriteText("preview-events.log", string.Join("\n", Lifecycle.Events.Select(item => $"{item.Time:HH:mm} [{item.Kind}] {item.Cause}"))); return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
