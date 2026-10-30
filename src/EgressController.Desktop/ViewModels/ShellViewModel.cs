using System.Collections.ObjectModel;
using System.Net;
using EgressController.Policy;
using EgressController.Runtime;
using EgressController.Desktop.Preview;
using EgressController.Desktop.Presentation.Controls;
namespace EgressController.Desktop.ViewModels;

/// <summary>All visible edits act on one typed policy draft. Only fixture and in-memory adapters exist here.</summary>
public sealed class ShellViewModel : Bindable
{
    public MacPolicyDocument Document { get; private set; }
    public MockCoreRuntime Core { get; } = new();
    public BoundedLifecycleLog Lifecycle { get; } = new();
    private readonly ConfigurationCoordinator _coordinator;
    private readonly FixtureResourceTransport _transport = new();
    private readonly ResourcePreparation _preparation;
    private readonly List<GroupProbe> _probes = [];
    private readonly Dictionary<string, GeoRuleSet> _preparedSources = new(StringComparer.Ordinal);
    private IReadOnlyList<AppRow> _visibleApps = []; private IReadOnlyList<AppRow> _overviewApps = []; private IReadOnlyList<RuleRow> _visibleRules = [];
    private List<AppRow> _apps = []; private List<RuleRow> _rules = []; private List<CatalogRow> _catalog = []; private List<NodeRow> _nodes = [];
    private int _page; private int _settingsTab; private string _appQuery = "", _catalogQuery = "", _ruleQuery = "";
    private bool _addingApps, _addingRules, _customRule, _onboarding, _hasChanges, _previewRunning = true, _applying;
    private string _notice = "此工作空间使用示例数据；所有操作都在隔离环境中。", _currentIssue = "", _dsl = "", _generated = "", _url = PreviewFixtures.SubscriptionUrl;
    private string _customName = "", _customEntries = ""; private bool _customIp;
    private Choice? _home, _nonHome, _global, _group, _member; private AppRow? _selectedApp; private RuleRow? _selectedRule; private int _step;
    private bool _runtimeUnavailable;
    private bool _needsValidation;
    private bool _subscriptionReady = true; private int _probeRound;
    public ShellViewModel(string scenario = "overview")
    {
        PreviewEnvironment.RequireIsolated();
        _transport.Add(new Uri(PreviewFixtures.SubscriptionUrl), System.Text.Encoding.UTF8.GetBytes(PreviewFixtures.SubscriptionYaml));
        _preparation = new(_transport, new InMemoryResourceCache());
        Document = PreviewFixtures.CreateDocument();
        foreach (string source in Document.RuleSets.SelectMany(x => x.Sources).Distinct(StringComparer.Ordinal)) PrepareFixtureSource(source);
        _coordinator = new(Core, new InMemoryGenerationStore(), Lifecycle);
        OverviewCommand = new(() => PageIndex = 0); AppsCommand = new(() => PageIndex = 1); RulesCommand = new(() => PageIndex = 2); NodesCommand = new(() => PageIndex = 3); SettingsCommand = new(() => PageIndex = 4);
        ShowLogDirectoryCommand = new(ShowLogDirectory);
        OpenLogsCommand = new(() => { PageIndex = 4; SettingsTab = 4; Notice = "日志只记录当前隔离会话，可在此查看。"; });
        RefreshSelectedInventoryCommand = new(RefreshSelectedInventory, () => SelectedApp is not null);
        AddAppsCommand = new(() => { AddingApps = true; CatalogQuery = ""; }); CancelAppsCommand = new(() => { AddingApps = false; foreach (var row in _catalog) row.IsSelected = false; });
        ConfirmAppsCommand = new(AddSelectedApplications, () => SelectedCatalogCount > 0);
        AddRulesCommand = new(() => { AddingRules = true; RuleQuery = ""; }); CloseRulesCommand = new(() => AddingRules = false);
        ShowCustomRuleCommand = new(() => { CreatingCustomRule = true; AddingRules = false; }); CancelCustomRuleCommand = new(() => CreatingCustomRule = false); CreateCustomRuleCommand = new(CreateCustomRule);
        ImportSubscriptionCommand = new(ImportSubscriptionAsync); RunProbesCommand = new(RunProbes); CompileCommand = new(CompileDraft); ApplyCommand = new(ApplyPreviewAsync);
        SaveCommand = new(SavePreview); ApplyDslCommand = new(ApplyDsl); ResetDslCommand = new(() => { DslText = PolicyYaml.Write(Document); Notice = "已从当前配置重新生成 DSL。"; });
        TogglePreviewCommand = new(TogglePreviewAsync);
        BeginSetupCommand = new(() => { SetupStep = 0; OnboardingVisible = true; }); CloseSetupCommand = new(() => OnboardingVisible = false); NextSetupCommand = new(NextSetup, () => CanAdvanceSetup); BackSetupCommand = new(() => SetupStep = Math.Max(0, SetupStep - 1), () => SetupStep > 0);
        RebuildProjection(); RunProbes(); CompileDraft(); _hasChanges = false; Raise(nameof(HasChanges));
        Events.Add(new("09:40", "隔离工作空间已准备", "已加载公开品牌图标与合成订阅；未读取本机应用或真实配置。", "setup"));
        switch (scenario)
        {
            case "apps": PageIndex = 1; break;
            case "add-apps": PageIndex = 1; AddingApps = true; break;
            case "rules": PageIndex = 2; break;
            case "add-rules": PageIndex = 2; AddingRules = true; break;
            case "nodes": PageIndex = 3; break;
            case "settings": PageIndex = 4; break;
            case "dsl": PageIndex = 4; SettingsTab = 2; break;
            case "onboarding": OnboardingVisible = true; SetupStep = 0; _subscriptionReady = false; Document.Defaults.Global = ExitChoice.Proxy(""); foreach (var manual in Document.Groups.Where(x => x.Kind == GroupKind.Select)) manual.SelectedMemberId = null; _global = null; Raise(nameof(SelectedGlobalTarget)); RaiseGroup(); break;
            case "empty": Document.Applications.Clear(); Document.RuleSets.Clear(); RebuildProjection(); PageIndex = 1; break;
            case "error": Document.Interfaces.NonHomeId = Document.Interfaces.HomeId; _nonHome = _home; DslText = PolicyYaml.Write(Document); CompileDraft(); PageIndex = 4; break;
            case "stopped": PreviewRunning = false; break;
            case "unavailable": PreviewRunning = false; SetRuntimeUnavailable(true); CurrentIssue = "示例状态：模拟运行不可用，未显示过期的健康状态。"; break;
        }
    }
    public IReadOnlyList<Choice> InterfaceChoices { get; } = [new("fixture-ethernet", "USB 2.5G Ethernet", "家宽 · 有线连接"), new("fixture-wifi", "Wi-Fi", "非家宽 · 无线连接"), new("fixture-usb", "USB LAN", "未连接 · 示例接口")];
    public IReadOnlyList<Choice> GlobalChoices { get; private set; } = [];
    public IReadOnlyList<Choice> GroupChoices { get; private set; } = [];
    public IReadOnlyList<Choice> GroupMemberChoices => SelectedGroupModel?.MemberIds.Select(id => new Choice(id, Document.Nodes.FirstOrDefault(x => x.Id == id)?.Name ?? Document.Groups.FirstOrDefault(x => x.Id == id)?.Name ?? id)).ToArray() ?? [];
    public IReadOnlyList<Choice> GroupStrategies { get; } = [new("Select", "手动选择"), new("UrlTest", "自动测速"), new("Fallback", "故障转移")];
    private Choice? _strategy;
    public Choice? SelectedStrategy { get => _strategy; set { if (Set(ref _strategy, value) && value is not null && SelectedGroupModel is { } g && Enum.TryParse<GroupKind>(value.Id, out var kind)) { g.Kind = kind; StateChanged(); RaiseGroup(); } } }
    public ProxyGroup? SelectedGroupModel => Document.Groups.FirstOrDefault(x => x.Id == _group?.Id);
    public Choice? SelectedHomeInterface { get => _home; set { if (Set(ref _home, value) && value is not null) { Document.Interfaces.HomeId = value.Id; StateChanged(); Raise(nameof(HomeInterfaceName)); } } }
    public Choice? SelectedNonHomeInterface { get => _nonHome; set { if (Set(ref _nonHome, value) && value is not null) { Document.Interfaces.NonHomeId = value.Id; StateChanged(); Raise(nameof(NonHomeInterfaceName)); } } }
    public Choice? SelectedGlobalTarget { get => _global; set { if (Set(ref _global, value) && value is not null) { Document.Defaults.Global = ExitChoice.Proxy(value.Id); StateChanged(); Raise(nameof(GlobalTargetName)); Raise(nameof(SetupGlobalManual)); Raise(nameof(SetupGlobalMemberChoices)); Raise(nameof(SelectedSetupGlobalMember)); NextSetupCommand.Refresh(); } } }
    public Choice? SelectedGroup { get => _group; set { if (Set(ref _group, value)) RaiseGroup(); } }
    public Choice? SelectedGroupMember { get => _member; set { if (Set(ref _member, value) && value is not null && SelectedGroupModel is { } g) { g.SelectedMemberId = value.Id; StateChanged(); } } }
    public bool IsManualGroup => SelectedGroupModel?.Kind == GroupKind.Select;
    public string GroupSummary => SelectedGroupModel is { } g ? $"{g.MemberIds.Count} 个成员 · {(g.Kind == GroupKind.Select ? "由你决定当前节点" : g.Kind == GroupKind.UrlTest ? $"每 {g.IntervalSeconds} 秒测速，保留 {g.ToleranceMilliseconds} ms 容差" : g.Kind == GroupKind.Fallback ? "按成员顺序选择可用节点" : "该分组类型不受支持，不能作为出口")}" : "先选择一个分组";
    public int PageIndex { get => _page; set { if (Set(ref _page, value)) { foreach (string name in new[] { nameof(OverviewVisible), nameof(AppsVisible), nameof(RulesVisible), nameof(NodesVisible), nameof(SettingsVisible), nameof(PageTitle), nameof(PageDescription) }) Raise(name); } } }
    public bool OverviewVisible => PageIndex == 0; public bool AppsVisible => PageIndex == 1; public bool RulesVisible => PageIndex == 2; public bool NodesVisible => PageIndex == 3; public bool SettingsVisible => PageIndex == 4;
    public string PageTitle => PageIndex switch { 0 => "总览", 1 => "应用分流", 2 => "规则集合", 3 => "节点与分组", _ => "设置" };
    public string PageDescription => PageIndex switch { 0 => "每一次连接，都有清晰的去向。", 1 => "为你的应用选择出口，未运行时策略依然保留。", 2 => "把域名与 IP 作为集合管理，让分流更简单。", 3 => "一个订阅，统一管理节点、分组与全局出口。", _ => "网络偏好、配置与诊断，尽在一处。" };
    public int SettingsTab { get => _settingsTab; set => Set(ref _settingsTab, value); }
    public string AppQuery { get => _appQuery; set { if (Set(ref _appQuery, value)) { PublishApplicationFilter(); Raise(nameof(NoAppsMatch)); } } }
    public string CatalogQuery { get => _catalogQuery; set { if (Set(ref _catalogQuery, value)) { Raise(nameof(VisibleCatalog)); Raise(nameof(NoCatalogMatch)); } } }
    public string RuleQuery { get => _ruleQuery; set { if (Set(ref _ruleQuery, value)) { PublishRuleFilter(); Raise(nameof(RuleSuggestions)); Raise(nameof(NoRulesMatch)); } } }
    public IReadOnlyList<AppRow> VisibleApps => _visibleApps;
    public IReadOnlyList<AppRow> OverviewApps => _overviewApps;
    public IReadOnlyList<CatalogRow> VisibleCatalog => _catalog.Where(x => Matches(x.Name + " " + x.Model.Category, CatalogQuery)).ToArray();
    public IReadOnlyList<RuleRow> VisibleRules => _visibleRules;
    public IReadOnlyList<NodeRow> Nodes => _nodes;
    public IReadOnlyList<RuleSuggestion> RuleSuggestions => new[] { ("openai", "OpenAI", "AI 服务 · 域名集合"), ("google", "Google", "域名 + IP · 一个条目统一管理"), ("youtube", "YouTube", "流媒体 · 域名 + IP"), ("github", "GitHub", "开发工具 · 域名集合"), ("netflix", "Netflix", "流媒体 · 域名集合"), ("apple", "Apple", "系统服务 · 域名集合") }.Where(x => Matches(x.Item1 + " " + x.Item2, RuleQuery)).Select(x => new RuleSuggestion(x.Item1, x.Item2, x.Item3, AddRule, Document.RuleSets.Any(r => r.Id == "rule-" + x.Item1))).ToArray();
    public bool AppsEmpty => _apps.Count == 0; public bool RulesEmpty => _rules.Count == 0;
    public bool NoAppsMatch => !AppsEmpty && VisibleApps.Count == 0; public bool NoCatalogMatch => VisibleCatalog.Count == 0; public bool NoRulesMatch => !RulesEmpty && VisibleRules.Count == 0;
    public int ApplicationCount => Document.Applications.Count; public int RuleCount => Document.RuleSets.Count;
    public string ApplicationCountLabel => $"{ApplicationCount} 个已管理应用"; public string RuleCountLabel => $"{RuleCount} 个用户集合";
    public string NodeCountLabel => $"{Document.Nodes.Count(x => x.IsSupported)} 个可用节点 · {Document.Nodes.Count(x => !x.IsSupported)} 项不支持";
    public bool AddingApps { get => _addingApps; set => Set(ref _addingApps, value); }
    public bool AddingRules { get => _addingRules; set => Set(ref _addingRules, value); }
    public bool CreatingCustomRule { get => _customRule; set => Set(ref _customRule, value); }
    public int SelectedCatalogCount => _catalog.Count(x => x.IsSelected); public string AddSelectedLabel => $"添加所选应用{(SelectedCatalogCount > 0 ? $" · {SelectedCatalogCount}" : "")}";
    public AppRow? SelectedApp { get => _selectedApp; set { if (Set(ref _selectedApp, value)) { Raise(nameof(HasSelectedApp)); Raise(nameof(SelectedAppName)); Raise(nameof(SelectedAppPaths)); RefreshSelectedInventoryCommand.Refresh(); } } }
    public bool HasSelectedApp => SelectedApp is not null;
    public string SelectedAppName => SelectedApp?.Name ?? ""; public IReadOnlyList<string> SelectedAppPaths => SelectedApp?.ExecutablePaths.ToArray() ?? [];
    public RuleRow? SelectedRule { get => _selectedRule; set { if (Set(ref _selectedRule, value)) { Raise(nameof(HasSelectedRule)); Raise(nameof(SelectedRuleName)); Raise(nameof(SelectedRuleSummary)); } } }
    public bool HasSelectedRule => SelectedRule is not null;
    public string SelectedRuleName => SelectedRule?.Name ?? ""; public string SelectedRuleSummary => SelectedRule?.MatchSummary ?? "";
    public string SubscriptionUrl { get => _url; set => Set(ref _url, value); }
    public string CustomRuleName { get => _customName; set => Set(ref _customName, value); }
    public string CustomRuleEntries { get => _customEntries; set => Set(ref _customEntries, value); }
    public bool CustomRuleIsIp { get => _customIp; set => Set(ref _customIp, value); }
    public string DnsServer { get => Document.Dns.UpstreamIp; set { if (Document.Dns.UpstreamIp == value) return; Document.Dns.UpstreamIp = value; Raise(); StateChanged(); } }
    public string DslText { get => _dsl; set => Set(ref _dsl, value); }
    public string GeneratedConfig { get => _generated; private set => Set(ref _generated, value); }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string CurrentIssue { get => _currentIssue; private set { if (Set(ref _currentIssue, value)) Raise(nameof(HasIssue)); } }
    public bool HasIssue => !string.IsNullOrEmpty(CurrentIssue);
    public bool ConfigValid { get; private set; }
    public string ValidationLabel => _needsValidation ? "草稿等待校验" : ConfigValid ? "配置校验通过" : "配置需要调整";
    public bool HasChanges { get => _hasChanges; private set { if (Set(ref _hasChanges, value)) Raise(nameof(SaveStatus)); } }
    public string SaveStatus => HasChanges ? "有待应用的更改" : "配置已同步";
    public bool PreviewRunning { get => _previewRunning; private set { if (Set(ref _previewRunning, value)) { Raise(nameof(StatusTitle)); Raise(nameof(StatusDescription)); Raise(nameof(TogglePreviewLabel)); Raise(nameof(StatusIconBrush)); Raise(nameof(StatusSurfaceBrush)); } } }
    public string StatusTitle => _runtimeUnavailable ? "模拟运行状态不可用" : PreviewRunning ? "模拟网络已就绪" : "模拟已手动停止";
    public string StatusDescription => _runtimeUnavailable ? "请查看诊断，校验后重新应用配置。" : PreviewRunning ? "出口严格绑定，策略清晰可见。" : "恢复模拟后继续使用保存的策略。";
    public string TogglePreviewLabel => _runtimeUnavailable ? "重试应用" : PreviewRunning ? "停止模拟" : "恢复模拟";
    public Avalonia.Media.IBrush StatusIconBrush => Avalonia.Media.Brush.Parse(_runtimeUnavailable ? "#BF7781" : PreviewRunning ? "#3F9B7F" : "#8D9DB4");
    public Avalonia.Media.IBrush StatusSurfaceBrush => Avalonia.Media.Brush.Parse(_runtimeUnavailable ? "#FFF0F2" : PreviewRunning ? "#ECF7F2" : "#F0F3F8");
    private void SetRuntimeUnavailable(bool value)
    {
        _runtimeUnavailable = value;
        foreach (string property in new[] { nameof(StatusTitle), nameof(StatusDescription), nameof(TogglePreviewLabel), nameof(StatusIconBrush), nameof(StatusSurfaceBrush) }) Raise(property);
    }
    public string HomeInterfaceName => SelectedHomeInterface?.Name ?? "尚未选择"; public string NonHomeInterfaceName => SelectedNonHomeInterface?.Name ?? "尚未选择"; public string GlobalTargetName => SelectedGlobalTarget?.Name ?? "必须选择一个代理";
    public string ProxyResolvedName => GroupSelector.Resolve(Document.Defaults.Global.TargetId ?? "", Document, _probes, PreviewFixtures.ObservationTime).NodeId is { } id ? Document.Nodes.FirstOrDefault(x => x.Id == id)?.Name ?? "待选择" : "待校验";
    public IReadOnlyList<Observation> Observations => PreviewFixtures.Observations;
    public BoundedEvents Events { get; } = [];
    public UiCommand ShowLogDirectoryCommand { get; }
    public UiCommand OverviewCommand { get; }
    public UiCommand AppsCommand { get; }
    public UiCommand RulesCommand { get; }
    public UiCommand NodesCommand { get; }
    public UiCommand SettingsCommand { get; }
    public UiCommand OpenLogsCommand { get; }
    public UiCommand RefreshSelectedInventoryCommand { get; }
    public UiCommand AddAppsCommand { get; }
    public UiCommand CancelAppsCommand { get; }
    public UiCommand ConfirmAppsCommand { get; }
    public UiCommand AddRulesCommand { get; }
    public UiCommand CloseRulesCommand { get; }
    public UiCommand ShowCustomRuleCommand { get; }
    public UiCommand CancelCustomRuleCommand { get; }
    public UiCommand CreateCustomRuleCommand { get; }
    public AsyncUiCommand ImportSubscriptionCommand { get; }
    public UiCommand RunProbesCommand { get; }
    public UiCommand CompileCommand { get; }
    public AsyncUiCommand ApplyCommand { get; }
    public UiCommand SaveCommand { get; }
    public UiCommand ApplyDslCommand { get; }
    public UiCommand ResetDslCommand { get; }
    public AsyncUiCommand TogglePreviewCommand { get; }
    public UiCommand BeginSetupCommand { get; }
    public UiCommand CloseSetupCommand { get; }
    public UiCommand NextSetupCommand { get; }
    public UiCommand BackSetupCommand { get; }
    public bool OnboardingVisible { get => _onboarding; set => Set(ref _onboarding, value); }
    public int SetupStep { get => _step; set { if (Set(ref _step, value)) { foreach (string name in new[] { nameof(SetupTitle), nameof(SetupDescription), nameof(SetupProgress), nameof(SetupInterfaces), nameof(SetupSubscription), nameof(SetupGlobal), nameof(SetupApplications), nameof(SetupReview), nameof(NextSetupLabel) }) Raise(name); BackSetupCommand?.Refresh(); NextSetupCommand?.Refresh(); } } }
    public string SetupTitle => SetupStep switch { 0 => "先认识你的两条网络", 1 => "接入你的节点订阅", 2 => "为全球流量指定出口", 3 => "让常用应用先行", _ => "一切准备就绪" };
    public string SetupDescription => SetupStep switch { 0 => "家宽与非家宽需要绑定两张不同的物理网卡。", 1 => "仅导入节点与分组，DNS 与分流由 Egress 统一管理。", 2 => "中国大陆默认非家宽直连，其他流量始终使用此代理。", 3 => "选择需要单独分流的应用；以后也可以随时添加。", _ => "先校验配置，再应用到隔离的模拟运行环境。" };
    private ProxyGroup? SetupGlobalGroup => Document.Groups.FirstOrDefault(group => group.Id == SelectedGlobalTarget?.Id);
    public bool SetupGlobalManual => SetupGlobalGroup?.Kind == GroupKind.Select;
    public IReadOnlyList<Choice> SetupGlobalMemberChoices => SetupGlobalGroup?.MemberIds.Select(id => new Choice(id, Document.Nodes.FirstOrDefault(node => node.Id == id)?.Name ?? Document.Groups.FirstOrDefault(group => group.Id == id)?.Name ?? id)).ToArray() ?? [];
    public Choice? SelectedSetupGlobalMember
    {
        get => SetupGlobalMemberChoices.FirstOrDefault(choice => choice.Id == SetupGlobalGroup?.SelectedMemberId);
        set { if (value is null || SetupGlobalGroup is not { } group || group.SelectedMemberId == value.Id) return; group.SelectedMemberId = value.Id; Raise(); StateChanged(); RaiseGroup(); NextSetupCommand.Refresh(); }
    }
    public bool CanAdvanceSetup => SetupStep != 2 || !SetupGlobalManual || SetupGlobalGroup is { } group && group.SelectedMemberId is { } member && group.MemberIds.Contains(member, StringComparer.Ordinal);
    public string SetupSubscriptionStatus => _subscriptionReady ? NodeCountLabel : "尚未导入";
    public string SetupProgress => $"{SetupStep + 1} / 5"; public bool SetupInterfaces => SetupStep == 0; public bool SetupSubscription => SetupStep == 1; public bool SetupGlobal => SetupStep == 2; public bool SetupApplications => SetupStep == 3; public bool SetupReview => SetupStep == 4;
    public string NextSetupLabel => SetupStep == 4 ? "校验并完成" : "继续";
    private static bool Matches(string text, string query) => string.IsNullOrWhiteSpace(query) || text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    private string GlobalId() => Document.Defaults.Global.TargetId ?? "";
    private void CatalogSelectionChanged() { Raise(nameof(SelectedCatalogCount)); Raise(nameof(AddSelectedLabel)); ConfirmAppsCommand.Refresh(); }
    private void RebuildProjection()
    {
        string? appId = SelectedApp?.Model.Id, ruleId = SelectedRule?.Model.Id;
        GlobalChoices = Document.Groups.Where(x => x.IsSupported).Select(x => new Choice(x.Id, x.Name, "分组")).Concat(Document.Nodes.Where(x => x.IsSupported).Select(x => new Choice(x.Id, x.Name, "节点"))).ToArray();
        GroupChoices = Document.Groups.Select(x => new Choice(x.Id, x.Name)).ToArray();

        var existingApps = _apps.ToDictionary(x => x.Model.Id, StringComparer.Ordinal);
        var existingRules = _rules.ToDictionary(x => x.Model.Id, StringComparer.Ordinal);
        bool selectedInventoryChanged = false;
        _apps = Document.Applications.Select(model =>
        {
            if (existingApps.TryGetValue(model.Id, out var row)) { bool changed = row.UpdateModel(model); if (model.Id == appId) selectedInventoryChanged = changed; return row; }
            return new AppRow(model, GlobalId, () => GlobalChoices, StateChanged, RemoveApp);
        }).ToList();
        _rules = Document.RuleSets.Select(model =>
        {
            if (existingRules.TryGetValue(model.Id, out var row)) { row.UpdateModel(model); return row; }
            return new RuleRow(model, GlobalId, () => GlobalChoices, StateChanged, RemoveRule, MoveRule, CanMoveRule, EffectiveRuleContent);
        }).ToList();
        var existingCatalog = _catalog.ToDictionary(x => x.Model.Id, StringComparer.Ordinal);
        _catalog = PreviewFixtures.InstalledApplications.Select(model =>
        {
            bool managed = Document.Applications.Any(app => app.BundlePath == model.BundlePath);
            return existingCatalog.TryGetValue(model.Id, out var row) && row.IsManaged == managed ? row : new CatalogRow(model, CatalogSelectionChanged, managed);
        }).ToList();
        PublishApplicationFilter(); PublishRuleFilter();
        if (selectedInventoryChanged) Raise(nameof(SelectedAppPaths));
        _nodes = Document.Nodes.Select(x => new NodeRow(x, () => SelectedGroupModel, () => { StateChanged(); RaiseGroup(); })).ToList();
        _home = InterfaceChoices.FirstOrDefault(x => x.Id == Document.Interfaces.HomeId); _nonHome = InterfaceChoices.FirstOrDefault(x => x.Id == Document.Interfaces.NonHomeId); _global = GlobalChoices.FirstOrDefault(x => x.Id == GlobalId()); _group = GroupChoices.FirstOrDefault(x => x.Id == _group?.Id) ?? GroupChoices.FirstOrDefault();
        SelectedApp = _apps.FirstOrDefault(x => x.Model.Id == appId); SelectedRule = _rules.FirstOrDefault(x => x.Model.Id == ruleId);
        foreach (string name in new[] { nameof(VisibleCatalog), nameof(Nodes), nameof(GlobalChoices), nameof(GroupChoices), nameof(SelectedHomeInterface), nameof(SelectedNonHomeInterface), nameof(SelectedGlobalTarget), nameof(SelectedGroup), nameof(ApplicationCount), nameof(RuleCount), nameof(ApplicationCountLabel), nameof(RuleCountLabel), nameof(NodeCountLabel), nameof(AppsEmpty), nameof(RulesEmpty), nameof(NoAppsMatch), nameof(NoCatalogMatch), nameof(NoRulesMatch), nameof(RuleSuggestions), nameof(HomeInterfaceName), nameof(NonHomeInterfaceName), nameof(GlobalTargetName) }) Raise(name);
        Raise(nameof(SelectedAppName)); Raise(nameof(SelectedRuleName)); Raise(nameof(SelectedRuleSummary)); Raise(nameof(DnsServer));
        RaiseGroup(); CatalogSelectionChanged(); DslText = PolicyYaml.Write(Document);
    }
    private void PublishApplicationFilter()
    {
        var visible = _apps.Where(row => Matches(row.Name + " " + row.Category, AppQuery)).ToArray();
        if (!_visibleApps.SequenceEqual(visible)) { _visibleApps = visible; Raise(nameof(VisibleApps)); }
        var overview = _apps.Take(4).ToArray();
        if (!_overviewApps.SequenceEqual(overview)) { _overviewApps = overview; Raise(nameof(OverviewApps)); }
    }
    private void PublishRuleFilter()
    {
        var visible = _rules.Where(row => Matches(row.Name + " " + row.MatchSummary, RuleQuery)).ToArray();
        if (!_visibleRules.SequenceEqual(visible)) { _visibleRules = visible; Raise(nameof(VisibleRules)); }
    }
    private void RaiseGroup()
    {
        _member = GroupMemberChoices.FirstOrDefault(x => x.Id == SelectedGroupModel?.SelectedMemberId); _strategy = GroupStrategies.FirstOrDefault(x => x.Id == SelectedGroupModel?.Kind.ToString());
        foreach (string name in new[] { nameof(GroupMemberChoices), nameof(SelectedGroupMember), nameof(SelectedStrategy), nameof(IsManualGroup), nameof(GroupSummary) }) Raise(name);
        foreach (var row in _nodes) row.RefreshMember();
    }
    private void StateChanged() { _needsValidation = true; Raise(nameof(ValidationLabel)); HasChanges = true; DslText = PolicyYaml.Write(Document); Notice = "更改已保存在草稿中，校验后可应用到模拟环境。"; CurrentIssue = ""; Raise(nameof(ProxyResolvedName)); }
    public void AddSelectedApplications()
    {
        var selected = _catalog.Where(x => x.IsSelected).ToArray(); if (selected.Length == 0) return;
        foreach (var row in selected) Document.Applications.Add(PreviewFixtures.Manage(row.Model, ExitChoice.NonHome));
        RebuildProjection(); StateChanged(); AddingApps = false; Notice = $"已添加 {selected.Length} 个应用，固定可执行清单已保存。";
    }
    private void RefreshSelectedInventory()
    {
        if (SelectedApp is not { } selected) return;
        var app = selected.Model; var fixture = PreviewFixtures.InstalledApplications.FirstOrDefault(x => x.BundlePath == app.BundlePath);
        if (fixture is null) { Notice = "该应用没有可刷新的示例清单；已保留全部旧路径。"; return; }
        int before = app.ExecutablePaths.Count;
        foreach (string path in fixture.ExecutablePaths) if (!app.ExecutablePaths.Contains(path, StringComparer.Ordinal)) app.ExecutablePaths.Add(path);
        string helper = $"{app.BundlePath}/Contents/Frameworks/{app.Name} Helper (Network).app/Contents/MacOS/{app.Name} Helper (Network)";
        if (!app.ExecutablePaths.Contains(helper, StringComparer.Ordinal)) app.ExecutablePaths.Add(helper);
        if (app.ExecutablePaths.Count != before) { selected.NotifyInventory(); Raise(nameof(SelectedAppPaths)); StateChanged(); Notice = $"已显式刷新示例清单：新增 {app.ExecutablePaths.Count - before} 条，全部旧路径已保留。"; }
        else Notice = "示例清单未变化，全部已保存路径继续保留；没有应用运行配置。";
    }
    private RuleContent EffectiveRuleContent(ManagedRuleSet rule) => new(
        rule.Domains.Concat(rule.Sources.Where(_preparedSources.ContainsKey).SelectMany(source => _preparedSources[source].Domains)).Distinct().ToArray(),
        rule.IpCidrs.Concat(rule.Sources.Where(_preparedSources.ContainsKey).SelectMany(source => _preparedSources[source].IpCidrs)).Distinct(StringComparer.Ordinal).ToArray());
    private bool CanMoveRule(RuleRow row, int direction) { int index = Document.RuleSets.IndexOf(row.Model); return index >= 0 && index + direction >= 0 && index + direction < Document.RuleSets.Count; }
    private void MoveRule(RuleRow row, int direction)
    {
        if (!CanMoveRule(row, direction)) return; int index = Document.RuleSets.IndexOf(row.Model); Document.RuleSets.RemoveAt(index); Document.RuleSets.Insert(index + direction, row.Model); RebuildProjection(); StateChanged(); Notice = "用户规则顺序已更新，等待校验应用。";
    }
    private void RemoveApp(AppRow row) { Document.Applications.Remove(row.Model); RebuildProjection(); StateChanged(); Notice = $"已移除 {row.Name} 的单独分流策略。"; }
    private void AddRule(string id, string name) { if (Document.RuleSets.Any(x => x.Id == "rule-" + id)) { Notice = "该集合已经添加。"; return; } var rule = PreviewFixtures.Rule(id, name, ExitChoice.Proxy(GlobalId())); foreach (string source in rule.Sources) PrepareFixtureSource(source); Document.RuleSets.Add(rule); RebuildProjection(); StateChanged(); AddingRules = false; Notice = $"已添加 {name}，域名与 IP 以同一条目管理。"; }
    private void RemoveRule(RuleRow row) { Document.RuleSets.Remove(row.Model); RebuildProjection(); StateChanged(); }
    private void CreateCustomRule()
    {
        var entries = CustomRuleEntries.Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(CustomRuleName) || entries.Length == 0) { CurrentIssue = "请填写集合名称及至少一条规则。"; return; }
        var rule = new ManagedRuleSet { Id = "custom-" + Guid.NewGuid().ToString("N"), Name = CustomRuleName.Trim(), Via = ExitChoice.NonHome };
        if (CustomRuleIsIp) rule.IpCidrs = entries.ToList(); else rule.Domains = entries.Select(x => new DomainRule(DomainMatchKind.Suffix, x)).ToList();
        Document.RuleSets.Add(rule); var errors = PolicyValidator.Validate(Document).Where(x => x.ObjectId == rule.Id && x.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) { Document.RuleSets.Remove(rule); CurrentIssue = string.Join(" ", errors.Select(x => x.Message)); return; }
        CreatingCustomRule = false; CustomRuleName = ""; CustomRuleEntries = ""; RebuildProjection(); StateChanged(); Notice = "自定义集合已加入草稿。";
    }
    public int FixtureSubscriptionRequestCount => _transport.RequestCount;
    public void SetFixtureSubscriptionPayload(string yaml)
    {
        PreviewEnvironment.RequireIsolated();
        _transport.Add(new Uri(PreviewFixtures.SubscriptionUrl), System.Text.Encoding.UTF8.GetBytes(yaml));
    }
    public async Task ImportSubscriptionAsync()
    {
        if (!Uri.TryCreate(SubscriptionUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) { CurrentIssue = "请输入有效的 HTTPS 订阅 URL。"; return; }
        if (SubscriptionUrl != PreviewFixtures.SubscriptionUrl) { CurrentIssue = "隔离预览仅支持示例 URL；没有发送网络请求。请使用下方示例地址。"; return; }
        var selectedBinding = CompileOptions().Interfaces.FirstOrDefault(x => x.StableId == Document.Interfaces.NonHomeId);
        if (selectedBinding is null) { CurrentIssue = "所选非家宽接口当前不可用，未发起示例请求。"; return; }
        CurrentIssue = ""; Notice = "正在读取并校验示例订阅…";
        ClashImportResult? checkedImport = null;
        byte[] payload;
        try { payload = await _preparation.PrepareAsync(new ResourceDescriptor { Id = "fixture-subscription", Url = uri, Interface = selectedBinding, CachePolicy = PreparationCachePolicy.Refresh }, bytes => { checkedImport = ClashSubscriptionImporter.Import(System.Text.Encoding.UTF8.GetString(bytes), "fixture-subscription"); return checkedImport.IsSuccess; }); }
        catch (ResourcePreparationException exception) { string reason = checkedImport is { IsSuccess: false } ? string.Join(" ", checkedImport.Diagnostics.Select(diagnostic => diagnostic.Message).Distinct()) : exception.Message; CurrentIssue = string.IsNullOrWhiteSpace(reason) ? "订阅结构无效，原有配置与已验证缓存已保留。" : reason; Events.Insert(0, new("09:42", "示例订阅更新失败", CurrentIssue, "subscription")); return; }
        var imported = ClashSubscriptionImporter.Import(System.Text.Encoding.UTF8.GetString(payload), "fixture-subscription");
        if (!imported.IsSuccess) { CurrentIssue = string.Join(" ", imported.Diagnostics.Select(x => x.Message)); return; }
        Document = SubscriptionReconciler.Merge(Document, "fixture-subscription", imported);
        if (!Document.Subscriptions.Any(x => x.Id == "fixture-subscription")) Document.Subscriptions.Add(new() { Id = "fixture-subscription", Name = "我的示例订阅", Url = SubscriptionUrl });
        _subscriptionReady = true; Raise(nameof(SetupSubscriptionStatus)); RebuildProjection(); RunProbes(); StateChanged(); Notice = $"示例订阅已更新：{NodeCountLabel}；原有选择已保留。";
        Events.Insert(0, new("09:42", "示例订阅已导入", "仅导入节点与分组。Hysteria2 已明确标记为不支持。", "subscription"));
    }
    public void RunProbes()
    {
        _probeRound++; _probes.Clear(); int i = 0;
        foreach (var row in _nodes)
        {
            if (!row.IsSupported) { row.SetLatency(null); continue; }
            double latency = new[] { 24d, 58d, 126d }[i++ % 3]; row.SetLatency(latency); foreach (string testUrl in Document.Groups.Select(x => x.TestUrl).Distinct(StringComparer.Ordinal)) _probes.Add(new(row.Model.Id, true, latency, PreviewFixtures.ObservationTime, testUrl));
        }
        Notice = $"示例测速已完成 · 09:42:00 · 第 {_probeRound} 次操作；未建立真实网络连接。"; Raise(nameof(ProxyResolvedName));
    }
    private void PrepareFixtureSource(string source)
    {
        if (_preparedSources.ContainsKey(source)) return;
        if (!PreviewFixtures.RuleSourceYaml.TryGetValue(source, out string? yaml)) throw new InvalidOperationException("The requested rule source has no verified fixture payload.");
        var kind = source.StartsWith("geoip/", StringComparison.Ordinal) ? RuleDataKind.Ip : RuleDataKind.Domain;
        string tag = "catalog-" + source.Replace('/', '-').Replace(".yaml", "", StringComparison.Ordinal);
        var parsed = GeoRuleCompiler.Parse(yaml, tag, kind);
        if (!parsed.IsSuccess) throw new InvalidOperationException("The fixture rule source did not pass semantic validation.");
        _preparedSources.Add(source, parsed);
    }
    public XrayCompileOptions CompileOptions(MacPolicyDocument? candidate = null)
    {
        var policy = candidate ?? Document;
        var referenced = policy.RuleSets.SelectMany(rule => rule.Sources).Distinct(StringComparer.Ordinal);
        return new()
        {
            Interfaces = InterfaceChoices.Take(2).Select((x, i) => new InterfaceBinding(x.Id, i == 0 ? "en5" : "en0")).ToList(),
            Probes = _probes.ToList(),
            PreparedRuleSources = referenced.Where(_preparedSources.ContainsKey).Select(source => new RuleSourceBinding(source, _preparedSources[source].Tag, _preparedSources[source].Domains.Count > 0 ? "geosite.dat" : null, _preparedSources[source].IpCidrs.Count > 0 ? "geoip.dat" : null)).ToList(),
            Now = PreviewFixtures.ObservationTime
        };
    }
    private string? _assetKey;
    private IReadOnlyDictionary<string, byte[]> _assets = new Dictionary<string, byte[]>();
    public IReadOnlyDictionary<string, byte[]> PreparedAssets()
    {
        string[] sources = Document.RuleSets.SelectMany(rule => rule.Sources).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string key = string.Join("|", sources);
        if (key == _assetKey) return _assets;
        var sets = sources.Where(_preparedSources.ContainsKey).Select(source => _preparedSources[source]).ToList();
        sets.Add(new() { Tag = "cn", Domains = [new(DomainMatchKind.Suffix, "fixture-china.example.invalid")], IpCidrs = ["198.51.100.0/24"] });
        var bundle = GeoRuleCompiler.Compile(sets); _assetKey = key; _assets = new Dictionary<string, byte[]> { { "geosite.dat", bundle.GeoSite }, { "geoip.dat", bundle.GeoIp } }; return _assets;
    }
    public void CompileDraft()
    {
        _needsValidation = false;
        if (!IPAddress.TryParse(DnsServer, out _)) { ConfigValid = false; CurrentIssue = "DNS 上游需要一个有效的 IP 地址。"; GeneratedConfig = ""; Raise(nameof(ConfigValid)); Raise(nameof(ValidationLabel)); return; }
        var result = XrayConfigCompiler.Compile(Document, CompileOptions()); ConfigValid = result.IsSuccess;
        CurrentIssue = string.Join(" ", result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.Message).Distinct());
        GeneratedConfig = result.Json ?? "配置未通过校验，未生成运行配置。"; Raise(nameof(ConfigValid)); Raise(nameof(ValidationLabel)); Raise(nameof(ProxyResolvedName));
        if (result.IsSuccess) Notice = "配置校验通过：三出口、全局代理与固定应用清单已一致编译。";
    }
    public async Task TogglePreviewAsync()
    {
        if (!PreviewRunning || _runtimeUnavailable) { await ApplyPreviewAsync(); return; }
        if (!await _coordinator.StopAsync("用户在隔离预览中停止模拟")) { PreviewRunning = false; SetRuntimeUnavailable(true); CurrentIssue = "模拟停止失败，运行状态不可用。请查看诊断后重新应用。"; return; }
        SetRuntimeUnavailable(false); PreviewRunning = false; Notice = "模拟内核已停止；保存的配置与固定路径清单仍然保留。";
        Events.Insert(0, new("09:42", "用户手动停止模拟", Notice, "lifecycle"));
    }
    public async Task ApplyPreviewAsync()
    {
        if (_applying) return; _applying = true;
        try
        {
            if (!SynchronizePendingDsl()) return;
            CompileDraft(); if (!ConfigValid) return;
            var result = await _coordinator.ApplyAsync(Document, CompileOptions(), PreparedAssets(), "用户在隔离预览中应用配置");
            if (result.Succeeded) { SetRuntimeUnavailable(false); HasChanges = false; PreviewRunning = true; CurrentIssue = ""; Notice = result.Disposition == ApplyDisposition.Unchanged ? "运行配置未改变，模拟内核没有重启。" : "配置已应用到模拟内核；未启动真实 TUN 或修改系统网络。"; Events.Insert(0, new("09:42", "模拟配置已应用", Notice, "configuration")); }
            else { if (_coordinator.Applied is null) { PreviewRunning = false; SetRuntimeUnavailable(true); } CurrentIssue = string.Join(" ", result.Diagnostics.Select(x => x.Message)); }
        }
        finally { _applying = false; }
    }
    private bool SynchronizePendingDsl()
    {
        if (DslText == PolicyYaml.Write(Document)) return true;
        ApplyDsl(); return !HasIssue;
    }
    private void ApplyDsl()
    {
        var parsed = PolicyYaml.Read(DslText); if (!parsed.IsSuccess || parsed.Document is null) { CurrentIssue = string.Join(" ", parsed.Diagnostics.Select(x => x.Message)); return; }
        var result = XrayConfigCompiler.Compile(parsed.Document, CompileOptions(parsed.Document)); if (!result.IsSuccess) { CurrentIssue = string.Join(" ", result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.Message)); return; }
        Document = parsed.Document; RebuildProjection(); StateChanged(); CompileDraft(); Notice = "DSL 已校验并同步到全部界面；草稿等待应用。";
    }
    public string? SavedPreviewPath { get; private set; }
    private void SavePreview()
    {
        PreviewEnvironment.RequireIsolated(); if (!SynchronizePendingDsl()) return; CompileDraft(); if (!ConfigValid) return;
        try
        {
            string path = PrivatePreviewFiles.WriteText("preview-policy.yaml", PolicyYaml.Write(Document)); SavedPreviewPath = path;
            Notice = "已保存到当前隔离会话目录；真实用户配置未被读取或写入。"; Events.Insert(0, new("09:42", "预览草稿已保存", path, "storage"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { CurrentIssue = "预览草稿未能保存；运行与保护状态未改变。"; }
    }
    private void ShowLogDirectory()
    {
        try
        {
            PrivatePreviewFiles.WriteText("preview-events.log", string.Join("\n", Events.Select(x => $"{x.Time} [{x.Kind}] {x.Title} - {x.Detail}")));
            Notice = "预览日志目录：" + PreviewEnvironment.SessionDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Notice = "诊断文件无法写入；当前运行与保护判断未改变。"; }
    }
    private void NextSetup()
    {
        if (SetupStep == 0 && (SelectedHomeInterface is null || SelectedNonHomeInterface is null || SelectedHomeInterface.Id == SelectedNonHomeInterface.Id)) { CurrentIssue = "请选择两张不同的物理网卡。"; return; }
        if (SetupStep == 1 && !_subscriptionReady) { CurrentIssue = "请先导入示例订阅。"; return; }
        if (SetupStep == 2 && SelectedGlobalTarget is null) { CurrentIssue = "必须明确选择一个全局代理节点或分组。"; return; }
        if (SetupStep == 2 && !CanAdvanceSetup) { CurrentIssue = "手动分组需要明确选择一个节点。"; return; }
        CurrentIssue = "";
        if (SetupStep == 4) { CompileDraft(); if (ConfigValid) { OnboardingVisible = false; ApplyCommand.Execute(null); } return; }
        SetupStep++;
    }
}
