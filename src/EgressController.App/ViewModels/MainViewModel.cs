using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using EgressController.Core.Diagnostics;
using EgressController.Core.Models;
using EgressController.Core.Routing;
using EgressController.Rules.Catalog;

namespace EgressController.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(RouterHost host)
    {
        Host = host;
        Overview = new OverviewViewModel(host);
        Apps = new AppsViewModel(host);
        Domains = new DomainsViewModel(host);
        Connections = new ConnectionsViewModel(host);

        // The host restores the last-known-good rule cache synchronously. The first remote
        // catalog refresh is explicit-upstream and asynchronous, so the window remains usable
        // even when the upstream proxy is unavailable.
        host.StartRemoteRulesRefresh();
        Apps.StartInitialScan();
        Domains.RefreshSearch();
        Overview.Refresh();
    }

    public RouterHost Host { get; }
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
        Status = Host.LastMessage.Length == 0 ? "就绪" : Host.LastMessage;
    }
}

public sealed class OverviewViewModel : ObservableObject
{
    private readonly RouterHost _host;
    private string _esim = "未扫描", _upstream = "127.0.0.1:7890", _router = "未启动", _systemProxy = "Off";
    private string _notice = "控制器负责 HTTP/HTTPS 的按来源进程和域名分流；未命中流量始终进入上游代理。";
    private AdapterOptionViewModel? _selectedAdapter;
    private string _adapterSignature = string.Empty;

    public OverviewViewModel(RouterHost host)
    {
        _host = host;
        RefreshCommand = new RelayCommand(() => { _host.RefreshAdapters(); Refresh(); });
        StartCommand = new RelayCommand(() => { Try(() => _host.Start()); Refresh(); });
        ToggleRoutingCommand = new RelayCommand(() =>
        {
            if (_host.RoutingEnabled) _host.StopRouting(); else _host.AcquireSystemProxy();
            Refresh();
        });
        RestoreStaleCommand = new RelayCommand(() => { _host.RestoreStaleProxy(); Refresh(); });
    }

    public string Esim { get => _esim; private set => SetProperty(ref _esim, value); }
    public string Upstream { get => _upstream; private set => SetProperty(ref _upstream, value); }
    public string Router { get => _router; private set => SetProperty(ref _router, value); }
    public string SystemProxy { get => _systemProxy; private set => SetProperty(ref _systemProxy, value); }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    public string ToggleRoutingText => _host.RoutingEnabled ? "停止路由并恢复代理" : "启用系统代理路由";
    public bool CanRestoreStale => _host.HasStaleProxy;

    public ObservableCollection<AdapterOptionViewModel> Adapters { get; } = new();
    public AdapterOptionViewModel? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (SetProperty(ref _selectedAdapter, value) && value is not null)
            {
                _host.SelectEsim(value.Guid);
                Refresh();
            }
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand ToggleRoutingCommand { get; }
    public RelayCommand RestoreStaleCommand { get; }

    public void Refresh()
    {
        Esim = _host.EsimSummary;
        Upstream = $"HTTP-compatible · {_host.UpstreamHost}:{_host.UpstreamPort}";
        Router = _host.RouterSummary;
        SystemProxy = _host.SystemProxySummary;
        Notice = _host.LastMessage.Length == 0
            ? "控制器负责 HTTP/HTTPS 的按来源进程和域名分流；未命中流量始终进入上游代理。"
            : _host.LastMessage;

        string currentAdapters = string.Join("|", _host.Adapters.Select(a => $"{a.Identity.Guid}:{a.IfIndex}:{a.IsUp}:{a.Addresses.Count}"));
        Guid? activeGuid = _host.Esim?.Identity.Guid ?? _selectedAdapter?.Guid;
        if (!string.Equals(currentAdapters, _adapterSignature, StringComparison.Ordinal))
        {
            Adapters.Clear();
            foreach (NetworkAdapterInfo adapter in _host.Adapters)
                Adapters.Add(new AdapterOptionViewModel(adapter));
            _selectedAdapter = activeGuid is null ? Adapters.FirstOrDefault() : Adapters.FirstOrDefault(a => a.Guid == activeGuid);
            _adapterSignature = currentAdapters;
            OnPropertyChanged(nameof(SelectedAdapter));
        }
        else if (activeGuid is Guid guid && _selectedAdapter?.Guid != guid)
        {
            _selectedAdapter = Adapters.FirstOrDefault(adapter => adapter.Guid == guid);
            OnPropertyChanged(nameof(SelectedAdapter));
        }
        OnPropertyChanged(nameof(ToggleRoutingText));
        OnPropertyChanged(nameof(CanRestoreStale));
    }

