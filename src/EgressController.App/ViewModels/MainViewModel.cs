using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;

namespace EgressController.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(AppController controller)
    {
        Controller = controller;
        Overview = new OverviewViewModel(controller);
        Apps = new AppsViewModel(controller);
        Domains = new DomainsViewModel(controller);
        Connections = new ConnectionsViewModel(controller);
        Traffic = new TrafficViewModel(controller);
        Apps.StartInitialScan();
        Domains.RefreshSearch();
        Overview.Refresh();
    }

    public AppController Controller { get; }
    public OverviewViewModel Overview { get; }
    public AppsViewModel Apps { get; }
    public DomainsViewModel Domains { get; }
    public ConnectionsViewModel Connections { get; }
    public TrafficViewModel Traffic { get; }

    private string _status = "正在初始化…";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void Refresh()
    {
        Overview.Refresh();
        Apps.RefreshStatuses();
        Domains.RefreshStatus();
        Connections.Refresh();
        Traffic.Refresh();
        Status = string.IsNullOrWhiteSpace(Controller.LastMessage)
            ? $"TUN：{Controller.TunStatus}"
            : Controller.LastMessage;
    }
}

public sealed class OverviewViewModel : ObservableObject
{
    private readonly AppController _controller;
    private bool _suppressAdapterChange;
    private string _esim = "未选择";
    private string _primary = "未选择";
    private string _upstream = "127.0.0.1:7890 · SOCKS5";
    private string _upstreamPortText;
    private string _core = "Managed core · 未准备";
    private string _tun = "已停止";
    private string _tunBadge = "TUN · 已停止";
    private string _notice = "C# 只负责配置和控制；Windows 全流量由 sing-box TUN 接管，未命中规则进入本地 SOCKS5。";
    private AdapterOptionViewModel? _selectedAdapter;
    private AdapterOptionViewModel? _selectedPrimaryAdapter;
    private string _adapterSignature = string.Empty;

    public OverviewViewModel(AppController controller)
    {
        _controller = controller;
        _upstreamPortText = controller.Profile.UpstreamPort.ToString();
        RefreshCommand = new RelayCommand(RefreshAdapters);
        StartCommand = new AsyncRelayCommand(StartTunAsync);
        ToggleRoutingCommand = new AsyncRelayCommand(ToggleTunAsync);
        RestoreStaleCommand = new RelayCommand(() => { });
    }

    public string Esim { get => _esim; private set => SetProperty(ref _esim, value); }
    public string Primary { get => _primary; private set => SetProperty(ref _primary, value); }
    public string Upstream { get => _upstream; private set => SetProperty(ref _upstream, value); }
    public string UpstreamPortText { get => _upstreamPortText; set => SetProperty(ref _upstreamPortText, value ?? string.Empty); }
    public string Core { get => _core; private set => SetProperty(ref _core, value); }
    public string Tun { get => _tun; private set => SetProperty(ref _tun, value); }
    public string TunBadge { get => _tunBadge; private set => SetProperty(ref _tunBadge, value); }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    public string Traffic => $"↑ {_controller.TrafficUp:N0} B · ↓ {_controller.TrafficDown:N0} B";
    public string ToggleRoutingText => _controller.IsTunRunning ? "停止 TUN" : "启动 TUN";
    public bool CanRestoreStale => false;

    public ObservableCollection<AdapterOptionViewModel> Adapters { get; } = new();
    public ObservableCollection<AdapterOptionViewModel> PrimaryAdapters { get; } = new();

