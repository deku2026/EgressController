using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Core.Routing;
using EgressController.Mac;
using EgressController.Presentation;

namespace EgressController.MacApp.ViewModels;

public sealed record NavigationEntry(string Key, string Title, string Description)
{ public Geometry Icon => Icons.For(Key); }
public sealed record RouteOption(string Label, RouteDestination Destination)
{ public override string ToString() => Label; }
public sealed partial class AdapterRow : ObservableObject
{
    public required ResolvedAdapter Adapter { get; set; }
    public void Update(ResolvedAdapter adapter) { Adapter = adapter; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Available)); }
    public string Id => Adapter.Id;
    public string Name => Adapter.Name;
    public string Detail => Adapter.InterfaceName + " · " + (Adapter.Address.Length > 0 ? Adapter.Address : "无 IP 地址");
    public string Status => Adapter.Available ? "已连接" : "不可用 · 保留选择";
    public bool Available => Adapter.Available;
    [ObservableProperty] private bool _selected;
    public Action? Changed { get; set; }
    partial void OnSelectedChanged(bool value) => Changed?.Invoke();
}
public sealed partial class AppRow : ObservableObject
{
    public required MacApplication App { get; init; }
    public string Name => App.Name;
    public string Path => App.Path;
    public bool IsCli => !App.Bundle;
    public string Scope => App.Bundle ? "App Bundle · 包内可执行文件" : "原生 CLI · 精确路径";
    public IReadOnlyList<RouteOption> Routes { get; set; } = [];
    public IReadOnlyList<string> Categories { get; } = ["未分类", "AI", "浏览器", "开发", "社交", "影音", "游戏", "办公"];
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private RouteOption? _route;
    [ObservableProperty] private string _category = "未分类";
    [ObservableProperty] private IImage? _icon;
    public Action? Changed { get; set; }
    partial void OnSelectedChanged(bool value) => Changed?.Invoke();
    partial void OnRouteChanged(RouteOption? value) => Changed?.Invoke();
    partial void OnCategoryChanged(string value) => Changed?.Invoke();
    public void ReplaceRoutes(IReadOnlyList<RouteOption> routes) { Routes = routes; OnPropertyChanged(nameof(Routes)); }
}
public sealed partial class RuleRow : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public IReadOnlyList<RouteOption> Routes { get; set; } = [];
    [ObservableProperty] private bool _selected;
    [ObservableProperty] private RouteOption? _route;
    public Action? Changed { get; set; }
    partial void OnSelectedChanged(bool value) => Changed?.Invoke();
    partial void OnRouteChanged(RouteOption? value) => Changed?.Invoke();
    public void ReplaceRoutes(IReadOnlyList<RouteOption> routes) { Routes = routes; OnPropertyChanged(nameof(Routes)); }
}
public sealed record PortRow(LocalProxy Proxy, string Status)
{
    public int Port => Proxy.Port;
    public string Name => Proxy.Name;
    public string Address => "127.0.0.1:" + Port;
    public string Owner => Proxy.OwnerPath;
}
public sealed record DomainRow(DomainRoute Rule, string RouteLabel)
{ public string Domain => Rule.Domain; }
public sealed record LogRow(string Time, string Title, string Detail);
