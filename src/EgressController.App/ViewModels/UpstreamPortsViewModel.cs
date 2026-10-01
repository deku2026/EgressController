using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EgressController.Core.Profile;
using EgressController.Windows.Process;

namespace EgressController.App.ViewModels;

public sealed class UpstreamPortsViewModel : ObservableObject
{
    private readonly AppController _controller;
    private EgressProfileDocument? _displayed;
    private string _portText = string.Empty;
    private string _status = string.Empty;
    private bool _busy;

    public UpstreamPortsViewModel(AppController controller)
    {
        _controller = controller;
        AddCommand = new AsyncRelayCommand(AddAsync);
        Refresh();
    }

    public ObservableCollection<UpstreamPortEntryViewModel> Entries { get; } = [];
    public string PortText { get => _portText; set => SetProperty(ref _portText, value ?? string.Empty); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool CanEdit => !_busy;
    public IAsyncRelayCommand AddCommand { get; }

    public void Refresh()
    {
        EgressProfileDocument profile = _controller.Profile;
        if (_busy || _controller.IsUpdatingProfile)
            return;
        if (ReferenceEquals(_displayed, profile)) { RefreshBindings(); return; }
        _displayed = profile;
        Entries.Clear();
        foreach (int port in profile.UpstreamPorts)
            Entries.Add(new UpstreamPortEntryViewModel(profile, port,
                () => ApplyAsync(() => _controller.SetDefaultUpstreamPortAsync(port)),
                () => ApplyAsync(() => _controller.RemoveUpstreamPortAsync(port))));
        RefreshBindings();
    }

    private void RefreshBindings()
    {
        string alias = _controller.Adapters.FirstOrDefault(adapter => adapter.Identity.Guid.ToString("D") == _controller.Profile.ProxyAdapterId)?.Identity.NameSnapshot ?? "未连接 / 未配置";
        foreach (var entry in Entries)
            entry.UpdateBinding(_controller.ProxyBindings.Ports.FirstOrDefault(binding => binding.Port == entry.Port), alias);
    }

    private async Task AddAsync()
    {
        if (!int.TryParse(PortText.Trim(), out int port) || port is < 1 or > 65535)
        {
            Status = "请输入 1-65535 的整数端口。";
            return;
        }
        if (_controller.Profile.UpstreamPorts.Contains(port))
        {
            Status = $"端口 {port} 已在列表中。";
            return;
        }
        if (await ApplyAsync(() => _controller.AddUpstreamPortAsync(port)))
            PortText = string.Empty;
    }

    private async Task<bool> ApplyAsync(Func<Task<ControllerOperationResult>> save)
    {
        if (_busy)
            return false;
        _busy = true;
        OnPropertyChanged(nameof(CanEdit));
        Status = "正在保存端口…";
        try
        {
            ControllerOperationResult result = await save();
            Status = result.Succeeded ? "端口已保存。" : result.Error ?? "端口保存失败。";
            return result.Succeeded;
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            return false;
        }
        finally
        {
            _busy = false;
            OnPropertyChanged(nameof(CanEdit));
            Refresh();
        }
    }
}

public sealed class UpstreamPortEntryViewModel : ObservableObject
{
    public UpstreamPortEntryViewModel(EgressProfileDocument profile, int port, Func<Task> setDefault, Func<Task> remove)
    {
        Port = port;
        Endpoint = $"127.0.0.1:{port}";
        IsDefault = port == profile.UpstreamPort;
        RemovalHint = profile.PortRemovalError(port) ?? "移除这个端口";
        CanRemove = profile.PortRemovalError(port) is null;
        SetDefaultCommand = new AsyncRelayCommand(setDefault);
        RemoveCommand = new AsyncRelayCommand(remove);
    }

    private string _bindingSummary = "等待识别监听进程", _ownerPath = "";
    public int Port { get; }
    public string BindingSummary { get => _bindingSummary; private set => SetProperty(ref _bindingSummary, value); }
    public string OwnerPath { get => _ownerPath; private set => SetProperty(ref _ownerPath, value); }
    public void UpdateBinding(ProxyPortBinding? binding, string adapterAlias)
    {
        BindingSummary = binding?.IsReady == true
            ? $"PID {binding.Owners[0].ProcessId} → Proxy-代理 · {adapterAlias}"
            : binding?.Error ?? "等待识别监听进程";
        OwnerPath = string.Join("\n", binding?.Owners.Select(owner => owner.CanonicalExecutablePath ?? $"PID {owner.ProcessId} · 路径未知") ?? []);
    }
    public string Endpoint { get; }
    public bool IsDefault { get; }
    public bool CanSetDefault => !IsDefault;
    public string DefaultLabel => IsDefault ? "✓ 默认" : "设为默认";
    public bool CanRemove { get; }
    public string RemovalHint { get; }
    public IAsyncRelayCommand SetDefaultCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
}