    public AdapterOptionViewModel? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (!SetProperty(ref _selectedAdapter, value) || value is null || _suppressAdapterChange)
                return;
            _ = SaveAdaptersAsync(value, _selectedPrimaryAdapter);
        }
    }

    public AdapterOptionViewModel? SelectedPrimaryAdapter
    {
        get => _selectedPrimaryAdapter;
        set
        {
            if (!SetProperty(ref _selectedPrimaryAdapter, value) || value is null || _suppressAdapterChange)
                return;
            _ = SaveAdaptersAsync(_selectedAdapter, value);
        }
    }

    public RelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand ToggleRoutingCommand { get; }
    public RelayCommand RestoreStaleCommand { get; }

    public void Refresh()
    {
        Esim = SelectedAdapter?.Display ?? "未选择";
        Primary = SelectedPrimaryAdapter?.Display ?? "未选择";
        Upstream = _controller.UpstreamSummary;
        Core = _controller.Profile.Core.Mode == "system"
            ? "System core · " + (_controller.Profile.Core.SystemPath ?? "未选择")
            : "Managed core · sing-box 1.13.x";
        Tun = _controller.TunStatus;
        TunBadge = "TUN · " + Tun;
        Notice = string.IsNullOrWhiteSpace(_controller.LastMessage)
            ? "C# 只负责配置和控制；Windows 全流量由 sing-box TUN 接管，未命中规则进入本地 SOCKS5。"
            : _controller.LastMessage;
        OnPropertyChanged(nameof(Traffic));
        OnPropertyChanged(nameof(ToggleRoutingText));

        string signature = string.Join('|', _controller.Adapters.Select(adapter =>
            $"{adapter.Identity.Guid}:{adapter.IfIndex}:{adapter.IsUp}:{adapter.Addresses.Count}"));
        if (signature == _adapterSignature)
            return;

        _suppressAdapterChange = true;
        try
        {
            Adapters.Clear();
            PrimaryAdapters.Clear();
            foreach (NetworkAdapterInfo adapter in _controller.Adapters)
            {
                Adapters.Add(new AdapterOptionViewModel(adapter));
                PrimaryAdapters.Add(new AdapterOptionViewModel(adapter));
            }

            Guid? esimId = Guid.TryParse(_controller.Profile.EsimAdapterId, out Guid esim) ? esim : null;
            Guid? primaryId = Guid.TryParse(_controller.Profile.PrimaryAdapterId, out Guid primary) ? primary : null;
            _selectedAdapter = esimId is Guid e
                ? Adapters.FirstOrDefault(option => option.Guid == e)
                : null;
            _selectedPrimaryAdapter = primaryId is Guid p
                ? PrimaryAdapters.FirstOrDefault(option => option.Guid == p)
                : null;
            OnPropertyChanged(nameof(SelectedAdapter));
            OnPropertyChanged(nameof(SelectedPrimaryAdapter));
            _adapterSignature = signature;
        }
        finally
        {
            _suppressAdapterChange = false;
        }
    }

    private void RefreshAdapters()
    {
        _controller.RefreshAdapters();
        _adapterSignature = string.Empty;
        Refresh();
    }

    private async Task SaveAdaptersAsync(AdapterOptionViewModel? esim, AdapterOptionViewModel? primary)
    {
        if (esim is null || primary is null)
            return;
        ControllerOperationResult result = await _controller.SetAdaptersAsync(primary.Guid, esim.Guid);
        if (!result.Succeeded)
            Notice = result.Error ?? "网卡配置失败。";
        Refresh();
    }

    private async Task StartTunAsync()
    {
        ControllerOperationResult result = await _controller.StartTunAsync();
        if (!result.Succeeded)
            Notice = result.Error ?? "TUN 启动失败。";
        Refresh();
    }

    private async Task ToggleTunAsync()
    {
        ControllerOperationResult result = await _controller.ToggleTunAsync();
        if (!result.Succeeded)
            Notice = result.Error ?? "TUN 操作失败。";
        Refresh();
    }

    public async Task CommitUpstreamPortAsync()
    {
        if (!int.TryParse(UpstreamPortText.Trim(), out int port) || port is < 1 or > 65535)
        {
            Notice = "SOCKS5 端口必须是 1-65535 的整数。";
            UpstreamPortText = _controller.Profile.UpstreamPort.ToString();
            return;
        }

        if (port == _controller.Profile.UpstreamPort)
        {
            UpstreamPortText = port.ToString();
            return;
        }

        ControllerOperationResult result = await _controller.SetUpstreamPortAsync(port);
        if (!result.Succeeded)
        {
            Notice = result.Error ?? "SOCKS5 端口保存失败。";
            UpstreamPortText = _controller.Profile.UpstreamPort.ToString();
        }
        else
        {
            UpstreamPortText = port.ToString();
        }
        Refresh();
    }
}

public sealed class AdapterOptionViewModel(NetworkAdapterInfo adapter)
{
    public Guid Guid => adapter.Identity.Guid;
    public string Display => $"{adapter.Identity.NameSnapshot} · {(adapter.IsUp ? "在线" : "离线")} · {adapter.AddressState} · ifIndex {adapter.IfIndex}";
}

