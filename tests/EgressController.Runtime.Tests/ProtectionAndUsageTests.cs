using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ProtectionAndUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GuardianAndInternetVerdictsAreIndependentAndReadErrorsAreNotHealthy()
    {
        var network = new NetworkReadinessMonitor(); network.ObserveLink(true, true);
        network.ObserveInternet(true, Now);
        Assert.True(network.Ready);
        network.ObserveInternet(false, Now.AddSeconds(30)); Assert.True(network.Ready);
        Assert.False(network.ObserveInternet(false, Now.AddSeconds(31)));
        network.ObserveInternet(false, Now.AddSeconds(35)); Assert.True(network.Ready);
        network.ObserveInternet(false, Now.AddSeconds(40)); Assert.False(network.Ready);
        Assert.Equal(GuardianState.Healthy, GuardianEvaluator.Evaluate(new(true, true, false, true, Now), Now).State);
        Assert.Equal(GuardianState.Unavailable, GuardianEvaluator.Evaluate(new(true, true, false, false, Now), Now).State);
        Assert.Equal(GuardianState.Unavailable, GuardianEvaluator.Evaluate(new(true, true, false, true, Now), Now.AddMinutes(2)).State);
        Assert.Equal(GuardianState.Protecting, GuardianEvaluator.Evaluate(new(false, true, false, true, Now), Now).State);
        Assert.Equal(GuardianState.ManuallyStopped, GuardianEvaluator.Evaluate(new(true, true, true, true, Now), Now).State);
    }

    [Fact]
    public void LinkAndIpLossActIndependentlyFromInternetRetryConfirmation()
    {
        var network = new NetworkReadinessMonitor(); network.ObserveLink(true, true); network.ObserveInternet(true, Now);
        network.ObserveLink(false, true); Assert.False(network.Ready); Assert.True(network.InternetConfirmed);
        network.ObserveLink(true, false); Assert.False(network.Ready); Assert.True(network.InternetConfirmed);
    }

    [Fact]
    public void NaturalDayPeriodsKeepPriorTotalsAndResumeDoesNotBillOfflineBytes()
    {
        var storage = new InMemoryTrafficStorage();
        var ledger = new TrafficLedger(new() { FirstOpenedAt = Now, CalendarTimeZone = "UTC" }, storage);
        Assert.Equal(DateOnly.FromDateTime(Now.Date), ledger.PeriodAt(Now.AddDays(29)).Start);
        Assert.Equal(DateOnly.FromDateTime(Now.AddDays(30).Date), ledger.PeriodAt(Now.AddDays(30)).Start);
        Assert.True(ledger.TryObserve([Observation(100, 100, Now)])); // Resume baseline.
        Assert.True(ledger.TryObserve([Observation(140, 180, Now.AddSeconds(30))]));
        Assert.Equal(120, ledger.Snapshot.Totals.Sum(total => total.UploadedBytes + total.DownloadedBytes));
        ledger.Resume();
        Assert.True(ledger.TryObserve([Observation(9000, 9000, Now.AddDays(31))]));
        Assert.True(ledger.TryObserve([Observation(9100, 9200, Now.AddDays(31).AddSeconds(30))]));
        Assert.Equal(420, ledger.Snapshot.Totals.Sum(total => total.UploadedBytes + total.DownloadedBytes));
        Assert.Equal(2, ledger.Snapshot.Totals.Select(total => total.PeriodStart).Distinct().Count());
    }

    [Fact]
    public void ObservedExitIsHistoricalAndStorageFailureCommitsNeitherTotalsNorCheckpoint()
    {
        var storage = new InMemoryTrafficStorage();
        var ledger = new TrafficLedger(new() { FirstOpenedAt = Now, CalendarTimeZone = "UTC" }, storage);
        ledger.TryObserve([Observation(100, 100, Now)]);
        ledger.TryObserve([Observation(150, 150, Now.AddSeconds(30))]);
        storage.FailCommit = true;
        Assert.False(ledger.TryObserve([Observation(250, 250, Now.AddMinutes(1)) with { Outbound = ExitKind.Proxy }]));
        Assert.Equal(150, ledger.Snapshot.Checkpoints.Single().UploadedBytes);
        storage.FailCommit = false;
        Assert.True(ledger.TryObserve([Observation(250, 250, Now.AddMinutes(1)) with { Outbound = ExitKind.Proxy }]));
        Assert.Equal(100, ledger.Snapshot.Totals.Single(total => total.Outbound == ExitKind.Home).UploadedBytes
            + ledger.Snapshot.Totals.Single(total => total.Outbound == ExitKind.Home).DownloadedBytes);
        Assert.Equal(200, ledger.Snapshot.Totals.Single(total => total.Outbound == ExitKind.Proxy).UploadedBytes
            + ledger.Snapshot.Totals.Single(total => total.Outbound == ExitKind.Proxy).DownloadedBytes);
    }

    [Fact]
    public void StaleObservationAndCounterResetCannotInventTraffic()
    {
        var ledger = new TrafficLedger(new() { FirstOpenedAt = Now, CalendarTimeZone = "UTC" }, new InMemoryTrafficStorage());
        ledger.TryObserve([Observation(100, 100, Now)]);
        ledger.TryObserve([Observation(400, 400, Now)]);
        ledger.TryObserve([Observation(1, 1, Now.AddSeconds(30))]);
        Assert.Empty(ledger.Snapshot.Totals);
    }

    [Fact]
    public void EmptyUnavailableOrInvalidTelemetryCannotConsumeResumeBaseline()
    {
        var storage = new CountingTrafficStorage();
        var ledger = new TrafficLedger(new() { FirstOpenedAt = Now }, storage);
        Assert.False(ledger.TryObserve(null));
        Assert.True(ledger.TryObserve([]));
        Assert.False(ledger.TryObserve([Observation(-1, 100, Now)]));
        Assert.Equal(0, storage.Commits);
        Assert.True(ledger.TryObserve([Observation(9000, 9000, Now.AddDays(1))]));
        Assert.Empty(ledger.Snapshot.Totals);
        Assert.True(ledger.TryObserve([Observation(9100, 9200, Now.AddDays(1).AddSeconds(30))]));
        Assert.Equal(300, ledger.Snapshot.Totals.Sum(total => total.UploadedBytes + total.DownloadedBytes));
        ledger.Resume();
        Assert.True(ledger.TryObserve([], completeSnapshot: true));
        Assert.True(ledger.TryObserve([Observation(99000, 99000, Now.AddDays(2))]));
        Assert.Equal(300, ledger.Snapshot.Totals.Sum(total => total.UploadedBytes + total.DownloadedBytes));
    }

    [Fact]
    public void OnlyAuthoritativeCompleteListsCanPruneAndCapacityNeverDropsOldData()
    {
        var ledger = new TrafficLedger(new() { FirstOpenedAt = Now }, new InMemoryTrafficStorage(), checkpointCapacity: 2);
        var a = Observation(0, 0, Now) with { ConnectionId = "a" };
        var b = a with { ConnectionId = "b" };
        Assert.True(ledger.TryObserve([a, b]));
        Assert.True(ledger.TryObserve([a with { UploadedBytes = 1, ObservedAt = Now.AddSeconds(1) }]));
        Assert.Equal(2, ledger.Snapshot.Checkpoints.Count);
        var c = a with { ConnectionId = "c", UploadedBytes = 3, ObservedAt = Now.AddSeconds(2) };
        Assert.False(ledger.TryObserve([c]));
        Assert.Equal(2, ledger.Snapshot.Checkpoints.Count);
        Assert.DoesNotContain(ledger.Snapshot.Checkpoints, item => item.ConnectionId == "c");
        Assert.True(ledger.TryObserve([a with { UploadedBytes = 2, ObservedAt = Now.AddSeconds(3) }], completeSnapshot: true));
        Assert.Equal("a", Assert.Single(ledger.Snapshot.Checkpoints).ConnectionId);
        Assert.True(ledger.TryObserve([c]));
        Assert.Equal(5, ledger.Snapshot.Totals.Sum(total => total.UploadedBytes));
    }

    [Fact]
    public async Task ConcurrentPartialObservationsCommitWithoutLostTotalsOrSharedSnapshotMutation()
    {
        var storage = new CountingTrafficStorage();
        var ledger = new TrafficLedger(new() { FirstOpenedAt = Now }, storage);
        ledger.TryObserve([Observation(0, 0, Now)]);
        await Task.WhenAll(Enumerable.Range(1, 100).Select(index => Task.Run(() =>
        {
            Assert.True(ledger.TryObserve([new("connection-" + index, "example", ExitKind.Home, 1, 2, Now.AddSeconds(1))]));
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(300, ledger.Snapshot.Totals.Sum(total => total.UploadedBytes + total.DownloadedBytes));
        Assert.Equal(101, storage.Commits);
        Assert.Equal(1, storage.MaximumConcurrent);
        var detached = ledger.Snapshot; detached.Totals.Clear(); detached.Checkpoints.Clear(); detached.CalendarTimeZone = "invalid";
        Assert.Single(ledger.Snapshot.Totals);
        Assert.Equal(101, ledger.Snapshot.Checkpoints.Count);
        Assert.Equal("UTC", ledger.Snapshot.CalendarTimeZone);
    }

    [Fact]
    public void NaturalDayPeriodsCrossDstWithoutBecoming720RunningHours()
    {
        var opened = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(1));
        var ledger = new TrafficLedger(new() { FirstOpenedAt = opened, CalendarTimeZone = "Europe/London" }, new InMemoryTrafficStorage());
        Assert.Equal(new DateOnly(2026, 10, 1), ledger.PeriodAt(new(2026, 10, 30, 23, 30, 0, TimeSpan.Zero)).Start);
        Assert.Equal(new DateOnly(2026, 10, 31), ledger.PeriodAt(new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero)).Start);
    }

    private sealed class CountingTrafficStorage : ITrafficLedgerStorage
    {
        public int Commits, MaximumConcurrent;
        private int _active;
        public void Commit(TrafficLedgerSnapshot snapshot)
        {
            int active = Interlocked.Increment(ref _active);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            Thread.Sleep(1); Interlocked.Increment(ref Commits); Interlocked.Decrement(ref _active);
        }
    }
    private static TrafficObservation Observation(long up, long down, DateTimeOffset time) => new("instance:created:connection", "example", ExitKind.Home, up, down, time);
}
