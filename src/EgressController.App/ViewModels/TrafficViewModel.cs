using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EgressController.App.Services;
using EgressController.Diagnostics;
using EgressController.Presentation;
using EgressController.State.Traffic;

namespace EgressController.App.ViewModels;

public sealed record ApplicationTrafficRow(ProcessVisual Appearance, long HomeBytes, long OtherBytes,
    long UnknownBytes, double Maximum, double WindowTotal)
{
    public string Name => Appearance.Name;
    public long Total => TrafficUsage.Add(TrafficUsage.Add(HomeBytes, OtherBytes), UnknownBytes);
    public string Amount => TrafficUnits.Bytes(Total);
    public string HomeAmount => "家宽 " + TrafficUnits.Bytes(HomeBytes);
    public string OtherAmount => "非家宽 " + TrafficUnits.Bytes(OtherBytes);
    public string UnknownAmount => "未识别 " + TrafficUnits.Bytes(UnknownBytes);
    public bool HasUnknown => UnknownBytes > 0;
    public double Share => WindowTotal > 0 ? Total / WindowTotal * 100 : 0;
    public string ShareLabel => $"{Share:0.#}%";
}

public sealed class TrafficViewModel(IAppController controller) : ObservableObject
{
    private TrafficUsageSnapshot? _last;
    private ApplicationTrafficRow[] _all = [];
    private string _query = "";
    public string Query { get => _query; set { if (SetProperty(ref _query, value ?? "")) Filter(); } }
    public ObservableCollection<ApplicationTrafficRow> Applications { get; } = [];
    public IReadOnlyList<ApplicationTrafficRow> TopApplications { get; private set; } = [];
    public IReadOnlyList<DailyTrafficPoint> Days { get; private set; } = [];
    public long HomeBytes { get; private set; }
    public long OtherBytes { get; private set; }
    public long UnknownBytes { get; private set; }
    public long TotalBytes => TrafficUsage.Add(TrafficUsage.Add(HomeBytes, OtherBytes), UnknownBytes);
    public bool HasUnknown => UnknownBytes > 0;
    public string Total => _last?.RecordedSinceUtc is null && !HasApplications ? "—" : TrafficUnits.Bytes(TotalBytes);
    public string Home => TrafficUnits.Bytes(HomeBytes);
    public string Other => TrafficUnits.Bytes(OtherBytes);
    public string Unknown => TrafficUnits.Bytes(UnknownBytes);
    public string Period => _last is null ? "近 30 天" : $"{_last.From:MM.dd} – {_last.Through:MM.dd}";
    public string RecordedSince => _last?.RecordedSinceUtc is { } at ? $"本机记录始于 {at.ToLocalTime():yyyy.MM.dd}" : "开始联网后记录应用用量";
    public string ApplicationCount => $"{_all.Length} 个应用";
    public bool IsEmpty => Applications.Count == 0;
    public bool HasApplications => _all.Length > 0;
    public string Status => controller.TrafficUsageError;
    public bool HasError => Status.Length > 0;

    public void Refresh()
    {
        var snapshot = controller.TrafficUsage;
        if (!ReferenceEquals(snapshot, _last))
        {
            _last = snapshot;
            var entries = snapshot.Entries.Where(row => row.Day >= snapshot.From && row.Day <= snapshot.Through).ToArray();
            static long Sum(IEnumerable<TrafficUsage> rows, TrafficExit exit) =>
                rows.Where(row => row.Exit == exit).Aggregate(0L, (sum, row) => TrafficUsage.Add(sum, row.Total));
            HomeBytes = Sum(entries, TrafficExit.Home); OtherBytes = Sum(entries, TrafficExit.Other); UnknownBytes = Sum(entries, TrafficExit.Unknown);
            var grouped = entries.GroupBy(row => controller.Visuals.ForProcess(row.ProcessPath).Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ApplicationTrafficRow(controller.Visuals.ForProcess(group.First().ProcessPath),
                    Sum(group, TrafficExit.Home), Sum(group, TrafficExit.Other), Sum(group, TrafficExit.Unknown), 0, 0))
                .Where(row => row.Total > 0).OrderByDescending(row => row.Total).ThenBy(row => row.Name).ToArray();
            double total = grouped.Sum(row => (double)row.Total), maximum = grouped.Select(row => (double)row.Total).DefaultIfEmpty(0).Max();
            _all = grouped.Select(row => row with { Maximum = maximum, WindowTotal = total }).ToArray();
            TopApplications = _all.Take(5).ToArray();
            Days = Enumerable.Range(0, 30).Select(index =>
            {
                var day = snapshot.From.AddDays(index); var rows = entries.Where(row => row.Day == day).ToArray();
                return new DailyTrafficPoint(day, Sum(rows, TrafficExit.Home), Sum(rows, TrafficExit.Other), Sum(rows, TrafficExit.Unknown));
            }).ToArray();
            Filter();
            foreach (string property in new[] { nameof(TopApplications), nameof(Days), nameof(HomeBytes), nameof(OtherBytes), nameof(UnknownBytes),
                nameof(TotalBytes), nameof(HasUnknown), nameof(Total), nameof(Home), nameof(Other), nameof(Unknown), nameof(Period), nameof(RecordedSince), nameof(ApplicationCount), nameof(HasApplications) })
                OnPropertyChanged(property);
        }
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(HasError));
    }

    private void Filter()
    {
        var desired = _all.Where(row => row.Name.Contains(Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        for (int index = 0; index < desired.Length; index++)
        {
            var row = desired[index];
            int current = -1;
            for (int i = index; i < Applications.Count; i++)
                if (Applications[i].Appearance.Key == row.Appearance.Key) { current = i; break; }
            if (current < 0) Applications.Insert(index, row);
            else
            {
                if (current != index) Applications.Move(current, index);
                if (Applications[index] != row) Applications[index] = row;
            }
        }
        while (Applications.Count > desired.Length) Applications.RemoveAt(Applications.Count - 1);
        OnPropertyChanged(nameof(IsEmpty));
    }
}