    private static void Try(Action action)
    {
        try { action(); } catch { /* the host exposes the actionable status */ }
    }
}

public sealed class AdapterOptionViewModel(NetworkAdapterInfo adapter)
{
    public Guid Guid => adapter.Identity.Guid;
    public string Display => $"{adapter.Identity.NameSnapshot} · {(adapter.IsUp ? "在线" : "离线")} · ifIndex {adapter.IfIndex}";
}

public sealed class AppsViewModel : ObservableObject
{
    private readonly RouterHost _host;
    private readonly List<AppEntryViewModel> _all = new();
    private string _query = string.Empty;
    private string _manualExecutable = string.Empty;
    private string _status = "尚未扫描";
    private bool _isScanning;

    public AppsViewModel(RouterHost host)
    {
        _host = host;
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        ClearAllCommand = new RelayCommand(() => SetAll(false));
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
    public string ScanText => IsScanning ? "扫描中…" : "主动扫描";
    public string Summary => $"{_all.Count} 个目标 · {_all.Count(e => e.Managed)} 个已选 Managed";

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand AddExecutableCommand { get; }

    public void StartInitialScan()
        => _ = ScanAsync();

    public void Scan()
        => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (IsScanning)
            return;

        IsScanning = true;
        Status = "正在扫描已安装应用、Microsoft Store、Program Files 和 App Paths…";
        try
        {
            IReadOnlyList<LaunchTarget> discovered = await Task.Run(_host.ScanTargets);
            var old = _all.ToDictionary(e => e.DiscoveryKey, e => e.Managed, StringComparer.Ordinal);
            foreach (AppEntryViewModel entry in _all)
                entry.Dispose();
            _all.Clear();
            foreach (LaunchTarget target in discovered)
            {
                if (old.TryGetValue(target.DiscoveryKey, out bool managed))
                    target.Managed = managed;
                _all.Add(new AppEntryViewModel(_host, target, () => { OnPropertyChanged(nameof(Summary)); }));
            }
            Status = $"扫描完成：{_all.Count} 个 Windows 应用。";
            RefreshVisible();
        }
        catch (Exception ex)
        {
            Status = "扫描失败：" + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void SetAll(bool managed)
    {
        foreach (AppEntryViewModel entry in _all.Where(entry => entry.CanManage))
            entry.Managed = managed;
        Status = managed ? "已选中当前扫描到的全部可路由目标。" : "已清空所有 Managed 选择。";
        OnPropertyChanged(nameof(Summary));
    }

    private void AddExecutable()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ManualExecutable))
                throw new ArgumentException("先填写 .exe 路径。", nameof(ManualExecutable));
            LaunchTarget target = _host.AddExecutable(ManualExecutable);
            if (_all.Any(entry => string.Equals(entry.DiscoveryKey, target.DiscoveryKey, StringComparison.Ordinal)))
            {
                Status = "该可执行文件已在应用列表中。";
                ManualExecutable = string.Empty;
                RefreshVisible();
                return;
            }
            _all.Add(new AppEntryViewModel(_host, target, () => OnPropertyChanged(nameof(Summary))));
            ManualExecutable = string.Empty;
            Status = "已添加可执行文件：" + target.Name;
            RefreshVisible();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private void RefreshVisible()
    {
        string query = _query.Trim();
        Entries.Clear();
        foreach (AppEntryViewModel entry in _all.Where(e => query.Length == 0
            || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || e.KindText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || e.Command.Contains(query, StringComparison.OrdinalIgnoreCase)))
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
    private readonly RouterHost _host;
    private readonly LaunchTarget _target;
    private readonly Action _changed;
    private bool _managed;
    private bool _managedLaunchObserved;
    private string _status = string.Empty;

    public AppEntryViewModel(RouterHost host, LaunchTarget target, Action changed)
    {
        _host = host;
        _target = target;
        _changed = changed;
        _managed = target.Managed;
        Icon = AppIconLoader.Load(target.IconPath ?? target.CanonicalExecutable ?? target.Command);
        LaunchCommand = new RelayCommand(Launch);
    }

    public string Id => _target.Id;
    public string DiscoveryKey => _target.DiscoveryKey;
    public string Name => _target.Name;
    public string Command => _target.Command ?? _target.Aumid ?? "未解析";
    public string Details
    {
        get
        {
            string source = string.IsNullOrWhiteSpace(_target.Source) ? string.Empty : $" · {_target.Source}";
            string version = string.IsNullOrWhiteSpace(_target.Version) ? string.Empty : $" · {_target.Version}";
            return _target.Kind == LaunchKind.PackagedAumid
                ? $"Package · {_target.PackageFamily} · {_target.Aumid} · EXE: {Command}{source}{version}"
                : Command + source + version;
        }
    }
    public string KindText => _target.Kind switch
    {
        LaunchKind.PackagedAumid => "APP · MSIX",
        LaunchKind.CliNative => "CLI · native",
        LaunchKind.CliWrapperResolved => "CLI · wrapper 未解析",
        LaunchKind.Shortcut => "快捷方式 · 未解析",
        _ => "APP · Win32",
    };
    public string Glyph => _target.Kind is LaunchKind.CliNative or LaunchKind.CliWrapperResolved ? ">_" : "▣";
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;
    public bool CanManage => CanLaunch;
    public bool CanLaunch => !_target.ResolutionUnsupported;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public bool Managed
    {
        get => _managed;
        set
        {
            if (!SetProperty(ref _managed, value))
                return;
            _target.Managed = value;
            _host.SetTargetManaged(_target.Id, value);
            _changed();
        }
    }

    public RelayCommand LaunchCommand { get; }

    private void Launch()
    {
        if (!CanLaunch)
        {
            Status = "未解析，不能安全启动";
            return;
        }
        try
        {
            Status = _host.LaunchTarget(_target.Id);
            _managedLaunchObserved |= _target.Managed;
            RefreshStatus();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    public void RefreshStatus()
    {
        LaunchSession[] sessions = _host.Sessions.All()
            .Where(session => string.Equals(session.TargetId, _target.Id, StringComparison.Ordinal))
            .ToArray();
        if (sessions.Length == 0)
        {
            if (_managedLaunchObserved)
                Status = "已结束 · Managed 会话已清理";
            return;
        }

        _managedLaunchObserved = true;
        int activeOwned = sessions.Sum(session => session.ActiveOwnedPids.Count);
        LaunchSession? rootAlive = sessions.FirstOrDefault(session => !session.RootExited);
        if (rootAlive is not null)
        {
            Status = sessions.Length == 1
                ? $"运行中 · Managed · PID {rootAlive.RootPid}"
                : $"运行中 · Managed · {sessions.Length} 个会话 · {activeOwned} 个进程";
            return;
        }

        Status = $"根进程已退出 · 子进程仍在 Managed · {activeOwned} 个进程";
    }

    public void Dispose()
        => Icon?.Dispose();
}

public sealed class DomainsViewModel : ObservableObject
{
    private readonly RouterHost _host;
    private string _query = string.Empty;
    private string _manualDomain = string.Empty;
    private string _status = "尚未获取 MetaCubeX 规则";
    private bool _isRefreshing;

    public DomainsViewModel(RouterHost host)
    {
        _host = host;
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshRemoteAsync);
        SelectAllCommand = new RelayCommand(() => SetVisible(true));
        ClearAllCommand = new RelayCommand(() => SetVisible(false));
        AddManualCommand = new RelayCommand(AddManual);
    }

    public ObservableCollection<RuleEntryViewModel> Results { get; } = new();
    public ObservableCollection<RuleEntryViewModel> SelectedRules { get; } = new();
    public ObservableCollection<ManualDomainViewModel> ManualDomains { get; } = new();
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value ?? string.Empty)) RefreshSearch(); }
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
    public string RefreshText => IsRefreshing ? "更新中…" : "检查并更新规则";
    public string CatalogDirectory => _host.CatalogDirectory;
    public string CatalogCommit => _host.CatalogCommit.Length == 0 ? "未激活" : _host.CatalogCommit;
    public int CatalogCount => _host.Catalog?.Count ?? 0;
    public string SelectedSummary => $"{_host.SelectedRuleNames.Count} 个规则集 · {ManualDomains.Count} 个自定义域名 → ESIM";

    public IAsyncRelayCommand RefreshCatalogCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand AddManualCommand { get; }

    public void RefreshSearch()
    {
        Results.Clear();
        if (_host.Catalog is not null)
        {
            IReadOnlyList<RuleCatalogEntry> entries = _query.Trim().Length == 0
                ? PopularEntries(_host.Catalog)
                : _host.Catalog.Search(_query, 80);
            foreach (RuleCatalogEntry entry in entries)
                Results.Add(new RuleEntryViewModel(_host, entry, RefreshSearch));
        }

        SelectedRules.Clear();
        if (_host.Catalog is not null)
            foreach (string name in _host.SelectedRuleNames)
                if (_host.Catalog.TryGet(name, out RuleCatalogEntry? entry) && entry is not null)
                    SelectedRules.Add(new RuleEntryViewModel(_host, entry, RefreshSearch));

        ManualDomains.Clear();
        foreach (string domain in _host.ManualDomains)
            ManualDomains.Add(new ManualDomainViewModel(_host, domain, RefreshSearch));

        RefreshStatus();
    }

    public void RefreshStatus()
    {
        Status = _host.CatalogMessage;
        OnPropertyChanged(nameof(CatalogDirectory));
        OnPropertyChanged(nameof(CatalogCommit));
        OnPropertyChanged(nameof(CatalogCount));
        OnPropertyChanged(nameof(SelectedSummary));
    }

    public void ImportLocalDirectory(string directory)
    {
        if (_host.ImportLocalRules(directory, out string error))
            RefreshSearch();
        else if (error.Length != 0)
            Status = error;
    }

    private async Task RefreshRemoteAsync()
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        Status = "正在通过显式上游代理获取官方规则…";
        try
        {
            await _host.RefreshRemoteRulesAsync();
            RefreshSearch();
        }
        catch (OperationCanceledException)
        {
            Status = "规则更新已取消；继续使用缓存。";
        }
        catch (Exception ex)
        {
            Status = "规则更新失败：" + ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            RefreshStatus();
        }
    }

    private static IReadOnlyList<RuleCatalogEntry> PopularEntries(RuleCatalog catalog)
    {
        string[] preferred = { "google", "youtube", "openai", "anthropic", "github", "telegram", "twitter" };
        var result = new List<RuleCatalogEntry>();
        foreach (string name in preferred)
            if (catalog.TryGet(name, out RuleCatalogEntry? entry) && entry is not null)
                result.Add(entry);
        return result;
    }

    private void SetVisible(bool selected)
    {
        foreach (RuleEntryViewModel entry in Results)
            entry.IsSelected = selected;
        RefreshSearch();
    }

    private void AddManual()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ManualDomain))
                throw new ArgumentException("先填写域名，例如 openai.com。", nameof(ManualDomain));
            _host.AddManualDomain(ManualDomain);
            ManualDomain = string.Empty;
            RefreshSearch();
        }
        catch (Exception ex) { Status = ex.Message; }
    }
}

