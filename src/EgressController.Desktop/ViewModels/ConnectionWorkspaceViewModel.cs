using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using EgressController.Desktop.Services;
using EgressController.Policy;

namespace EgressController.Desktop.ViewModels;

/// <summary>A passive, bounded projection of observer facts; no command changes policy or connections.</summary>
public sealed class ConnectionWorkspaceViewModel : Bindable
{
    private const int MaximumRecords = 2000;
    private readonly bool _isPreview;
    private readonly Dictionary<string, ConnectionRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IImage> _artwork = new(StringComparer.Ordinal);
    private bool _isOpen, _activeOnly, _paused, _available = true, _hasSnapshot;
    private string _query = "", _limitation = "", _sort = "time";
    private bool _descending = true;
    private ConnectionRow? _selected;
    private ConnectionApplicationFilter? _application;
    private ConnectionWorkspaceSnapshot? _pending;
    private DateTimeOffset _capturedAt;

    public ConnectionWorkspaceViewModel(bool isPreview)
    {
        _isPreview = isPreview;
        Applications.Add(new("all", "所有应用", "apps")); _application = Applications[0];
        ShowSummaryCommand = new(() => IsOpen = false); ShowConnectionsCommand = new(() => IsOpen = true);
        ShowRecentCommand = new(() => ActiveOnly = false); ShowActiveCommand = new(() => ActiveOnly = true);
        TogglePauseCommand = new(TogglePause, () => _rows.Count > 0 || _paused);
        ResetFiltersCommand = new(() => { Query = ""; SelectedApplication = Applications[0]; ActiveOnly = false; }, () => Query.Length > 0 || ActiveOnly || _application?.Id != "all");
        SortTimeCommand = new(() => SortBy("time")); SortApplicationCommand = new(() => SortBy("application"));
        SortDestinationCommand = new(() => SortBy("destination")); SortUploadCommand = new(() => SortBy("upload")); SortDownloadCommand = new(() => SortBy("download"));
    }

