using System.Collections.ObjectModel;
using EgressController.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Core.Profile;

namespace EgressController.App.ViewModels;

public sealed record ApplicationTrafficRow(Services.ProcessVisual Appearance, string Amount, double Share)
{ public string Name => Appearance.Name; public string ShareLabel => $"{Share:0.#}%"; }

public sealed class DashboardViewModel(IAppController controller) : ObservableObject
{
    private TrafficSnapshot _snapshot = new(null, null, null, [], [], 1024);
    public IReadOnlyList<RatePoint> UploadSamples => _snapshot.Upload;
    public IReadOnlyList<RatePoint> DownloadSamples => _snapshot.Download;
    public double ScaleMaximum => _snapshot.ScaleMaximum;
    public bool HasCurrentRate => _snapshot.HasCurrentRate;
    public bool ObservationFresh => _snapshot.ObservationFresh;
    public string FreshnessLabel => ObservationFresh ? "最新快照" : _snapshot.ObservedAtUtc is null ? "等待采样" : "上次快照";
    public string TelemetryStatus => HasCurrentRate ? "最近 60 秒 · 上传与下载使用同一刻度"
        : "暂无有效网速，缺失采样保持空白";
    public string SampleTime => _snapshot.ObservedAtUtc is { } at ? $"最近采样 {at.ToLocalTime():HH:mm:ss}" : "尚未收到内核统计";
    public ObservableCollection<ApplicationTrafficRow> Applications { get; } = [];
    public string UploadRate => TrafficUnits.Rate(_snapshot.UploadRate);
    public string DownloadRate => TrafficUnits.Rate(_snapshot.DownloadRate);
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
        var groups = controller.ConnectionHistory.ActiveSnapshot().GroupBy(item => controller.Visuals.ForProcess(item.ProcessPath).Key)
            .Select(group => (Appearance: controller.Visuals.ForProcess(group.First().ProcessPath), Bytes: group.Aggregate(0L,
                (sum, item) => TrafficFormat.AddCounters(sum, TrafficFormat.AddCounters(item.Upload, item.Download)))))
            .OrderByDescending(item => item.Bytes).ToArray();
        double total = Math.Max(1, groups.Sum(item => (double)item.Bytes));
        Applications.Clear();
        foreach (var item in groups.Take(5)) Applications.Add(new(item.Appearance, TrafficFormat.Bytes(item.Bytes), (double)item.Bytes / total * 100));
        foreach (string name in new[] { nameof(UploadSamples), nameof(DownloadSamples), nameof(ScaleMaximum), nameof(HasCurrentRate), nameof(ObservationFresh), nameof(FreshnessLabel), nameof(TelemetryStatus), nameof(SampleTime), nameof(UploadRate), nameof(DownloadRate), nameof(TotalTraffic), nameof(UploadTotal), nameof(DownloadTotal),
            nameof(ActiveCount), nameof(ProtectedCount), nameof(ExecutableCount), nameof(PortCount), nameof(DirectAdapter), nameof(ProxyAdapter), nameof(DirectLatency), nameof(ProxyLatency), nameof(Ready),
            nameof(StatusTitle), nameof(StatusDetail), nameof(HasTraffic), nameof(IsEmpty) }) OnPropertyChanged(name);
    }
}