public sealed class RuleEntryViewModel : ObservableObject
{
    private readonly RouterHost _host;
    private readonly RuleCatalogEntry _entry;
    private readonly Action _changed;
    private bool _selected;
    private bool _changing;
    private string _status = string.Empty;

    public RuleEntryViewModel(RouterHost host, RuleCatalogEntry entry, Action changed)
    {
        _host = host;
        _entry = entry;
        _changed = changed;
        _selected = host.SelectedRuleNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Name => _entry.Name;
    public string Path => _entry.Path;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value || _changing)
                return;
            _selected = value;
            OnPropertyChanged();
            _ = ApplySelectionAsync(value);
        }
    }

    private async Task ApplySelectionAsync(bool value)
    {
        _changing = true;
        Status = value ? "正在下载并校验…" : "正在移除…";
        OnPropertyChanged(nameof(Status));
        try
        {
            (bool succeeded, string error) = await _host.SetRuleSetAsync(Name, value);
            if (!succeeded)
            {
                _selected = !value;
                OnPropertyChanged(nameof(IsSelected));
                Status = error;
                OnPropertyChanged(nameof(Status));
                return;
            }
            Status = value ? "已加载并启用" : "已移除";
            OnPropertyChanged(nameof(Status));
            _changed();
        }
        catch (Exception ex)
        {
            _selected = !value;
            OnPropertyChanged(nameof(IsSelected));
            Status = "规则操作失败：" + ex.Message;
            OnPropertyChanged(nameof(Status));
        }
        finally
        {
            _changing = false;
        }
    }
}

