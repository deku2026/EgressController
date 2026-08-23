using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Diagnostics;
using EgressController.SingBox.Api.Models;
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
        Apps.StartInitialScan();
        Domains.RefreshSearch();
        Overview.Refresh();
    }

    public AppController Controller { get; }
    public OverviewViewModel Overview { get; }
    public AppsViewModel Apps { get; }
    public DomainsViewModel Domains { get; }
    public ConnectionsViewModel Connections { get; }

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
                entry.SetManagedLocal(enabled);
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
    private readonly AppController? _controller;
    private readonly RouterHost? _legacyHost;
    private readonly Action _changed;
    private bool _managed;
    private bool _changing;
    private string _status = string.Empty;

    public AppEntryViewModel(AppController controller, LaunchTarget target, Action changed)
        : this(target, changed)
    {
        _controller = controller;
    }

    public AppEntryViewModel(RouterHost legacyHost, LaunchTarget target, Action changed)
        : this(target, changed)
    {
        _legacyHost = legacyHost;
    }

    private AppEntryViewModel(LaunchTarget target, Action changed)
    {
        Target = target;
        _changed = changed;
        _managed = target.Managed;
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

    public bool Managed
    {
        get => _managed;
        set
        {
            if (_changing || value == _managed)
                return;
            _ = ApplyManagedAsync(value);
        }
    }

    public bool IsEsim
    {
        get => _managed;
        set => Managed = value;
    }

    public RelayCommand LaunchCommand { get; }

    internal void SetManagedLocal(bool value)
    {
        if (SetProperty(ref _managed, value))
        {
            Target.Managed = value;
            OnPropertyChanged(nameof(IsEsim));
            _changed();
        }
    }

    private async Task ApplyManagedAsync(bool enabled)
    {
        _changing = true;
        Status = enabled ? "正在应用 eSIM 选择…" : "正在移除 eSIM 选择…";
        ControllerOperationResult result;
        try
        {
            result = _controller is not null
                ? await _controller.SetApplicationsEsimAsync([Target], enabled)
                : _legacyHost!.SetTargetManaged(Target.Id, enabled)
                    ? ControllerOperationResult.Success()
                    : ControllerOperationResult.Failure("应用选择失败。");
        }
        catch (Exception exception)
        {
            result = ControllerOperationResult.Failure(exception.Message);
        }
        if (!result.Succeeded)
            Status = result.Error ?? "应用选择失败。";
        else
        {
            SetManagedLocal(enabled);
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
            Status = _controller?.LaunchTarget(Target.Id)
                ?? _legacyHost!.LaunchTarget(Target.Id);
            RefreshStatus();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    public void RefreshStatus()
    {
        string status = _controller?.GetTargetStatus(Target.Id)
            ?? GetLegacyTargetStatus();
        if (status.Length > 0)
            Status = status;
    }

    private string GetLegacyTargetStatus()
    {
        LaunchSession[] sessions = _legacyHost!.Sessions.All()
            .Where(session => string.Equals(session.TargetId, Target.Id, StringComparison.Ordinal))
            .ToArray();
        if (sessions.Length == 0)
            return Status.StartsWith("运行中", StringComparison.Ordinal) ? "已结束" : string.Empty;
        bool running = sessions.Any(session =>
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)session.RootPid));
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        });
        return running ? "运行中" : "已结束";
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
    private string _query = string.Empty;
    private long _dropped;
    private int _activeConnections;
    private string _lastUpdated = "等待 sing-box API";
    private string _dnsHost = string.Empty;
    private string _dnsResult = string.Empty;

    public ConnectionsViewModel(AppController controller)
    {
        _controller = controller;
        CloseAllCommand = new AsyncRelayCommand(CloseAllAsync);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        QueryDnsCommand = new AsyncRelayCommand(QueryDnsAsync);
        FlushDnsCommand = new AsyncRelayCommand(FlushDnsAsync);
    }

    public ObservableCollection<ConnectionRowViewModel> Rows { get; } = new();
    public ObservableCollection<CoreLogRowViewModel> CoreLogs { get; } = new();
    public ConnectionColumnLayout Columns { get; } = new();
    public long Dropped { get => _dropped; private set => SetProperty(ref _dropped, value); }
    public int ActiveConnections { get => _activeConnections; private set => SetProperty(ref _activeConnections, value); }
    public string ActiveSummary => $"活动 {ActiveConnections} · ↑ {_controller.TrafficUp:N0} · ↓ {_controller.TrafficDown:N0}";
    public int Count => Rows.Count;
    public string LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }
    public string Query { get => _query; set { if (SetProperty(ref _query, value ?? string.Empty)) Refresh(); } }
    public string DnsHost { get => _dnsHost; set => SetProperty(ref _dnsHost, value ?? string.Empty); }
    public string DnsResult { get => _dnsResult; private set => SetProperty(ref _dnsResult, value); }
    public IAsyncRelayCommand CloseAllCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public IAsyncRelayCommand QueryDnsCommand { get; }
    public IAsyncRelayCommand FlushDnsCommand { get; }

    public void Refresh()
    {
        IReadOnlyList<ConnectionObservation> active = _controller.ConnectionHistory.ActiveSnapshot();
        IReadOnlyList<ConnectionObservation> closed = _controller.ConnectionHistory.ClosedSnapshot();
        string query = _query.Trim();
        Rows.Clear();
        foreach (ConnectionObservation item in active.Reverse().Concat(closed.Reverse()).Take(500).Where(item => Matches(item, query)))
            Rows.Add(new ConnectionRowViewModel(item, Columns, item.ClosedAtUtc is null));
        CoreLogs.Clear();
        foreach (CoreLogEntry entry in _controller.Logs.Snapshot().Reverse().Take(500))
            CoreLogs.Add(new CoreLogRowViewModel(entry));
        Dropped = _controller.ConnectionHistory.DroppedClosed + _controller.Logs.Dropped;
        ActiveConnections = active.Count;
        OnPropertyChanged(nameof(ActiveSummary));
        OnPropertyChanged(nameof(Count));
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private async Task CloseAllAsync()
    {
        ControllerOperationResult result = await _controller.CloseAllConnectionsAsync();
        if (!result.Succeeded)
            DnsResult = result.Error ?? "关闭连接失败。";
        Refresh();
    }

    private void ClearHistory()
    {
        _controller.ClearConnectionHistory();
        Refresh();
    }

    private async Task QueryDnsAsync()
    {
        try
        {
            SingBoxDnsResponse result = await _controller.QueryDnsAsync(DnsHost);
            DnsResult = $"Status={result.Status} · Server={result.Server} · Answer={result.Answer.ValueKind}";
        }
        catch (Exception exception)
        {
            DnsResult = exception.Message;
        }
    }

    private async Task FlushDnsAsync()
    {
        ControllerOperationResult result = await _controller.FlushDnsCacheAsync();
        DnsResult = result.Succeeded ? "DNS 缓存已清理。" : result.Error ?? "DNS 缓存清理失败。";
    }

    private static bool Matches(ConnectionObservation item, string query)
    {
        if (query.Length == 0)
            return true;
        string text = string.Join('\n', item.Id, item.ProcessId, item.ProcessPath, item.Host,
            item.DestinationIp, item.DestinationPort, item.Network, item.Rule, item.RulePayload,
            item.Outbound, string.Join(' ', item.Chains));
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ConnectionColumnLayout : ObservableObject
{
    private GridLength _time = new(82);
    private GridLength _source = new(300);
    private GridLength _target = new(1.2, GridUnitType.Star);
    private GridLength _decision = new(100);
    private GridLength _reason = new(110);
    private GridLength _rule = new(1, GridUnitType.Star);
    private GridLength _status = new(100);

    public GridLength Time { get => _time; set => SetProperty(ref _time, value); }
    public GridLength Source { get => _source; set => SetProperty(ref _source, value); }
    public GridLength Target { get => _target; set => SetProperty(ref _target, value); }
    public GridLength Decision { get => _decision; set => SetProperty(ref _decision, value); }
    public GridLength Reason { get => _reason; set => SetProperty(ref _reason, value); }
    public GridLength Rule { get => _rule; set => SetProperty(ref _rule, value); }
    public GridLength Status { get => _status; set => SetProperty(ref _status, value); }
}

public sealed class ConnectionRowViewModel
{
    private readonly ConnectionObservation _item;

    public ConnectionRowViewModel(ConnectionObservation item, ConnectionColumnLayout columns, bool active)
    {
        _item = item;
        Columns = columns;
        IsActive = active;
    }

    public ConnectionColumnLayout Columns { get; }
    public bool IsActive { get; }
    public string Time => _item.StartedAtUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Timestamp => _item.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    public string Source => _item.ProcessId is uint pid ? $"PID {pid}" : "unknown";
    public string ProcessName => string.IsNullOrWhiteSpace(_item.ProcessPath) ? "unknown" : Path.GetFileName(_item.ProcessPath);
    public string Pid => _item.ProcessId?.ToString() ?? "unknown";
    public string Host => string.IsNullOrWhiteSpace(_item.Host)
        ? $"{_item.DestinationIp}:{_item.DestinationPort}"
        : $"{_item.Host}:{_item.DestinationPort}";
    public string Executable => _item.ProcessPath ?? "—";
    public string Session => "—";
    public string Decision => _item.Outbound ?? _item.Chains.LastOrDefault() ?? "unknown";
    public string Reason => string.IsNullOrWhiteSpace(_item.Rule) ? "默认规则" : _item.Rule;
    public string Rule => string.IsNullOrWhiteSpace(_item.RulePayload) ? (_item.Rule ?? "—") : _item.RulePayload;
    public string RuleSet => _item.Rule ?? "—";
    public string RuleText => _item.RulePayload ?? "—";
    public string Interface => string.Join(" → ", _item.Chains);
    public string Status => IsActive ? "活动" : "已关闭";
    public string Bytes => (_item.Upload + _item.Download).ToString("N0");
    public string Latency => "—";
}

public sealed class CoreLogRowViewModel(CoreLogEntry entry)
{
    public string Time => entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Source => entry.Source;
    public string Level => entry.Level;
    public string Message => entry.Message;
}
