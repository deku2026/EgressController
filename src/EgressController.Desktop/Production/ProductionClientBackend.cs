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
    private readonly ConfigurationCoordinator _coordinator;
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly string _root, _staging;
    private MacRuntimeLaunch? _launch;
    private bool _disposed;
    public bool IsPreview => false;
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public string LogDirectory { get; }

    public ProductionClientBackend()
    {
        _gate = LiveOperationGate.ForExplicitLiveUse();
        _platform = new(_gate); _native = new(_gate);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _root = Path.Combine(home, "Library", "Application Support", "EgressController");
        _staging = Path.Combine(_root, "helper-staging");
        LogDirectory = Path.Combine(home, "Library", "Logs", "EgressController");
        _catalog = new(new PhysicalMacCatalogFileSystem(_gate, _platform), _platform, MacApplicationCatalog.StandardRoots(home));
        _icons = new(_platform); _helper = new(_gate); _cache = new PrivateDiskResourceCache(Path.Combine(_root, "resource-cache"));
        _drafts = new(Path.Combine(_root, "configuration"));
        var runtime = new MacXrayCoreRuntime(_helper, _staging, _ => Task.FromResult(_launch ?? throw new InvalidOperationException("No validated runtime launch is prepared.")));
        _coordinator = new(runtime, new AtomicGenerationStore(Path.Combine(_root, "runtime")), new PrivateLifecycleFileLog(LogDirectory));
    }

    public async Task<ClientBootstrap> LoadAsync(CancellationToken cancellationToken = default)
    {
        _gate.Require("load production workspace");
        var document = await _drafts.LoadAsync(cancellationToken).ConfigureAwait(false);
        var interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false);
        var applications = await DiscoverApplicationsAsync(cancellationToken).ConfigureAwait(false);
        return new(document ?? new(), interfaces.Where(Physical).Select(item => new ClientInterface(item.ServiceId, item.BsdName,
            item.DisplayName, item.IsActive ? item.BsdName + " · 已连接" : item.BsdName + " · 未连接", item.IsActive)).ToArray(),
            applications, document is not null, await ReadStatusAsync(cancellationToken).ConfigureAwait(false));
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
    public async Task<IReadOnlyList<ClientRuleCatalogEntry>> LoadRuleCatalogAsync(InterfaceBinding binding, MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        await CheckBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        var catalog = await Resources(binding, document.Dns.UpstreamIp).LoadRuleCatalogAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return catalog.Entries.GroupBy(item => item.Name, StringComparer.Ordinal).Select(group =>
            new ClientRuleCatalogEntry(SafeId(group.Key), group.Key, group.Any(item => item.Kind == RuleDataKind.Ip) && group.Any(item => item.Kind != RuleDataKind.Ip)
                ? "域名 + IP 集合" : group.First().Kind == RuleDataKind.Ip ? "IP/CIDR 集合" : "域名集合",
                group.Select(item => item.Id.Replace(':', '/') + ".yaml").ToArray())).ToArray();
    }
    public async Task<MacPolicyDocument> ImportSubscriptionAsync(Uri url, MacPolicyDocument current, InterfaceBinding binding, CancellationToken cancellationToken = default)
    {
        await CheckBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        string sourceId = current.Subscriptions.FirstOrDefault(item => item.Url == url.AbsoluteUri)?.Id
            ?? (current.Subscriptions.Count == 1 ? current.Subscriptions[0].Id : "subscription-" + Digest(url.AbsoluteUri)[..24]);
        var source = new SubscriptionSource { Id = sourceId, Name = "节点订阅", Url = url.AbsoluteUri };
        var imported = await Resources(binding, current.Dns.UpstreamIp).FetchSubscriptionAsync(source, PreparationCachePolicy.Refresh, cancellationToken).ConfigureAwait(false);
        if (!imported.IsSuccess) throw new InvalidDataException("订阅结构无效，保留原有节点与选择。");
        var merged = SubscriptionReconciler.Merge(current, sourceId, imported);
        merged.Subscriptions.RemoveAll(item => item.Id == sourceId); merged.Subscriptions.Add(source); return merged;
    }
    public async Task<ClientPreparedResources> PrepareResourcesAsync(MacPolicyDocument document, InterfaceBinding binding, CancellationToken cancellationToken = default)
    {
        await CheckBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        var result = await Resources(binding, document.Dns.UpstreamIp).PrepareRulesAsync(document.RuleSets.Where(item => item.Enabled).SelectMany(item => item.Sources), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(result.Bindings, result.Assets, result.Sources);
    }
    public async Task<IReadOnlyList<GroupProbe>> ProbeAsync(MacPolicyDocument document, InterfaceBinding binding, CancellationToken cancellationToken = default)
    {
        await CheckBindingAsync(binding, cancellationToken).ConfigureAwait(false);
        var core = await CoreManager(binding, document.Dns.UpstreamIp).PrepareAsync(cancellationToken).ConfigureAwait(false);
        var interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false);
        var options = new XrayCompileOptions { Interfaces = interfaces.Where(Physical).Select(item => new InterfaceBinding(item.ServiceId, item.BsdName)).ToList() };
        return await new ObservatoryProbes(new XrayObservatoryProcess(() => _gate.Require("run isolated outbound probe without TUN"), Path.Combine(_root, "probes")))
            .ProbeAsync(document, options, core.ExecutablePath, cancellationToken).ConfigureAwait(false);
    }
    public async Task<ApplyResult> ApplyAsync(MacPolicyDocument document, XrayCompileOptions options, IReadOnlyDictionary<string, byte[]> assets, CancellationToken cancellationToken = default)
    {
        await _apply.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = PolicyYaml.Read(PolicyYaml.Write(document)).Document ?? throw new InvalidDataException("策略结构无效。");
            var compiled = XrayConfigCompiler.Compile(snapshot, options);
            if (!compiled.IsSuccess) return new(ApplyDisposition.Rejected, null, compiled.Diagnostics);
            var diagnostics = RuntimeAssetValidator.Validate(compiled.Json!, assets);
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return new(ApplyDisposition.Rejected, null, diagnostics);
            var binding = options.Interfaces.Single(item => item.StableId == snapshot.Interfaces.NonHomeId);
            await CheckBindingAsync(binding, cancellationToken).ConfigureAwait(false);
            var core = await CoreManager(binding, snapshot.Dns.UpstreamIp).PrepareAsync(cancellationToken).ConfigureAwait(false);
            _launch = new(core.ExecutablePath, core.Sha256, snapshot.Interfaces.HomeId, snapshot.Interfaces.NonHomeId);
            await EnsureHelperAsync(core, cancellationToken).ConfigureAwait(false);
            await _drafts.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return await _coordinator.ApplyAsync(snapshot, options, assets, "用户应用生产策略", cancellationToken).ConfigureAwait(false);
        }
        finally { _apply.Release(); }
    }
    public Task<bool> StopAsync(CancellationToken cancellationToken = default) => _coordinator.StopAsync("用户停止网络接管", cancellationToken);
    public async Task<ClientRuntimeState> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(MacHelperEndpoint.Installed.ExpectedHelperPath))
                return new(false, true, false, "首次连接时请求安装特权助手；当前未接管系统网络。");
            var state = await _helper.InspectAsync(cancellationToken).ConfigureAwait(false);
            bool healthy = state.Running && state.TunTakenOver && state.Failure is null;
            return new(healthy, state.Installed && state.Failure is null, state.Installed,
                healthy ? "Xray 与双栈 TUN 接管已确认。" : state.Failure is not null ? "当前接管未获确认，请查看诊断。" : state.Installed ? "已停止接管，系统 DNS 已恢复。" : "启动时将请求安装特权助手。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        { return new(false, false, false, "特权助手未安装或当前不可用；尚未接管系统网络。"); }
    }
    public Task SaveDraftAsync(MacPolicyDocument document, CancellationToken cancellationToken = default) => _drafts.SaveAsync(document, cancellationToken);
    public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default)
    {
        _gate.Require("open lifecycle log directory"); cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(LogDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add(LogDirectory);
        using var child = Process.Start(start); return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        try { if (_coordinator.Applied is not null || _helper.Status.Running) await StopAsync().ConfigureAwait(false); }
        finally { await _helper.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task EnsureHelperAsync(PreparedCoreRelease core, CancellationToken cancellationToken)
    {
        var status = await _helper.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (status.Installed && status.InstalledCoreSha256 == core.Sha256) return;
        string gui = Environment.ProcessPath ?? throw new IOException("无法定位当前客户端。");
        if (Path.GetFileName(gui) != "EgressController.Desktop") throw new IOException("网络接管需要使用已打包的 Egress.app；不要以 root 身份运行 GUI。");
        string helper = Path.Combine(AppContext.BaseDirectory, "helper", "EgressController.MacHelper");
        string library = Path.Combine(AppContext.BaseDirectory, "helper", "libEgressMacNative.dylib");
        var installation = new MacHelperInstallation(2, _native.GetUserId(), _native.GetGroupId(), gui, await FileHashAsync(gui, cancellationToken).ConfigureAwait(false),
            _staging, core.Sha256, await FileHashAsync(helper, cancellationToken).ConfigureAwait(false), await FileHashAsync(library, cancellationToken).ConfigureAwait(false));
        await new MacHelperInstaller(_gate).InstallAsync(new(helper, library, core.ExecutablePath, installation), cancellationToken).ConfigureAwait(false);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                var fresh = await _helper.InspectAsync(readiness.Token).ConfigureAwait(false);
                if (fresh.Installed && fresh.InstalledCoreSha256 == core.Sha256) return;
                await Task.Delay(200, readiness.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("助手安装完成后尚未建立受信连接；未启动 TUN，请查看诊断后重试。"); }
    }
    private ProductionResources Resources(InterfaceBinding binding, string dns) => new(Transport(dns), _cache, _ => ValueTask.FromResult(binding));
    private XrayCoreReleaseManager CoreManager(InterfaceBinding binding, string dns) => new(Transport(dns), _cache, Path.Combine(_root, "cores"), _ => ValueTask.FromResult(binding));
    private IResourceTransport Transport(string dns)
    {
        var binder = new MacPhysicalSocketBinder(_native);
        if (!IPAddress.TryParse(dns, out var resolver) || IPAddress.IsLoopback(resolver) || resolver.Equals(IPAddress.Any) || resolver.Equals(IPAddress.IPv6Any))
            throw new InvalidDataException("资源解析器需要明确的非回环 DNS IP 地址。");
        return new BoundHttpsResourceTransport(binder, new BoundDnsResolver(binder, resolver));
    }
    private async Task CheckBindingAsync(InterfaceBinding binding, CancellationToken cancellationToken)
    {
        var interfaces = await Task.Run(_platform.ReadInterfaces, cancellationToken).ConfigureAwait(false);
        if (!interfaces.Any(item => Physical(item) && item.ServiceId == binding.StableId && item.BsdName == binding.BsdName && item.IsActive))
            throw new IOException("所选非家宽接口已失效或身份变化；不会改走其他网卡。");
    }
    private static bool Physical(MacNetworkInterface item) => !item.BsdName.StartsWith("utun", StringComparison.Ordinal) && item.BsdName != "lo0";
    private static ClientApplication MapApplication(MacApplication item)
    {
        string product = item.BundleIdentifier switch { "com.openai.chat" or "com.openai.chatgpt" => "openai", "com.openai.codex" => "codex",
            "com.anthropic.claudefordesktop" => "claude", "com.todesktop.230313mzl4w4u92" => "cursor", "com.google.Chrome" => "chrome", "com.apple.Safari" => "safari",
            "com.microsoft.VSCode" => "vscode", "notion.id" => "notion", "com.tinyspeck.slackmacgap" => "slack", "com.spotify.client" => "spotify", _ => "app" };
        return new("app-" + Digest(item.Id)[..24], item.DisplayName, product, "macOS App", item.BundlePath, item.PrimaryExecutablePath, item.ExecutablePaths, false, item.BundleIdentifier);
    }
    private static string SafeId(string value) => value.Length <= 100 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? value : "catalog-" + Digest(value)[..24];
    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static async Task<string> FileHashAsync(string path, CancellationToken cancellationToken)
    { await using var file = File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)); }
}