    public ObservableCollection<ConnectionRow> VisibleRows { get; } = [];
    public ObservableCollection<ConnectionApplicationFilter> Applications { get; } = [];
    public event Action? ReconciliationStarting;
    public event Action? ReconciliationFinished;
    public bool IsOpen { get => _isOpen; set { if (Set(ref _isOpen, value)) Raise(nameof(SummaryVisible)); } }
    public bool SummaryVisible => !IsOpen;
    public bool ActiveOnly { get => _activeOnly; set { if (Set(ref _activeOnly, value)) { Raise(nameof(RecentSelected)); Publish(); } } }
    public bool RecentSelected => !ActiveOnly;
    public string Query { get => _query; set { if (Set(ref _query, value)) Publish(); } }
    public ConnectionApplicationFilter? SelectedApplication
    {
        get => _application;
        set { if (value is null || value.Id == _application?.Id) return; _application = value; Raise(); Publish(); }
    }
    public ConnectionRow? SelectedConnection
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Raise(nameof(HasSelection)); }
    }
    public bool HasSelection => SelectedConnection is not null;
    public bool IsEmpty => VisibleRows.Count == 0;
    public bool IsPaused => _paused;
    public string PauseLabel => IsPaused ? "继续显示" : "暂停显示";
    public string PauseDescription => IsPaused ? "显示已暂停，网络连接不受影响。" : "只暂停界面更新，不会断开连接。";
    public bool IsAvailable => _available;
    public string StatusLabel => !_available ? "观测不可用" : !_hasSnapshot ? "等待观测" : IsPaused ? "显示已暂停" : "持续更新";
    public IBrush StatusBrush => Brush.Parse(!_available ? "#B27652" : !_hasSnapshot || IsPaused ? "#8D97A9" : "#24866D");
    public string SourceCaption => _isPreview ? "合成连接样本 · 隔离预览" : "核心实际观测 · 不解密 HTTPS 内容";
    public string Limitation => _limitation;
    public bool HasLimitation => !string.IsNullOrWhiteSpace(Limitation);
    public string UpdatedLabel => !_hasSnapshot ? "尚未收到样本" : "更新于 " + _capturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string CountLabel => $"{VisibleRows.Count} 条显示 · {_rows.Count} 条最近记录";
    public string ActiveLabel => $"活跃连接  {_rows.Values.Count(row => row.Model.Activity == ConnectionActivity.Active)}";
    public string RecentLabel => $"最近连接  {_rows.Count}";
    public string EmptyTitle => !_available ? "连接观测暂不可用" : !_hasSnapshot || _rows.Count == 0 ? "等待第一条连接" : ActiveOnly ? "没有已确认的活跃连接" : "没有匹配的连接";
    public string EmptyDescription => !_available ? "可查看日志了解原因；已有网络设置不会因观测失败而改变。" : !_hasSnapshot || _rows.Count == 0 ? "启动网络后，这里只显示核心实际报告的连接。" : ActiveOnly ? "仅有接入日志时无法确认存活状态，可切换“最近连接”查看。" : "尝试其他域名、应用或 IP，或清除筛选。";
    public string TimeHeader => Header("time", "时间");
    public string ApplicationHeader => Header("application", "应用 / 进程");
    public string DestinationHeader => Header("destination", "目标地址");
    public string UploadHeader => Header("upload", "上传");
    public string DownloadHeader => Header("download", "下载");
    public UiCommand ShowSummaryCommand { get; }
    public UiCommand ShowConnectionsCommand { get; }
    public UiCommand ShowRecentCommand { get; }
    public UiCommand ShowActiveCommand { get; }
    public UiCommand TogglePauseCommand { get; }
    public UiCommand ResetFiltersCommand { get; }
    public UiCommand SortTimeCommand { get; }
    public UiCommand SortApplicationCommand { get; }
    public UiCommand SortDestinationCommand { get; }
    public UiCommand SortUploadCommand { get; }
    public UiCommand SortDownloadCommand { get; }

    /// <summary>UI dispatcher only. Stable IDs reuse row objects and an unchanged collection.</summary>
    public void Reconcile(ConnectionWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_paused && snapshot.IsAvailable) { _pending = snapshot; return; }
        if (_paused) _pending = snapshot;
        ReconciliationStarting?.Invoke();
        _hasSnapshot = true; _available = snapshot.IsAvailable; _capturedAt = snapshot.CapturedAt;
        _limitation = snapshot.Limitation ?? "未观测到的进程、流量、速率与规则信息显示为未知。";
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in snapshot.Records.Where(record => !string.IsNullOrWhiteSpace(record.Id)).Take(MaximumRecords))
        {
            if (!retained.Add(input.Id)) continue;
            var record = snapshot.IsAvailable ? input : input with { Activity = ConnectionActivity.Unknown, UploadBytesPerSecond = null, DownloadBytesPerSecond = null };
            if (_rows.TryGetValue(record.Id, out var row)) row.Update(record, snapshot.CapturedAt);
            else { row = new(record, _isPreview); row.Update(record, snapshot.CapturedAt); _rows.Add(record.Id, row); }
            if (record.ApplicationId is { } id && _artwork.TryGetValue(id, out var icon)) row.Icon = icon;
        }
        // Unavailability must immediately revoke active facts without erasing recent history.
        if (!snapshot.IsAvailable)
        {
            foreach (var row in _rows.Values.Where(row => !retained.Contains(row.Id))) row.Update(row.Model with { Activity = ConnectionActivity.Unknown, UploadBytesPerSecond = null, DownloadBytesPerSecond = null }, snapshot.CapturedAt);
        }
        else foreach (var id in _rows.Keys.Where(id => !retained.Contains(id)).ToArray()) _rows.Remove(id);
        if (_rows.Count > MaximumRecords)
            foreach (var id in _rows.Values.OrderByDescending(row => row.Model.LastObservedAt).Skip(MaximumRecords).Select(row => row.Id).ToArray()) _rows.Remove(id);
        PublishApplications(); Publish();
        if (_selected is null && VisibleRows.Count > 0) SelectedConnection = VisibleRows[0];
        foreach (string name in new[] { nameof(IsAvailable), nameof(StatusLabel), nameof(StatusBrush), nameof(Limitation), nameof(HasLimitation), nameof(UpdatedLabel), nameof(ActiveLabel), nameof(RecentLabel) }) Raise(name);
        TogglePauseCommand.Refresh();
        ReconciliationFinished?.Invoke();
    }

    /// <summary>Accepts already decoded, cached artwork from the application service; never loads native icons.</summary>
    public void SetApplicationArtwork(string applicationId, IImage icon)
    {
        if (_artwork.Count >= 128 && !_artwork.ContainsKey(applicationId)) _artwork.Remove(_artwork.Keys.First());
        _artwork[applicationId] = icon;
        foreach (var row in _rows.Values.Where(row => row.Model.ApplicationId == applicationId)) row.Icon = icon;
        foreach (var app in Applications.Where(app => app.Id == applicationId)) app.Icon = icon;
    }
    private void PublishApplications()
    {
        var groups = _rows.Values.GroupBy(row => row.ApplicationKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        Applications[0].Count = _rows.Count;
        foreach (var app in Applications.Skip(1).ToArray())
        {
            if (!groups.TryGetValue(app.Id, out var records)) { if (_application?.Id == app.Id) _application = Applications[0]; Applications.Remove(app); }
            else { var row = records[0]; app.Update(row.ApplicationName, row.Product, records.Length, row.Icon); }
        }
        foreach (var group in groups.OrderBy(group => group.Value[0].ApplicationName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Applications.Any(app => app.Id == group.Key)) continue;
            var row = group.Value[0]; var app = new ConnectionApplicationFilter(group.Key, row.ApplicationName, row.Product) { Count = group.Value.Length, Icon = row.Icon };
            Applications.Add(app);
        }
        Raise(nameof(SelectedApplication));
    }
    private void Publish()
    {
        var query = Query.Trim();
        var rows = _rows.Values.Where(row => (!ActiveOnly || row.Model.Activity == ConnectionActivity.Active)
            && (_application?.Id is null or "all" || row.ApplicationKey == _application.Id)
            && (query.Length == 0 || row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)));
        IOrderedEnumerable<ConnectionRow> ordered = _sort switch
        {
            "application" => Order(rows, row => row.ApplicationName),
            "destination" => Order(rows, row => row.Destination),
            "upload" => Order(rows, row => row.Model.UploadedBytes ?? -1),
            "download" => Order(rows, row => row.Model.DownloadedBytes ?? -1),
            _ => Order(rows, row => row.SortTimestamp)
        };
        var desired = ordered.ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
        for (int index = 0; index < desired.Length; index++)
        {
            if (index < VisibleRows.Count && ReferenceEquals(VisibleRows[index], desired[index])) continue;
            int existing = VisibleRows.IndexOf(desired[index]);
            if (existing >= 0) VisibleRows.Move(existing, index); else VisibleRows.Insert(index, desired[index]);
        }
        while (VisibleRows.Count > desired.Length) VisibleRows.RemoveAt(VisibleRows.Count - 1);
        if (_selected is not null && !desired.Contains(_selected)) SelectedConnection = null;
        foreach (string name in new[] { nameof(IsEmpty), nameof(CountLabel), nameof(EmptyTitle), nameof(EmptyDescription) }) Raise(name);
        ResetFiltersCommand.Refresh();
    }
    private IOrderedEnumerable<ConnectionRow> Order<T>(IEnumerable<ConnectionRow> rows, Func<ConnectionRow, T> key) => _descending ? rows.OrderByDescending(key) : rows.OrderBy(key);
    private void SortBy(string key)
    {
        _descending = _sort == key ? !_descending : key is "time" or "upload" or "download"; _sort = key; Publish();
        foreach (string name in new[] { nameof(TimeHeader), nameof(ApplicationHeader), nameof(DestinationHeader), nameof(UploadHeader), nameof(DownloadHeader) }) Raise(name);
    }
    private string Header(string key, string text) => text + (_sort == key ? _descending ? " ↓" : " ↑" : "");
    private void TogglePause()
    {
        _paused = !_paused;
        if (!_paused && _pending is { } snapshot) { _pending = null; Reconcile(snapshot); }
        foreach (string name in new[] { nameof(IsPaused), nameof(PauseLabel), nameof(PauseDescription), nameof(StatusLabel), nameof(StatusBrush) }) Raise(name);
    }
}

