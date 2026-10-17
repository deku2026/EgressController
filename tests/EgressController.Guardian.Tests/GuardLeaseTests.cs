namespace EgressController.Guardian.Tests;

public sealed class GuardLeaseTests
{
    [Fact]
    public void Cold_start_and_connection_without_tun_evidence_remain_protected()
    {
        var lease = new GuardLease();
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
        lease.Connect(Guid.NewGuid(), Fixture.Main, Fixture.Policy.MainExecutable);
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
    }
    [Fact]
    public void Healthy_tun_lease_expires_at_three_seconds_not_network_probe_interval()
    {
        var clock = new FakeClock();
        var lease = new GuardLease(clock);
        Guid session = Guid.NewGuid();
        lease.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable);
        Assert.True(lease.Report(session, 1, Fixture.Heartbeat()));
        clock.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.True(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
    }
    [Fact]
    public void Replayed_or_out_of_order_heartbeats_do_not_extend_lease()
    {
        var clock = new FakeClock(); var lease = new GuardLease(clock); Guid session = Guid.NewGuid();
        lease.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable);
        lease.Report(session, 5, Fixture.Heartbeat());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(lease.Report(session, 4, Fixture.Heartbeat()));
        Assert.False(lease.Report(session, 5, Fixture.Heartbeat()));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
    }
    [Fact]
    public void Old_connection_disconnect_and_messages_cannot_change_replacement_session()
    {
        var lease = new GuardLease(); Guid old = Guid.NewGuid(), current = Guid.NewGuid();
        lease.Connect(old, Fixture.Main, Fixture.Policy.MainExecutable);
        lease.Connect(current, Fixture.Main, Fixture.Policy.MainExecutable);
        lease.Report(current, 1, Fixture.Heartbeat());
        lease.Disconnect(old);
        Assert.False(lease.Report(old, 100, Fixture.Heartbeat() with { TunReady = false }));
        Assert.True(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
        lease.Disconnect(current);
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
    }
    [Theory]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, "TUN route missing")]
    public void Main_exit_or_tun_loss_protects_without_waiting_for_network(bool alive, bool tun, string? error)
    {
        var lease = new GuardLease(); var session = Guid.NewGuid();
        lease.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable);
        lease.Report(session, 1, Fixture.Heartbeat() with { TunReady = tun });
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, alive, error).Ready);
    }
    [Fact]
    public void Changed_policy_or_missing_core_identity_cannot_release()
    {
        var lease = new GuardLease(); var session = Guid.NewGuid();
        lease.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable);
        lease.Report(session, 1, Fixture.Heartbeat());
        Assert.False(lease.Evaluate(Guid.NewGuid().ToString("N"), true, null).Ready);
        lease.Report(session, 2, Fixture.Heartbeat() with { Core = null });
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
        lease.Report(session, 3, Fixture.Heartbeat() with { CorePath = null });
        Assert.False(lease.Evaluate(Fixture.Policy.Revision, true, null).Ready);
    }
}
