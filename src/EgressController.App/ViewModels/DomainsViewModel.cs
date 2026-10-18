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

public sealed class DomainsViewModel : ObservableObject
{
    private readonly IAppController _controller;
    private readonly Dictionary<string, RuleEntryViewModel> _ruleEntries = new(StringComparer.Ordinal);
    private string _query = string.Empty;
    private string _manualDomain = string.Empty;
    private string _status = "勾选规则并选择出口，即时应用。";
    private bool _isRefreshing;
    private bool _refreshingRoutes;
    private IReadOnlyList<RouteOptionViewModel> _routeOptions;
    private RouteOptionViewModel _manualRoute;

    public DomainsViewModel(IAppController controller)
    {
        _controller = controller;
        RefreshCatalogCommand = new AsyncRelayCommand(RefreshRemoteAsync);
        SelectAllCommand = new AsyncRelayCommand(() => SetVisibleAsync(true));
        ClearAllCommand = new AsyncRelayCommand(() => SetVisibleAsync(false));
        AddManualCommand = new AsyncRelayCommand(AddManualAsync);
        _routeOptions = RouteOptionViewModel.Create(controller.Profile);
        _manualRoute = _routeOptions[0];
    }

    public ObservableCollection<RuleEntryViewModel> Results { get; } = new();
    public ObservableCollection<RuleEntryViewModel> SelectedRules { get; } = new();
    public ObservableCollection<ManualDomainViewModel> ManualDomains { get; } = new();
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanSelectVisible));
                RefreshSearch();
            }
        }
    }
    public IReadOnlyList<RouteOptionViewModel> RouteOptions => _routeOptions;
    public RouteOptionViewModel ManualRoute
    {
        get => _manualRoute;
        set { if (value is not null && !_refreshingRoutes) SetProperty(ref _manualRoute, value); }
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
    public string RefreshText => IsRefreshing ? "更新中…" : "更新规则库";
    public string CatalogDirectory => _controller.CatalogDirectory;
    public string CatalogCommit => _controller.CatalogCommit.Length == 0 ? "未激活" : _controller.CatalogCommit;
    public int CatalogCount => _controller.Catalog?.Count ?? 0;
    public string SelectedSummary => $"{_controller.SelectedRuleNames.Count} 个 SRS · {ManualDomains.Count} 个自定义域名";
    public bool NoResults => Results.Count == 0;
    public bool NoSelectedRules => SelectedRules.Count == 0;
    public bool CanSelectVisible => _query.Trim().Length > 0;

    public IAsyncRelayCommand RefreshCatalogCommand { get; }
    public IAsyncRelayCommand SelectAllCommand { get; }
    public IAsyncRelayCommand ClearAllCommand { get; }
    public IAsyncRelayCommand AddManualCommand { get; }

    public void RefreshSearch()
    {
        Results.Clear();
        SingBoxRuleCatalog? catalog = _controller.Catalog;
        if (catalog is not null)
        {
            IReadOnlyList<SingBoxRuleCatalogEntry> entries = _query.Trim().Length == 0
                ? PopularEntries(catalog)
                : catalog.Search(_query, 80);
            foreach (SingBoxRuleCatalogEntry entry in entries)
                Results.Add(GetRuleEntry(entry.Name, entry.Path));
        }
        RefreshRoutes();
    }

    private RuleEntryViewModel GetRuleEntry(string name, string path)
    {
        if (!_ruleEntries.TryGetValue(name, out RuleEntryViewModel? entry))
        {
            entry = new RuleEntryViewModel(_controller, name, path, RefreshRoutes);
            _ruleEntries.Add(name, entry);
        }
        return entry;
    }

    public void RefreshRoutes()
    {
        if (_controller.IsUpdatingProfile)
            return;
        _refreshingRoutes = true;
        var target = _manualRoute.Target;
        _routeOptions = RouteOptionViewModel.Create(_controller.Profile);
        _manualRoute = _routeOptions.FirstOrDefault(option => option.Target == target) ?? _routeOptions[0];
        OnPropertyChanged(nameof(RouteOptions));
        OnPropertyChanged(nameof(ManualRoute));
        _refreshingRoutes = false;
        foreach (RuleEntryViewModel entry in _ruleEntries.Values)
            entry.RefreshRoute();

        string[] selected = _controller.SelectedRuleNames.ToArray();
        if (!SelectedRules.Select(entry => entry.Name).SequenceEqual(selected))
        {
            SelectedRules.Clear();
            foreach (string name in selected)
            {
                string path = _controller.Catalog?.TryGet(name, out SingBoxRuleCatalogEntry? entry) == true
                    ? entry!.Path : "规则集尚未缓存";
                SelectedRules.Add(GetRuleEntry(name, path));
            }
        }
        if (!ManualDomains.Select(entry => entry.Domain).SequenceEqual(_controller.ManualDomains))
        {
            ManualDomains.Clear();
            foreach (string domain in _controller.ManualDomains)
                ManualDomains.Add(new ManualDomainViewModel(_controller, domain, RefreshRoutes));
        }
        foreach (ManualDomainViewModel entry in ManualDomains)
            entry.RefreshRoute();
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(CatalogDirectory));
        OnPropertyChanged(nameof(CatalogCommit));
        OnPropertyChanged(nameof(CatalogCount));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(NoSelectedRules));
    }

    private async Task RefreshRemoteAsync()
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        Status = $"正在通过默认端口 {_controller.Profile.UpstreamPort} 获取 MetaCubeX sing catalog…";
        try
        {
            SingBoxCatalogUpdateResult result = await _controller.RefreshCatalogAsync();
            Status = result.Succeeded ? "sing catalog 已更新。" : "规则更新失败：" + result.Error;
            RefreshSearch();
        }
        catch (Exception exception)
        {
            Status = "规则更新失败：" + exception.Message;
            _controller.ReportFailure("规则更新", "rules.update", Status, exception);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task SetVisibleAsync(bool enabled)
    {
        RuleEntryViewModel[] entries = Results.Where(entry => entry.Routing.CanEdit).ToArray();
        ControllerOperationResult result = await _controller.SetRuleSetsAsync(
            entries.Select(entry => entry.Name),
            enabled, routes: entries.ToDictionary(entry => entry.Name, entry => entry.Routing.SelectedRoute.Target));
        if (!result.Succeeded)
        {
            Status = result.Error ?? "规则批量操作失败。";
        }
        else
        {
            foreach (RuleEntryViewModel entry in entries)
            {
                entry.RefreshRoute();
            }
            Status = enabled ? "已批量加载并启用所选 SRS。" : "已批量移除所选 SRS。";
        }
        RefreshSearch();
    }

    private async Task AddManualAsync()
    {
        try
        {
            ControllerOperationResult result = await _controller.AddManualDomainAsync(ManualDomain, ManualRoute.Target);
            if (!result.Succeeded)
            {
                Status = result.Error ?? "自定义域名失败。";
                return;
            }
            ManualDomain = string.Empty;
            RefreshSearch();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _controller.ReportFailure("自定义域名", "domain.save", Status, exception);
        }
    }

    private static IReadOnlyList<SingBoxRuleCatalogEntry> PopularEntries(SingBoxRuleCatalog catalog)
    {
        string[] preferred = ["google", "youtube", "openai", "anthropic", "github", "telegram", "twitter"];
        return preferred
            .Select(name => catalog.TryGet(name, out SingBoxRuleCatalogEntry? entry) ? entry : null)
            .Where(entry => entry is not null)
            .Cast<SingBoxRuleCatalogEntry>()
            .ToArray();
    }
}
