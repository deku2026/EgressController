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

public sealed class AppEntryViewModel : ObservableObject, IDisposable
{
    private readonly IAppController _controller;

    public AppEntryViewModel(IAppController controller, LaunchTarget target, Action changed)
    {
        _controller = controller;
        Target = target;
        Routing = new RouteSelectionViewModel(controller.Profile,
            controller.Profile.Applications.FirstOrDefault(route => route.DiscoveryKey == target.DiscoveryKey)?.Target,
            (enabled, route) => controller.SetApplicationsRouteAsync([Target], enabled, route), changed,
            error => controller.ReportFailure("应用分流", "apps.route", error.Message, error));
        Icon = controller.IsPreview ? null : AppIconLoader.Load(target.IconPath ?? target.CanonicalExecutable ?? target.Command);
    }

    public LaunchTarget Target { get; }
    public string Id => Target.Id;
    public string DiscoveryKey => Target.DiscoveryKey;
    public string Name => Target.Name;
    public string Initial => Name.Length == 0 ? "?" : Name[..1].ToUpperInvariant();
    private IReadOnlyList<string> InventoryPaths => EgressController.Launcher.Discovery.ApplicationInventorySnapshot.Create([Target])
        .CaptureSelection(DiscoveryKey, Routing.SelectedRoute.Target,
            _controller.Profile.Applications.FirstOrDefault(item => item.DiscoveryKey == DiscoveryKey), Target.OwnedRoots).ExecutablePaths;
    public string InventoryDetails => string.Join("\n", InventoryPaths);
    public string InventorySummary => $"{InventoryPaths.Count} 个 EXE";
    public string Command => Target.Command ?? Target.Aumid ?? "未解析";
    public string Details => Target.Kind == LaunchKind.PackagedAumid
        ? $"Package · {Target.PackageFamily} · {Target.Aumid} · EXE: {Command}"
        : Command + (string.IsNullOrWhiteSpace(Target.Source) ? string.Empty : " · " + Target.Source);
    public string KindText => Target.Kind switch
    {
        LaunchKind.PackagedAumid => "APP · MSIX",
        _ => "APP · Win32",
    };
    public string Glyph => "▣";
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;
    public bool CanRoute => Target.CanRoute;
    public bool CanManage => CanRoute || Routing.IsSelected;
    public RouteSelectionViewModel Routing { get; }
    public void RefreshRoute()
    {
        Routing.Refresh(_controller.Profile, _controller.Profile.Applications.FirstOrDefault(route => route.DiscoveryKey == DiscoveryKey)?.Target);
        OnPropertyChanged(nameof(InventorySummary)); OnPropertyChanged(nameof(InventoryDetails));
    }

    public void Dispose() => Icon?.Dispose();
}