public sealed class ConnectionApplicationFilter(string id, string name, string product) : Bindable
{
    private int _count; private IImage? _icon; private string _name = name, _product = product;
    public string Id => id;
    public bool IsAll => id == "all";
    public string Name => _name;
    public string Product => _product;
    public int Count { get => _count; set => Set(ref _count, value); }
    public IImage? Icon { get => _icon; set => Set(ref _icon, value); }
    public void Update(string updatedName, string updatedProduct, int count, IImage? icon) { Set(ref _name, updatedName, nameof(Name)); Set(ref _product, updatedProduct, nameof(Product)); Count = count; Icon = icon; }
}

public sealed class ConnectionRow : Bindable
{
    private readonly bool _preview;
    private readonly DateTimeOffset _firstObservedAt;
    private DateTimeOffset _now;
    private IImage? _icon;
    public ConnectionRow(ConnectionRecord model, bool preview) { Model = model; _preview = preview; _firstObservedAt = model.LastObservedAt; }
    public ConnectionRecord Model { get; private set; }
    public string Id => Model.Id;
    public string ApplicationKey => Model.ApplicationId ?? Model.ExecutablePath ?? Model.ApplicationName ?? "unknown";
    public string ApplicationName => string.IsNullOrWhiteSpace(Model.ApplicationName) ? "未知进程" : Model.ApplicationName;
    public string Product => !_preview ? "app" : ApplicationName.ToLowerInvariant() switch { var name when name.Contains("claude") => "claude", var name when name.Contains("cursor") => "cursor", var name when name.Contains("chrome") => "chrome", var name when name.Contains("safari") => "safari", var name when name.Contains("slack") => "slack", var name when name.Contains("code") => "vscode", var name when name.Contains("spotify") => "spotify", _ => "app" };
    public IImage? Icon { get => _icon; set => Set(ref _icon, value); }
    public DateTimeOffset SortTimestamp => Model.StartedAt ?? _firstObservedAt;
    public string Time => SortTimestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string Started => Model.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "未知 · 仅有观测时间";
    public string LastObserved => Model.LastObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string Ended => Model.EndedAt?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "未知";
    public string Destination => Endpoint(string.IsNullOrWhiteSpace(Model.Host) ? Model.RemoteIp : Model.Host, Model.RemotePort);
    public string RemoteAddress => Endpoint(Model.RemoteIp, Model.RemotePort);
    public string HostName => Model.Host ?? "未知";
    public string Process => Model.ProcessId is > 0 ? "PID " + Model.ProcessId.Value.ToString(CultureInfo.InvariantCulture) : "PID 未知";
    public string ProcessPath => Model.ExecutablePath ?? "未知 · 核心未报告可执行路径";
    public string Transport => string.IsNullOrWhiteSpace(Model.Transport) ? "未知" : Model.Transport.ToUpperInvariant();
    public string ActivityLabel => Model.Activity switch { ConnectionActivity.Active => "活跃", ConnectionActivity.Closed => "已结束", _ => "状态未知" };
    public IBrush ActivityBrush => Brush.Parse(Model.Activity switch { ConnectionActivity.Active => "#249078", ConnectionActivity.Closed => "#B0B9C8", _ => "#B49A6C" });
    public string RouteLabel => Model.ObservedExitKind switch { ExitKind.Home => "家宽直连", ExitKind.NonHome => "非家宽直连", ExitKind.Proxy => Model.ObservedTargetName ?? "代理", _ => "出口未知" };
    public IBrush RouteBrush => Brush.Parse(Model.ObservedExitKind switch { ExitKind.Home => "#8763CD", ExitKind.NonHome => "#218F9C", ExitKind.Proxy => "#4178D7", _ => "#929BAA" });
    public string Interface => Model.ObservedInterface ?? "未知";
    public string MatchedRule => Model.MatchedRule ?? "未知 · 核心未报告命中规则";
    public string Uploaded => Bytes(Model.UploadedBytes);
    public string Downloaded => Bytes(Model.DownloadedBytes);
    public string UploadRate => Rate(Model.UploadBytesPerSecond);
    public string DownloadRate => Rate(Model.DownloadBytesPerSecond);
    public string Duration
    {
        get
        {
            if (Model.StartedAt is not { } start || (Model.EndedAt is null && Model.Activity != ConnectionActivity.Active)) return "—";
            var duration = (Model.EndedAt ?? _now) - start; if (duration < TimeSpan.Zero) return "—";
            return duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
        }
    }
    public string SearchText => string.Join(' ', ApplicationName, Model.ExecutablePath, Model.Host, Model.RemoteIp, Model.RemotePort, Model.MatchedRule, Model.ObservedTargetName, Model.ObservedInterface, Model.Transport);
    public void Update(ConnectionRecord model, DateTimeOffset now)
    {
        bool changed = Model != model; Model = model; _now = now;
        if (changed) foreach (string name in new[] { nameof(ApplicationName), nameof(Product), nameof(Time), nameof(Started), nameof(Ended), nameof(LastObserved), nameof(Destination), nameof(RemoteAddress), nameof(HostName), nameof(Process), nameof(ProcessPath), nameof(Transport), nameof(ActivityLabel), nameof(ActivityBrush), nameof(RouteLabel), nameof(RouteBrush), nameof(Interface), nameof(MatchedRule), nameof(Uploaded), nameof(Downloaded), nameof(UploadRate), nameof(DownloadRate) }) Raise(name);
        Raise(nameof(Duration));
    }
    private static string Endpoint(string? host, int? port) => string.IsNullOrWhiteSpace(host) ? "未知目标" : port is > 0 and <= 65535 ? (host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port.Value.ToString(CultureInfo.InvariantCulture) : host;
    private static string Bytes(long? bytes)
    {
        if (bytes is null or < 0) return "—"; double value = bytes.Value; string[] units = ["B", "KB", "MB", "GB", "TB"]; int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(value >= 100 || unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
    private static string Rate(double? rate) => rate is null or < 0 || !double.IsFinite(rate.Value) || rate > long.MaxValue ? "未知" : Bytes((long)rate.Value) + "/s";
}
