using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;

namespace EgressController.App.ViewModels;

public sealed class OverviewViewModel : ObservableObject
{
    private readonly IAppController _controller;
    public OverviewViewModel(IAppController controller)
    {
        _controller = controller;
        Ports = new UpstreamPortsViewModel(controller);
        NetworkAdapters = new NetworkAdaptersViewModel(controller);
        RefreshCommand = new RelayCommand(() => { controller.RefreshAdapters(); Refresh(); });
    }
    public UpstreamPortsViewModel Ports { get; }
    public NetworkAdaptersViewModel NetworkAdapters { get; }
    public string Upstream => _controller.Profile.UpstreamPorts.Count == 0 ? "未配置端口" : "默认端口 · " + _controller.UpstreamSummary;
    public bool NeedsStartupResources => !_controller.IsTunRunning;
    public string DownloadStatus => _controller.DownloadStatus;
    public string DownloadRoute => _controller.Profile.UpstreamPorts.Count == 0 ? "未配置默认代理端口" : $"下载出口：默认 SOCKS5 127.0.0.1:{_controller.Profile.UpstreamPort}";
    public string Core => "sing-box · 自动准备与恢复";
    public string Tun => _controller.TunStatus;
    public string TunBadge => "TUN · " + Tun;
    public string Traffic => $"↑ {_controller.TrafficUp:N0} B · ↓ {_controller.TrafficDown:N0} B";
    public string ProtectionStatus => _controller.ProtectionStatus;
    public string Notice => _controller.LastMessage;
    public RelayCommand RefreshCommand { get; }
    public void Refresh()
    {
        Ports.Refresh(); NetworkAdapters.Refresh();
        foreach (string property in new[] { nameof(NeedsStartupResources), nameof(DownloadStatus), nameof(DownloadRoute), nameof(Upstream), nameof(Core), nameof(Tun), nameof(TunBadge), nameof(Traffic), nameof(ProtectionStatus), nameof(Notice) })
            OnPropertyChanged(property);
    }
}
