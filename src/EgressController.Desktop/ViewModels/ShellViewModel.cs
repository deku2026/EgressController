using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EgressController.Policy;
using EgressController.Runtime;
using EgressController.Desktop.Preview;
using EgressController.Desktop.Presentation.Controls;
using EgressController.Desktop.Services;
namespace EgressController.Desktop.ViewModels;

/// <summary>One typed policy draft; backend composition decides production versus explicit preview.</summary>
public sealed partial class ShellViewModel : Bindable, IAsyncDisposable
{
    private readonly IClientBackend _backend;
    private readonly CancellationTokenSource _lifetime = new();
    private ClientPreparedResources _prepared = new([], new Dictionary<string, byte[]>(), new Dictionary<string, GeoRuleSet>());
    private IReadOnlyList<ClientInterface> _interfaces = [];
    private List<ClientApplication> _applications = [];
    private IReadOnlyList<ClientRuleCatalogEntry> _ruleCatalog = [];
    private readonly List<GroupProbe> _probes = [];
    private IReadOnlyDictionary<string, GeoRuleSet> _preparedSources => _prepared.Sources;
    private IReadOnlyList<AppRow> _visibleApps = []; private IReadOnlyList<AppRow> _overviewApps = []; private IReadOnlyList<RuleRow> _visibleRules = []; private IReadOnlyList<CatalogRow> _visibleCatalog = [];
    private List<AppRow> _apps = []; private List<RuleRow> _rules = []; private List<CatalogRow> _catalog = []; private List<NodeRow> _nodes = [];
    private readonly Dictionary<string, Bitmap> _icons = new(StringComparer.Ordinal);
    private readonly Queue<string> _iconOrder = new();
    private Task? _initialization;
    private int _page, _settingsTab, _step, _busy;
    private string _nodeQuery = "", _localSubscription = "";
    private string _appQuery = "", _catalogQuery = "", _ruleQuery = "", _notice = "正在载入工作空间…", _currentIssue = "", _dsl = "", _generated = "", _url = "", _customName = "", _customEntries = "";
    private bool _addingApps, _addingRules, _customRule, _onboarding, _hasChanges, _previewRunning, _applying, _customIp, _runtimeUnavailable, _needsValidation, _subscriptionReady, _initialized, _disposed;
    private long _revision, _attemptRevision;
    private Choice? _home, _nonHome, _global, _group, _member;
    private AppRow? _selectedApp; private RuleRow? _selectedRule;
    private ClientRuntimeState _state = new(false, true, false, "");
    private string? _statusIssue;
    public MacPolicyDocument Document { get; private set; } = new();
    public bool IsPreview => _backend.IsPreview;
    public bool IsProduction => !IsPreview;
    public ConnectionWorkspaceViewModel Connections { get; }
    public ConnectionAttemptViewModel ConnectionAttempt { get; }
    public bool IsLoading { get; private set; }
    public bool IsBusy => _busy > 0;
    public bool CanEdit => !_closing && !IsBusy && !IsLoading;
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _operationCompletion;
    private bool _closing;
    private CancellationToken ActionToken => _operation?.Token ?? _lifetime.Token;
    public UiCommand CancelOperationCommand { get; }
    public string ImportLabel => IsPreview ? "导入示例订阅" : "导入订阅";
    public string LatencyHeader => IsPreview ? "示例延迟" : "实测延迟";
    public string ObservationDescription => IsPreview ? "示例观测 · 09:32–09:42 · 缺失采样保留空隙" : "未收到实际流量样本时不绘制曲线。";
    public string WindowTitle => IsPreview ? "Egress — macOS 隔离预览" : "Egress";
    public string ModeLabel => IsPreview ? "隔离预览" : "macOS";
    public string RuntimeBadge => IsPreview ? "MOCK" : PreviewRunning ? "XRAY" : "未连接";
    public string ApplyLabel => IsPreview ? "应用到模拟环境" : "应用并连接";
    public string SaveLabel => IsPreview ? "保存预览草稿" : "保存配置草稿";
    public string ProbeLabel => IsPreview ? "示例测速" : "测速";
    public string RefreshInventoryLabel => IsPreview ? "显式刷新示例清单" : "刷新固定清单";
    public string WorkspaceSafetyText => IsPreview ? "预览安全模式 · 不接管系统流量" : "Xray 唯一内核 · 出口严格绑定";
    public string SetupPermissionText => IsPreview ? "完全隔离 · 不使用管理员权限" : "启动时请求必要的网络授权";
    public string SetupApplyDescription => IsPreview ? "当前仅应用到内存模拟环境，真实 TUN 与系统 DNS 保持未启用。" : "TUN 启动不要求物理网卡在线。建立后经所选非家宽节点准备规则；出口失败会明确提示，准备未完成时恢复原网络状态。";
    public string SubscriptionDescription => IsPreview ? "隔离预览使用 example.invalid 示例 URL，读取内存响应；不会访问真实订阅。" : "订阅和节点提供器通过非家宽物理网卡直连，无需启动 Xray 或 TUN。直连失败时请检查订阅地址与网络后重试。";
    public string SubscriptionName => Document.Subscriptions.FirstOrDefault()?.Name ?? "节点订阅";
    public string CatalogDescription => IsPreview ? "先搜索，再明确选择。AI、开发、浏览器与日常应用均可添加。" : "从已安装的应用中搜索并选择，添加时读取完整的固定可执行清单。";
    public string TunStatusText => PreviewRunning ? "TUN 已接管" : _state.HelperInstalled ? "后台服务已就绪" : "尚未安装后台服务";
    public string TunDescription => IsPreview ? "当前是隔离预览，系统网络与管理员权限均未使用。" : "GUI 保持普通权限；启动时由授权后台服务管理 TUN、DNS 与网络恢复。";
    public string SourceStatusText => IsPreview ? "本地示例缓存" : _prepared.Assets.Count > 0 ? "资源已验证" : "等待准备";
    public string DnsDescription => IsPreview ? "系统 DNS 的备份与恢复属于特权服务；预览只编译 DNS 设置，不修改本机 DNS。" : "系统 DNS 的备份与恢复由授权后台服务处理；停止连接时恢复原设置。";
    public IBrush ModeBrush => Brush.Parse(IsPreview ? "#B2925A" : "#8A99AE");
    public string UpdateDescription => IsPreview ? "本次预览不下载或执行真实内核，不检查远程发布源。" : "启动前自动验证内核与规则资源，使用可用缓存并保留失败前的正常文件。";
    public string CatalogFooter => IsPreview ? "新应用默认未勾选 · 添加时保存固定 Helper 清单" : "仅显式选择的应用会加入；扫描不完整时保留原配置并报告原因。";
    public string NoObservationsText => "尚无连接观测数据；启动后只显示实际采样。";
    public MockCoreRuntime Core => (_backend as FixtureClientBackend)?.Core ?? throw new InvalidOperationException("Mock core is only available in an explicit fixture backend.");
    public BoundedLifecycleLog Lifecycle => (_backend as FixtureClientBackend)?.Lifecycle ?? throw new InvalidOperationException("Mock lifecycle is only available in an explicit fixture backend.");
    public ShellViewModel(string scenario = "overview") : this(new FixtureClientBackend(), scenario)
    {
        PreviewEnvironment.RequireIsolated(); InitializeAsync().GetAwaiter().GetResult(); ApplyScenario(scenario);
    }
    public ShellViewModel(IClientBackend backend, string scenario = "overview")
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Connections = new(backend.IsPreview);
        CancelOperationCommand = new(() => _operation?.Cancel());
        ManageGroupsCommand = new(() => ManagingGroups = !ManagingGroups);
        OverviewCommand = new(() => PageIndex = 0); AppsCommand = new(() => PageIndex = 1); RulesCommand = new(() => PageIndex = 2); NodesCommand = new(() => PageIndex = 3); SettingsCommand = new(() => PageIndex = 4);
        OpenLogsCommand = new(() => { OnboardingVisible = false; PageIndex = 4; SettingsTab = 4; });
        ShowLogDirectoryCommand = new(() => RunOperationAsync(ShowLogDirectoryAsync));
        RefreshSelectedInventoryCommand = new(() => RunOperationAsync(RefreshSelectedInventoryAsync), () => SelectedApp is not null && !IsBusy);
        AddAppsCommand = new(() => RunOperationAsync(ShowApplicationCatalogAsync)); CancelAppsCommand = new(() => { AddingApps = false; foreach (var row in _catalog) row.IsSelected = false; });
        ConfirmAppsCommand = new(() => RunOperationAsync(AddSelectedApplicationsAsync), () => SelectedCatalogCount > 0 && !IsBusy);
        AddRulesCommand = new(() => RunOperationAsync(ShowRuleCatalogAsync)); CloseRulesCommand = new(() => AddingRules = false);
        ShowCustomRuleCommand = new(() => { CreatingCustomRule = true; AddingRules = false; }); CancelCustomRuleCommand = new(() => CreatingCustomRule = false); CreateCustomRuleCommand = new(CreateCustomRule);
        ImportLocalSubscriptionCommand = new(ImportLocalSubscription);
        ImportSubscriptionCommand = new(() => RunOperationAsync(ImportSubscriptionAsync)); RunProbesCommand = new(async () => await RunProbesAsync()); CompileCommand = new(() => RunOperationAsync(CompileDraftAsync));
        ApplyCommand = new(() => RunOperationAsync(ApplyPreviewAsync)); SaveCommand = new(() => RunOperationAsync(SaveDraftAsync)); ApplyDslCommand = new(() => RunOperationAsync(ApplyDslAsync));
        ConnectionAttempt = new(() => ApplyCommand.Execute(null));
        _backend.SetProgressSink(new UiProgress(progress => ConnectionAttempt.Observe(progress)));
        ResetDslCommand = new(() => { DslText = PolicyYaml.Write(Document); Notice = "已从当前配置重新生成 DSL。"; }); TogglePreviewCommand = new(() => RunOperationAsync(TogglePreviewAsync));
        InitializeResetCommands();
        BeginSetupCommand = new(() => { SetupStep = 0; OnboardingVisible = true; }); CloseSetupCommand = new(() => OnboardingVisible = false); NextSetupCommand = new(() => RunOperationAsync(NextSetupAsync), () => CanAdvanceSetup && !IsBusy); BackSetupCommand = new(() => SetupStep = Math.Max(0, SetupStep - 1), () => SetupStep > 0 && !IsBusy);
    }
    public IReadOnlyList<Choice> InterfaceChoices { get; private set; } = [];
    public IReadOnlyList<Choice> GlobalChoices { get; private set; } = [];
    public IReadOnlyList<Choice> GroupChoices { get; private set; } = [];
    public IReadOnlyList<Choice> GroupMemberChoices => SelectedGroupModel?.MemberIds.Select(id => new Choice(id, GroupGraph.Name(id, Document))).ToArray() ?? [];
    public IReadOnlyList<Choice> GroupStrategies { get; } = [new("Select", "手动选择"), new("UrlTest", "自动测速"), new("Fallback", "故障转移")];
    private Choice? _strategy;
    private bool _reconciling;
    private IReadOnlyList<Choice> _availableProxyChoices=[];
    public Choice? SelectedStrategy { get => _strategy; set { if(_reconciling)return;if (Set(ref _strategy, value) && value is not null && SelectedGroupModel is { } g && Enum.TryParse<GroupKind>(value.Id, out var kind)) { if(g.Kind==kind)return;g.Kind = kind; g.StrategyOverridden = true; StateChanged(); RaiseGroup(); } } }
    public ProxyGroup? SelectedGroupModel => Document.Groups.FirstOrDefault(x => x.Id == _group?.Id);
    public Choice? SelectedHomeInterface { get => _home; set { if(_reconciling)return;if (Set(ref _home, value) && value is not null) { if(Document.Interfaces.HomeId==value.Id)return;Document.Interfaces.HomeId = value.Id; StateChanged(); Raise(nameof(HomeInterfaceName)); } } }
    public Choice? SelectedNonHomeInterface { get => _nonHome; set { if(_reconciling)return;if (Set(ref _nonHome, value) && value is not null) { if(Document.Interfaces.NonHomeId==value.Id)return;Document.Interfaces.NonHomeId = value.Id; StateChanged(); Raise(nameof(NonHomeInterfaceName)); } } }
    public Choice? SelectedGlobalTarget { get => _global; set { if(_reconciling)return;if (Set(ref _global, value) && value is not null) { if(Document.Defaults.Global.Kind==ExitKind.Proxy&&Document.Defaults.Global.TargetId==value.Id)return;Document.Defaults.Global = ExitChoice.Proxy(value.Id); StateChanged(); Raise(nameof(GlobalTargetName)); Raise(nameof(SetupGlobalManual)); Raise(nameof(SetupGlobalMemberChoices)); Raise(nameof(SelectedSetupGlobalMember)); NextSetupCommand.Refresh(); } } }
    public Choice? SelectedGroup { get => _group; set { if (Set(ref _group, value)) RaiseGroup(); } }
    public Choice? SelectedGroupMember { get => _member; set { if(_reconciling)return;if (Set(ref _member, value) && value is not null && SelectedGroupModel is { } g) { if(g.SelectedMemberId==value.Id)return;g.SelectedMemberId = value.Id; StateChanged(); } } }
    public bool IsManualGroup => SelectedGroupModel?.Kind == GroupKind.Select;
    public string GroupSummary => SelectedGroupModel is { } g ? $"{g.MemberIds.Count} 个成员 · {(g.Kind == GroupKind.Select ? "由你决定当前节点" : g.Kind == GroupKind.UrlTest ? $"每 {g.IntervalSeconds} 秒测速，保留 {g.ToleranceMilliseconds} ms 容差" : g.Kind == GroupKind.Fallback ? "按成员顺序选择可用节点" : "该分组类型不受支持，不能作为出口")}" : "先选择一个分组";
    public int PageIndex { get => _page; set { if (Set(ref _page, value)) { foreach (string name in new[] { nameof(OverviewVisible), nameof(AppsVisible), nameof(RulesVisible), nameof(NodesVisible), nameof(SettingsVisible), nameof(PageTitle), nameof(PageDescription) }) Raise(name); _ = LoadVisibleIconsAsync(); } } }
    public bool OverviewVisible => PageIndex == 0; public bool AppsVisible => PageIndex == 1; public bool RulesVisible => PageIndex == 2; public bool NodesVisible => PageIndex == 3; public bool SettingsVisible => PageIndex == 4;
    public string PageTitle => PageIndex switch { 0 => "总览", 1 => "应用分流", 2 => "规则集合", 3 => "节点与分组", _ => "设置" };
    public string PageDescription => PageIndex switch { 0 => "每一次连接，都有清晰的去向。", 1 => "为你的应用选择出口，未运行时策略依然保留。", 2 => "把域名与 IP 作为集合管理，让分流更简单。", 3 => "一个订阅，统一管理节点、分组与全局出口。", _ => "网络偏好、配置与诊断，尽在一处。" };
    public int SettingsTab { get => _settingsTab; set => Set(ref _settingsTab, value); }
    public string NodeQuery { get => _nodeQuery; set { if (Set(ref _nodeQuery, value)) { Raise(nameof(VisibleNodes)); Raise(nameof(NoNodesMatch)); PublishGroups(); } } }
    public IReadOnlyList<NodeRow> VisibleNodes => _nodes.Where(row => Matches(row.Name + " " + row.Protocol + " " + row.Model.Server + " "
        + string.Join(" ", Document.Groups.Where(group => group.MemberIds.Contains(row.Model.Id)).Select(group => group.Name)), NodeQuery)).ToArray();
    public bool NoNodesMatch => VisibleNodes.Count == 0;
    public string LocalSubscriptionYaml { get => _localSubscription; set => Set(ref _localSubscription, value); }
    public UiCommand ImportLocalSubscriptionCommand { get; }
    public int ResourceProxyPort { get => Document.Defaults.ResourceProxyPort; set { if (value == Document.Defaults.ResourceProxyPort) return; Document.Defaults.ResourceProxyPort = value; Raise(); StateChanged(); } }
    public string AppQuery { get => _appQuery; set { if (Set(ref _appQuery, value)) { PublishApplicationFilter(); Raise(nameof(NoAppsMatch)); _ = LoadVisibleIconsAsync(); } } }
    public string CatalogQuery { get => _catalogQuery; set { if (Set(ref _catalogQuery, value)) { PublishCatalogFilter(); Raise(nameof(NoCatalogMatch)); _ = LoadVisibleIconsAsync(); } } }
    public string RuleQuery { get => _ruleQuery; set { if (Set(ref _ruleQuery, value)) { PublishRuleFilter(); Raise(nameof(RuleSuggestions)); Raise(nameof(NoRulesMatch)); } } }
    public IReadOnlyList<AppRow> VisibleApps => _visibleApps;
    public IReadOnlyList<AppRow> OverviewApps => _overviewApps;
    public IReadOnlyList<CatalogRow> VisibleCatalog => _visibleCatalog;
    public IReadOnlyList<RuleRow> VisibleRules => _visibleRules;
    public IReadOnlyList<NodeRow> Nodes => _nodes;
    public IReadOnlyList<RuleSuggestion> RuleSuggestions => _ruleCatalog.Where(entry => Matches(entry.Id + " " + entry.Name, RuleQuery)).Select(entry => new RuleSuggestion(entry, () => RunOperationAsync(() => AddRuleAsync(entry)), RuleAlreadyAdded(entry))).ToArray();

    public bool AppsEmpty => _apps.Count == 0; public bool RulesEmpty => _rules.Count == 0;
    public bool NoAppsMatch => !AppsEmpty && VisibleApps.Count == 0; public bool NoCatalogMatch => VisibleCatalog.Count == 0; public bool NoRulesMatch => !RulesEmpty && VisibleRules.Count == 0;
    public int ApplicationCount => Document.Applications.Count; public int RuleCount => Document.RuleSets.Count;
    public string ApplicationCountLabel => $"{ApplicationCount} 个已管理应用"; public string RuleCountLabel => $"{RuleCount} 个用户集合";
    public string NodeCountLabel => $"{Document.Nodes.Count(x => x.IsSupported)} 个受支持节点 · {Document.Nodes.Count(x => !x.IsSupported)} 项不支持";
    public bool AddingApps { get => _addingApps; set { if (Set(ref _addingApps, value) && value) _ = LoadVisibleIconsAsync(); } }
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
    public string StatusTitle => IsLoading ? "正在载入工作空间" : _runtimeUnavailable ? (IsPreview ? "模拟运行状态不可用" : "网络服务状态不可用") : PreviewRunning ? (IsPreview ? "模拟网络已就绪" : "网络已连接") : IsPreview ? "模拟已手动停止" : _state.HelperInstalled ? "网络已停止" : "完成设置，连接你的网络";
    public string StatusDescription => IsPreview ? (_runtimeUnavailable ? "请查看诊断，校验后重新应用配置。" : PreviewRunning ? "出口严格绑定，策略清晰可见。" : "恢复模拟后继续使用保存的策略。") : string.IsNullOrWhiteSpace(_state.Message) ? "选择网卡与全局代理后启动。" : _state.Message;
    public string TogglePreviewLabel => _runtimeUnavailable ? "重试应用" : PreviewRunning ? (IsPreview ? "停止模拟" : "停止连接") : (IsPreview ? "恢复模拟" : "启动连接");
    public Avalonia.Media.IBrush StatusIconBrush => Avalonia.Media.Brush.Parse(_runtimeUnavailable ? "#BF7781" : PreviewRunning ? "#3F9B7F" : "#8D9DB4");
    public Avalonia.Media.IBrush StatusSurfaceBrush => Avalonia.Media.Brush.Parse(_runtimeUnavailable ? "#FFF0F2" : PreviewRunning ? "#ECF7F2" : "#F0F3F8");
    private void SetRuntimeUnavailable(bool value)
    {
        _runtimeUnavailable = value;
        foreach (string property in new[] { nameof(StatusTitle), nameof(StatusDescription), nameof(TogglePreviewLabel), nameof(StatusIconBrush), nameof(StatusSurfaceBrush) }) Raise(property);
    }
    public string HomeInterfaceName => SelectedHomeInterface?.Name ?? "尚未选择"; public string NonHomeInterfaceName => SelectedNonHomeInterface?.Name ?? "尚未选择"; public string GlobalTargetName => SelectedGlobalTarget?.Name ?? "必须选择一个代理";
    public string ProxyResolvedName => GroupSelector.Resolve(Document.Defaults.Global.TargetId ?? "", Document, _probes, _backend.Now).NodeId is { } id ? GroupGraph.Name(id, Document) : "待校验";
    public IReadOnlyList<Observation> Observations => IsPreview ? PreviewFixtures.Observations : [];
    public bool HasObservations => Observations.Count > 0;
    public BoundedEvents Events { get; } = [];
    public AsyncUiCommand ShowLogDirectoryCommand { get; }
    public UiCommand OverviewCommand { get; }
    public UiCommand AppsCommand { get; }
    public UiCommand RulesCommand { get; }
    public UiCommand NodesCommand { get; }
    public UiCommand SettingsCommand { get; }
    public UiCommand OpenLogsCommand { get; }
    public AsyncUiCommand RefreshSelectedInventoryCommand { get; }
    public AsyncUiCommand AddAppsCommand { get; }
    public UiCommand CancelAppsCommand { get; }
    public AsyncUiCommand ConfirmAppsCommand { get; }
    public AsyncUiCommand AddRulesCommand { get; }
    public UiCommand CloseRulesCommand { get; }
    public UiCommand ShowCustomRuleCommand { get; }
    public UiCommand CancelCustomRuleCommand { get; }
    public UiCommand CreateCustomRuleCommand { get; }
    public AsyncUiCommand ImportSubscriptionCommand { get; }
    public AsyncUiCommand RunProbesCommand { get; }
    public AsyncUiCommand CompileCommand { get; }
    public AsyncUiCommand ApplyCommand { get; }
    public AsyncUiCommand SaveCommand { get; }
    public AsyncUiCommand ApplyDslCommand { get; }
    public UiCommand ResetDslCommand { get; }
    public AsyncUiCommand TogglePreviewCommand { get; }
    public UiCommand BeginSetupCommand { get; }
    public UiCommand CloseSetupCommand { get; }
    public AsyncUiCommand NextSetupCommand { get; }
    public UiCommand BackSetupCommand { get; }
    public bool OnboardingVisible { get => _onboarding; set => Set(ref _onboarding, value); }
    public int SetupStep { get => _step; set { if (Set(ref _step, value)) { foreach (string name in new[] { nameof(SetupTitle), nameof(SetupDescription), nameof(SetupProgress), nameof(SetupInterfaces), nameof(SetupSubscription), nameof(SetupGlobal), nameof(SetupApplications), nameof(SetupReview), nameof(NextSetupLabel) }) Raise(name); BackSetupCommand?.Refresh(); NextSetupCommand?.Refresh(); if (SetupStep == 3) _ = LoadVisibleIconsAsync(); } } }
    public string SetupTitle => SetupStep switch { 0 => "先认识你的两条网络", 1 => "接入你的节点订阅", 2 => "保留你需要的分组", 3 => "让常用应用先行", _ => "检查并启动连接" };
    public string SetupDescription => SetupStep switch { 0 => "家宽与非家宽需要绑定两张不同的物理网卡。", 1 => "仅导入节点与分组，DNS 与分流由 Egress 统一管理。", 2 => "应用只需选择分组；节点、DIRECT 和自动测速在分组内管理。", 3 => "选择需要单独分流的应用；以后也可以随时添加。", _ => IsPreview ? "先校验配置，再应用到隔离的模拟运行环境。" : "先请求管理员授权并启动内置 Xray TUN，再通过所选非家宽分组节点下载规则；失败时可以重试。" };
    private ProxyGroup? SetupGlobalGroup => Document.Groups.FirstOrDefault(group => group.Id == SelectedGlobalTarget?.Id);
    public bool SetupGlobalManual => SetupGlobalGroup?.Kind == GroupKind.Select;
    public IReadOnlyList<Choice> SetupGlobalMemberChoices => SetupGlobalGroup?.MemberIds.Select(id => new Choice(id, Document.Nodes.FirstOrDefault(node => node.Id == id)?.Name ?? Document.Groups.FirstOrDefault(group => group.Id == id)?.Name ?? id)).ToArray() ?? [];
    public Choice? SelectedSetupGlobalMember
    {
        get => SetupGlobalMemberChoices.FirstOrDefault(choice => choice.Id == SetupGlobalGroup?.SelectedMemberId);
        set { if (_reconciling || value is null || SetupGlobalGroup is not { } group || group.SelectedMemberId == value.Id) return; group.SelectedMemberId = value.Id; Raise(); StateChanged(); RaiseGroup(); NextSetupCommand.Refresh(); }
    }
    public bool CanAdvanceSetup => SetupStep switch
    {
        0 => SelectedHomeInterface is not null && SelectedNonHomeInterface is not null && SelectedHomeInterface.Id != SelectedNonHomeInterface.Id,
        1 => _subscriptionReady,
        2 => SelectedGlobalTarget is not null && (!SetupGlobalManual || SetupGlobalGroup is { } group && group.SelectedMemberId is { } member && group.MemberIds.Contains(member, StringComparer.Ordinal)),
        _ => true
    };
    public string SetupSubscriptionStatus => _subscriptionReady ? NodeCountLabel : "尚未导入";
    public string SetupProgress => $"{SetupStep + 1} / 5"; public bool SetupInterfaces => SetupStep == 0; public bool SetupSubscription => SetupStep == 1; public bool SetupGlobal => SetupStep == 2; public bool SetupApplications => SetupStep == 3; public bool SetupReview => SetupStep == 4;
    public string NextSetupLabel => SetupStep == 4 ? (IsPreview ? "校验并完成" : "校验并启动") : "继续";
    private static bool Matches(string text, string query) => string.IsNullOrWhiteSpace(query) || text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    private string GlobalId() => Document.Defaults.Global.TargetId ?? "";
    private void CatalogSelectionChanged() { Raise(nameof(SelectedCatalogCount)); Raise(nameof(AddSelectedLabel)); ConfirmAppsCommand.Refresh(); }
    public Task InitializeAsync() => _initialization ??= LoadWorkspaceAsync();
    private async Task LoadWorkspaceAsync()
    {
        IsLoading = true; Raise(nameof(IsLoading)); Raise(nameof(CanEdit)); Raise(nameof(StatusTitle));
        try
        {
            var bootstrap = await _backend.LoadAsync(_lifetime.Token);
            Document = bootstrap.Document; _interfaces = bootstrap.Interfaces; _applications = bootstrap.Applications.ToList();
            InterfaceChoices = _interfaces.Select(item => new Choice(item.Id, item.Name, item.Detail)).ToArray(); Raise(nameof(InterfaceChoices));
            _subscriptionReady = Document.Nodes.Count > 0 || Document.Groups.Count > 0;
            SubscriptionUrl = Document.Subscriptions.FirstOrDefault()?.Url ?? (IsPreview ? PreviewFixtures.SubscriptionUrl : "");
            _state = bootstrap.Runtime; PreviewRunning = _state.Running; SetRuntimeUnavailable(!_state.Available);
            _initialized = true; RebuildProjection();
            if (IsPreview)
            {
                _ruleCatalog = await _backend.LoadRuleCatalogAsync(Document, ActionToken);
                await MeasureGroupsAsync(applyAutomatic: false); await CompileDraftAsync();
            }
            else
            {
                _needsValidation = true; ConfigValid = false; Raise(nameof(ValidationLabel));
                OnboardingVisible = !bootstrap.HasSavedPolicy || PolicyValidator.Validate(Document).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error); SetupStep = 0;
                Notice = bootstrap.HasSavedPolicy ? "已载入保存的配置；系统网络尚未因打开界面而改变。" : "选择两张网卡并导入订阅，完成首次设置。";
                _ = LoadVisibleIconsAsync();
            }
            HasChanges = false; Raise(nameof(SubscriptionName)); Raise(nameof(SetupSubscriptionStatus));
            await RefreshConnectionsAsync();
            AddEvent(IsPreview ? "隔离工作空间已准备" : "工作空间已载入", IsPreview ? "仅使用合成资源与内存适配器。" : "已载入本地草稿与已安装应用目录。", "startup");
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            RecordFailure("workspace.load", exception); CurrentIssue = SafeFailure(exception, "工作空间载入失败；请查看日志后重试。");
            SetRuntimeUnavailable(true); Notice = "工作空间尚未完成载入。";
        }
        finally { IsLoading = false; Raise(nameof(IsLoading)); Raise(nameof(CanEdit)); Raise(nameof(StatusTitle)); }
    }
    private void ApplyScenario(string scenario)
    {
        if (!IsPreview) return;
        switch (scenario)
        {
            case "apps": PageIndex = 1; break;
            case "add-apps": PageIndex = 1; AddingApps = true; break;
            case "rules": PageIndex = 2; break;
            case "add-rules": PageIndex = 2; AddingRules = true; break;
            case "nodes": PageIndex = 3; break;
            case "settings": PageIndex = 4; break;
            case "dsl": PageIndex = 4; SettingsTab = 2; break;
            case "onboarding": OnboardingVisible = true; SetupStep = 0; _subscriptionReady = false; Document.Defaults.Global = ExitChoice.Proxy(""); foreach (var manual in Document.Groups.Where(group => group.Kind == GroupKind.Select)) manual.SelectedMemberId = null; _global = null; Raise(nameof(SelectedGlobalTarget)); RaiseGroup(); DslText = PolicyYaml.Write(Document); break;
            case "empty": Document.Applications.Clear(); Document.RuleSets.Clear(); RebuildProjection(); PageIndex = 1; break;
            case "error": Document.Interfaces.NonHomeId = Document.Interfaces.HomeId; _nonHome = _home; DslText = PolicyYaml.Write(Document); CompileDraft(); PageIndex = 4; break;
            case "stopped": PreviewRunning = false; break;
            case "unavailable": PreviewRunning = false; SetRuntimeUnavailable(true); CurrentIssue = "示例状态：模拟运行不可用，未显示过期的健康状态。"; break;
        }
    }
    private static bool IsExpectedFailure(Exception exception) => exception is IOException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException or ArgumentException or PlatformNotSupportedException or OperationCanceledException;
    private static string SafeFailure(Exception exception, string fallback) => exception is OperationCanceledException ? "操作已取消；原有配置已保留。" : exception is ResourcePreparationException or RuntimeFailureException ? exception.Message : fallback;
    private async Task RunOperationAsync(Func<Task> operation)
    {
        if (_disposed || _closing || IsBusy) return;
        if (IsProbing) { _probeCancellation?.Cancel(); if (_probeCompletion is { } probe) await probe.Task; }
        if (_maintainingGroups)
        {
            _groupMaintenanceCancellation?.Cancel();
            if (_groupMaintenanceCompletion is { } pending) await pending.Task;
            if (_disposed || _closing || IsBusy) return;
        }
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = owned;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _operationCompletion = completion;
        _busy++; Raise(nameof(IsBusy)); Raise(nameof(CanEdit)); RefreshCommands();
        try { await operation(); }
        catch (Exception exception) when (IsExpectedFailure(exception)) { RecordFailure("client.operation", exception); CurrentIssue = SafeFailure(exception, "操作未能完成，请检查配置与日志后重试。"); AddEvent("操作未完成", CurrentIssue, "diagnostic"); }
        finally { if (ReferenceEquals(_operation, owned)) _operation = null; _busy--; Raise(nameof(IsBusy)); Raise(nameof(CanEdit)); RefreshCommands(); completion.TrySetResult(); }
    }
    private void RefreshCommands()
    {
        ConfirmAppsCommand.Refresh(); RefreshSelectedInventoryCommand.Refresh(); NextSetupCommand.Refresh(); BackSetupCommand.Refresh();
        RequestResetCommand.Refresh(); CancelResetCommand.Refresh(); ConfirmResetCommand.Refresh();
    }
    private void RecordFailure(string operation, Exception error)
    { try { _backend.RecordFailure(operation, error); } catch (Exception) { /* Diagnostics are observational. */ } }
    private void AddEvent(string title, string detail, string kind)
    {
        Events.Insert(0, new(_backend.Now.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), title, detail, kind));
        try { _backend.RecordEvent(kind, title + " · " + detail); } catch (Exception) { }
    }
    private ClientApplication? AppMetadata(string bundlePath) => _applications.FirstOrDefault(app => app.BundlePath == bundlePath);
    private static string StableObjectId(string prefix, string value) => prefix + (value.Length > 0 && value.Length <= 100 && char.IsAsciiLetterOrDigit(value[0]) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? value : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24]);
    private bool RuleAlreadyAdded(ClientRuleCatalogEntry entry) => Document.RuleSets.Any(rule => rule.Id == StableObjectId("rule-", entry.Id) || entry.Sources.Count > 0 && entry.Sources.All(source => rule.Sources.Contains(source, StringComparer.Ordinal)));
    private void RebuildProjection()
    {
        _reconciling=true;
        try
        {
        string? appId = SelectedApp?.Model.Id, ruleId = SelectedRule?.Model.Id;
        _availableProxyChoices = Document.Groups.Where(group => group.IsSupported && group.Retained).Select(group => new Choice(group.Id, group.Name, "分组")).ToArray();
        GlobalChoices=Document.Defaults.Global.TargetId is {Length:>0} missing&&!_availableProxyChoices.Any(choice=>choice.Id==missing)
            ?_availableProxyChoices.Append(new Choice(missing,"缺失的全局代理","请明确重新选择")).ToArray():_availableProxyChoices;
        GroupChoices = Document.Groups.Select(group => new Choice(group.Id, group.Name)).ToArray();
        var previousApps = _apps.ToDictionary(row => row.Model.Id, StringComparer.Ordinal); var previousRules = _rules.ToDictionary(row => row.Model.Id, StringComparer.Ordinal); bool selectedInventoryChanged = false;
        _apps = Document.Applications.Select(model =>
        {
            if (previousApps.TryGetValue(model.Id, out var row)) { if (row.UpdateModel(model) && model.Id == appId) selectedInventoryChanged = true; return row; }
            var added = new AppRow(model, GlobalId, () => _availableProxyChoices, StateChanged, RemoveApp, AppMetadata, IsPreview); if (_icons.TryGetValue(model.BundlePath, out var icon)) added.SetIcon(icon); return added;
        }).ToList();
        _rules = Document.RuleSets.Select(model =>
        {
            if (previousRules.TryGetValue(model.Id, out var row)) { row.UpdateModel(model); return row; }
            return new RuleRow(model, GlobalId, () => _availableProxyChoices, StateChanged, RemoveRule, MoveRule, CanMoveRule, EffectiveRuleContent);
        }).ToList();
        var previousCatalog = _catalog.ToDictionary(row => row.Model.Id, StringComparer.Ordinal);
        _catalog = _applications.Select(model =>
        {
            bool managed = Document.Applications.Any(app => app.BundlePath == model.BundlePath);
            if (previousCatalog.TryGetValue(model.Id, out var row)) { row.UpdateModel(model, managed); return row; }
            var added = new CatalogRow(model, CatalogSelectionChanged, managed, IsPreview); if (_icons.TryGetValue(model.BundlePath, out var icon)) added.SetIcon(icon); return added;
        }).ToList();
        _nodes = Document.Nodes.Select(model => new NodeRow(model, () => SelectedGroupModel, () => { StateChanged(); RaiseGroup(); })).ToList();
        RefreshNodeLatencies(); PublishApplicationFilter(); PublishRuleFilter(); PublishCatalogFilter(); if (selectedInventoryChanged) Raise(nameof(SelectedAppPaths));
        _home = InterfaceChoices.FirstOrDefault(choice => choice.Id == Document.Interfaces.HomeId); _nonHome = InterfaceChoices.FirstOrDefault(choice => choice.Id == Document.Interfaces.NonHomeId);
        _global = GlobalChoices.FirstOrDefault(choice => choice.Id == GlobalId()); _group = GroupChoices.FirstOrDefault(choice => choice.Id == _group?.Id) ?? GroupChoices.FirstOrDefault();
        SelectedApp = _apps.FirstOrDefault(row => row.Model.Id == appId); SelectedRule = _rules.FirstOrDefault(row => row.Model.Id == ruleId);
        foreach (string property in new[] { nameof(Nodes), nameof(GlobalChoices), nameof(GroupChoices), nameof(SelectedHomeInterface), nameof(SelectedNonHomeInterface), nameof(SelectedGlobalTarget), nameof(SelectedGroup), nameof(ApplicationCount), nameof(RuleCount), nameof(ApplicationCountLabel), nameof(RuleCountLabel), nameof(NodeCountLabel), nameof(AppsEmpty), nameof(RulesEmpty), nameof(NoAppsMatch), nameof(NoCatalogMatch), nameof(NoRulesMatch), nameof(RuleSuggestions), nameof(HomeInterfaceName), nameof(NonHomeInterfaceName), nameof(GlobalTargetName), nameof(SelectedAppName), nameof(SelectedRuleName), nameof(SelectedRuleSummary), nameof(DnsServer), nameof(SubscriptionName), nameof(SetupGlobalManual), nameof(SetupGlobalMemberChoices), nameof(SelectedSetupGlobalMember) }) Raise(property);
        RebuildGroups(); Raise(nameof(VisibleNodes)); Raise(nameof(NoNodesMatch)); Raise(nameof(ResourceProxyPort)); RaiseGroup(); CatalogSelectionChanged(); DslText = PolicyYaml.Write(Document);
        }
        finally{_reconciling=false;}
    }
    private void PublishApplicationFilter()
    {
        var visible = _apps.Where(row => Matches(row.Name + " " + row.Category + " " + row.Model.BundleIdentifier + " " + row.BundlePath + " " + string.Join(" ", row.ExecutablePaths), AppQuery)).ToArray(); if (!_visibleApps.SequenceEqual(visible)) { _visibleApps = visible; Raise(nameof(VisibleApps)); }
        var overview = _apps.Take(4).ToArray(); if (!_overviewApps.SequenceEqual(overview)) { _overviewApps = overview; Raise(nameof(OverviewApps)); }
    }
    private void PublishCatalogFilter()
    {
        var visible = _catalog.Where(row => Matches(row.Name + " " + row.Model.Category + " " + row.Model.BundleIdentifier + " " + row.Model.BundlePath + " " + row.Model.PrimaryExecutable, CatalogQuery)).ToArray(); if (!_visibleCatalog.SequenceEqual(visible)) { _visibleCatalog = visible; Raise(nameof(VisibleCatalog)); }
    }
    private void PublishRuleFilter()
    {
        var visible = _rules.Where(row => Matches(row.Name + " " + row.MatchSummary, RuleQuery)).ToArray(); if (!_visibleRules.SequenceEqual(visible)) { _visibleRules = visible; Raise(nameof(VisibleRules)); }
    }
    private void RaiseGroup()
    {
        _member = GroupMemberChoices.FirstOrDefault(choice => choice.Id == SelectedGroupModel?.SelectedMemberId); _strategy = GroupStrategies.FirstOrDefault(choice => choice.Id == SelectedGroupModel?.Kind.ToString());
        foreach (string property in new[] { nameof(GroupMemberChoices), nameof(SelectedGroupMember), nameof(SelectedStrategy), nameof(IsManualGroup), nameof(GroupSummary) }) Raise(property);
        foreach (var row in _nodes) row.RefreshMember(); RefreshNodeLatencies();
    }
    private void StateChanged()
    {
        _revision++; _needsValidation = true; Raise(nameof(ValidationLabel)); HasChanges = true; DslText = PolicyYaml.Write(Document); ScheduleDraftSave(); Notice = "更改等待应用，配置草稿自动保存。"; CurrentIssue = ""; Raise(nameof(ProxyResolvedName)); NextSetupCommand.Refresh();
    }
    private async Task ShowApplicationCatalogAsync()
    {
        _missingIcons.Clear(); AddingApps = true; CatalogQuery = ""; Notice = "正在读取已安装应用目录…";
        var discovered = await _backend.DiscoverApplicationsAsync(ActionToken);
        var byPath = discovered.ToDictionary(app => app.BundlePath, StringComparer.Ordinal); var oldPaths = _applications.Select(app => app.BundlePath).ToHashSet(StringComparer.Ordinal);
        _applications = _applications.Where(app => byPath.ContainsKey(app.BundlePath)).Select(app => byPath[app.BundlePath]).Concat(discovered.Where(app => !oldPaths.Contains(app.BundlePath))).ToList(); RebuildProjection(); Notice = "搜索后勾选需要加入的应用。"; await LoadVisibleIconsAsync();
    }
    public void AddSelectedApplications() { if (!IsPreview) throw new InvalidOperationException("Use the asynchronous production command."); AddSelectedApplicationsAsync().GetAwaiter().GetResult(); }
    public async Task AddSelectedApplicationsAsync()
    {
        var selected = _catalog.Where(row => row.IsSelected).ToArray(); if (selected.Length == 0) return;
        var inventories = new List<ClientApplication>();
        foreach (var row in selected)
        {
            var inventory = await _backend.BuildInventoryAsync(row.Model, [], ActionToken);
            if (!inventory.InventoryComplete || inventory.ExecutablePaths.Count == 0) { CurrentIssue = inventory.InventoryError ?? "应用扫描不完整，尚未添加。"; return; }
            inventories.Add(inventory);
        }
        foreach (var app in inventories)
        {
            if (Document.Applications.Any(saved => saved.BundlePath == app.BundlePath)) continue;
            Document.Applications.Add(new() { Id = StableObjectId("app-", app.Id), Name = app.Name, BundlePath = app.BundlePath, BundleIdentifier = app.BundleIdentifier, PrimaryExecutable = app.PrimaryExecutable, ExecutablePaths = app.ExecutablePaths.Distinct(StringComparer.Ordinal).ToList(), Via = ExitChoice.NonHome });
            int index = _applications.FindIndex(existing => existing.Id == app.Id); if (index >= 0) _applications[index] = app;
        }
        RebuildProjection(); StateChanged(); await PersistDraftAsync(); AddingApps = false; Notice = $"已添加 {inventories.Count} 个应用，固定可执行清单已保存。"; await LoadVisibleIconsAsync();
    }
    private async Task RefreshSelectedInventoryAsync()
    {
        if (SelectedApp is not { } selected) return;
        var app = selected.Model;
        var metadata = AppMetadata(app.BundlePath) ?? new ClientApplication(app.Id, app.Name, "app", "已管理应用", app.BundlePath, app.PrimaryExecutable, app.ExecutablePaths, false, app.BundleIdentifier);
        var inventory = await _backend.BuildInventoryAsync(metadata, app.ExecutablePaths.ToArray(), ActionToken);
        if (!inventory.InventoryComplete) { CurrentIssue = inventory.InventoryError ?? "清单扫描不完整；已保存的全部路径与出口保持不变。"; return; }
        string[] merged = app.ExecutablePaths.Concat(inventory.ExecutablePaths).Distinct(StringComparer.Ordinal).ToArray();
        if (app.ExecutablePaths.SequenceEqual(merged, StringComparer.Ordinal)) { Notice = "固定清单未变化，保存的旧路径继续保留。"; return; }
        app.ExecutablePaths = merged.ToList(); selected.NotifyInventory(); Raise(nameof(SelectedAppPaths)); StateChanged(); Notice = "固定可执行清单已更新；校验应用前保留原有运行配置。";
    }
    private RuleContent EffectiveRuleContent(ManagedRuleSet rule) => new(rule.Domains.Concat(rule.Sources.Where(_preparedSources.ContainsKey).SelectMany(source => _preparedSources[source].Domains)).Distinct().ToArray(), rule.IpCidrs.Concat(rule.Sources.Where(_preparedSources.ContainsKey).SelectMany(source => _preparedSources[source].IpCidrs)).Distinct(StringComparer.Ordinal).ToArray(), rule.Sources.All(_preparedSources.ContainsKey));
    private bool CanMoveRule(RuleRow row, int direction) { int index = Document.RuleSets.IndexOf(row.Model); return index >= 0 && index + direction >= 0 && index + direction < Document.RuleSets.Count; }
    private void MoveRule(RuleRow row, int direction) { if (!CanMoveRule(row, direction)) return; int index = Document.RuleSets.IndexOf(row.Model); Document.RuleSets.RemoveAt(index); Document.RuleSets.Insert(index + direction, row.Model); RebuildProjection(); StateChanged(); Notice = "用户规则顺序已更新，等待校验应用。"; }
    private void RemoveApp(AppRow row) { Document.Applications.Remove(row.Model); RebuildProjection(); StateChanged(); Notice = $"已移除 {row.Name} 的单独分流策略。"; }
    private void RemoveRule(RuleRow row) { Document.RuleSets.Remove(row.Model); RebuildProjection(); StateChanged(); }
    private async Task ShowRuleCatalogAsync()
    {
        AddingRules = true; RuleQuery = ""; Notice = "正在载入规则目录…";
        _ruleCatalog = await _backend.LoadRuleCatalogAsync(Document, ActionToken); Raise(nameof(RuleSuggestions)); Notice = "搜索服务名称，添加域名与 IP 集合。";
    }
    private async Task AddRuleAsync(ClientRuleCatalogEntry entry)
    {
        if (RuleAlreadyAdded(entry)) { Notice = "该集合已经添加。"; return; }
        var candidate = CopyDocument(Document); candidate.RuleSets.Add(new() { Id = StableObjectId("rule-", entry.Id), Name = entry.Name, Sources = entry.Sources.ToList(), Via = ExitChoice.Proxy(GlobalId()) });
        var prepared = await _backend.PrepareResourcesAsync(candidate, ActionToken);
        if (entry.Sources.Any(source => !prepared.Bindings.Any(binding => binding.Source == source))) { CurrentIssue = "规则来源尚未完成准备；当前集合未改变。"; return; }
        SetPreparedResources(prepared); Document = candidate; RebuildProjection(); StateChanged(); AddingRules = false; Notice = $"已添加 {entry.Name}，域名与 IP 由同一条目管理。";
    }
    private void CreateCustomRule()
    {
        string[] entries = CustomRuleEntries.Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(CustomRuleName) || entries.Length == 0) { CurrentIssue = "请填写集合名称及至少一条规则。"; return; }
        var rule = new ManagedRuleSet { Id = "custom-" + Guid.NewGuid().ToString("N"), Name = CustomRuleName.Trim(), Via = ExitChoice.NonHome };
        if (CustomRuleIsIp) rule.IpCidrs = entries.ToList(); else rule.Domains = entries.Select(entry => new DomainRule(DomainMatchKind.Suffix, entry)).ToList();
        Document.RuleSets.Add(rule); var errors = PolicyValidator.Validate(Document).Where(diagnostic => diagnostic.ObjectId == rule.Id && diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0) { Document.RuleSets.Remove(rule); CurrentIssue = string.Join(" ", errors.Select(error => error.Message)); return; }
        CreatingCustomRule = false; CustomRuleName = ""; CustomRuleEntries = ""; RebuildProjection(); StateChanged(); Notice = "自定义集合已加入草稿。";
    }
    public int FixtureSubscriptionRequestCount => (_backend as FixtureClientBackend)?.RequestCount ?? throw new InvalidOperationException("Fixture hook is unavailable in production.");
    public void SetFixtureSubscriptionPayload(string yaml) { if (_backend is not FixtureClientBackend fixture) throw new InvalidOperationException("Fixture hook is unavailable in production."); fixture.SetSubscriptionPayload(yaml); }
    public void ImportLocalSubscription()
    {
        const string sourceId = "local-bootstrap";
        if (Encoding.UTF8.GetByteCount(LocalSubscriptionYaml) > 8 * 1024 * 1024) { CurrentIssue = "本地订阅超出 8 MB 限制。"; return; }
        var imported = ClashSubscriptionImporter.Import(LocalSubscriptionYaml, sourceId);
        if (!imported.IsSuccess || imported.Nodes.Count == 0) { CurrentIssue = "请粘贴包含实际节点的 Clash YAML；仅有在线提供器时无法建立首次连接。"; return; }
        if (imported.Groups.Count == 0)
            imported = imported with { Groups = [new ProxyGroup { Id = "local-bootstrap-group", Name = "本地导入", SourceId = sourceId,
                Kind = GroupKind.Select, MemberIds = imported.Nodes.Where(node => node.IsSupported).Select(node => node.Id).ToList() }] };
        Document = SubscriptionReconciler.Merge(Document, sourceId, imported);
        _probes.Clear(); _subscriptionReady = true; CurrentIssue = ""; LocalSubscriptionYaml = "";
        InitializeImportedDefault(); RebuildProjection(); StateChanged(); Raise(nameof(SetupSubscriptionStatus));
        Notice = "本地节点与分组已导入。保留需要的分组并选择默认组，然后连接。";
    }
    public async Task ImportSubscriptionAsync()
    {
        if (!Uri.TryCreate(SubscriptionUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) { CurrentIssue = "请输入有效的 HTTPS 订阅 URL。"; return; }
        CurrentIssue = ""; Notice = "正在直连获取并校验订阅…";
        try
        {
            var imported = await _backend.ImportSubscriptionAsync(uri, Document, ActionToken);
            Document = imported; InitializeImportedDefault(); _probes.Clear(); _subscriptionReady = Document.Nodes.Count > 0 || Document.Groups.Count > 0; RebuildProjection(); StateChanged();
            if (IsPreview) await MeasureGroupsAsync(applyAutomatic: false);
            Raise(nameof(SetupSubscriptionStatus)); Notice = $"订阅已更新：{NodeCountLabel}；原有选择已保留。"; AddEvent("订阅已更新", "已校验节点与分组；原有配置选择继续保留。", "subscription");
        }
        catch (Exception exception) when (IsExpectedFailure(exception)) { RecordFailure("subscription.import", exception); Notice = exception is OperationCanceledException ? "订阅导入已取消；原有节点与缓存已保留。" : "订阅直连未完成；原有节点与缓存已保留，可以重试。"; CurrentIssue = SafeFailure(exception, "订阅更新失败；原有配置与已验证缓存已保留。"); AddEvent("订阅更新失败", CurrentIssue, "subscription"); }
    }
    public void RunProbes() { if (!IsPreview) throw new InvalidOperationException("Use asynchronous real probing."); RunProbesAsync().GetAwaiter().GetResult(); }
    public Task RunProbesAsync() => RunGroupProbeAsync(null);
    private async Task MeasureGroupsAsync(bool applyAutomatic)
    {
        CurrentIssue = ""; Notice = IsPreview ? "正在执行示例测速…" : "正在通过指定非家宽出口测速…";
        _ = RequireNonHomeBinding();
        var probes = await _backend.ProbeMembersAsync(CopyDocument(Document), CompileOptions(),
            GroupRequests(Document.Groups.Where(group => group.Retained), Document), ActionToken);
        MergeProbes(probes); if (applyAutomatic) await ApplyAutomaticObservationsAsync(ActionToken);
        Notice = IsPreview ? "示例测速已完成；没有建立真实网络连接。" : $"测速已完成 · {_backend.Now.ToLocalTime():HH:mm:ss}；失败或缺失结果不显示为零。"; Raise(nameof(ProxyResolvedName));
    }
    private void RefreshNodeLatencies()
    {
        string? url = SelectedGroupModel?.TestUrl;
        foreach (var row in _nodes)
        {
            var latest = _probes.Where(probe => probe.NodeId == row.Model.Id && (url is null || probe.TestUrl == url) && probe.ObservedAt <= _backend.Now && _backend.Now - probe.ObservedAt <= TimeSpan.FromMinutes(10)).OrderByDescending(probe => probe.ObservedAt).FirstOrDefault();
            row.SetLatency(latest is { Available: true } ? latest.LatencyMilliseconds : null);
        }
    }
    private InterfaceBinding RequireNonHomeBinding(MacPolicyDocument? document = null)
    {
        var policy = document ?? Document; var binding = _interfaces.FirstOrDefault(item => item.Id == policy.Interfaces.NonHomeId);
        if (binding is null) throw new RuntimeFailureException("interface.identity");
        return new(binding.Id, binding.BsdName);
    }
    public XrayCompileOptions CompileOptions(MacPolicyDocument? candidate = null) => new() { Interfaces = _interfaces.Select(item => new InterfaceBinding(item.Id, item.BsdName)).ToList(), Probes = _probes.ToList(), PreparedRuleSources = _prepared.Bindings.ToList(), Now = _backend.Now, ResourceNodeId = GroupGraph.ResourceNode(candidate ?? Document, _probes, _backend.Now) };
    private void SetPreparedResources(ClientPreparedResources prepared) { _prepared = prepared; Raise(nameof(SourceStatusText)); }
    public IReadOnlyDictionary<string, byte[]> PreparedAssets() => _prepared.Assets;
    public void CompileDraft() { if (!IsPreview) throw new InvalidOperationException("Use asynchronous production validation."); CompileDraftAsync().GetAwaiter().GetResult(); }
    public async Task CompileDraftAsync()
    {
        CurrentIssue = ""; Notice = "正在准备并校验运行配置…"; SetPreparedResources(await _backend.PrepareResourcesAsync(Document, ActionToken)); CompilePrepared(Document);
        foreach (var row in _rules) row.UpdateModel(row.Model);
    }
    private XrayCompileResult CompilePrepared(MacPolicyDocument document)
    {
        _needsValidation = false; var result = XrayConfigCompiler.Compile(document, CompileOptions(document)); ConfigValid = result.IsSuccess;
        CurrentIssue = string.Join(" ", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Message).Distinct());
        GeneratedConfig = result.Json ?? "配置未通过校验，未生成运行配置。"; Raise(nameof(ConfigValid)); Raise(nameof(ValidationLabel)); Raise(nameof(ProxyResolvedName));
        if (result.IsSuccess) Notice = "配置校验通过：三出口、全局代理与固定应用清单已一致编译。";
        return result;
    }
    public async Task TogglePreviewAsync()
    {
        if (!PreviewRunning || _runtimeUnavailable) { await ApplyPreviewAsync(); return; }
        bool stopped = await _backend.StopAsync(ActionToken);
        if (stopped) _appliedGroups = null;
        if (!stopped) { PreviewRunning = false; SetRuntimeUnavailable(true); CurrentIssue = "停止失败，网络服务状态不可用。请查看诊断后重试。"; return; }
        await RefreshStatusAsync(); Notice = IsPreview ? "模拟内核已停止；保存的配置与固定路径清单仍然保留。" : "网络已停止；保存的配置与固定路径清单继续保留。"; AddEvent("用户停止连接", Notice, "lifecycle");
    }
    public async Task ApplyPreviewAsync()
    {
        if (_applying) return; _applying = true;
        bool applied = false;
        long attemptRevision = ++_attemptRevision; CurrentIssue = ""; ConnectionAttempt.Begin();
        _backend.SetProgressSink(new UiProgress(progress => { if (_attemptRevision == attemptRevision) ConnectionAttempt.Observe(progress); }));
        try
        {
            if (!await SynchronizePendingDslAsync()) { ConnectionAttempt.Fail(CurrentIssue); return; }
            var candidate = CopyDocument(Document); long revision = _revision;
            var policyErrors = PolicyValidator.Validate(candidate).Where(item => item.Severity == DiagnosticSeverity.Error && item.Code != "group.no-observation").ToArray();
            if (policyErrors.Length > 0) { CurrentIssue = string.Join(" ", policyErrors.Select(item => item.Message).Distinct()); ConnectionAttempt.Fail(CurrentIssue); return; }
            ConnectionAttempt.Observe(new(ConnectionAttemptStage.Core, "验证内置 Xray，随后请求授权并建立 TUN；无需物理网卡在线。"));
            await _backend.BeginConnectionAsync(candidate, CompileOptions(candidate), ActionToken);
            ConnectionAttempt.Observe(new(ConnectionAttemptStage.Resources, "TUN 已建立，准备所选代理分组与规则；出口连接失败不会自动换卡。"));
            if (RequiredGroupProbes.NeedsProbes(candidate, CompileOptions(candidate)))
            {
                var probes = await _backend.PrepareRequiredProbesAsync(candidate, CompileOptions(candidate), ActionToken); _probes.Clear(); _probes.AddRange(probes); RefreshNodeLatencies();
            }
            ConnectionAttempt.Observe(new(ConnectionAttemptStage.Resources, "优先使用已验证缓存；缺失资源自动选择默认分组中可用的非 DIRECT 节点。"));
            var ready = await _backend.PrepareConnectionResourcesAsync(candidate, CompileOptions(candidate), ActionToken);
            MergeProbes(ready.Probes); SetPreparedResources(ready.Resources);
            if (!CompilePrepared(candidate).IsSuccess) { ConnectionAttempt.Fail(CurrentIssue); return; }
            var result = await _backend.ApplyAsync(candidate, CompileOptions(candidate), _prepared.Assets, ActionToken);
            if (result.Succeeded)
            {
                applied = true; RememberAppliedGroups(candidate);
                if (revision == _revision) HasChanges = false; CurrentIssue = ""; await RefreshStatusAsync();
                Notice = result.Disposition == ApplyDisposition.Unchanged ? "配置已保存，运行内容未改变。" : IsPreview ? "配置已应用到模拟内核；未启动真实 TUN。" : "配置已应用，网络状态以后台服务实际报告为准。";
                AddEvent("配置已应用", Notice, "configuration");
                if (_state.Running && _state.Available) { ConnectionAttempt.Complete(IsPreview); if (SetupStep == 4) OnboardingVisible = false; }
                else ConnectionAttempt.Fail("配置已提交，但接管状态尚未获得确认。请查看诊断后重试。");
            }
            else { CurrentIssue = string.Join(" ", result.Diagnostics.Select(diagnostic => diagnostic.Message)); ConnectionAttempt.Fail(CurrentIssue); await RefreshStatusAsync(); }
        }
        catch (OperationCanceledException) { ConnectionAttempt.Fail("已取消本次准备；已验证缓存与保存的配置保留。", canceled: true); throw; }
        catch (Exception error) when (IsExpectedFailure(error)) { RecordFailure("connection.prepare", error); CurrentIssue = SafeFailure(error, "连接准备失败，请检查 Xray 非家宽节点、端口、TUN 与授权状态后重试。"); ConnectionAttempt.Fail(CurrentIssue); throw; }
        finally
        {
            try { await _backend.EndConnectionAsync(applied); if (!applied) await RefreshStatusAsync(); }
            catch (RuntimeFailureException failure) { RecordFailure("connection.recovery", failure); ConnectionAttempt.Fail(failure.Message); CurrentIssue = failure.Message; SetRuntimeUnavailable(true); throw; }
            finally { _applying = false; }
        }
    }
    private async Task<bool> SynchronizePendingDslAsync()
    {
        if (DslText == PolicyYaml.Write(Document)) return true; await ApplyDslAsync(); return !HasIssue;
    }
    public async Task ApplyDslAsync()
    {
        var parsed = PolicyYaml.Read(DslText); if (!parsed.IsSuccess || parsed.Document is null) { CurrentIssue = string.Join(" ", parsed.Diagnostics.Select(diagnostic => diagnostic.Message)); return; }
        var candidate = parsed.Document;
        if (IsProduction)
        {
            Document = candidate; _subscriptionReady = Document.Groups.Count > 0; RebuildProjection(); StateChanged(); CurrentIssue = "";
            Notice = "DSL 结构已校验并保存为草稿；连接时通过内置 Xray 准备并验证规则。"; return;
        }
        var prepared = await _backend.PrepareResourcesAsync(candidate, ActionToken);
        if (RequiredGroupProbes.NeedsProbes(candidate, CompileOptions(candidate)))
        {
            var probes = await _backend.PrepareRequiredProbesAsync(candidate, CompileOptions(candidate), ActionToken); _probes.Clear(); _probes.AddRange(probes);
        }
        var options = CompileOptions(candidate); options.PreparedRuleSources = prepared.Bindings.ToList();
        var result = XrayConfigCompiler.Compile(candidate, options);
        if (!result.IsSuccess) { CurrentIssue = string.Join(" ", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.Message)); return; }
        SetPreparedResources(prepared); Document = candidate; RebuildProjection(); StateChanged(); CompilePrepared(Document); Notice = "DSL 已校验并同步到界面，草稿等待应用。";
    }
    public string? SavedPreviewPath => (_backend as FixtureClientBackend)?.SavedPath;
    public async Task SaveDraftAsync()
    {
        if (!await SynchronizePendingDslAsync()) return;
        // A partial first-run draft is useful; saving never starts the runtime or requires administrator authorization.
        await _backend.SaveDraftAsync(Document, ActionToken); Notice = IsPreview ? "已保存到当前隔离会话；真实用户配置未被读取或写入。" : "配置草稿已安全保存。"; AddEvent("配置草稿已保存", "配置凭据未写入诊断记录。", "storage");
    }
    private async Task ShowLogDirectoryAsync() { await _backend.OpenLogDirectoryAsync(ActionToken); Notice = IsPreview ? "预览日志目录：" + _backend.LogDirectory : "已打开日志目录。"; }
    public async Task RefreshStatusAsync()
    {
        try { _state = await _backend.ReadStatusAsync(_lifetime.Token); PreviewRunning = _state.Running; SetRuntimeUnavailable(!_state.Available); if (_statusIssue is not null && CurrentIssue == _statusIssue) CurrentIssue = ""; _statusIssue = null; }
        catch (Exception exception) when (IsExpectedFailure(exception)) { PreviewRunning = false; SetRuntimeUnavailable(true); _statusIssue = SafeFailure(exception, "无法读取后台服务状态；不显示过期的健康结果。"); CurrentIssue = _statusIssue; }
        Raise(nameof(RuntimeBadge)); Raise(nameof(TunStatusText)); Raise(nameof(StatusDescription)); Raise(nameof(ProxyResolvedName)); RefreshNodeLatencies();
        await RefreshConnectionsAsync();
    }
    private async Task RefreshConnectionsAsync()
    {
        try { Connections.Reconcile(await _backend.ReadConnectionsAsync(_lifetime.Token)); }
        catch (Exception)
        { Connections.Reconcile(new([], _backend.Now, false, "连接观测暂时不可用；网络服务状态保持独立。")); }
    }
    private async Task NextSetupAsync()
    {
        if (SetupStep == 0 && (SelectedHomeInterface is null || SelectedNonHomeInterface is null || SelectedHomeInterface.Id == SelectedNonHomeInterface.Id)) { CurrentIssue = "请选择两张不同的物理网卡。"; return; }
        if (SetupStep == 1 && !_subscriptionReady) { CurrentIssue = IsPreview ? "请先导入示例订阅。" : "请先导入节点订阅。"; return; }
        if (SetupStep == 2 && SelectedGlobalTarget is null) { CurrentIssue = "必须明确选择一个全局代理代理分组。"; return; }
        if (SetupStep == 2 && !CanAdvanceSetup) { CurrentIssue = "手动分组需要明确选择一个节点。"; return; }
        CurrentIssue = "";
        if (SetupStep == 3 && SelectedCatalogCount > 0) { await AddSelectedApplicationsAsync(); if (HasIssue) return; }
        if (SetupStep == 4) { await ApplyPreviewAsync(); if (!HasIssue && PreviewRunning) OnboardingVisible = false; return; }
        SetupStep++;
    }
    private static MacPolicyDocument CopyDocument(MacPolicyDocument document) => PolicyYaml.Read(PolicyYaml.Write(document)).Document ?? throw new InvalidOperationException("The policy draft could not be copied.");
    private readonly Queue<string> _pendingIcons = new();
    private readonly HashSet<string> _queuedIcons = new(StringComparer.Ordinal);
    private Task? _iconPump;
    private readonly HashSet<string> _missingIcons = new(StringComparer.Ordinal);
    public Task LoadVisibleIconsAsync() => RequestIconsAsync(AddingApps || OnboardingVisible && SetupStep == 3
        ? VisibleCatalog.Take(24).Select(row => row.Model.BundlePath)
        : AppsVisible ? VisibleApps.Take(24).Select(row => row.BundlePath) : OverviewVisible ? OverviewApps.Select(row => row.BundlePath) : []);
    public Task RequestIconsAsync(IEnumerable<string> bundles)
    {
        if (IsPreview || !_initialized || _disposed) return Task.CompletedTask;
        foreach (string bundle in bundles.Distinct(StringComparer.Ordinal))
        {
            if (_icons.TryGetValue(bundle, out var cached)) { AssignIcon(bundle, cached); continue; }
            if (!_missingIcons.Contains(bundle) && _queuedIcons.Add(bundle)) _pendingIcons.Enqueue(bundle);
        }
        while (_pendingIcons.Count > 128) { string stale = _pendingIcons.Dequeue(); _queuedIcons.Remove(stale); }
        if (_iconPump is null || _iconPump.IsCompleted) _iconPump = PumpIconsAsync();
        return _iconPump;
    }
    private async Task PumpIconsAsync()
    {
        while (_pendingIcons.Count > 0 && !_disposed && !_closing)
        {
            string bundle = _pendingIcons.Dequeue(); _queuedIcons.Remove(bundle);
            if (_icons.ContainsKey(bundle)) continue;
            try
            {
                byte[]? bytes = AppMetadata(bundle)?.IconPng ?? await _backend.LoadIconAsync(bundle, _lifetime.Token);
                if (bytes is null || bytes.Length == 0) { _missingIcons.Add(bundle); continue; }
                Bitmap bitmap = await Task.Run(() => { using var stream = new MemoryStream(bytes, false); return new Bitmap(stream); }, _lifetime.Token);
                if (_disposed) { bitmap.Dispose(); break; }
                _icons[bundle] = bitmap; _iconOrder.Enqueue(bundle); AssignIcon(bundle, bitmap);
                while (_iconOrder.Count > 128) { string oldest = _iconOrder.Dequeue(); if (_icons.Remove(oldest, out var removed)) { AssignIcon(oldest, null); removed.Dispose(); } }
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or OperationCanceledException or InvalidOperationException) {/* Artwork never changes protection or readiness. */}
        }
    }
    private void AssignIcon(string bundle, IImage? image) { foreach (var row in _apps.Where(row => row.BundlePath == bundle)) row.SetIcon(image); foreach (var row in _catalog.Where(row => row.Model.BundlePath == bundle)) row.SetIcon(image); if (image is not null && AppMetadata(bundle) is { } app) Connections.SetApplicationArtwork(app.Id, image); }
    private sealed class UiProgress(Action<ClientOperationProgress> action) : IProgress<ClientOperationProgress>
    {
        public void Report(ClientOperationProgress value)
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action(value);
            else Avalonia.Threading.Dispatcher.UIThread.Post(() => action(value));
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _closing = true; Raise(nameof(CanEdit));
        Notice = "正在恢复网络并保存配置，完成后退出…";
        _operation?.Cancel(); _probeCancellation?.Cancel(); _groupMaintenanceCancellation?.Cancel(); _draftDelay?.Cancel();
        if (_operationCompletion is { } operation) await operation.Task;
        if (_probeCompletion is { } probe) await probe.Task;
        if (_groupMaintenanceCompletion is { } maintenance) await maintenance.Task;
        // A failed draft write must never bypass network restoration. A failed restore keeps the window open for retry.
        try { await PersistDraftAsync(CancellationToken.None); }
        catch (Exception error) { RecordFailure("shutdown.save", error); }
        await _backend.DisposeAsync();
        _disposed = true; _lifetime.Cancel();
        if (_iconPump is not null) await _iconPump;
        foreach (var icon in _icons.Values) icon.Dispose(); _icons.Clear(); _lifetime.Dispose();
    }
}
