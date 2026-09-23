using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Core.Profile;

namespace EgressController.App.ViewModels;

public sealed record RouteOptionViewModel(EgressRouteTarget Target, string Label)
{
    public static IReadOnlyList<RouteOptionViewModel> Create(EgressProfileDocument profile)
        => new[]
        {
            new RouteOptionViewModel(EgressRouteTarget.Esim, "eSIM"),
            new RouteOptionViewModel(EgressRouteTarget.Default, $"默认 · {profile.UpstreamPort}"),
        }.Concat(profile.UpstreamPorts.Select(port =>
            new RouteOptionViewModel(EgressRouteTarget.ForPort(port), $"端口 {port}"))).ToArray();
}

/// <summary>One transactional editor shared by application, catalog and custom-domain rows.</summary>
public sealed class RouteSelectionViewModel : ObservableObject
{
    private readonly Func<bool, EgressRouteTarget, Task<ControllerOperationResult>> _save;
    private readonly Action _changed;
    private bool _isSelected;
    private bool _isBusy;
    private string _status = string.Empty;
    private IReadOnlyList<RouteOptionViewModel> _options;
    private RouteOptionViewModel _selectedRoute;

    public RouteSelectionViewModel(EgressProfileDocument profile, EgressRouteTarget? target,
        Func<bool, EgressRouteTarget, Task<ControllerOperationResult>> save, Action changed)
    {
        _save = save;
        _changed = changed;
        _isSelected = target is not null;
        _options = RouteOptionViewModel.Create(profile);
        _selectedRoute = _options.First(option => option.Target == (target ?? EgressRouteTarget.Esim));
    }

    public IReadOnlyList<RouteOptionViewModel> Options => _options;
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
            if (_isBusy || value is null || value == _selectedRoute)
                return;
            if (_isSelected)
                _ = ApplyAsync(true, value);
            else
                SetProperty(ref _selectedRoute, value);
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
        var options = RouteOptionViewModel.Create(profile);
        _options = options;
        _selectedRoute = options.FirstOrDefault(option => option.Target == preferred) ?? options[0];
        _isSelected = target is not null;
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(SelectedRoute));
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
            _changed();
        }
    }
}
