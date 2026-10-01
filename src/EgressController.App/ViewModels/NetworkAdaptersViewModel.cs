using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Profile;
using EgressController.Windows.Network;

namespace EgressController.App.ViewModels;

public sealed record DnsAdapterOption(string? Id, string Display);

public sealed class NetworkAdaptersViewModel : ObservableObject
{
    private readonly AppController _controller;
    private string _signature = "";
    private bool _refreshing;
    private bool _busy;
    private string _name = "";
    private string _status = "";
    private AdapterOptionViewModel? _available;
    private DnsAdapterOption? _dns;
    public NetworkAdaptersViewModel(AppController controller)
    {
        _controller = controller;
        AddCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedAvailable is null) { Status = "请先选择实际网卡。"; return; }
            string name = string.IsNullOrWhiteSpace(Name) ? $"网卡{Entries.Count + 1} · {SelectedAvailable.Name}" : Name;
            await SaveAsync(() => controller.AddAdapterAsync(SelectedAvailable.Guid, name));
        });
    }
    public ObservableCollection<AdapterEntryViewModel> Entries { get; } = [];
    public ObservableCollection<AdapterOptionViewModel> Available { get; } = [];
    public ObservableCollection<DnsAdapterOption> DnsOptions { get; } = [];
    public bool CanEdit => !_busy;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public AdapterOptionViewModel? SelectedAvailable { get => _available; set => SetProperty(ref _available, value); }
    public DnsAdapterOption? SelectedDns
    {
        get => _dns;
        set
        {
            if (_refreshing || _busy || value is null || value == _dns) return;
            SetProperty(ref _dns, value);
            _ = SaveAsync(() => _controller.SetDnsAdapterAsync(value.Id));
        }
    }
    public IAsyncRelayCommand AddCommand { get; }
    public void Refresh()
    {
        if (_busy || _controller.IsUpdatingProfile) return;
        EgressProfileDocument profile = _controller.Profile;
        string signature = string.Join('|', profile.Adapters.Select(adapter => adapter.Id + adapter.Name))
            + profile.DefaultAdapterId + profile.DnsAdapterId
            + string.Join('|', _controller.Adapters.Select(adapter => $"{adapter.Identity.Guid}:{adapter.Identity.NameSnapshot}:{adapter.IsUp}:{adapter.AddressState}"));
        if (_signature == signature) return;
        _refreshing = true;
        try
        {
            Guid? selected = _available?.Guid;
            Available.Clear(); Entries.Clear(); DnsOptions.Clear();
            foreach (var adapter in _controller.Adapters.Where(NetworkEnvironmentResolver.IsSelectable)
                .Where(adapter => profile.Adapters.All(item => item.Id != adapter.Identity.Guid.ToString("D"))))
                Available.Add(new(adapter));
            _available = Available.FirstOrDefault(adapter => adapter.Guid == selected) ?? Available.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedAvailable));
            foreach (EgressAdapterDefinition adapter in profile.Adapters)
            {
                var physical = _controller.Adapters.FirstOrDefault(item => item.Identity.Guid.ToString("D") == adapter.Id);
                string state = physical is null ? "未连接 / 未找到" : $"{physical.Identity.NameSnapshot} · {(physical.IsUp ? "已连接" : "已断开")} · {physical.AddressState}";
                Entries.Add(new(adapter, profile, state,
                    () => SaveAsync(() => _controller.SetDefaultAdapterAsync(adapter.Id)),
                    () => SaveAsync(() => _controller.RemoveAdapterAsync(adapter.Id)),
                    name => SaveAsync(() => _controller.RenameAdapterAsync(adapter.Id, name))));
            }
            DnsOptions.Add(new(null, "默认网卡 · " + (profile.Adapters.FirstOrDefault(adapter => adapter.Id == profile.DefaultAdapterId)?.Name ?? "未设置")));
            foreach (EgressAdapterDefinition adapter in profile.Adapters) DnsOptions.Add(new(adapter.Id, adapter.Name));
            _dns = DnsOptions.FirstOrDefault(option => option.Id == profile.DnsAdapterId);
            OnPropertyChanged(nameof(SelectedDns));
            _signature = signature;
        }
        finally { _refreshing = false; }
    }
    private async Task SaveAsync(Func<Task<ControllerOperationResult>> save)
    {
        if (_busy) return;
        _busy = true; OnPropertyChanged(nameof(CanEdit));
        try
        {
            var result = await save();
            Status = result.Succeeded ? "网卡配置已保存。" : result.Error ?? "网卡配置失败。";
        }
        catch (Exception exception) { Status = exception.Message; }
        finally { _busy = false; _signature = ""; OnPropertyChanged(nameof(CanEdit)); Refresh(); }
    }
}

public sealed class AdapterEntryViewModel : ObservableObject
{
    private string _name;
    public AdapterEntryViewModel(EgressAdapterDefinition adapter, EgressProfileDocument profile, string state,
        Func<Task> setDefault, Func<Task> remove, Func<string, Task> rename)
    {
        _name = adapter.Name; State = state; IsDefault = adapter.Id == profile.DefaultAdapterId;
        RemovalHint = profile.AdapterRemovalError(adapter.Id) ?? "移除网卡";
        CanRemove = profile.AdapterRemovalError(adapter.Id) is null;
        SetDefaultCommand = new AsyncRelayCommand(setDefault);
        RemoveCommand = new AsyncRelayCommand(remove);
        RenameCommand = new AsyncRelayCommand(() => rename(Name));
    }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string State { get; }
    public bool IsDefault { get; }
    public bool CanSetDefault => !IsDefault;
    public string DefaultLabel => IsDefault ? "✓ 默认网卡" : "设为默认";
    public bool CanRemove { get; }
    public string RemovalHint { get; }
    public IAsyncRelayCommand SetDefaultCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
}
