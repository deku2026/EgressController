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

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(IAppController controller)
    {
        Controller = controller;
        Dashboard = new DashboardViewModel(controller);
        Overview = new OverviewViewModel(controller);
        Apps = new AppsViewModel(controller);
        Domains = new DomainsViewModel(controller);
        Connections = new ConnectionsViewModel(controller);
        Traffic = new TrafficViewModel(controller);
        Network = new NetworkViewModel(controller);
        Diagnostics = new DiagnosticsViewModel(controller.Diagnostics, controller.Logs, controller.LogPath);
        Domains.RefreshSearch();
        Overview.Refresh();
        Network.Refresh();
        _ = InitializeAsync();
    }

    public IAppController Controller { get; }
    public DashboardViewModel Dashboard { get; }
    public IReadOnlyList<NavigationItem> Pages { get; } = [
        new("overview", "概览", "连接状态与流量，一目了然"),
        new("network", "网络与出口", "管理两张网卡、代理端口与联网检测"),
        new("apps", "应用", "选择需要保护的应用与固定程序路径"),
        new("rules", "域名规则", "为域名与规则集指定出口"),
        new("connections", "连接", "查看当前连接与已结束的会话"),
        new("traffic", "流量", "查看流量趋势与家宽套餐用量"),
        new("protection", "进程保护", "主程序的保护状态与终止记录"),
        new("guardian", "独立守护", "只核验主程序存活与 TUN 接管"),
        new("events", "运行事件", "查看保护、启动与重启的关键原因")];
    private NavigationItem? _selectedPage;
    public NavigationItem SelectedPage
    {
        get => _selectedPage ?? Pages[0];
        set { if (SetProperty(ref _selectedPage, value)) { OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(PageDescription)); } }
    }
    public string PageTitle => SelectedPage.Title;
    public string PageDescription => SelectedPage.Description;
    public bool IsPreview => Controller.IsPreview;
    public bool IsTunRunning => Controller.IsTunRunning;
    public bool IsProtecting => !Controller.Protection.Ready;
    public string TunCaption => Controller.IsTunRunning ? "TUN 已接管" : Controller.LastTunFailure.Length > 0 ? "TUN 未就绪" : "TUN 准备中";
    public string GuardCaption => Guard.Disabled ? "守护已停用" : Guard.Red ? "守护保护中" : "守护在线";
    public string ShortStatus => Controller.Protection.Ready ? "保护就绪" : "保护进行中";
    public OverviewViewModel Overview { get; }
    public AppsViewModel Apps { get; }
    public DomainsViewModel Domains { get; }
    public ConnectionsViewModel Connections { get; }
    public TrafficViewModel Traffic { get; }
    public NetworkViewModel Network { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public EgressController.Guardian.GuardManagementViewModel Guard => Controller.Guard;
    public string GuardianConnectionStatus => Controller.GuardianConnectionStatus;

    private async Task InitializeAsync()
    {
        try
        {
            await Apps.ScanInitialAsync();
            // The independent one-second supervisor starts TUN after discovery.
        }
        catch (Exception exception)
        {
            Status = "初始化失败：" + exception.Message;
            Controller.ReportFailure("初始化", "app.initialize", Status, exception);
        }
        Refresh();
    }

    private EgressProfileDocument? _routingProfile;
    private string _status = "正在初始化…";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception error)
        {
            Status = "界面刷新失败：" + error.Message;
            Controller.ReportFailure("界面刷新", "ui.refresh", Status, error);
            Diagnostics.Refresh();
        }
    }

    private void RefreshCore()
    {
        Dashboard.Refresh();
        Overview.Refresh();
        if (!Controller.IsUpdatingProfile && !ReferenceEquals(_routingProfile, Controller.Profile))
        {
            _routingProfile = Controller.Profile;
            Apps.RefreshStatuses();
            Domains.RefreshRoutes();
        }
        Domains.RefreshStatus();
        Connections.Refresh();
        Traffic.Refresh();
        Network.Refresh();
        Diagnostics.Refresh();
        _ = Guard.RefreshAsync();
        OnPropertyChanged(nameof(GuardianConnectionStatus));
        Status = Network.ProtectionStatus;
        foreach (string name in new[] { nameof(IsTunRunning), nameof(IsProtecting), nameof(TunCaption), nameof(GuardCaption), nameof(ShortStatus) }) OnPropertyChanged(name);
    }
}
