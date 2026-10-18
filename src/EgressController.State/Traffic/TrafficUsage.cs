namespace EgressController.State.Traffic;

public enum TrafficExit { Home, Other, Unknown }

public sealed record TrafficCounter(string ConnectionId, DateTimeOffset? StartedAtUtc, string ProcessPath,
    TrafficExit Exit, long Upload, long Download);

public sealed record TrafficUsage(string ProcessPath, DateOnly Day, TrafficExit Exit, long Upload, long Download)
{
    public long Total => Add(Upload, Download);
    public static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed record TrafficUsageSnapshot(DateOnly From, DateOnly Through, DateTimeOffset? RecordedSinceUtc,
    IReadOnlyList<TrafficUsage> Entries)
{
    public static TrafficUsageSnapshot Empty(DateOnly today) => new(today, today.AddDays(29), null, []);
}

/// <summary>Fixed calendar periods anchored to first use, never a rolling or running-hours window.</summary>
public readonly record struct TrafficPeriod(DateOnly From, DateOnly Through)
{
    public static TrafficPeriod At(DateOnly firstDay, DateOnly today)
    {
        int period = Math.Max(0, today.DayNumber - firstDay.DayNumber) / 30;
        var from = firstDay.AddDays(period * 30);
        return new(from, from.AddDays(29));
    }
}