public sealed class AppsViewModel : ObservableObject
{
    private readonly AppController _controller;
    private readonly List<AppEntryViewModel> _all = new();
    private string _query = string.Empty;
    private string _manualExecutable = string.Empty;
    private string _status = "尚未扫描";
    private bool _isScanning;

    public AppsViewModel(AppController controller)
    {
        _controller = controller;
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        SelectAllCommand = new RelayCommand(() => _ = SetAllAsync(true));
        ClearAllCommand = new RelayCommand(() => _ = SetAllAsync(false));
        AddExecutableCommand = new RelayCommand(AddExecutable);
    }

    public ObservableCollection<AppEntryViewModel> Entries { get; } = new();
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value ?? string.Empty)) RefreshVisible(); }
    }
    public string ManualExecutable { get => _manualExecutable; set => SetProperty(ref _manualExecutable, value ?? string.Empty); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value))
                return;
            OnPropertyChanged(nameof(ScanText));
        }
    }
    public string ScanText => IsScanning ? "扫描中…" : "扫描应用";
    public string Summary => $"{_all.Count} 个目标 · {_all.Count(entry => entry.IsEsim)} 个已选 eSIM";

    public IAsyncRelayCommand ScanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand AddExecutableCommand { get; }

    public void StartInitialScan() => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (IsScanning)
            return;
        IsScanning = true;
        Status = "正在扫描 Windows 应用和 EXE 所有权边界…";
        try
        {
            IReadOnlyList<LaunchTarget> discovered = await Task.Run(_controller.ScanTargets);
            foreach (AppEntryViewModel entry in _all)
                entry.Dispose();
            _all.Clear();
            foreach (LaunchTarget target in discovered)
                _all.Add(new AppEntryViewModel(_controller, target, RefreshVisible));
            Status = $"扫描完成：{_all.Count} 个 Windows 应用。";
            RefreshVisible();
        }
        catch (Exception exception)
        {
            Status = "扫描失败：" + exception.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task SetAllAsync(bool enabled)
    {
        AppEntryViewModel[] targets = _all.Where(entry => entry.CanManage).ToArray();
        ControllerOperationResult result = await _controller.SetApplicationsEsimAsync(
            targets.Select(entry => entry.Target),
            enabled);
        if (!result.Succeeded)
        {
            Status = result.Error ?? "应用选择失败。";
        }
        else
        {
            foreach (AppEntryViewModel entry in targets)
                entry.SetEsimLocal(enabled);
            Status = enabled ? "已将当前可路由应用加入 eSIM。" : "已清空当前应用的 eSIM 选择。";
        }
        RefreshVisible();
    }

    private void AddExecutable()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ManualExecutable))
                throw new ArgumentException("先填写 .exe 路径。", nameof(ManualExecutable));
            LaunchTarget target = _controller.AddExecutable(ManualExecutable);
            if (_all.Any(entry => entry.DiscoveryKey == target.DiscoveryKey))
            {
                Status = "该可执行文件已在应用列表中。";
                ManualExecutable = string.Empty;
                return;
            }
            _all.Add(new AppEntryViewModel(_controller, target, RefreshVisible));
            ManualExecutable = string.Empty;
            Status = "已添加可执行文件：" + target.Name;
            RefreshVisible();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private void RefreshVisible()
    {
        string query = _query.Trim();
        Entries.Clear();
        foreach (AppEntryViewModel entry in _all.Where(entry => query.Length == 0
            || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.KindText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Command.Contains(query, StringComparison.OrdinalIgnoreCase)))
            Entries.Add(entry);
        OnPropertyChanged(nameof(Summary));
    }

    public void RefreshStatuses()
    {
        foreach (AppEntryViewModel entry in _all)
            entry.RefreshStatus();
    }
}

public sealed class AppEntryViewModel : ObservableObject, IDisposable
{
    private readonly AppController _controller;
    private readonly Action _changed;
    private bool _isEsim;
    private bool _changing;
    private string _status = string.Empty;

    public AppEntryViewModel(AppController controller, LaunchTarget target, Action changed)
    {
        _controller = controller;
        Target = target;
        _changed = changed;
        _isEsim = target.EsimSelected;
        Icon = AppIconLoader.Load(target.IconPath ?? target.CanonicalExecutable ?? target.Command);
        LaunchCommand = new RelayCommand(Launch);
    }

