using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;

namespace EgressController.App.ViewModels;

public sealed class AppEntryViewModel : ObservableObject, IDisposable
{
    private readonly IAppController _controller;
    private LaunchTarget _originalTarget;
    private readonly Action _changed;
    private bool _categoryBusy;
    private string _categoryStatus = "";
    private bool _launching;
    private string _launchStatus = "";
    private bool _launchFailed;
    private (bool Visible, bool Enabled, string Text, string Hint)? _launchState;

    public AppEntryViewModel(IAppController controller, LaunchTarget target, Action changed)
    {
        _controller = controller;
        _originalTarget = target; _changed = changed;
        Routing = new RouteSelectionViewModel(controller.Profile,
            controller.Profile.Applications.FirstOrDefault(route => route.DiscoveryKey == target.DiscoveryKey)?.Target
                ?? controller.Profile.Applications.FirstOrDefault(route => target.SelectionKeys.Contains(route.DiscoveryKey))?.Target,
            (enabled, route) => controller.SetApplicationsRouteAsync([Target], enabled, route), changed,
            error => controller.ReportFailure("应用分流", "apps.route", error.Message, error), () => controller.Adapters);
        UnifyRouteCommand = new AsyncRelayCommand(Routing.CommitCurrentAsync);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => CanLaunch);
        Routing.PropertyChanged += OnRoutingChanged;
        Appearance = controller.Visuals.ForApplication(target);
        RefreshInventoryCommand = new AsyncRelayCommand(async () =>
        {
            CategoryStatus = "正在读取应用目录中的 EXE…";
            var result = await controller.RefreshApplicationInventoryAsync(Target);
            CategoryStatus = result.Succeeded ? "" : result.Error ?? "更新失败";
            RefreshRoute(); changed();
        });
    }

    public IAsyncRelayCommand UnifyRouteCommand { get; }
    public IAsyncRelayCommand LaunchCommand { get; }
    public bool ShowLaunch => Routing.IsSelected && !Services.ApplicationLaunchPolicy.IsCli(Target);
    private string? LaunchBlockReason => !Routing.CanEdit ? "正在保存分流，请稍后启动。"
        : Services.ApplicationLaunchPolicy.BlockReason(Target, _controller.Profile, _controller.IsTunRunning, _controller.IsUpdatingProfile, _controller.Protection);
    public bool CanLaunch => ShowLaunch && !_launching && LaunchBlockReason is null;
    public string LaunchText => _launching ? "启动中" : "启动";
    public string LaunchHint => _launching ? "正在请求 Windows 打开应用"
        : LaunchBlockReason ?? "打开 " + Name;
    public string RowDetail => Routing.HasStatus ? Routing.Status : HasCategoryStatus ? CategoryStatus
        : _launchStatus.Length > 0 ? _launchStatus : Command;
    public bool HasRowIssue => Routing.HasStatus || HasCategoryStatus || _launchFailed;
    private async Task LaunchAsync()
    {
        if (!CanLaunch) return;
        _launching = true; _launchStatus = ""; _launchFailed = false;
        RefreshLaunchState(); NotifyDetail();
        try
        {
            var result = await _controller.LaunchApplicationAsync(DiscoveryKey);
            _launchFailed = !result.Succeeded;
            _launchStatus = result.Succeeded ? "已发送启动请求" : result.Error ?? "启动失败，请重试。";
        }
        catch (Exception error) { _launchFailed = true; _launchStatus = "启动失败：" + error.Message; }
        finally { _launching = false; RefreshLaunchState(); NotifyDetail(); }
    }
    private void OnRoutingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Routing.IsSelected) or nameof(Routing.CanEdit)) RefreshLaunchState();
        if (args.PropertyName is nameof(Routing.Status) or nameof(Routing.HasStatus)) NotifyDetail();
    }
    private void NotifyDetail() { OnPropertyChanged(nameof(RowDetail)); OnPropertyChanged(nameof(HasRowIssue)); }
    public void RefreshLaunchState()
    {
        var state = (ShowLaunch, CanLaunch, LaunchText, LaunchHint);
        if (_launchState == state) return;
        _launchState = state;
        OnPropertyChanged(nameof(ShowLaunch)); OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchText)); OnPropertyChanged(nameof(LaunchHint));
        LaunchCommand.NotifyCanExecuteChanged();
    }
    public void UpdateTarget(LaunchTarget target)
    {
        _originalTarget = target;
        foreach (var name in new[] { nameof(Name), nameof(Command), nameof(Details), nameof(KindLabel), nameof(KindText), nameof(CanRoute), nameof(CanManage) }) OnPropertyChanged(name);
        RefreshRoute(); NotifyDetail();
    }
    public LaunchTarget Target => _controller.FindApplication(_originalTarget.DiscoveryKey) ?? _originalTarget;
    public IAsyncRelayCommand RefreshInventoryCommand { get; }
    public IReadOnlyList<ApplicationCategory> Categories => ApplicationCategory.BuiltIn;
    public string CategoryId => Target.SelectionKeys.Select(key => _controller.ApplicationCategories.GetValueOrDefault(key)).FirstOrDefault(value => value is not null) ?? "";
    public ApplicationCategory SelectedCategory
    {
        get => Categories.FirstOrDefault(item => item.Id == CategoryId) ?? Categories[0];
        set { if (value is not null && value.Id != CategoryId && !_categoryBusy) _ = SaveCategoryAsync(value); }
    }
    public bool CanEditCategory => !_categoryBusy;
    public string CategoryStatus { get => _categoryStatus; private set { SetProperty(ref _categoryStatus, value); OnPropertyChanged(nameof(HasCategoryStatus)); NotifyDetail(); } }
    public bool HasCategoryStatus => CategoryStatus.Length > 0;
    private async Task SaveCategoryAsync(ApplicationCategory category)
    {
        _categoryBusy = true; OnPropertyChanged(nameof(CanEditCategory));
        try { var result = await _controller.SetApplicationCategoryAsync(Target, category.Id); CategoryStatus = result.Succeeded ? "" : result.Error ?? "分类保存失败"; }
        catch (Exception error) { CategoryStatus = "分类保存失败：" + error.Message; }
        finally { _categoryBusy = false; OnPropertyChanged(nameof(CanEditCategory)); OnPropertyChanged(nameof(SelectedCategory)); OnPropertyChanged(nameof(CategoryId)); _changed(); }
    }
    public string Id => Target.Id;
    public string DiscoveryKey => Target.DiscoveryKey;
    public string Name => Target.Name;
    public string Initial => Name.Length == 0 ? "?" : Name[..1].ToUpperInvariant();
    private IReadOnlyList<string> InventoryPaths => EgressController.Launcher.Discovery.ApplicationCatalogProjection.Merge(Target,
        _controller.Profile.Applications.Where(item => Target.SelectionKeys.Contains(item.DiscoveryKey)), Routing.SelectedRoute.Target).ExecutablePaths;
    public bool HasConflictingRoutes => _controller.Profile.Applications.Where(item => Target.SelectionKeys.Contains(item.DiscoveryKey)).Select(item => item.Target).Distinct().Count() > 1;
    public string KindLabel => Target.Kind == LaunchKind.CliNative ? "命令行" : Target.Kind == LaunchKind.PackagedAumid ? "应用" : "桌面";
    public string InventoryDetails => string.Join("\n", InventoryPaths);
    public string InventoryRoots => string.Join("\n", Target.OwnedRoots);
    public bool HasInventoryRoots => Target.OwnedRoots.Count > 0;
    public bool HasPendingInventory => !Target.InventoryReady && (Target.OwnedExecutables.Count > 0 || Routing.IsSelected);
    public string InventoryDescription => Target.InventoryReady ? "上次扫描保存的 EXE 清单。新增文件需要刷新，刷新时保留历史路径。"
        : HasPendingInventory ? "关联目录已增加。刷新 EXE 清单后，新增路径才会应用到分流与守护。"
        : "尚未建立递归清单。勾选应用或点击刷新时，只扫描下列应用目录。";
    public string InventorySummary => Target.InventoryReady ? $"{InventoryPaths.Count} 个 EXE" : HasPendingInventory ? "目录变更 · 待刷新" : "按需建立清单";
    public string Command => Target.Command ?? Target.OwnedRoots.FirstOrDefault() ?? Target.Aumid ?? "尚未定位 · 请指定安装目录";
    public string Details => Target.Kind == LaunchKind.PackagedAumid
        ? $"Package · {Target.PackageFamily} · {Target.Aumid} · EXE: {Command}"
        : Command + (string.IsNullOrWhiteSpace(Target.Source) ? string.Empty : " · " + Target.Source);
    public string KindText => Target.Kind switch
    {
        LaunchKind.PackagedAumid => "APP · MSIX",
        _ => "APP · Win32",
    };
    public string Glyph => "▣";
    private Services.ProcessVisual _appearance = null!;
    public Services.ProcessVisual Appearance { get => _appearance; private set => SetProperty(ref _appearance, value); }
    public Bitmap? Icon => Appearance.Icon;
    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;
    public bool CanRoute => Target.CanRoute;
    public bool CanManage => CanRoute || Routing.IsSelected;
    public bool CanChooseMainEntry => Target.Kind == LaunchKind.DirectExe;
    public RouteSelectionViewModel Routing { get; }
    public void RefreshRoute()
    {
        Appearance = _controller.Visuals.ForApplication(Target);
        Routing.Refresh(_controller.Profile, _controller.Profile.Applications.FirstOrDefault(route => route.DiscoveryKey == DiscoveryKey)?.Target
            ?? _controller.Profile.Applications.FirstOrDefault(route => Target.SelectionKeys.Contains(route.DiscoveryKey))?.Target);
        OnPropertyChanged(nameof(HasConflictingRoutes));
        OnPropertyChanged(nameof(CanChooseMainEntry));
        OnPropertyChanged(nameof(InventorySummary)); OnPropertyChanged(nameof(InventoryDetails));
        OnPropertyChanged(nameof(InventoryDescription));
        OnPropertyChanged(nameof(InventoryRoots)); OnPropertyChanged(nameof(HasInventoryRoots)); OnPropertyChanged(nameof(HasPendingInventory));
        OnPropertyChanged(nameof(SelectedCategory)); OnPropertyChanged(nameof(CategoryId));
        RefreshLaunchState();
    }

    public void Dispose() => Routing.PropertyChanged -= OnRoutingChanged; // Shared cache owns icons.
}
