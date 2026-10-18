using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;
using EgressController.State.Quota;

namespace EgressController.App.ViewModels;

public sealed class NetworkViewModel : ObservableObject
{
    private readonly IAppController _controller;
    private string _monitorStatus = "未启动";
    private string _protectionStatus = "TUN 未运行";
    private string _directInternetStatus = "等待家宽联网检测";
    private string _proxyInternetStatus = "等待非家宽联网检测";
    private string _lastChecked = "尚未检测";
    private string _status = string.Empty;

    public NetworkViewModel(IAppController controller)
    {
        _controller = controller;
        CheckCommand = new AsyncRelayCommand(CheckAsync);
        ClearProtectionHistoryCommand = new RelayCommand(() => { _controller.ClearProtectionHistory(); Refresh(); });
    }

    public ObservableCollection<ProtectionEventViewModel> ProtectionEvents { get; } = new();
    public bool NoProtectionEvents => ProtectionEvents.Count == 0;
    private IReadOnlyList<EgressController.Core.Protection.ProtectionEvent>? _lastEvents;

    public string MonitorStatus { get => _monitorStatus; private set => SetProperty(ref _monitorStatus, value); }
    public string ProtectionStatus { get => _protectionStatus; private set => SetProperty(ref _protectionStatus, value); }
    public string DirectInternetStatus { get => _directInternetStatus; private set => SetProperty(ref _directInternetStatus, value); }
    public string ProxyInternetStatus { get => _proxyInternetStatus; private set => SetProperty(ref _proxyInternetStatus, value); }
    public string LastChecked { get => _lastChecked; private set => SetProperty(ref _lastChecked, value); }
    public string TunFailure => _controller.LastTunFailure;
    public bool HasTunFailure => TunFailure.Length > 0;
    public string Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value))
                return;
            OnPropertyChanged(nameof(HasStatus));
        }
    }
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public IAsyncRelayCommand CheckCommand { get; }
    public IRelayCommand ClearProtectionHistoryCommand { get; }

    public void Refresh()
    {
        OnPropertyChanged(nameof(TunFailure));
        OnPropertyChanged(nameof(HasTunFailure));
        MonitorStatus = _controller.NetworkMonitorStatus;
        ProtectionStatus = _controller.ProtectionStatus;
        DirectInternetStatus = _controller.DirectInternetStatus;
        ProxyInternetStatus = _controller.ProxyInternetStatus;
        LastChecked = _controller.NetworkLastCheckedAtUtc is { } checkedAt
            ? checkedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "尚未检测";

        var events = _controller.ProtectionEvents;
        if (!ReferenceEquals(_lastEvents, events))
        {
            _lastEvents = events;
            ProtectionEvents.Clear();
            foreach (var item in events) ProtectionEvents.Add(new(item));
            OnPropertyChanged(nameof(NoProtectionEvents));
        }
    }

    private async Task CheckAsync()
    {
        ControllerOperationResult result = await _controller.CheckInternetHealthAsync();
        Status = result.Succeeded ? "联网检测完成。" : result.Error ?? "联网检测失败。";
        Refresh();
    }
}
