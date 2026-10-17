namespace EgressController.Guardian.Tests;

public sealed class MainGuardAgentTests
{
    [Fact]
    public async Task Initializes_once_then_persists_before_sending_fresh_tun_heartbeat()
    {
        var environment = new FakeEnvironment(); var client = new FakeClient(environment);
        int observations = 0;
        await using var agent = new MainGuardAgent(environment, client, () => Fixture.Policy,
            policy => { observations++; Assert.NotNull(environment.Desired); return Fixture.Heartbeat(policy); });
        await agent.PollAsync(TestContext.Current.CancellationToken); await agent.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, environment.Initializations);
        Assert.Equal(1, environment.Writes);
        Assert.Equal(2, observations);
        Assert.Equal(2, client.Heartbeats);
    }

    [Fact]
    public async Task Manual_stop_keeps_latest_selection_without_heartbeat_or_restart()
    {
        var environment = new FakeEnvironment { UserDisabled = true }; var client = new FakeClient(environment);
        GuardPolicy policy = Fixture.Policy;
        await using var agent = new MainGuardAgent(environment, client, () => policy, Fixture.Heartbeat);
        await agent.PollAsync(TestContext.Current.CancellationToken);
        policy = policy with { Revision = Guid.NewGuid().ToString("N"), Executables = [] };
        await agent.SyncAsync(TestContext.Current.CancellationToken); await agent.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal(policy.Revision, environment.Desired!.Revision);
        Assert.Equal(0, client.Heartbeats); Assert.Equal(0, client.Preparations); Assert.Equal(0, client.Syncs);
        Assert.Equal(1, client.Disposals);
        Assert.Equal(1, environment.Initializations);
    }

    [Fact]
    public async Task Disk_failure_prevents_heartbeat_and_can_recover_without_reinitialization()
    {
        var environment = new FakeEnvironment { WriteFails = true }; var client = new FakeClient(environment);
        await using var agent = new MainGuardAgent(environment, client, () => Fixture.Policy, Fixture.Heartbeat);
        await Assert.ThrowsAsync<IOException>(() => agent.PollAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, client.Heartbeats);
        environment.WriteFails = false;
        await agent.PollAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, client.Heartbeats); Assert.Equal(1, environment.Initializations);
    }

    [Fact]
    public async Task Sync_ack_failure_is_propagated_and_never_reports_success()
    {
        var environment = new FakeEnvironment(); var client = new FakeClient(environment) { Fail = true };
        await using var agent = new MainGuardAgent(environment, client, () => Fixture.Policy, Fixture.Heartbeat);
        await Assert.ThrowsAsync<IOException>(() => agent.SyncAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, client.Heartbeats);
        Assert.NotNull(environment.Desired);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0, true)]
    public async Task Transition_requires_protecting_latest_policy_and_no_failed_terminations(bool protecting, int failures, bool stale)
    {
        var environment = new FakeEnvironment(); var client = new FakeClient(environment)
        {
            Reply = new(true, null, new(protecting, "mock", 1, failures,
                stale ? Guid.NewGuid().ToString("N") : Fixture.Policy.Revision, [])),
        };
        await using var agent = new MainGuardAgent(environment, client, () => Fixture.Policy, Fixture.Heartbeat);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.PrepareAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, environment.Initializations);
    }

    [Fact]
    public async Task Valid_guard_handoff_allows_tun_transition_without_network_observations()
    {
        var environment = new FakeEnvironment(); var client = new FakeClient(environment);
        await using var agent = new MainGuardAgent(environment, client, () => Fixture.Policy,
            _ => throw new InvalidOperationException("Transition must not probe network or sample a ready heartbeat"));
        await agent.PrepareAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, client.Preparations); Assert.Equal(0, client.Heartbeats);
    }

    private sealed class FakeEnvironment : IMainGuardEnvironment
    {
        public int Initializations, Writes;
        public bool WriteFails = false;
        public bool UserDisabled { get; set; }
        public GuardPolicy? Desired;
        public void Initialize(GuardPolicy policy) => Initializations++;
        public void SaveDesired(GuardPolicy policy)
        {
            if (WriteFails) throw new IOException("mock disk full");
            Assert.Equal(1, Initializations);
            Writes++; Desired = policy;
        }
    }
    private sealed class FakeClient(FakeEnvironment environment) : IMainGuardClient
    {
        public int Heartbeats, Syncs, Preparations, Disposals;
        public bool Fail = false;
        public GuardReply Reply = new(true, null, new(true, "mock", 1, 0, Fixture.Policy.Revision, []));
        private Task<GuardReply> Send(GuardPolicy policy)
        {
            Assert.Equal(policy.Revision, environment.Desired!.Revision);
            return Fail ? Task.FromException<GuardReply>(new IOException("mock connection lost")) : Task.FromResult(Reply);
        }
        public Task<GuardReply> HeartbeatAsync(GuardPolicy policy, GuardHeartbeat heartbeat, CancellationToken token)
        { Heartbeats++; return Send(policy); }
        public Task<GuardReply> SyncAsync(GuardPolicy policy, CancellationToken token) { Syncs++; return Send(policy); }
        public Task<GuardReply> PrepareAsync(GuardPolicy policy, CancellationToken token) { Preparations++; return Send(policy); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
