using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Routing;
using EgressController.Mac;
using EgressController.SubscriptionUI;

namespace EgressController.MacApp.ViewModels;

public sealed partial class MacMainViewModel : ObservableObject, IDisposable
{
    private readonly IMacWorkspace _workspace;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _pendingEdit;
    private MacSettings _settings = new();
    private bool _loading;
    private long _editVersion;
    private EgressController.Subscriptions.SurgeSource? _subscriptionSource;
    [ObservableProperty] private string _subscriptionNodesStatus = "尚未读取机场节点";
    public MacMainViewModel(IMacWorkspace workspace, SubscriptionViewModel subscriptions)
    {
        _workspace = workspace; Subscriptions = subscriptions;
        SelectedPage = Pages[0];
    }
    public bool IsPreview => _workspace.IsPreview;
    public SubscriptionViewModel Subscriptions { get; }
    public IReadOnlyList<NavigationEntry> Pages { get; } = [
        new("overview", "概览", "Surge 状态与配置进度，一目了然"), new("network", "网络与出口", "管理物理网卡与已有的本地 SOCKS5 端口"),
        new("apps", "应用分流", "选择 App Bundle 或原生命令，并指定出口"), new("rules", "域名规则", "为域名与 Surge 规则集选择出口"),
        new("subscriptions", "机场订阅", "按需获取订阅，校验并确认服务器接收"), new("configuration", "配置管理", "预览、校验并导出独立 Surge 配置"),
        new("events", "运行日志", "查看配置与订阅操作的历史记录"), new("settings", "设置", "连接 Surge，并配置本机运行环境")];
    [ObservableProperty] private NavigationEntry _selectedPage = null!;
    [ObservableProperty] private string _status = "正在读取本地配置";
    [ObservableProperty] private string _configStatus = "尚未生成";
    [ObservableProperty] private bool _surgeConnected;
    [ObservableProperty] private string _surgeStatus = "尚未连接";
    [ObservableProperty] private string _observedAt = "尚未检测";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _category = "全部";
    [ObservableProperty] private string _ruleSearch = "";
    [ObservableProperty] private string _manualPath = "";
    [ObservableProperty] private string _newPort = "7890";
    [ObservableProperty] private AdapterRow? _proxyAdapter;
    [ObservableProperty] private string _newDomain = "";
    [ObservableProperty] private RouteOption? _newDomainRoute;
    [ObservableProperty] private RouteOption? _defaultRoute;
    [ObservableProperty] private PortRow? _downloadPort;
    [ObservableProperty] private string _configText = "# 选择出口与分流规则后，生成完整 Surge 配置。\n# 校验不会加载或重载 Surge。";
    [ObservableProperty] private string _surgeEndpoint = "http://127.0.0.1:6171/";
    [ObservableProperty] private string _surgeKey = "";
    [ObservableProperty] private string _surgeCliPath = "/Applications/Surge.app/Contents/Applications/surge-cli";
    public ObservableCollection<AdapterRow> Adapters { get; } = [];
    public ObservableCollection<AppRow> Applications { get; } = [];
    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<PortRow> Ports { get; } = [];
    public ObservableCollection<DomainRow> Domains { get; } = [];
    public ObservableCollection<LogRow> Logs { get; } = [];
    public IReadOnlyList<string> Categories { get; } = ["全部", "未分类", "AI", "浏览器", "开发", "社交", "影音", "游戏", "办公"];
    public IReadOnlyList<RouteOption> RouteOptions { get; private set; } = [new("系统直连", new())];
    public IReadOnlyList<AppRow> VisibleApplications { get; private set; } = [];
    public IReadOnlyList<RuleRow> VisibleRules { get; private set; } = [];
    public int SelectedApps => Applications.Count(x => x.Selected);
    public int SelectedRules => Rules.Count(x => x.Selected) + Domains.Count;
    public string SelectedAdapters => string.Join(" · ", Adapters.Where(a => a.Selected).Select(a => a.Name));
    public string ExportLocation => Path.Combine(_workspace.DirectoryPath, "profiles", "last-valid.conf");
    public string NetworkSummary => Ports.Count == 0 ? "尚未添加本地代理" : $"{Ports.Count} 个本地端口 · {Adapters.Count(a => a.Selected)} 张已选网卡";
    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            _loading = true;
            try
            {
                var snapshot = await _workspace.LoadAsync(_lifetime.Token);
                _settings = snapshot.Settings; SurgeEndpoint = _settings.SurgeEndpoint; SurgeCliPath = _settings.SurgeCliPath;
                foreach (var adapter in snapshot.Adapters.Concat(snapshot.Profile.Adapters.Where(s => snapshot.Adapters.All(a => a.Id != s.Id)).Select(s => new ResolvedAdapter(s.Id, s.Label, "—", false, "已保存网卡暂不可用"))))
                    Adapters.Add(new() { Adapter = adapter, Selected = snapshot.Profile.Adapters.Any(s => s.Id == adapter.Id), Changed = Edit });
                foreach (var port in snapshot.Profile.Ports) Ports.Add(new(port, "尚未检测"));
                _subscriptionSource = await _workspace.ReadSubscriptionAsync(_lifetime.Token);
                SubscriptionNodesStatus = _subscriptionSource is null ? "尚无已校验 Surge 订阅" : $"{_subscriptionSource.Names.Length} 个机场节点";
                UpdateRoutes();
                DefaultRoute = FindRoute(snapshot.Profile.DefaultRoute); DownloadPort = Ports.FirstOrDefault(p => p.Port == snapshot.Profile.DefaultDownloadPort);
                ProxyAdapter = Adapters.FirstOrDefault(a => a.Selected);
                foreach (var app in snapshot.Applications.Concat(snapshot.Profile.Applications.Where(s => snapshot.Applications.All(a => a.Path != s.Path)).Select(s => new MacApplication(s.Id, s.Name, s.Path, s.Bundle, null))))
                {
                    var saved = snapshot.Profile.Applications.FirstOrDefault(s => s.Path == app.Path);
                    Applications.Add(new() { App = app, Selected = saved is not null, Route = FindRoute(saved?.Target ?? new()), Category = snapshot.Profile.ApplicationMetadata.FirstOrDefault(m => m.Path == app.Path)?.Category ?? saved?.Category ?? "未分类", Routes = RouteOptions, Changed = Edit });
                }
                foreach (var rule in snapshot.Profile.RuleSets) Rules.Add(new() { Id = rule.Id, Name = rule.Name, Url = rule.Url, Selected = true, Route = FindRoute(rule.Target), Routes = RouteOptions, Changed = Edit });
                foreach (var domain in snapshot.Profile.Domains) Domains.Add(new(domain, FindRoute(domain.Target).Label));
                foreach (var row in Applications) { if (IsPreview) row.Icon = Preview.MockIcons.Load(row.Name); else _ = LoadIconAsync(row); }
                FilterApps(); FilterRules(); RefreshSummary();
                Status = "本地配置已读取"; ConfigStatus = "已保存 · 待校验";
                await Subscriptions.InitializeAsync();
                if (IsPreview) { await LoadRulesCoreAsync(false); await CheckSurgeCoreAsync(); }
            }
            finally { _loading = false; }
        });
    }
    private async Task LoadIconAsync(AppRow row)
    {
        string? iconPath = row.App.IconPath;
        if (iconPath is null && row.IsCli)
            iconPath = Applications.FirstOrDefault(a => !a.IsCli && string.Equals(a.Name, row.Name.Replace(" CLI", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))?.App.IconPath;
        var image = await MacIconCache.LoadAsync(iconPath);
        if (!_lifetime.IsCancellationRequested) row.Icon = image;
    }
    private RouteOption FindRoute(RouteDestination target) => RouteOptions.FirstOrDefault(r => r.Destination == target) ?? new("已保存出口 · 待确认", target);
    private void UpdateRoutes()
    {
        RouteOptions = new[] { new RouteOption("系统直连", new()) }.Concat(Adapters.Where(a => a.Selected).Select(a => new RouteOption(a.Name + " · 直连", RouteDestination.Adapter(a.Id))))
            .Concat(Ports.Select(p => new RouteOption(p.Name + " · " + p.Port, RouteDestination.Socks(p.Port)))).Concat((_subscriptionSource?.Names ?? []).Select(n => new RouteOption(n, new() { Kind = "subscription", Id = n }))).ToArray();
        OnPropertyChanged(nameof(RouteOptions));
        foreach (var app in Applications) app.ReplaceRoutes(RouteOptions);
        foreach (var rule in Rules) rule.ReplaceRoutes(RouteOptions);
        NewDomainRoute ??= RouteOptions[0]; DefaultRoute ??= RouteOptions[0];
    }
    public RoutingProfile Snapshot() => new()
    {
        Adapters = Adapters.Where(a => a.Selected).Select(a => new AdapterSelection(a.Id, a.Name)).ToArray(),
        Ports = Ports.Select(p => p.Proxy).ToArray(), DefaultDownloadPort = DownloadPort?.Port,
        Applications = Applications.Where(a => a.Selected).Select(a => new ApplicationRoute(a.App.Id, a.Name, a.Path, a.App.Bundle, a.Route?.Destination ?? new(), a.Category)).ToArray(),
        ApplicationMetadata = Applications.Where(a => a.Category != "未分类").Select(a => new ApplicationMetadata(a.Path, a.Category)).ToArray(),
        Domains = Domains.Select(d => d.Rule).ToArray(), RuleSets = Rules.Where(r => r.Selected).Select(r => new RuleSetRoute(r.Id, r.Name, r.Url, r.Route?.Destination ?? new())).ToArray(),
        DefaultRoute = DefaultRoute?.Destination ?? new()
    };
    private void Edit()
    {
        if (_loading) return;
        _editVersion++;
        UpdateRoutes(); RefreshSummary(); ConfigStatus = "正在保存";
        _pendingEdit?.Cancel();
        var owned = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _pendingEdit = owned;
        _ = SaveEditedAsync(owned);
    }
    private async Task SaveEditedAsync(CancellationTokenSource owned)
    {
        bool saved = false;
        try
        {
            await Task.Delay(450, owned.Token); var profile = Snapshot();
            await _workspace.SaveAsync(profile, owned.Token); saved = true;
            owned.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_pendingEdit, owned)) return;
            ConfigStatus = "已保存 · 待校验"; Status = "选择已自动保存";
            // Edits compile a preview; native validation remains an explicit command.
            ConfigText = new EgressController.Surge.SurgeConfigCompiler().Compile(profile, Adapters.Select(a => a.Adapter).ToArray(), _subscriptionSource?.ProxySection);
        }
        catch (OperationCanceledException) when (owned.IsCancellationRequested) { }
        catch (Exception ex) { if (ReferenceEquals(_pendingEdit, owned)) { Status = ex.Message; ConfigStatus = saved ? "已保存 · 需要完善配置" : "保存失败 · 请重试"; } }
        finally { if (ReferenceEquals(_pendingEdit, owned)) _pendingEdit = null; owned.Dispose(); }
    }
    private void RefreshSummary() { OnPropertyChanged(nameof(SelectedApps)); OnPropertyChanged(nameof(SelectedRules)); OnPropertyChanged(nameof(SelectedAdapters)); OnPropertyChanged(nameof(NetworkSummary)); }
    partial void OnDefaultRouteChanged(RouteOption? value) => Edit();
    partial void OnDownloadPortChanged(PortRow? value) => Edit();
    partial void OnSearchChanged(string value) => FilterApps();
    partial void OnCategoryChanged(string value) => FilterApps();
    partial void OnRuleSearchChanged(string value) => FilterRules();
    private void FilterApps() { VisibleApplications = Applications.Where(a => (Category == "全部" || a.Category == Category) && (a.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Path.Contains(Search, StringComparison.OrdinalIgnoreCase))).ToArray(); OnPropertyChanged(nameof(VisibleApplications)); }
    private void FilterRules() { VisibleRules = Rules.Where(r => r.Name.Contains(RuleSearch, StringComparison.OrdinalIgnoreCase)).ToArray(); OnPropertyChanged(nameof(VisibleRules)); }
    [RelayCommand] private Task CheckSurgeAsync() => RunAsync(CheckSurgeCoreAsync);
    private async Task CheckSurgeCoreAsync()
    {
        SurgeConnected = false; SurgeStatus = "正在连接";
        try { var status = await _workspace.CheckSurgeAsync(_settings with { SurgeEndpoint = SurgeEndpoint }, SurgeKey, _lifetime.Token); SurgeConnected = status.Connected; SurgeStatus = status.Summary; ObservedAt = status.ObservedAt.ToLocalTime().ToString("HH:mm:ss"); }
        catch { SurgeStatus = "连接失败 · 请检查设置"; throw; }
    }
    [RelayCommand] private Task PrepareAsync() => RunAsync(async () =>
    {
        _pendingEdit?.Cancel(); var profile = Snapshot(); long version = _editVersion; await _workspace.SaveAsync(profile, _lifetime.Token); ConfigStatus = "正在校验";
        try { var result = await _workspace.PrepareAsync(profile, _lifetime.Token); if (version == _editVersion) { ConfigText = result.Content; ConfigStatus = result.Validation.Summary; } else ConfigStatus = "旧快照已检查 · 新更改待校验"; AddLog("配置校验", result.Validation.Summary); }
        catch { ConfigStatus = "校验未通过 · 原配置保留"; throw; }
    });
    [RelayCommand] private Task RestoreAsync() => RunAsync(async () => { ConfigText = await _workspace.RestoreAsync(_lifetime.Token); ConfigStatus = "已恢复上次有效文件 · 尚未应用"; AddLog("恢复配置", ConfigStatus); });
    [RelayCommand] private Task ReloadSubscriptionNodesAsync() => RunAsync(async () =>
    {
        _subscriptionSource = await _workspace.ReadSubscriptionAsync(_lifetime.Token);
        SubscriptionNodesStatus = _subscriptionSource is null ? "尚无已校验 Surge 订阅" : $"{_subscriptionSource.Names.Length} 个机场节点已读取";
        UpdateRoutes(); Status = SubscriptionNodesStatus;
    });
    [RelayCommand] private Task RefreshAdaptersAsync() => RunAsync(async () =>
    {
        var live = await _workspace.RefreshAdaptersAsync(_lifetime.Token);
        foreach (var row in Adapters) row.Update(live.FirstOrDefault(a => a.Id == row.Id) ?? row.Adapter with { Available = false, Address = "" });
        foreach (var adapter in live) if (Adapters.All(a => a.Id != adapter.Id)) Adapters.Add(new() { Adapter = adapter, Changed = Edit });
        UpdateRoutes(); RefreshSummary(); Status = "网卡状态已刷新 · 原选择保持不变";
    });
    [RelayCommand] private Task RefreshApplicationsAsync() => RunAsync(async () =>
    {
        foreach (var app in await _workspace.RefreshApplicationsAsync(_lifetime.Token))
            if (Applications.All(a => a.Path != app.Path)) Applications.Add(new() { App = app, Route = RouteOptions[0], Routes = RouteOptions, Changed = Edit });
        foreach (var row in Applications.Where(a => a.Icon is null)) if (!IsPreview) _ = LoadIconAsync(row);
        FilterApps(); Status = "应用列表已更新 · 原选择与顺序保留";
    });
    [RelayCommand] private Task AddApplicationAsync() => RunAsync(async () =>
    {
        var app = await _workspace.InspectApplicationAsync(ManualPath, _lifetime.Token);
        if (Applications.All(a => a.Path != app.Path)) Applications.Add(new() { App = app, Route = RouteOptions[0], Routes = RouteOptions, Changed = Edit });
        ManualPath = ""; FilterApps();
    });
    [RelayCommand] private Task AddPortAsync() => RunAsync(async () =>
    {
        if (!int.TryParse(NewPort, out int port) || port is < 1 or > 65535) throw new ArgumentException("请输入 1–65535 的端口。");
        if (Ports.Any(p => p.Port == port)) throw new ArgumentException("该端口已经存在。");
        if (ProxyAdapter is not { Selected: true, Available: true }) throw new ArgumentException("先选择已连接的代理核心物理出口。");
        var result = await _workspace.ProbeAsync(port, _lifetime.Token);
        if (!result.Socks5 || result.OwnerPath.Length == 0) throw new InvalidOperationException(result.Summary + "；需要可确认的核心路径。");
        var row = new PortRow(new(port, "本地代理", result.OwnerPath, ProxyAdapter.Id), result.Summary); Ports.Add(row); DownloadPort ??= row; Edit();
    });
    [RelayCommand] private void RemovePort(PortRow row)
    {
        var route = RouteDestination.Socks(row.Port);
        if (Snapshot().Applications.Any(a => a.Target == route) || Snapshot().Domains.Any(d => d.Target == route) || Snapshot().RuleSets.Any(r => r.Target == route) || DefaultRoute?.Destination == route)
        { Status = "该端口仍被规则引用，请先修改对应出口。"; return; }
        Ports.Remove(row); if (DownloadPort == row) DownloadPort = null; Edit();
    }
    [RelayCommand] private void AddDomain()
    {
        var domain = NewDomain.Trim(); if (Uri.CheckHostName(domain) != UriHostNameType.Dns) { Status = "请输入不含协议与路径的域名。"; return; }
        if (Domains.Any(d => string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase))) { Status = "该域名已存在。"; return; }
        var route = NewDomainRoute ?? RouteOptions[0]; Domains.Add(new(new(domain, route.Destination), route.Label)); NewDomain = ""; Edit();
    }
    [RelayCommand] private void RemoveDomain(DomainRow row) { Domains.Remove(row); Edit(); }
    [RelayCommand] private Task RefreshRulesAsync() => RunAsync(() => LoadRulesCoreAsync(true));
    private async Task LoadRulesCoreAsync(bool refresh)
    {
        foreach (var rule in await _workspace.RulesAsync(refresh, _lifetime.Token))
            if (Rules.All(r => r.Url != rule.Url)) Rules.Add(new() { Id = rule.Id, Name = rule.Name, Url = rule.Url, Route = RouteOptions[0], Routes = RouteOptions, Changed = Edit });
        FilterRules(); Status = "规则目录已更新 · 已保存规则保持不变";
    }
    [RelayCommand] private void SelectVisibleRules() { _loading = true; foreach (var rule in VisibleRules) rule.Selected = true; _loading = false; Edit(); }
    [RelayCommand] private void ClearVisibleRules() { _loading = true; foreach (var rule in VisibleRules) rule.Selected = false; _loading = false; Edit(); }
    [RelayCommand] private Task SaveSettingsAsync() => RunAsync(async () =>
    {
        var settings = _settings with { SurgeEndpoint = SurgeEndpoint, SurgeCliPath = SurgeCliPath };
        await _workspace.SaveSettingsAsync(settings, SurgeKey, _lifetime.Token); _settings = settings; SurgeKey = ""; Status = "设置已保存 · 密钥保存在系统 Keychain";
    });
    [RelayCommand] private Task OpenLogsAsync() => RunAsync(_workspace.OpenLogsAsync);
    public void Navigate(string key) => SelectedPage = Pages.First(p => p.Key == key);
    private async Task RunAsync(Func<Task> action)
    {
        if (Busy) return; Busy = true;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Status = ex.Message; AddLog("操作未完成", ex.Message); }
        finally { Busy = false; }
    }
    private void AddLog(string title, string detail) { Logs.Insert(0, new(DateTimeOffset.Now.ToString("HH:mm:ss"), title, detail)); while (Logs.Count > 100) Logs.RemoveAt(Logs.Count - 1); }
    public void Dispose() { _pendingEdit?.Cancel(); _lifetime.Cancel(); _lifetime.Dispose(); Subscriptions.Dispose(); }
}
