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
    public static TrafficUsageSnapshot Empty(DateOnly today) => new(today.AddDays(-29), today, null, []);
}
