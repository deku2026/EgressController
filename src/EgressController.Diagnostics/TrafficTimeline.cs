namespace EgressController.Diagnostics;

public sealed record RatePoint(double SecondsAgo, double? Value);
public sealed record TrafficSnapshot(DateTimeOffset? ObservedAtUtc, long? UploadRate, long? DownloadRate,
    IReadOnlyList<RatePoint> Upload, IReadOnlyList<RatePoint> Download, double ScaleMaximum, bool ObservationFresh = false)
{
    public bool HasCurrentRate => UploadRate is not null && DownloadRate is not null;
}

/// <summary>Only a real observation appends a sample. UI refresh merely projects a fixed time window.</summary>
public sealed class TrafficTimeline(TimeProvider? clock = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(3);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Queue<Reading> _readings = new();
    private DateTimeOffset? _lastObservation;
    private sealed record Reading(long Stamp, long? Up, long? Down, bool Observed);

    public void Observe(long? upload, long? download) => Add(upload, download, true);
    private void Add(long? upload, long? download, bool observed)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            _readings.Enqueue(new(now, upload is >= 0 ? upload : null, download is >= 0 ? download : null, observed));
            if (observed) _lastObservation = _clock.GetUtcNow();
            Prune(now);
        }
    }
    public void Unavailable() => Add(null, null, false);
    public void Reset()
    {
        lock (_gate) { _readings.Clear(); _lastObservation = null; }
    }
    private void Prune(long now)
    {
        while (_readings.Count > 0 && (_readings.Count > 240 || _clock.GetElapsedTime(_readings.Peek().Stamp, now) > Window))
            _readings.Dequeue();
    }
    public TrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp(); Prune(now);
            var values = _readings.ToArray();
            var last = values.LastOrDefault();
            bool fresh = last is not null && _clock.GetElapsedTime(last.Stamp, now) <= Freshness;
            double Age(Reading point) => Math.Max(0, _clock.GetElapsedTime(point.Stamp, now).TotalSeconds);
            return new(_lastObservation, fresh ? last?.Up : null, fresh ? last?.Down : null,
                values.Select(point => new RatePoint(Age(point), point.Up)).ToArray(),
                values.Select(point => new RatePoint(Age(point), point.Down)).ToArray(),
                TrafficUnits.ScaleCeiling(values.SelectMany(point => new[] { point.Up, point.Down }).OfType<long>().Select(value => (double)value).DefaultIfEmpty(0).Max()), fresh && last?.Observed == true);
        }
    }
}

public static class TrafficUnits
{
    public static string Bytes(double value)
    {
        if (!double.IsFinite(value) || value < 0) return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
    public static string Rate(double? value) => value is { } rate && double.IsFinite(rate) && rate >= 0 ? Bytes(rate) + "/s" : "—";
    public static double ScaleCeiling(double peak)
    {
        if (!double.IsFinite(peak) || peak <= 1024) return 1024;
        double unit = Math.Pow(1024, Math.Floor(Math.Log(peak, 1024)));
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak / unit)));
        double normalized = peak / unit / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude * unit;
    }
}
