using EgressController.Core.Protection;

namespace EgressController.Guardian.Tests;

public sealed class GuardRuntimeTests
{
    [Fact]
    public void Infrastructure_failure_keeps_last_saved_selection_protected_until_fresh_heartbeat()
    {
        var processes = new FakeProcesses { Snapshot = [Fixture.Process()] };
        var runtime = new GuardRuntime(new FakeStore(), processes, new FakeFacts(), []);
        Guid session = Guid.NewGuid();
        runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable);
        runtime.Report(session, 1, Fixture.Heartbeat());
        Assert.False(runtime.Tick().Protecting);
        runtime.SetInfrastructureError("mock mailbox corruption");
        runtime.Report(session, 2, Fixture.Heartbeat());
        Assert.Contains("mock mailbox corruption", runtime.Tick().Reason);
        Assert.Single(processes.Killed);
        runtime.SetInfrastructureError("mock mailbox corruption");
        runtime.SetInfrastructureError(null);
        Assert.True(runtime.Tick().Protecting);
        runtime.Report(session, 3, Fixture.Heartbeat());
        Assert.False(runtime.Tick().Protecting);
    }
    [Fact]
    public void Main_absent_continuously_terminates_all_instances_directory_exes_and_descendants()
    {
        var processes = new FakeProcesses { Snapshot = [
            Fixture.Process(), Fixture.Process(11), Fixture.Process(12, @"C:\apps\AI\updated\new.exe"),
            Fixture.Process(13, @"C:\helpers\child.exe", 10, 2),
            Fixture.Process(14, @"C:\helpers\grandchild.exe", 13, 3),
            Fixture.Process(15, @"C:\apps\AI-other\ai.exe"),
            Fixture.Process(16, @"C:\other\ai.exe"),
            Fixture.Process(17, @"C:\apps\AI\proxy.exe")] };
        var runtime = new GuardRuntime(new FakeStore(), processes, new FakeFacts(), []);
        Assert.True(runtime.Tick().Protecting);
        Assert.Equal(new uint[] { 10, 11, 12, 13, 14 }, processes.Killed.Select(x => x.Pid).Order().ToArray());
        processes.Killed.Clear();
        runtime.Tick();
        Assert.Equal(5, processes.Killed.Count);
    }
    [Fact]
    public void Healthy_tun_stops_only_guard_kills_network_protection_remains_independent()
    {
        var processes = new FakeProcesses { Snapshot = [Fixture.Process()] };
        var runtime = new GuardRuntime(new FakeStore(), processes, new FakeFacts(), []);
        var session = Guid.NewGuid();
        runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable);
        runtime.Report(session, 1, Fixture.Heartbeat());
        Assert.False(runtime.Tick().Protecting);
        Assert.Empty(processes.Killed);
        var network = new ProcessProtection(processes);
        network.Sweep(Fixture.Policy.Executables, new HashSet<string>(), false, "网络连续失败");
        Assert.Single(processes.Killed);
        Assert.False(runtime.Tick().Protecting);
        Assert.Single(processes.Killed);
    }
    [Fact]
    public void Persisted_orphan_is_terminated_after_guard_restart_but_reused_pid_is_not()
    {
        var store = new FakeStore();
        var processes = new FakeProcesses { Snapshot = [Fixture.Process(), Fixture.Process(20, @"C:\helper\child.exe", 10, 2)] };
        var runtime = new GuardRuntime(store, processes, new FakeFacts(), []);
        Guid session = Guid.NewGuid();
        runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable); runtime.Report(session, 1, Fixture.Heartbeat());
        runtime.Tick();
        Assert.Empty(processes.Killed);
        processes.Snapshot = [Fixture.Process(20, @"C:\helper\child.exe", 10, 2), Fixture.Process(10, @"C:\unrelated.exe", 0, 9)];
        var restarted = new GuardRuntime(store, processes, new FakeFacts(), []);
        restarted.Tick();
        Assert.Equal((uint)20, Assert.Single(processes.Killed).Pid);
    }
    [Fact]
    public void Removing_selection_drops_tracked_descendants()
    {
        var processes = new FakeProcesses { Snapshot = [Fixture.Process(), Fixture.Process(20, @"C:\helper.exe", 10, 2)] };
        var runtime = new GuardRuntime(new FakeStore(), processes, new FakeFacts(), []);
        runtime.Tick(); processes.Killed.Clear();
        runtime.SetPolicy(Fixture.Policy with { Revision = Guid.NewGuid().ToString("N"), Executables = [], Directories = [] });
        runtime.Tick();
        Assert.Empty(processes.Killed);
    }
    [Fact]
    public void Saved_ready_state_is_never_reused_on_service_restart()
    {
        var store = new FakeStore();
        var processes = new FakeProcesses { Snapshot = [Fixture.Process()] };
        var runtime = new GuardRuntime(store, processes, new FakeFacts(), []);
        Guid session = Guid.NewGuid();
        runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable); runtime.Report(session, 1, Fixture.Heartbeat());
        Assert.False(runtime.Tick().Protecting);
        var restarted = new GuardRuntime(store, processes, new FakeFacts(), []);
        Assert.True(restarted.Tick().Protecting);
        Assert.Single(processes.Killed);
    }
    [Fact]
    public void Failed_policy_commit_retains_previous_targets_and_revokes_ready()
    {
        var store = new FakeStore(); var processes = new FakeProcesses { Snapshot = [Fixture.Process()] };
        var runtime = new GuardRuntime(store, processes, new FakeFacts(), []);
        Guid session = Guid.NewGuid();
        runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable); runtime.Report(session, 1, Fixture.Heartbeat());
        runtime.Tick(); store.PolicyWriteFails = true;
        Assert.Throws<IOException>(() => runtime.SetPolicy(Fixture.Policy with { Revision = Guid.NewGuid().ToString("N"), Executables = [], Directories = [] }));
        Assert.Equal(Fixture.Policy.Revision, store.Policy!.Revision);
        Assert.True(runtime.Tick().Protecting); Assert.Single(processes.Killed);
    }
    [Fact]
    public void Changed_policy_requires_fresh_matching_heartbeat()
    {
        var runtime = new GuardRuntime(new FakeStore(), new FakeProcesses(), new FakeFacts(), []);
        Guid session = Guid.NewGuid();
        runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable); runtime.Report(session, 1, Fixture.Heartbeat());
        Assert.False(runtime.Tick().Protecting);
        var next = Fixture.Policy with { Revision = Guid.NewGuid().ToString("N") };
        runtime.SetPolicy(next);
        Assert.True(runtime.Tick().Protecting);
        runtime.Report(session, 2, Fixture.Heartbeat());
        Assert.True(runtime.Tick().Protecting);
        runtime.Report(session, 3, Fixture.Heartbeat(next));
        Assert.False(runtime.Tick().Protecting);
    }
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Inspection_capture_and_storage_errors_are_not_reported_as_healthy(bool factsFail, bool captureFail, bool saveFail)
    {
        var processes = new FakeProcesses { Snapshot = [Fixture.Process()], CaptureFails = captureFail };
        var runtime = new GuardRuntime(new FakeStore { CheckpointWriteFails = saveFail }, processes, new FakeFacts { Throw = factsFail }, []);
        Guid session = Guid.NewGuid(); runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable);
        runtime.Report(session, 1, Fixture.Heartbeat());
        Assert.True(runtime.Tick().Protecting);
    }
    [Fact]
    public void Termination_failures_retry_and_history_clear_does_not_clear_tracking()
    {
        var clock = new FakeClock();
        var processes = new FakeProcesses { Snapshot = [Fixture.Process()], Result = new(false, "Access denied") };
        var runtime = new GuardRuntime(new FakeStore(), processes, new FakeFacts(), [], clock);
        Assert.Equal(1, runtime.Tick().FailedCount);
        Assert.Single(runtime.Tick().Events);
        Assert.Equal(2, processes.Killed.Count);
        runtime.ClearHistory();
        Assert.Empty(runtime.Status.Events);
        processes.Result = new(true);
        Assert.Equal(0, runtime.Tick().FailedCount);
        Assert.Equal(3, processes.Killed.Count);
        clock.Advance(TimeSpan.FromHours(25));
        processes.Snapshot.Clear();
        Assert.Empty(runtime.Tick().Events);
    }
    [Fact]
    public void Guardian_and_proxy_recovery_executables_are_never_terminated()
    {
        var policy = Fixture.Policy with { Executables = [new(@"C:\apps\AI\guard.exe", "bad selection")] };
        var processes = new FakeProcesses { Snapshot = [Fixture.Process(1, @"C:\apps\AI\guard.exe"), Fixture.Process(2, @"C:\apps\AI\proxy.exe")] };
        var runtime = new GuardRuntime(new FakeStore { Policy = policy }, processes, new FakeFacts(), [@"C:\apps\AI\guard.exe"]);
        runtime.Tick(); Assert.Empty(processes.Killed);
    }
    [Fact]
    public void Prepare_transition_performs_sweep_before_ack_and_reports_failures()
    {
        var processes = new FakeProcesses { Snapshot = [Fixture.Process()], Result = new(false, "cannot terminate") };
        var runtime = new GuardRuntime(new FakeStore(), processes, new FakeFacts(), []);
        Guid session = Guid.NewGuid();
        runtime.Connect(session, Fixture.Main, Fixture.Policy.MainExecutable); runtime.Report(session, 1, Fixture.Heartbeat());
        Assert.False(runtime.Tick().Protecting);
        var reply = runtime.PrepareTransition();
        Assert.True(reply.Protecting); Assert.Equal(1, reply.FailedCount); Assert.Single(processes.Killed);
    }
    [Fact]
    public void Missing_policy_is_visible_and_never_fabricated_as_ready()
    {
        var runtime = new GuardRuntime(new FakeStore { Policy = null }, new FakeProcesses(), new FakeFacts(), []);
        Assert.True(runtime.Tick().Protecting);
        Assert.Contains("名单", runtime.Status.Reason);
    }
}
