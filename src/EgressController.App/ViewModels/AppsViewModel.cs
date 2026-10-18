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

public sealed class AppsViewModel : ObservableObject
{
    private readonly IAppController _controller;
    private readonly List<AppEntryViewModel> _all = new();
    private string _query = string.Empty;
    private string _status = "尚未扫描";
    private bool _isScanning;
    private string _discoveryNotice = "";
    public string DiscoveryNotice { get => _discoveryNotice; private set { SetProperty(ref _discoveryNotice, value); OnPropertyChanged(nameof(HasDiscoveryNotice)); } }
    public bool HasDiscoveryNotice => DiscoveryNotice.Length > 0;

    public AppsViewModel(IAppController controller)
    {
        _controller = controller;
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        SelectAllCommand = new AsyncRelayCommand(() => SetAllAsync(true));
        ClearAllCommand = new AsyncRelayCommand(() => SetAllAsync(false));
    }

    public ObservableCollection<AppEntryViewModel> Entries { get; } = new();
    public string Query
    {
        get => _query;
        set { if (SetProperty(ref _query, value ?? string.Empty)) RefreshVisible(); }
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
    public string ScanText => IsScanning ? "扫描中…" : "扫描应用";
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
        Status = "正在扫描 Windows 应用和 EXE 所有权边界…";
        try
        {
            IReadOnlyList<LaunchTarget> discovered = _controller.IsPreview ? _controller.ScanTargets() : await Task.Run(_controller.ScanTargets);
            foreach (AppEntryViewModel entry in _all)
                entry.Dispose();
            _all.Clear();
            foreach (LaunchTarget target in discovered)
                _all.Add(new AppEntryViewModel(_controller, target, RefreshVisible));
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
        AppEntryViewModel[] targets = _all.Where(entry => entry.CanManage && entry.Routing.CanEdit).ToArray();
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
            Status = enabled ? "已启用应用分流，新选择默认走默认网卡。" : "已清空应用分流选择。";
        }
        RefreshVisible();
    }

    private void RefreshVisible()
    {
        string query = _query.Trim();
        Entries.Clear();
        foreach (AppEntryViewModel entry in _all.Where(entry => query.Length == 0
            || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.KindText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Command.Contains(query, StringComparison.OrdinalIgnoreCase)))
            Entries.Add(entry);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(HasEntries));
    }

    public void RefreshStatuses()
    {
        foreach (AppEntryViewModel entry in _all)
            entry.RefreshRoute();
        OnPropertyChanged(nameof(Summary));
    }
}
