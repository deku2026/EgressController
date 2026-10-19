using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;

namespace EgressController.App.ViewModels;

public sealed class ConnectionsViewModel : ObservableObject
{
    private readonly IAppController _controller;
    private readonly Dictionary<string, ConnectionRowViewModel> _rowsByKey = new(StringComparer.Ordinal);
    private string _query = string.Empty;
    private bool _includeClosed = true;
    private long _droppedConnections;
    private int _activeConnections;
    private string _lastUpdated = "等待 sing-box API";
    private string _actionMessage = string.Empty;
    private TrafficSnapshot? _traffic;
    private ConnectionRowViewModel? _selectedRow;

    public ConnectionsViewModel(IAppController controller)
    {
        _controller = controller;
        CloseAllCommand = new AsyncRelayCommand(CloseAllAsync);
        CloseSelectedCommand = new AsyncRelayCommand(CloseSelectedAsync);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        ToggleHistoryCommand = new RelayCommand(() => IncludeClosed = !IncludeClosed);
    }

    public ObservableCollection<ConnectionRowViewModel> Rows { get; } = new();

    public ConnectionRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(CanCloseSelected));
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool CanCloseSelected => SelectedRow?.IsActive == true;
    public bool HasSelection => SelectedRow is not null;
    public bool IsEmpty => Rows.Count == 0;
    public int ActiveConnections { get => _activeConnections; private set => SetProperty(ref _activeConnections, value); }
    public int Count => Rows.Count;
    public long DroppedConnections { get => _droppedConnections; private set => SetProperty(ref _droppedConnections, value); }
    public string MonitorStatus => _controller.DiagnosticsStatus;
    public string ActiveSummary => $"{(_traffic?.ObservationFresh == true ? "活动" : "上次快照")} {ActiveConnections} · ↑ {TrafficUnits.Rate(_traffic?.UploadRate)} · ↓ {TrafficUnits.Rate(_traffic?.DownloadRate)}";
    public string TotalSummary => $"当前会话 ↑ {TrafficFormat.Bytes(_controller.TrafficUp)} · ↓ {TrafficFormat.Bytes(_controller.TrafficDown)}";
    public string LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }
    public bool HasActionMessage => !string.IsNullOrWhiteSpace(ActionMessage);
    public string ActionMessage { get => _actionMessage; private set { if (SetProperty(ref _actionMessage, value)) OnPropertyChanged(nameof(HasActionMessage)); } }
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
        _traffic = _controller.TrafficSnapshot;
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

            row.SetAppearance(_controller.Visuals.ForProcess(item.ProcessPath));
            row.SetRouteNames((tag, details) => RouteNames.Outbound(tag, _controller.Profile, _controller.Adapters, details));
            row.SetRateFreshness(_traffic.ObservationFresh);
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

        ActiveConnections = active.Count;
        DroppedConnections = _controller.ConnectionHistory.DroppedClosed;
        OnPropertyChanged(nameof(ActiveSummary));
        OnPropertyChanged(nameof(TotalSummary));
        OnPropertyChanged(nameof(MonitorStatus));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        LastUpdated = TrafficFormat.UpdatedAt(_controller.ConnectionsUpdatedAtUtc);
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

    private bool Matches(ConnectionObservation item, string query)
    {
        if (query.Length == 0)
            return true;
        string text = string.Join('\n', item.Id, item.ProcessPath, item.Host, item.DestinationIp,
            item.DestinationPort, item.Network, item.Type, item.DnsMode, item.Rule,
            item.Outbound, string.Join(' ', item.Chains), _controller.Visuals.ForProcess(item.ProcessPath).Name,
            RouteNames.Outbound(item.Outbound, _controller.Profile, _controller.Adapters, true));
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
