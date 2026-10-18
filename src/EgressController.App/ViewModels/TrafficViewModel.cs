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

    public TrafficViewModel(IAppController controller)
    {
        _controller = controller;
        SaveQuotaCommand = new AsyncRelayCommand(SaveQuotaAsync);
        ClearUsageCommand = new RelayCommand(ClearUsage);
        Refresh();
    }

    public string TotalInput { get => _totalInput; set => SetProperty(ref _totalInput, value ?? string.Empty); }
    public string RemainingInput { get => _remainingInput; set => SetProperty(ref _remainingInput, value ?? string.Empty); }
    public string Total => TrafficFormat.Bytes(_controller.Quota.TotalBytes);
    public string Remaining => TrafficFormat.Bytes(_controller.Quota.RemainingBytes);
    public string Used => TrafficFormat.Bytes(_controller.Quota.UsedBytes);
    public string UsedSummary => $"本地已统计：{Used}";
    public string UsedPercentText => $"已使用 {_controller.Quota.UsedPercent:0.##}%";
    public double UsedPercent => _controller.Quota.UsedPercent;
    public string RemainingPercentText => $"剩余 {_controller.Quota.RemainingPercent:0.##}%";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string MonitorStatus => _controller.DiagnosticsStatus;
    public string LastUpdated => TrafficFormat.UpdatedAt(_controller.TrafficUpdatedAtUtc);
    public string CurrentRate => $"↑ {TrafficFormat.Rate(_controller.TrafficUpRate)} · ↓ {TrafficFormat.Rate(_controller.TrafficDownRate)}";
    public string Active => _controller.ConnectionHistory.ActiveCount.ToString("N0");
    public string Note => "仅统计走 默认网卡 的连接；保存套餐后会把本地累计用量从 0 开始计算。";
    public IAsyncRelayCommand SaveQuotaCommand { get; }
    public RelayCommand ClearUsageCommand { get; }

    public void Refresh()
    {
        EgressQuotaSnapshot quota = _controller.Quota;
        if (string.IsNullOrWhiteSpace(TotalInput) && quota.TotalBytes > 0)
            TotalInput = QuotaFormat.Gigabytes(quota.TotalBytes);
        if (string.IsNullOrWhiteSpace(RemainingInput) && quota.TotalBytes > 0)
            RemainingInput = QuotaFormat.Gigabytes(quota.RemainingBytes);
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
                Status = "请输入有效的套餐总量和剩余量（GB）。";
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
