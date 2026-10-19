using System.Collections.ObjectModel;
using EgressController.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Core.Profile;

namespace EgressController.App.ViewModels;

public sealed class DashboardViewModel(IAppController controller, TrafficViewModel? usage = null) : ObservableObject
{
    private readonly TrafficViewModel _usage = usage ?? new(controller);
    private TrafficSnapshot _snapshot = new(null, null, null, [], [], 1024);
    public IReadOnlyList<RatePoint> UploadSamples => _snapshot.Upload;
    public IReadOnlyList<RatePoint> DownloadSamples => _snapshot.Download;
    public double ScaleMaximum => _snapshot.ScaleMaximum;
    public bool HasCurrentRate => _snapshot.HasCurrentRate;
    public bool ObservationFresh => _snapshot.ObservationFresh;
    public string FreshnessLabel => ObservationFresh ? "最新快照" : _snapshot.ObservedAtUtc is null ? "等待采样" : "上次快照";
    public string TelemetryStatus => HasCurrentRate ? "最近 60 秒 · 实时速度"
        : "等待有效采样";
    public string SampleTime => _snapshot.ObservedAtUtc is { } at ? $"最近采样 {at.ToLocalTime():HH:mm:ss}" : "尚未收到内核统计";
    public IReadOnlyList<ApplicationTrafficRow> Applications => _usage.TopApplications;
    public string UploadRate => TrafficUnits.Rate(_snapshot.UploadRate);
    public string DownloadRate => TrafficUnits.Rate(_snapshot.DownloadRate);
    public string UploadValue => UploadRate.Split(' ')[0];
    public string UploadUnit => UploadRate.Contains(' ') ? " " + UploadRate.Split(' ')[1] : "";
    public string DownloadValue => DownloadRate.Split(' ')[0];
    public string DownloadUnit => DownloadRate.Contains(' ') ? " " + DownloadRate.Split(' ')[1] : "";
    public string TotalTraffic => TrafficFormat.Bytes(TrafficFormat.AddCounters(controller.TrafficUp, controller.TrafficDown));
    public string UploadTotal => TrafficFormat.Bytes(controller.TrafficUp);
    public string DownloadTotal => TrafficFormat.Bytes(controller.TrafficDown);
    public string ActiveCount => controller.ConnectionHistory.ActiveCount.ToString();
    public string ProtectedCount => controller.Profile.Applications.Count.ToString();
    public string ExecutableCount => controller.Profile.Applications.SelectMany(item => item.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString();
    public string PortCount => controller.Profile.UpstreamPorts.Count.ToString();
    public string DirectAdapter => Adapter(controller.Profile.DefaultAdapterId);
    public string ProxyAdapter => Adapter(controller.Profile.ProxyAdapterId);
    public string DirectLatency => controller.DirectDelayMilliseconds?.ToString() ?? "—";
    public string CheckTime => controller.NetworkLastCheckedAtUtc is {} at ? $"检测于 {at.ToLocalTime():HH:mm:ss}" : "等待首次检测";
    public string ProxyLatency => controller.ProxyDelayMilliseconds?.ToString() ?? "—";
    public bool Ready => controller.Protection.Ready;
    public string StatusTitle => Ready ? "应用保护已就绪" : "正在保护所选应用";
    public string StatusDetail => controller.Protection.Reason;
    public bool HasTraffic => Applications.Count > 0;
    public bool IsEmpty => Applications.Count == 0;
    private string Adapter(string? id) => controller.Adapters.FirstOrDefault(item => item.Identity.Guid.ToString() == id)?.Identity.NameSnapshot ?? "尚未选择";
    public void Refresh()
    {
        _snapshot = controller.TrafficSnapshot;
        _usage.Refresh();
        OnPropertyChanged(nameof(Applications));
        foreach (string name in new[] { nameof(UploadSamples), nameof(DownloadSamples), nameof(ScaleMaximum), nameof(HasCurrentRate), nameof(ObservationFresh), nameof(FreshnessLabel), nameof(TelemetryStatus), nameof(SampleTime), nameof(UploadRate), nameof(DownloadRate), nameof(UploadValue), nameof(UploadUnit), nameof(DownloadValue), nameof(DownloadUnit), nameof(TotalTraffic), nameof(UploadTotal), nameof(DownloadTotal),
            nameof(ActiveCount), nameof(ProtectedCount), nameof(ExecutableCount), nameof(PortCount), nameof(DirectAdapter), nameof(ProxyAdapter), nameof(DirectLatency), nameof(CheckTime), nameof(ProxyLatency), nameof(Ready),
            nameof(StatusTitle), nameof(StatusDetail), nameof(HasTraffic), nameof(IsEmpty) }) OnPropertyChanged(name);
    }
}