    public LaunchTarget Target { get; }
    public string Id => Target.Id;
    public string DiscoveryKey => Target.DiscoveryKey;
    public string Name => Target.Name;
    public string Command => Target.Command ?? Target.Aumid ?? "未解析";
    public string Details => Target.Kind == LaunchKind.PackagedAumid
        ? $"Package · {Target.PackageFamily} · {Target.Aumid} · EXE: {Command}"
        : Command + (string.IsNullOrWhiteSpace(Target.Source) ? string.Empty : " · " + Target.Source);
    public string KindText => Target.Kind switch
    {
        LaunchKind.PackagedAumid => "APP · MSIX",
        LaunchKind.CliNative => "CLI · native",
        LaunchKind.CliWrapperResolved => "CLI · wrapper 未解析",
        LaunchKind.Shortcut => "快捷方式 · 未解析",
        _ => "APP · Win32",
    };
    public string Glyph => Target.Kind is LaunchKind.CliNative or LaunchKind.CliWrapperResolved ? ">_" : "▣";
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;
    public bool CanRoute => Target.CanRoute;
    public bool CanManage => CanRoute;
    public bool CanLaunch => Target.CanLaunch;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool IsEsim
    {
        get => _isEsim;
        set
        {
            if (_changing || value == _isEsim)
                return;
            _ = ApplyEsimAsync(value);
        }
    }

    public RelayCommand LaunchCommand { get; }

    internal void SetEsimLocal(bool value)
    {
        if (SetProperty(ref _isEsim, value))
        {
            Target.EsimSelected = value;
            _changed();
        }
    }

    private async Task ApplyEsimAsync(bool enabled)
    {
        _changing = true;
        Status = enabled ? "正在应用 eSIM 选择…" : "正在移除 eSIM 选择…";
        ControllerOperationResult result;
        try
        {
            result = await _controller.SetApplicationsEsimAsync([Target], enabled);
        }
        catch (Exception exception)
        {
            result = ControllerOperationResult.Failure(exception.Message);
        }
        if (!result.Succeeded)
            Status = result.Error ?? "应用选择失败。";
        else
        {
            SetEsimLocal(enabled);
            Status = enabled ? "已加入 eSIM" : "已移除 eSIM";
        }
        _changing = false;
        _changed();
    }