public sealed class ManualDomainViewModel(RouterHost host, string domain, Action changed)
{
    public string Domain { get; } = domain;
    public RelayCommand RemoveCommand { get; } = new(() =>
    {
        host.RemoveManualDomain(domain);
        changed();
    });
}

public sealed class ConnectionsViewModel : ObservableObject
{
    private readonly RouterHost _host;
    private long _dropped;
    private int _activeConnections;
    private string _query = string.Empty;
    private string _lastUpdated = "等待连接";

    public ConnectionsViewModel(RouterHost host)
    {
        _host = host;
        CloseAllCommand = new AsyncRelayCommand(CloseAllAsync);
    }

    public ObservableCollection<ConnectionRowViewModel> Rows { get; } = new();
    public ConnectionColumnLayout Columns { get; } = new();
    public long Dropped { get => _dropped; private set => SetProperty(ref _dropped, value); }
    public int ActiveConnections { get => _activeConnections; private set => SetProperty(ref _activeConnections, value); }
    public string ActiveSummary => $"活动 {ActiveConnections}";
    public int Count => Rows.Count;
    public string LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
                Refresh();
        }
    }
    public IAsyncRelayCommand CloseAllCommand { get; }

    public void Refresh()
    {
        Dropped = _host.Log.Dropped;
        ActiveConnections = _host.ActiveConnections;
        Rows.Clear();
        string query = _query.Trim();
        foreach (ConnectionEvent e in _host.Log.Latest().TakeLast(250).Reverse().Where(e => Matches(e, query)))
            Rows.Add(new ConnectionRowViewModel(e, Columns));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(ActiveSummary));
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }

    private async Task CloseAllAsync()
    {
        await _host.CloseAllConnectionsAndClearLogAsync();
        Refresh();
    }

    private static bool Matches(ConnectionEvent e, string query)
    {
        if (query.Length == 0)
            return true;

        string text = string.Join('\n',
            e.ProcessName,
            e.FinalExePath,
            e.SessionId,
            e.SourcePid?.ToString(),
            e.Host,
            e.Port.ToString(),
            e.Egress.ToString(),
            e.Reason.ToString(),
            e.RuleSet,
            e.RuleText,
            e.Interface,
            e.Status.ToString());
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
    private readonly ConnectionEvent _item;

    public ConnectionRowViewModel(ConnectionEvent item, ConnectionColumnLayout columns)
    {
        _item = item;
        Columns = columns;
    }

    public ConnectionColumnLayout Columns { get; }
    public string Time => _item.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Timestamp => _item.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
    public string Source => _item.SourcePid is null ? "unknown" : $"{_item.ProcessName} · PID {_item.SourcePid}";
    public string ProcessName => string.IsNullOrWhiteSpace(_item.ProcessName) ? "unknown" : _item.ProcessName;
    public string Pid => _item.SourcePid?.ToString() ?? "unknown";
    public string Host => $"{_item.Host}:{_item.Port}";
    public string Executable => _item.FinalExePath ?? "—";
    public string Session => _item.SessionId ?? "—";
    public string Decision => _item.Egress == Egress.Esim ? "ESIM" : "UPSTREAM";
    public string Reason => _item.Reason switch
    {
        RouteReason.ManagedApp => "Managed 应用",
        RouteReason.DomainMatch => "域名命中",
        RouteReason.SourceUnknown => "来源未知",
        _ => "默认上游",
    };
    public string Rule => _item.RuleSet is null ? "—" : $"{_item.RuleSet} {_item.RuleText}";
    public string RuleSet => _item.RuleSet ?? "—";
    public string RuleText => _item.RuleText ?? "—";
    public string Interface => string.IsNullOrWhiteSpace(_item.Interface) ? "—" : _item.Interface;
    public string Status => _item.Status.ToString();
    public string Bytes => _item.Bytes.ToString("N0");
    public string Latency => $"{_item.Latency.TotalMilliseconds:N1} ms";
}
