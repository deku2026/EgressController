using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.Core.Profile;

namespace EgressController.App.ViewModels;

public sealed record ApplicationTrafficRow(string Name, string Amount, double Share, string Initial);

public sealed class DashboardViewModel(IAppController controller) : ObservableObject
{
    private readonly List<double> _upload = [], _download = [];
    public IReadOnlyList<double> UploadSamples { get; private set; } = [];
    public IReadOnlyList<double> DownloadSamples { get; private set; } = [];
    public ObservableCollection<ApplicationTrafficRow> Applications { get; } = [];
    public string UploadRate => TrafficFormat.Rate(controller.TrafficUpRate);
    public string DownloadRate => TrafficFormat.Rate(controller.TrafficDownRate);
    public string TotalTraffic => TrafficFormat.Bytes(TrafficFormat.AddCounters(controller.TrafficUp, controller.TrafficDown));
    public string UploadTotal => TrafficFormat.Bytes(controller.TrafficUp);
    public string DownloadTotal => TrafficFormat.Bytes(controller.TrafficDown);
    public string ActiveCount => controller.ConnectionHistory.ActiveCount.ToString();
    public string ProtectedCount => controller.Profile.Applications.Count.ToString();
    public string ExecutableCount => controller.Profile.Applications.SelectMany(item => item.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString();
    public string PortCount => controller.Profile.UpstreamPorts.Count.ToString();
    public string DirectAdapter => Adapter(controller.Profile.DefaultAdapterId);
    public string ProxyAdapter => Adapter(controller.Profile.ProxyAdapterId);
    public string DirectLatency => Latency(controller.DirectInternetStatus);
    public string ProxyLatency => Latency(controller.ProxyInternetStatus);
    public bool Ready => controller.Protection.Ready;
    public string StatusTitle => Ready ? "应用保护已就绪" : "正在保护所选应用";
    public string StatusDetail => controller.Protection.Reason;
    public bool HasTraffic => Applications.Count > 0;
    public bool IsEmpty => Applications.Count == 0;
    private string Adapter(string? id) => controller.Adapters.FirstOrDefault(item => item.Identity.Guid.ToString() == id)?.Identity.NameSnapshot ?? "尚未选择";
    private static string Latency(string status)
    {
        var match = Regex.Match(status, @"(\d+)\s*ms", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        return match.Success ? match.Groups[1].Value : "—";
    }
    public void Refresh()
    {
        _upload.Add(controller.TrafficUpRate); _download.Add(controller.TrafficDownRate);
        if (_upload.Count > 60) _upload.RemoveAt(0);
        if (_download.Count > 60) _download.RemoveAt(0);
        UploadSamples = _upload.ToArray(); DownloadSamples = _download.ToArray();
        var groups = controller.ConnectionHistory.ActiveSnapshot().GroupBy(item => item.ProcessPath ?? "未识别应用")
            .Select(group => (Name: Path.GetFileNameWithoutExtension(group.Key), Bytes: group.Aggregate(0L,
                (sum, item) => TrafficFormat.AddCounters(sum, TrafficFormat.AddCounters(item.Upload, item.Download)))))
            .OrderByDescending(item => item.Bytes).Take(5).ToArray();
        double maximum = Math.Max(1, groups.Select(item => (double)item.Bytes).DefaultIfEmpty(1).Max());
        Applications.Clear();
        foreach (var item in groups) Applications.Add(new(item.Name, TrafficFormat.Bytes(item.Bytes), item.Bytes * 100 / maximum, item.Name.Length == 0 ? "?" : item.Name[..1]));
        foreach (string name in new[] { nameof(UploadSamples), nameof(DownloadSamples), nameof(UploadRate), nameof(DownloadRate), nameof(TotalTraffic), nameof(UploadTotal), nameof(DownloadTotal),
            nameof(ActiveCount), nameof(ProtectedCount), nameof(ExecutableCount), nameof(PortCount), nameof(DirectAdapter), nameof(ProxyAdapter), nameof(DirectLatency), nameof(ProxyLatency), nameof(Ready),
            nameof(StatusTitle), nameof(StatusDetail), nameof(HasTraffic), nameof(IsEmpty) }) OnPropertyChanged(name);
    }
}