    private void Launch()
    {
        if (!CanLaunch)
        {
            Status = "未解析，不能安全启动";
            return;
        }
        try
        {
            Status = _controller.LaunchTarget(Target.Id);
            RefreshStatus();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    public void RefreshStatus()
    {
        string status = _controller.GetTargetStatus(Target.Id);
        if (status.Length > 0)
            Status = status;
    }

    public void Dispose() => Icon?.Dispose();
}

public sealed class DomainsViewModel : ObservableObject
{
    private readonly AppController _controller;
    private string _query = string.Empty;
    private string _manualDomain = string.Empty;
    private string _status = "尚未获取 sing catalog";
    private bool _isRefreshing;

    public DomainsViewModel(AppController controller)
    {
        _controller = controller;
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshRemoteAsync);
        SelectAllCommand = new RelayCommand(() => _ = SetVisibleAsync(true));
        ClearAllCommand = new RelayCommand(() => _ = SetVisibleAsync(false));
        AddManualCommand = new RelayCommand(() => _ = AddManualAsync());
    }

    public ObservableCollection<RuleEntryViewModel> Results { get; } = new();
    public ObservableCollection<RuleEntryViewModel> SelectedRules { get; } = new();
    public ObservableCollection<ManualDomainViewModel> ManualDomains { get; } = new();
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSelectVisible));
                RefreshSearch();
            }
        }
    }
    public string ManualDomain { get => _manualDomain; set => SetProperty(ref _manualDomain, value ?? string.Empty); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetProperty(ref _isRefreshing, value))
                return;
            OnPropertyChanged(nameof(RefreshText));
        }
    }
    public string RefreshText => IsRefreshing ? "更新中…" : "更新 sing catalog";
    public string CatalogDirectory => _controller.CatalogDirectory;
    public string CatalogCommit => _controller.CatalogCommit.Length == 0 ? "未激活" : _controller.CatalogCommit;
    public int CatalogCount => _controller.Catalog?.Count ?? 0;
    public string SelectedSummary => $"{_controller.SelectedRuleNames.Count} 个 SRS · {ManualDomains.Count} 个自定义域名 → eSIM";
    public bool CanSelectVisible => _query.Trim().Length > 0;

    public IAsyncRelayCommand RefreshCatalogCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand AddManualCommand { get; }

    public void RefreshSearch()
    {
        Results.Clear();
        SingBoxRuleCatalog? catalog = _controller.Catalog;
        if (catalog is not null)
        {
            IReadOnlyList<SingBoxRuleCatalogEntry> entries = _query.Trim().Length == 0
                ? PopularEntries(catalog)
                : catalog.Search(_query, 80);
            foreach (SingBoxRuleCatalogEntry entry in entries)
                Results.Add(new RuleEntryViewModel(_controller, entry, RefreshSearch));
        }

        SelectedRules.Clear();
        if (catalog is not null)
        {
            foreach (string name in _controller.SelectedRuleNames)
            {
                if (catalog.TryGet(name, out SingBoxRuleCatalogEntry? entry) && entry is not null)
                    SelectedRules.Add(new RuleEntryViewModel(_controller, entry, RefreshSearch));
            }
        }

        ManualDomains.Clear();
        foreach (string domain in _controller.ManualDomains)
            ManualDomains.Add(new ManualDomainViewModel(_controller, domain, RefreshSearch));
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        Status = _controller.Catalog is null ? "没有本地 sing catalog，请显式更新。" : "就绪";
        OnPropertyChanged(nameof(CatalogDirectory));
        OnPropertyChanged(nameof(CatalogCommit));
        OnPropertyChanged(nameof(CatalogCount));
        OnPropertyChanged(nameof(SelectedSummary));
    }

    private async Task RefreshRemoteAsync()
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        Status = "正在通过显式 SOCKS5 7890 获取 MetaCubeX sing catalog…";
        try
        {
            SingBoxCatalogUpdateResult result = await _controller.RefreshCatalogAsync();
            Status = result.Succeeded ? "sing catalog 已更新。" : "规则更新失败：" + result.Error;
            RefreshSearch();
        }
        catch (Exception exception)
        {
            Status = "规则更新失败：" + exception.Message;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task SetVisibleAsync(bool enabled)
    {
        RuleEntryViewModel[] entries = Results.ToArray();
        ControllerOperationResult result = await _controller.SetRuleSetsAsync(
            entries.Select(entry => entry.Name),
            enabled);
        if (!result.Succeeded)
        {
            Status = result.Error ?? "规则批量操作失败。";
        }
        else
        {
            foreach (RuleEntryViewModel entry in entries)
            {
                entry.SetSelectedLocal(enabled);
                entry.SetStatusLocal(enabled ? "已加载并启用" : "已移除");
            }
            Status = enabled ? "已批量加载并启用所选 SRS。" : "已批量移除所选 SRS。";
        }
        RefreshSearch();
    }

    private async Task AddManualAsync()
    {
        try
        {
            ControllerOperationResult result = await _controller.AddManualDomainAsync(ManualDomain);
            if (!result.Succeeded)
            {
                Status = result.Error ?? "自定义域名失败。";
                return;
            }
            ManualDomain = string.Empty;
            RefreshSearch();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private static IReadOnlyList<SingBoxRuleCatalogEntry> PopularEntries(SingBoxRuleCatalog catalog)
    {
        string[] preferred = ["google", "youtube", "openai", "anthropic", "github", "telegram", "twitter"];
        return preferred
            .Select(name => catalog.TryGet(name, out SingBoxRuleCatalogEntry? entry) ? entry : null)
            .Where(entry => entry is not null)
            .Cast<SingBoxRuleCatalogEntry>()
            .ToArray();
    }
}

public sealed class RuleEntryViewModel : ObservableObject
{
    private readonly AppController _controller;
    private readonly SingBoxRuleCatalogEntry _entry;
    private readonly Action _changed;
    private bool _selected;
    private bool _changing;
    private string _status = string.Empty;

    public RuleEntryViewModel(AppController controller, SingBoxRuleCatalogEntry entry, Action changed)
    {
        _controller = controller;
        _entry = entry;
        _changed = changed;
        _selected = controller.SelectedRuleNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Name => _entry.Name;
    public string Path => _entry.Path;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_changing || value == _selected)
                return;
            _ = ApplySelectionAsync(value);
        }
    }

    internal void SetSelectedLocal(bool value) => SetProperty(ref _selected, value);

    internal void SetStatusLocal(string value) => Status = value;

    internal async Task ApplySelectionAsync(bool value)
    {
        if (_changing)
            return;
        _changing = true;
        Status = value ? "正在下载并校验 SRS…" : "正在移除 SRS…";
        ControllerOperationResult result;
        try
        {
            result = await _controller.SetRuleSetAsync(Name, value);
        }
        catch (Exception exception)
        {
            result = ControllerOperationResult.Failure(exception.Message);
        }
        if (!result.Succeeded)
        {
            Status = result.Error ?? "规则操作失败。";
        }
        else
        {
            SetSelectedLocal(value);
            Status = value ? "已加载并启用" : "已移除";
        }
        _changing = false;
        _changed();
    }
}

