using EgressController.State.Traffic;

namespace EgressController.App;

public sealed partial class AppController
{
    private readonly TrafficUsageStore _usageStore;
    private TrafficUsageSnapshot _usageSnapshot = TrafficUsageSnapshot.Empty(DateOnly.FromDateTime(DateTime.Today));
    private string _usageReadError = "", _usageWriteError = "";
    public TrafficUsageSnapshot TrafficUsage => Volatile.Read(ref _usageSnapshot);
    public string TrafficUsageError => string.Join("\n", new[] { Volatile.Read(ref _usageReadError), Volatile.Read(ref _usageWriteError) }.Where(message => message.Length > 0));

    private async Task TrafficUsageLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { Volatile.Write(ref _usageSnapshot, _usageStore.Read(DateTimeOffset.UtcNow)); Volatile.Write(ref _usageReadError, ""); }
            catch (Exception error) { Volatile.Write(ref _usageReadError, "无法读取流量记录：" + error.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void RecordTrafficUsage(IReadOnlyList<TrafficCounter> counters, DateTimeOffset observedAtUtc)
    {
        try { _usageStore.Record(counters, observedAtUtc); Volatile.Write(ref _usageWriteError, ""); }
        catch (Exception error) { Volatile.Write(ref _usageWriteError, "无法保存流量记录：" + error.Message); }
        // Accounting has no TUN/protection authority. The traffic page alone reports these errors.
    }
}
