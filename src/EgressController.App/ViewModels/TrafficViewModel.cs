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

public sealed class TrafficViewModel : ObservableObject
{
    private readonly IAppController _controller;
    private string _totalInput = string.Empty;
    private string _remainingInput = string.Empty;
    private string _status = string.Empty;
    private bool _inputsInitialized;
    private EgressQuotaSnapshot _quota = new(0, 0, 0, DateTimeOffset.MinValue);
    private TrafficSnapshot? _traffic;

    public TrafficViewModel(IAppController controller)
    {
        _controller = controller;
        SaveQuotaCommand = new AsyncRelayCommand(SaveQuotaAsync);
        ClearUsageCommand = new RelayCommand(ClearUsage);
        Refresh();
    }

    public string TotalInput { get => _totalInput; set => SetProperty(ref _totalInput, value ?? string.Empty); }
    public string RemainingInput { get => _remainingInput; set => SetProperty(ref _remainingInput, value ?? string.Empty); }
    public string Total => TrafficFormat.Bytes(_quota.TotalBytes);
    public string Remaining => IsConfigured ? TrafficFormat.Bytes(_quota.RemainingBytes) : "—";
    public string Used => TrafficFormat.Bytes(_quota.UsedBytes);
    public string UsedSummary => $"本地已统计：{Used}";
    public bool IsConfigured => _quota.TotalBytes > 0;
    public string UsedPercentText => IsConfigured ? $"已使用 {_quota.UsedPercent:0.##}%" : "未设置";
    public double UsedPercent => _quota.UsedPercent;
    public string RemainingPercentText => IsConfigured ? $"剩余 {_quota.RemainingPercent:0.##}%" : "请先填写套餐";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string MonitorStatus => _controller.DiagnosticsStatus;
    public string LastUpdated => TrafficFormat.UpdatedAt(_controller.TrafficUpdatedAtUtc);
    public string CurrentRate => $"↑ {TrafficUnits.Rate(_traffic?.UploadRate)} · ↓ {TrafficUnits.Rate(_traffic?.DownloadRate)}";
    public string Active => _controller.ConnectionHistory.ActiveCount.ToString("N0");
    public string Note => "仅统计走 默认网卡 的连接；保存套餐后会把本地累计用量从 0 开始计算。";
    public IAsyncRelayCommand SaveQuotaCommand { get; }
    public RelayCommand ClearUsageCommand { get; }

    public void Refresh()
    {
        _quota = _controller.Quota;
        _traffic = _controller.TrafficSnapshot;
        if (!_inputsInitialized)
        {
            _inputsInitialized = true;
            if (_quota.TotalBytes > 0) { TotalInput = QuotaFormat.Gigabytes(_quota.TotalBytes); RemainingInput = QuotaFormat.Gigabytes(_quota.RemainingBytes); }
        }
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(Used));
        OnPropertyChanged(nameof(UsedSummary));
        OnPropertyChanged(nameof(UsedPercent));
        OnPropertyChanged(nameof(UsedPercentText));
        OnPropertyChanged(nameof(RemainingPercentText));
        OnPropertyChanged(nameof(CurrentRate));
        OnPropertyChanged(nameof(Active));
        OnPropertyChanged(nameof(MonitorStatus));
        OnPropertyChanged(nameof(LastUpdated));
    }

    private async Task SaveQuotaAsync()
    {
        try
        {
            if (!QuotaFormat.TryParseGigabytes(TotalInput, out decimal total)
                || !QuotaFormat.TryParseGigabytes(RemainingInput, out decimal remaining))
            {
                Status = "请输入有效的套餐总量和剩余量（GiB）。";
                return;
            }

            _controller.ConfigureQuota(total, remaining);
            Status = "套餐已保存，本地 默认网卡 用量已从 0 开始统计。";
            Refresh();
        }
        catch (Exception exception)
        {
            Status = "保存套餐失败：" + exception.Message;
            _controller.ReportFailure("流量套餐", "quota.save", Status, exception);
        }
        await Task.CompletedTask;
    }

    private void ClearUsage()
    {
        _controller.ClearQuotaUsage();
        Status = "已清空本地 默认网卡 用量统计。";
        Refresh();
    }
}