public sealed class ManualDomainViewModel
{
    public ManualDomainViewModel(AppController controller, string domain, Action changed)
    {
        Domain = domain;
        RemoveCommand = new AsyncRelayCommand(async () =>
        {
            await controller.RemoveManualDomainAsync(domain);
            changed();
        });
    }

    public string Domain { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
}

public sealed class ConnectionsViewModel : ObservableObject
{
    private readonly AppController _controller;
    private readonly Dictionary<string, ConnectionRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private string _query = string.Empty;
    private bool _includeClosed = true;
    private long _droppedConnections;
    private long _droppedLogs;
    private int _activeConnections;
    private string _lastUpdated = "等待 sing-box API";
    private string _actionMessage = string.Empty;
    private ConnectionRowViewModel? _selectedRow;

    public ConnectionsViewModel(AppController controller)
    {
        _controller = controller;
        CloseAllCommand = new AsyncRelayCommand(CloseAllAsync);
        CloseSelectedCommand = new AsyncRelayCommand(CloseSelectedAsync);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        ToggleHistoryCommand = new RelayCommand(() => IncludeClosed = !IncludeClosed);
    }

    public ObservableCollection<ConnectionRowViewModel> Rows { get; } = new();
    public ObservableCollection<CoreLogRowViewModel> CoreLogs { get; } = new();

