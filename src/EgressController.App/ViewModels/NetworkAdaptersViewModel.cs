using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Windows.Network;

namespace EgressController.App.ViewModels;

public sealed record DnsAdapterOption(string? Id, string Display);
public sealed record RoleAdapterOption(string Id, string Display);

public sealed class NetworkAdaptersViewModel : ObservableObject
{
    private readonly Func<EgressProfileDocument> _profile;
    private readonly Func<IReadOnlyList<NetworkAdapterInfo>> _adapters;
    private readonly Func<bool> _updating;
    private readonly Func<string?, string?, Task<ControllerOperationResult>> _saveRoles;
    private readonly Func<string?, Task<ControllerOperationResult>> _saveDns;
    private string _signature = "";
    private bool _refreshing, _busy, _dirty, _showAll;
    private string _status = "", _configurationStatus = "";
    private RoleAdapterOption? _direct, _proxy;
    private DnsAdapterOption? _dns;

    public NetworkAdaptersViewModel(AppController controller) : this(
        () => controller.Profile, () => controller.Adapters,
        controller.SetAdapterRolesAsync, controller.SetDnsAdapterAsync, () => controller.IsUpdatingProfile) { }

    // Injectable without starting the controller, TUN or any native network operation.
    public NetworkAdaptersViewModel(Func<EgressProfileDocument> profile, Func<IReadOnlyList<NetworkAdapterInfo>> adapters,
        Func<string?, string?, Task<ControllerOperationResult>> saveRoles,
        Func<string?, Task<ControllerOperationResult>> saveDns, Func<bool>? updating = null)
    {
        _profile = profile; _adapters = adapters; _saveRoles = saveRoles; _saveDns = saveDns;
        _updating = updating ?? (() => false);
        SaveCommand = new AsyncRelayCommand(SaveRolesAsync);
        SwapCommand = new RelayCommand(() =>
        {
            (_direct, _proxy) = (_proxy, _direct); _dirty = true;
            OnPropertyChanged(nameof(SelectedDirect)); OnPropertyChanged(nameof(SelectedProxy));
            Status = "网卡已交换，点击保存两个出口后生效。";
        });
        CancelCommand = new RelayCommand(() => { _dirty = false; _signature = ""; Status = ""; Refresh(); });
        Refresh();
    }

    public ObservableCollection<RoleAdapterOption> Available { get; } = [];
    public ObservableCollection<DnsAdapterOption> DnsOptions { get; } = [];
    public bool CanEdit => !_busy;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ConfigurationStatus { get => _configurationStatus; private set => SetProperty(ref _configurationStatus, value); }
    public bool ShowAll
    {
        get => _showAll;
        set { if (SetProperty(ref _showAll, value)) { _signature = ""; Refresh(); } }
    }
    public RoleAdapterOption? SelectedDirect
    {
        get => _direct;
        set { if (!_refreshing && !_busy && value is not null && SetProperty(ref _direct, value)) MarkDirty(); }
    }
    public RoleAdapterOption? SelectedProxy
    {
        get => _proxy;
        set { if (!_refreshing && !_busy && value is not null && SetProperty(ref _proxy, value)) MarkDirty(); }
    }
    private void MarkDirty() { _dirty = true; Status = "选择尚未保存。"; }
    public DnsAdapterOption? SelectedDns
    {
        get => _dns;
        set
        {
            if (_refreshing || _busy || value is null || value == _dns) return;
            SetProperty(ref _dns, value);
            _ = SaveAsync(() => _saveDns(value.Id), roles: false);
        }
    }
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand SwapCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public void Refresh()
    {
        if (_busy || _updating()) return;
        EgressProfileDocument profile = _profile();
        IReadOnlyList<NetworkAdapterInfo> adapters = _adapters();
        string? directId = _dirty ? _direct?.Id : profile.DefaultAdapterId;
        string? proxyId = _dirty ? _proxy?.Id : profile.ProxyAdapterId;
        string signature = $"{directId}|{proxyId}|{profile.DnsAdapterId}|{profile.AdapterConfigurationError}|{ShowAll}|"
            + string.Join('|', adapters.Select(adapter => $"{adapter.Identity.Guid}:{adapter.Identity.NameSnapshot}:{adapter.IsUp}:{adapter.AddressState}:{adapter.Description}"));
        if (_signature == signature) return;
        _refreshing = true;
        try
        {
            Available.Clear(); DnsOptions.Clear();
            foreach (var adapter in adapters.Where(adapter => NetworkEnvironmentResolver.IsSelectable(adapter)
                && (ShowAll || NetworkEnvironmentResolver.IsRecommended(adapter)
                    || adapter.Identity.Guid.ToString("D") == directId || adapter.Identity.Guid.ToString("D") == proxyId))
                .OrderByDescending(adapter => adapter.IsUp).ThenBy(adapter => adapter.Identity.NameSnapshot))
            {
                string state = !adapter.IsUp ? "已断开" : !NetworkEnvironmentResolver.ToSelection(adapter).IsReady ? "没有 IP" : "已连接";
                Available.Add(new(adapter.Identity.Guid.ToString("D"), $"{adapter.Identity.NameSnapshot} · {state} · {adapter.Description}"));
            }
            foreach (string id in new[] { directId, proxyId }.OfType<string>().Distinct())
                if (Available.All(option => option.Id != id)) Available.Add(new(id, $"未连接 / 未找到 · {id}"));
            _direct = Available.FirstOrDefault(option => option.Id == directId);
            _proxy = Available.FirstOrDefault(option => option.Id == proxyId);
            OnPropertyChanged(nameof(SelectedDirect)); OnPropertyChanged(nameof(SelectedProxy));
            DnsOptions.Add(new(null, "默认直连 · ESIM-家宽"));
            foreach (var adapter in profile.Adapters) DnsOptions.Add(new(adapter.Id, adapter.Name));
            if (profile.DnsAdapterId is string dnsId && DnsOptions.All(option => option.Id != dnsId))
                DnsOptions.Add(new(dnsId, "旧网卡，请重新选择"));
            _dns = DnsOptions.First(option => option.Id == profile.DnsAdapterId);
            OnPropertyChanged(nameof(SelectedDns));
            ConfigurationStatus = profile.AdapterConfigurationError ?? "两个出口已配置；更换网卡后，相应应用和域名规则跟随该出口。";
            _signature = signature;
        }
        finally { _refreshing = false; }
    }

    private Task SaveRolesAsync()
    {
        if (_direct is null || _proxy is null) { Status = "请选择两张不同的实际网卡。"; return Task.CompletedTask; }
        if (_direct.Id == _proxy.Id) { Status = "两个出口不能使用同一张网卡。"; return Task.CompletedTask; }
        return SaveAsync(() => _saveRoles(_direct.Id, _proxy.Id), roles: true);
    }
    private async Task SaveAsync(Func<Task<ControllerOperationResult>> save, bool roles)
    {
        if (_busy) return;
        _busy = true; OnPropertyChanged(nameof(CanEdit));
        try
        {
            var result = await save();
            if (result.Succeeded && roles) _dirty = false;
            Status = result.Succeeded ? "网卡配置已保存。" : result.Error ?? "网卡配置失败。";
        }
        catch (Exception exception) { Status = exception.Message; }
        finally { _busy = false; _signature = ""; OnPropertyChanged(nameof(CanEdit)); Refresh(); }
    }
}
