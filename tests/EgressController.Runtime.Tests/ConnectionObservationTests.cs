// SPDX-License-Identifier: MIT
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ConnectionObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static XrayConnectionObserver Observer(int capacity = 128) => new(new string('a', 64), capacity);

    [Fact]
    public void PinnedTunAndDispatcherLinesJoinOnlyObservedFactsAndAccessDoesNotDuplicateTheTuple()
    {
        var observer = Observer();
        observer.ObserveLine("2026/10/31 12:00:00.000001 [Info] [42] proxy/tun: processing from tcp:192.0.2.4:54321 to tcp:203.0.113.8:443", Now);
        observer.ObserveLine("2026/10/31 12:00:00.000002 [Info] [42] app/dispatcher: Hit route rule: [app-synthetic] so taking detour [node-1] for [tcp:example.test:443]", Now.AddMilliseconds(1));
        observer.ObserveLine("2026/10/31 12:00:00.000003 from tcp:192.0.2.4:54321 accepted tcp:203.0.113.8:443 [tun -> node-1]", Now.AddMilliseconds(2));
        var snapshot = observer.Snapshot(Now.AddSeconds(1));
        var row = Assert.Single(snapshot.Records);
        Assert.Equal("192.0.2.4", row.SourceIp); Assert.Equal(54321, row.SourcePort);
        Assert.Equal("example.test", row.Host); Assert.Equal("203.0.113.8", row.RemoteIp); Assert.Equal(443, row.RemotePort);
        Assert.Equal("node-1", row.OutboundTag); Assert.Equal("app-synthetic", row.MatchedRule);
        Assert.Equal(CoreConnectionActivity.Unknown, row.Activity);
        Assert.Null(row.ProcessId); Assert.Null(row.ExecutablePath); Assert.Null(row.UploadedBytes); Assert.Null(row.DownloadedBytes);
        Assert.True(CoreConnectionSnapshots.IsValid(snapshot, Now.AddSeconds(1)));
        Assert.Contains("不提供", snapshot.Limitation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("udp:2001:db8::2:54321", "udp:2001:db8::3:53", "2001:db8::2", "2001:db8::3", "udp")]
    [InlineData("tcp:[2001:db8::2]:54321", "tcp:[2001:db8::3]:443", "2001:db8::2", "2001:db8::3", "tcp")]
    public void IPv6SupportsPinnedUnbracketedAndBracketedAddressFormatting(string source, string target, string sourceIp, string remoteIp, string transport)
    {
        var observer = Observer(); observer.ObserveLine($"from {source} accepted {target} [tun -> nonhome]", Now);
        var row = Assert.Single(observer.Snapshot(Now).Records);
        Assert.Equal(sourceIp, row.SourceIp); Assert.Equal(remoteIp, row.RemoteIp); Assert.Equal(transport, row.Transport);
        Assert.Equal(CoreConnectionActivity.Unknown, row.Activity);
    }

    [Fact]
    public void MissingTunEventNeverInventsProcessSourceOrConnectionLiveness()
    {
        var observer = Observer();
        observer.ObserveLine("[Info] [43] app/dispatcher: Hit route rule: [global] so taking detour [node-2] for [tcp:example.test:443]", Now);
        var row = Assert.Single(observer.Snapshot(Now).Records);
        Assert.Null(row.SourceIp); Assert.Null(row.SourcePort); Assert.Null(row.ProcessId); Assert.Null(row.ExecutablePath);
        Assert.Equal("global", row.MatchedRule); Assert.Equal(CoreConnectionActivity.Unknown, row.Activity);
    }

    [Fact]
    public void SessionCollisionNeverRelabelsThePriorTupleAndMismatchedDestinationIsNotJoined()
    {
        var observer = Observer();
        observer.ObserveLine("[Info] [44] proxy/tun: processing from tcp:192.0.2.4:20001 to tcp:203.0.113.1:443", Now);
        observer.ObserveLine("[Info] [44] proxy/tun: processing from tcp:192.0.2.4:20002 to tcp:203.0.113.2:443", Now);
        observer.ObserveLine("[Info] [44] app/dispatcher: Hit route rule: [global] so taking detour [node-1] for [tcp:203.0.113.1:443]", Now);
        Assert.All(observer.Snapshot(Now).Records, row => Assert.Null(row.OutboundTag));
        observer.ObserveLine("[Info] [44] app/dispatcher: Hit route rule: [global] so taking detour [node-2] for [tcp:203.0.113.2:443]", Now);
        var rows = observer.Snapshot(Now).Records;
        Assert.Null(rows.Single(row => row.SourcePort == 20001).OutboundTag);
        Assert.Equal("node-2", rows.Single(row => row.SourcePort == 20002).OutboundTag);
        Assert.Equal(2, rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("[Info] [42] proxy/masque: processing from tcp:192.0.2.4:1234 to tcp:example.test:443")]
    [InlineData("from tcp:192.0.2.4:1234 accepted tcp:user:secret@example.test:443 [tun -> node-1]")]
    [InlineData("from tcp:192.0.2.4:1234 accepted tcp:example.test/path:443 [tun -> node-1]")]
    [InlineData("from tcp:192.0.2.4:1234 accepted tcp:example.test:443 [dns-query -> nonhome]")]
    [InlineData("[Info] [42] app/dispatcher: Hit route rule: [secret rule] so taking detour [node] for [tcp:example.test:443]")]
    [InlineData("[Warning] private subscription https://user:secret@example.invalid/private")]
    public void UnrecognizedOrCredentialBearingLinesCannotBecomeObservationFacts(string line)
    { var observer = Observer(); observer.ObserveLine(line, Now); Assert.Empty(observer.Snapshot(Now).Records); }

    [Fact]
    public void BoundedHistoryReportsEvictionAndReturnsDetachedSnapshots()
    {
        var observer = Observer(2);
        for (int index = 1; index <= 3; index++) observer.ObserveLine($"from tcp:192.0.2.4:{20000 + index} accepted tcp:203.0.113.1:443 [tun -> node-1] email: discarded-secret", Now);
        var snapshot = observer.Snapshot(Now); Assert.Equal(2, snapshot.Records.Count); Assert.Equal(1, snapshot.DroppedRecords);
        ((CoreConnectionRecord[])snapshot.Records)[0] = new("mutated", Now);
        Assert.DoesNotContain(observer.Snapshot(Now).Records, row => row.Id == "mutated");
        Assert.All(observer.Snapshot(Now).Records, row => Assert.Equal(CoreConnectionActivity.Unknown, row.Activity));
    }

    [Fact]
    public async Task OutputPumpDiscardsOversizedLinesAndContinuesDrainingWithoutUnboundedAllocation()
    {
        var observer = Observer();
        using var input = new StringReader(new string('x', 100_000) + "\nfrom udp:192.0.2.4:20001 accepted udp:203.0.113.1:53 [tun -> dns]\r\n");
        await XrayConnectionOutputPump.DrainAsync(input, observer);
        var snapshot = observer.Snapshot(DateTimeOffset.UtcNow);
        Assert.Single(snapshot.Records); Assert.Equal(1, snapshot.DroppedRecords); Assert.False(snapshot.IsAvailable); // EOF is not a health fact.
    }

    [Fact]
    public async Task OutputReaderFailureOnlyMarksTelemetryUnavailable()
    {
        var observer = Observer();
        await XrayConnectionOutputPump.DrainAsync(new FailingReader(), observer);
        Assert.False(observer.Snapshot(Now).IsAvailable);
    }

    [Fact]
    public async Task ConcurrentOutputAndSnapshotsRemainBoundedAndValid()
    {
        var observer = Observer();
        await Task.WhenAll(Enumerable.Range(0, 256).Select(index => Task.Run(() =>
        {
            observer.ObserveLine($"from tcp:192.0.2.4:{20000 + index} accepted tcp:203.0.113.1:443 [tun -> nonhome]", Now);
            Assert.True(CoreConnectionSnapshots.IsValid(observer.Snapshot(Now), Now));
        }, TestContext.Current.CancellationToken)));
        var snapshot = observer.Snapshot(Now);
        Assert.Equal(128, snapshot.Records.Count); Assert.Equal(128, snapshot.DroppedRecords);
    }

    private sealed class FailingReader : TextReader
    { public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) => throw new IOException("Synthetic reader failure."); }
}