    public ConnectionRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
                OnPropertyChanged(nameof(CanCloseSelected));
        }
    }

    public bool CanCloseSelected => SelectedRow?.IsActive == true;
    public int ActiveConnections { get => _activeConnections; private set => SetProperty(ref _activeConnections, value); }
    public int Count => Rows.Count;
    public long DroppedConnections { get => _droppedConnections; private set => SetProperty(ref _droppedConnections, value); }
    public long DroppedLogs { get => _droppedLogs; private set => SetProperty(ref _droppedLogs, value); }
    public string MonitorStatus => _controller.DiagnosticsStatus;
    public string ActiveSummary => $"活动 {ActiveConnections} · ↑ {TrafficFormat.Rate(_controller.TrafficUpRate)} · ↓ {TrafficFormat.Rate(_controller.TrafficDownRate)}";
    public string TotalSummary => $"当前会话 ↑ {TrafficFormat.Bytes(_controller.TrafficUp)} · ↓ {TrafficFormat.Bytes(_controller.TrafficDown)}";
    public string LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }
    public string ActionMessage { get => _actionMessage; private set => SetProperty(ref _actionMessage, value); }
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
                Refresh();
        }
    }

    public bool IncludeClosed
    {
        get => _includeClosed;
        set
        {
            if (SetProperty(ref _includeClosed, value))
            {
                OnPropertyChanged(nameof(HistoryToggleText));
                Refresh();
            }
        }
    }

    public string HistoryToggleText => IncludeClosed ? "隐藏已结束" : "显示历史";
    public IAsyncRelayCommand CloseAllCommand { get; }
    public IAsyncRelayCommand CloseSelectedCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ToggleHistoryCommand { get; }

    public void Refresh()
    {
        IReadOnlyList<ConnectionObservation> active = _controller.ConnectionHistory.ActiveSnapshot();
        IReadOnlyList<ConnectionObservation> closed = _controller.ConnectionHistory.ClosedSnapshot();
        string query = _query.Trim();
        var visible = active
            .OrderByDescending(item => item.StartedAtUtc)
            .Select(item => (Item: item, Active: true))
            .Concat(IncludeClosed
                ? closed.OrderByDescending(item => item.ClosedAtUtc ?? item.LastSeenAtUtc).Select(item => (Item: item, Active: false))
                : [])
            .Where(entry => Matches(entry.Item, query))
            .Take(500)
            .ToArray();
        var desiredKeys = visible.Select(entry => RowKey(entry.Item, entry.Active)).ToHashSet(StringComparer.Ordinal);

        for (int index = 0; index < visible.Length; index++)
        {
            (ConnectionObservation item, bool activeRow) = visible[index];
            string key = RowKey(item, activeRow);
            if (!_rowsByKey.TryGetValue(key, out ConnectionRowViewModel? row))
            {
                row = new ConnectionRowViewModel(item, activeRow);
                _rowsByKey[key] = row;
            }
            else
            {
                row.Update(item, activeRow);
            }

            if (index < Rows.Count && ReferenceEquals(Rows[index], row))
                continue;
            int currentIndex = Rows.IndexOf(row);
            if (currentIndex >= 0)
                Rows.Move(currentIndex, index);
            else
                Rows.Insert(index, row);
        }

        while (Rows.Count > visible.Length)
            Rows.RemoveAt(Rows.Count - 1);
        foreach (string key in _rowsByKey.Keys.Where(key => !desiredKeys.Contains(key)).ToArray())
            _rowsByKey.Remove(key);

        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
            SelectedRow = null;

        CoreLogs.Clear();
        foreach (CoreLogEntry entry in _controller.Logs.Snapshot().Reverse().Take(500))
            CoreLogs.Add(new CoreLogRowViewModel(entry));

        ActiveConnections = active.Count;
        DroppedConnections = _controller.ConnectionHistory.DroppedClosed;
        DroppedLogs = _controller.Logs.Dropped;
        OnPropertyChanged(nameof(ActiveSummary));
        OnPropertyChanged(nameof(TotalSummary));
        OnPropertyChanged(nameof(MonitorStatus));
        OnPropertyChanged(nameof(Count));
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private async Task CloseAllAsync()
    {
        ControllerOperationResult result = await _controller.CloseAllConnectionsAsync();
        ActionMessage = result.Succeeded ? "已请求关闭全部活动连接。" : result.Error ?? "关闭连接失败。";
        Refresh();
    }

    private async Task CloseSelectedAsync()
    {
        ConnectionRowViewModel? selected = SelectedRow;
        if (selected is null || !selected.IsActive)
            return;
        ControllerOperationResult result = await _controller.CloseConnectionAsync(selected.Id);
        ActionMessage = result.Succeeded ? "已请求关闭选中连接。" : result.Error ?? "关闭连接失败。";
        Refresh();
    }

    private void ClearHistory()
    {
        _controller.ClearConnectionHistory();
        ActionMessage = "已清空已结束连接；不会影响当前活动连接。";
        Refresh();
    }

    private static string RowKey(ConnectionObservation item, bool active)
        => active
            ? "active:" + item.Id
            : $"closed:{item.Id}:{item.StartedAtUtc.UtcTicks}:{item.ClosedAtUtc?.UtcTicks ?? 0}";

    private static bool Matches(ConnectionObservation item, string query)
    {
        if (query.Length == 0)
            return true;
        string text = string.Join('\n', item.Id, item.ProcessPath, item.Host, item.DestinationIp,
            item.DestinationPort, item.Network, item.Type, item.DnsMode, item.Rule,
            item.Outbound, string.Join(' ', item.Chains));
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class TrafficViewModel : ObservableObject
{
    private readonly AppController _controller;
    private string _lastUpdated = "等待 sing-box API";
    private string _currentRate = "↑ 0 B/s · ↓ 0 B/s";
    private string _total = "↑ 0 B · ↓ 0 B";
    private string _active = "0";

    public TrafficViewModel(AppController controller)
    {
        _controller = controller;
    }

    public string CurrentRate { get => _currentRate; private set => SetProperty(ref _currentRate, value); }
    public string Total { get => _total; private set => SetProperty(ref _total, value); }
    public string Active { get => _active; private set => SetProperty(ref _active, value); }
    public string MonitorStatus => _controller.DiagnosticsStatus;
    public string LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }
    public string Note => "流量来自 sing-box Clash API：实时速度读取 /traffic，当前会话总量读取连接快照；两者不是同一个统计口径。";

    public void Refresh()
    {
        CurrentRate = $"↑ {TrafficFormat.Rate(_controller.TrafficUpRate)} · ↓ {TrafficFormat.Rate(_controller.TrafficDownRate)}";
        Total = $"↑ {TrafficFormat.Bytes(_controller.TrafficUp)} · ↓ {TrafficFormat.Bytes(_controller.TrafficDown)}";
        Active = _controller.ConnectionHistory.ActiveCount.ToString("N0");
        OnPropertyChanged(nameof(MonitorStatus));
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }
}

public sealed class ConnectionRowViewModel : ObservableObject
{
    private ConnectionObservation _item;
    private bool _isActive;

    public ConnectionRowViewModel(ConnectionObservation item, bool active)
    {
        _item = item;
        _isActive = active;
    }

    public ConnectionObservation Observation => _item;
    public string Id => _item.Id;
    public bool IsActive => _isActive;
    public string Status => IsActive ? "活动" : "已结束";
    public string ProcessName => string.IsNullOrWhiteSpace(_item.ProcessPath)
        ? "未识别进程"
        : Path.GetFileName(_item.ProcessPath);
    public string ProcessPath => string.IsNullOrWhiteSpace(_item.ProcessPath) ? "未识别路径" : _item.ProcessPath;
    public string Target => Endpoint(_item.Host, _item.DestinationIp, _item.DestinationPort);
    public string SourceEndpoint => Endpoint(string.Empty, _item.SourceIp, _item.SourcePort);
    public string DestinationEndpoint => Endpoint(string.Empty, _item.DestinationIp, _item.DestinationPort);
    public string Protocol => string.IsNullOrWhiteSpace(_item.Network) ? "未知" : _item.Network.ToUpperInvariant();
    public string Type => string.IsNullOrWhiteSpace(_item.Type) ? "未知" : _item.Type;
    public string Route => _item.Outbound ?? _item.Chains.FirstOrDefault() ?? "未知出口";
    public string RoutePath => _item.Chains.Count == 0 ? Route : string.Join(" → ", _item.Chains);
    public string Reason => string.IsNullOrWhiteSpace(_item.Rule) ? "final" : _item.Rule;
    public string Rule => string.IsNullOrWhiteSpace(_item.Rule) ? "未提供" : _item.Rule;
    public string DnsMode => string.IsNullOrWhiteSpace(_item.DnsMode) ? "未提供" : _item.DnsMode;
    public string Traffic => TrafficFormat.Bytes(_item.Upload + _item.Download);
    public string Speed => TrafficFormat.Rate(_item.UploadRate + _item.DownloadRate);
    public string Duration => TrafficFormat.Duration(DurationValue());
    public string StartedAt => _item.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    public string ClosedAt => _item.ClosedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz") ?? "仍在活动";

    public void Update(ConnectionObservation item, bool active)
    {
        _item = item;
        bool activeChanged = _isActive != active;
        _isActive = active;
        if (activeChanged)
            OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ProcessName));
        OnPropertyChanged(nameof(ProcessPath));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(SourceEndpoint));
        OnPropertyChanged(nameof(DestinationEndpoint));
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(Route));
        OnPropertyChanged(nameof(RoutePath));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(Rule));
        OnPropertyChanged(nameof(DnsMode));
        OnPropertyChanged(nameof(Traffic));
        OnPropertyChanged(nameof(Speed));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(StartedAt));
        OnPropertyChanged(nameof(ClosedAt));
    }

    public void RefreshDuration() => OnPropertyChanged(nameof(Duration));

    private TimeSpan DurationValue()
    {
        DateTimeOffset end = _item.ClosedAtUtc ?? DateTimeOffset.UtcNow;
        return end > _item.StartedAtUtc ? end - _item.StartedAtUtc : TimeSpan.Zero;
    }

    private static string Endpoint(string? host, string? ip, string? port)
    {
        string address = !string.IsNullOrWhiteSpace(host) ? host : ip ?? string.Empty;
        if (address.Length == 0)
            return string.IsNullOrWhiteSpace(port) ? "未知目标" : $":{port}";
        if (address.Contains(':') && !address.StartsWith("[", StringComparison.Ordinal))
            address = $"[{address}]";
        return string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
    }
}

internal static class TrafficFormat
{
    public static string Bytes(long value)
    {
        double amount = Math.Max(0, value);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return unit == 0 ? $"{amount:0} {units[unit]}" : $"{amount:0.##} {units[unit]}";
    }

    public static string Rate(long value) => Bytes(value) + "/s";

    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}d {value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";
        return value.TotalHours >= 1
            ? $"{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }
}

public sealed class CoreLogRowViewModel(CoreLogEntry entry)
{
    public string Time => entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Source => entry.Source;
    public string Level => entry.Level;
    public string Message => entry.Message;
}
