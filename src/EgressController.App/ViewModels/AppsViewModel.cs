using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Diagnostics;
using EgressController.Rules.Catalog;
using Avalonia.Media;
using EgressController.Presentation;

namespace EgressController.App.ViewModels;

public sealed class AppsViewModel : ObservableObject
{
    private readonly IAppController _controller;
    private readonly List<AppEntryViewModel> _all = new();
    private string _query = string.Empty;
    private string _status = "尚未扫描";
    private bool _isScanning;
    private AppCategoryFilter _selectedCategory;
    private string _discoveryNotice = "";
    public string DiscoveryNotice { get => _discoveryNotice; private set { if (SetProperty(ref _discoveryNotice, value)) OnPropertyChanged(nameof(HasDiscoveryNotice)); } }
    public bool HasDiscoveryNotice => DiscoveryNotice.Length > 0;

    public AppsViewModel(IAppController controller)
    {
        _controller = controller;
        Categories = new[] { new AppCategoryFilter("all", "全部应用", "apps"), new AppCategoryFilter("selected", "已勾选", "guardian") }
            .Concat(ApplicationCategory.BuiltIn.Select(item => new AppCategoryFilter(item.Id, item.Name, item.Icon))).ToArray();
        _selectedCategory = Categories[0];
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        SelectAllCommand = new AsyncRelayCommand(() => SetAllAsync(true));
        ClearAllCommand = new AsyncRelayCommand(() => SetAllAsync(false));
        ReplaceEntries(controller.CachedTargets);
    }

    public IReadOnlyList<AppCategoryFilter> Categories { get; }
    public AppCategoryFilter SelectedCategory { get => _selectedCategory; set { if (value is not null && SetProperty(ref _selectedCategory, value)) RefreshVisible(true); } }
    public string CategoryTitle => SelectedCategory.Name;
    public string CategoryDescription => SelectedCategory.Id switch { "all" => "新应用默认未分类 · 勾选后单独分流", "selected" => "已启用单独分流与进程保护的应用", _ => "分类由你决定 · 修改分类不会影响分流" };

