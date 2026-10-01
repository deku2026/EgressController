using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Core.Profile;

namespace EgressController.App.ViewModels;

public sealed record RouteOptionViewModel(EgressRouteTarget Target, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<RouteOptionViewModel> Create(EgressProfileDocument profile)
        => new[]
        {
            new RouteOptionViewModel(EgressRouteTarget.DefaultAdapter,
                "默认直连 · " + (profile.Adapters.FirstOrDefault(adapter => adapter.Id == profile.DefaultAdapterId)?.Name ?? "未设置")),
        }.Concat(profile.Adapters.Select(adapter => new RouteOptionViewModel(EgressRouteTarget.ForAdapter(adapter.Id), adapter.Name + " · 直连")))
        .Concat(profile.UpstreamPorts.Count == 0 ? [] : new[] { new RouteOptionViewModel(EgressRouteTarget.Default, $"默认端口 · {profile.UpstreamPort}") })
        .Concat(profile.UpstreamPorts.Select(port => new RouteOptionViewModel(EgressRouteTarget.ForPort(port), $"端口 {port}"))).ToArray();
}

/// <summary>One transactional editor shared by application, catalog and custom-domain rows.</summary>
public sealed class RouteSelectionViewModel : ObservableObject
{
    private readonly Func<bool, EgressRouteTarget, Task<ControllerOperationResult>> _save;
    private readonly Action _changed;
    private bool _isSelected;
    private bool _isBusy;
    private bool _notifyingOptions;
    private string _status = string.Empty;
    private IReadOnlyList<RouteOptionViewModel> _options;
    private RouteOptionViewModel _selectedRoute;

    public RouteSelectionViewModel(EgressProfileDocument profile, EgressRouteTarget? target,
        Func<bool, EgressRouteTarget, Task<ControllerOperationResult>> save, Action changed)
    {
        _save = save;
        _changed = changed;
        _isSelected = target is not null;
        _options = WithCurrent(RouteOptionViewModel.Create(profile), target);
        _selectedRoute = _options.First(option => option.Target == (target ?? EgressRouteTarget.DefaultAdapter));
    }

    private static IReadOnlyList<RouteOptionViewModel> WithCurrent(IReadOnlyList<RouteOptionViewModel> options, EgressRouteTarget? target)
        => target is not null && options.All(option => option.Target != target)
            ? options.Append(new(target, "旧网卡，请重新选择")).ToArray() : options;

    public IReadOnlyList<RouteOptionViewModel> Options => _options;
    public IReadOnlyList<string> Kinds { get; } = ["网卡", "端口"];
    public IReadOnlyList<RouteOptionViewModel> VisibleOptions => _options.Where(option => option.Target.IsAdapter == _selectedRoute.Target.IsAdapter).ToArray();
    public string SelectedKind
    {
        get => _selectedRoute.Target.IsAdapter ? "网卡" : "端口";
        set
        {
            if (_isBusy || _notifyingOptions || value == SelectedKind) return;
            RouteOptionViewModel? first = _options.FirstOrDefault(option => option.Target.IsAdapter == (value == "网卡"));
            if (first is not null) SelectedRoute = first;
        }
    }
    private void NotifyRouteOptions()
    {
        _notifyingOptions = true;
        try
        {
            OnPropertyChanged(nameof(SelectedKind));
            OnPropertyChanged(nameof(VisibleOptions));
            OnPropertyChanged(nameof(SelectedRoute));
        }
        finally { _notifyingOptions = false; }
    }
    public bool CanEdit => !_isBusy;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!_isBusy && value != _isSelected)
                _ = ApplyAsync(value, _selectedRoute);
        }
    }
    public RouteOptionViewModel SelectedRoute
    {
        get => _selectedRoute;
        set
        {
            if (_isBusy || _notifyingOptions || value is null || value == _selectedRoute)
                return;
            if (_isSelected)
                _ = ApplyAsync(true, value);
            else
            {
                SetProperty(ref _selectedRoute, value);
                NotifyRouteOptions();
            }
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public void Refresh(EgressProfileDocument profile, EgressRouteTarget? target)
    {
        if (_isBusy)
            return;
        _isBusy = true;
        // Keep an unchecked row's chosen destination until the user enables it.
        EgressRouteTarget preferred = target ?? _selectedRoute.Target;
        var options = WithCurrent(RouteOptionViewModel.Create(profile), target);
        _options = options;
        _selectedRoute = options.FirstOrDefault(option => option.Target == preferred) ?? options[0];
        _isSelected = target is not null;
        OnPropertyChanged(nameof(Options));
        NotifyRouteOptions();
        OnPropertyChanged(nameof(IsSelected));
        _isBusy = false;
    }

    private async Task ApplyAsync(bool enabled, RouteOptionViewModel option)
    {
        _isBusy = true;
        OnPropertyChanged(nameof(CanEdit));
        bool previousEnabled = _isSelected;
        RouteOptionViewModel previousRoute = _selectedRoute;
        _isSelected = enabled;
        _selectedRoute = option;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(SelectedRoute));
        Status = "正在保存分流…";
        try
        {
            ControllerOperationResult result = await _save(enabled, option.Target);
            if (!result.Succeeded)
            {
                _isSelected = previousEnabled;
                _selectedRoute = previousRoute;
                Status = result.Error ?? "分流保存失败，已保留原选择。";
            }
            else
                Status = enabled ? $"已选择 {option.Label}" : "已取消单独分流";
        }
        catch (Exception exception)
        {
            _isSelected = previousEnabled;
            _selectedRoute = previousRoute;
            Status = exception.Message;
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(SelectedRoute));
            OnPropertyChanged(nameof(CanEdit));
            NotifyRouteOptions();
            _changed();
        }
    }
}