    private ObservableCollection<AppEntryViewModel> _entries = new();
    public ObservableCollection<AppEntryViewModel> Entries { get => _entries; private set => SetProperty(ref _entries, value); }
    public void RefreshLaunchStates()
    {
        foreach (var entry in _all.Where(entry => entry.ShowLaunch)) entry.RefreshLaunchState();
        DiscoveryNotice = string.Join("\n", _controller.DiscoveryNotices);
    }
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value ?? string.Empty)) RefreshVisible(true); }
    }
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
    public string ScanText => IsScanning ? "刷新中…" : "刷新列表";
    public string Summary => $"{_all.Count} 个目标 · {_all.Count(entry => entry.Routing.IsSelected)} 个单独分流";
    public bool IsEmpty => Entries.Count == 0;
    public bool HasEntries => Entries.Count > 0;

    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand SelectAllCommand { get; }
    public IAsyncRelayCommand ClearAllCommand { get; }

    public Task ScanInitialAsync() => ScanAsync();

    private async Task ScanAsync()
    {
        if (IsScanning)
            return;
        IsScanning = true;
        Status = "正在读取 Windows 安装登记与游戏库…";
        try
        {
            IReadOnlyList<LaunchTarget> discovered = _controller.IsPreview ? _controller.ScanTargets() : await Task.Run(_controller.ScanTargets);
            ReplaceEntries(discovered);
            Status = $"扫描完成：{_all.Count(entry => entry.Target.Kind != LaunchKind.CliNative)} 个应用，{_all.Count(entry => entry.Target.Kind == LaunchKind.CliNative)} 个 CLI。";
            DiscoveryNotice = string.Join("\n", _controller.DiscoveryNotices);
            RefreshVisible();
        }
        catch (Exception exception)
        {
            Status = "扫描失败：" + exception.Message;
            _controller.ReportFailure("应用扫描", "apps.scan", Status, exception);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task SetAllAsync(bool enabled)
    {
        AppEntryViewModel[] targets = Entries.Where(entry => entry.CanManage && entry.Routing.CanEdit).ToArray();
        ControllerOperationResult result = await _controller.SetApplicationsRouteAsync(
            targets.Select(entry => entry.Target),
            enabled, routes: targets.ToDictionary(entry => entry.DiscoveryKey, entry => entry.Routing.SelectedRoute.Target));
        if (!result.Succeeded)
        {
            Status = result.Error ?? "应用选择失败。";
        }
        else
        {
            foreach (AppEntryViewModel entry in targets)
                entry.RefreshRoute();
            Status = enabled ? "已为当前列表启用分流与保护。" : "已取消当前列表的分流与保护。";
        }
        RefreshVisible();
    }

    private void RefreshVisible() => RefreshVisible(false);
    private void RefreshVisible(bool filterChanged)
    {
        string query = _query.Trim();
        var visible = _all.Where(entry => MatchesCategory(entry) && (query.Length == 0
            || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.KindText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Command.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        // A filter is a single new viewport, not hundreds of Move notifications that make
        // the virtualizing panel repeatedly anchor and traverse the intervening rows.
        if (filterChanged && !Entries.SequenceEqual(visible)) Entries = new(visible);
        else Reconcile(Entries, visible);
        foreach (var category in Categories) category.Count = _all.Count(entry => category.Id == "all" || category.Id == "selected" && entry.Routing.IsSelected
            || category.Id is not ("all" or "selected") && entry.CategoryId == category.Id);
        OnPropertyChanged(nameof(CategoryTitle)); OnPropertyChanged(nameof(CategoryDescription));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HasEntries));
    }

    private bool MatchesCategory(AppEntryViewModel entry) => SelectedCategory.Id switch
    { "all" => true, "selected" => entry.Routing.IsSelected, _ => entry.CategoryId == SelectedCategory.Id };
    private void ReplaceEntries(IReadOnlyList<LaunchTarget> targets)
    {
        var remaining = targets.DistinctBy(target => target.DiscoveryKey).ToDictionary(target => target.DiscoveryKey, StringComparer.Ordinal);
        // Existing rows keep their identity, order and in-flight commands. Discoveries are appended;
        // a metadata refresh must not reset selection/focus or move the apps being read.
        foreach (var item in _all.ToArray())
        {
            if (remaining.Remove(item.DiscoveryKey, out var target)) item.UpdateTarget(target);
            else { item.Dispose(); _all.Remove(item); }
        }
        foreach (var target in targets)
            if (remaining.Remove(target.DiscoveryKey)) _all.Add(new(_controller, target, RefreshVisible));
        RefreshVisible();
    }
    private static void Reconcile(ObservableCollection<AppEntryViewModel> collection, IReadOnlyList<AppEntryViewModel> desired)
    {
        var keep = desired.ToHashSet();
        for (int index = collection.Count - 1; index >= 0; index--)
            if (!keep.Contains(collection[index])) collection.RemoveAt(index);
        for (int index = 0; index < desired.Count; index++)
        {
            if (index < collection.Count && ReferenceEquals(collection[index], desired[index])) continue;
            int current = collection.IndexOf(desired[index]);
            if (current < 0) collection.Insert(index, desired[index]);
            else if (current != index) collection.Move(current, index);
        }
        while (collection.Count > desired.Count) collection.RemoveAt(collection.Count - 1);
    }
    public async Task AddManualAsync(string path, string? replaceKey = null)
    {
        var result = await _controller.AddManualApplicationAsync(path, replaceKey);
        Status = result.Succeeded ? replaceKey is null ? "应用已添加，尚未启用分流。" : "主程序或安装目录已记录；请刷新 EXE 清单后启动。" : result.Error ?? "添加失败";
        if (result.Succeeded) ReplaceEntries(_controller.CachedTargets);
    }

    public void RefreshStatuses()
    {
        foreach (AppEntryViewModel entry in _all)
            entry.UpdateTarget(_controller.FindApplication(entry.DiscoveryKey) ?? entry.Target);
        RefreshVisible();
    }
}

public sealed class AppCategoryFilter(string id, string name, string icon) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    private Geometry? _glyph;
    public Geometry Glyph => _glyph ??= Icons.For(icon);
    private int _count;
    public int Count { get => _count; set => SetProperty(ref _count, value); }
}
