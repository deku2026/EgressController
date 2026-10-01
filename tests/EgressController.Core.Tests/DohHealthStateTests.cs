using EgressController.Core.Protection;

namespace EgressController.Core.Tests;

public sealed class DohHealthStateTests
{
    [Fact]
    public void Startup_stays_protected_and_recheck_preserves_confirmed_result_past_old_twenty_second_limit()
    {
        var clock = new Clock();
        var state = new DohHealthState(clock);
        DohCheckTicket first = state.BeginCheck();
        Assert.False(state.Snapshot.Ready);
        Assert.True(state.Complete(first, true));
        clock.Advance(19);
        DohCheckTicket recheck = state.BeginCheck();
        clock.Advance(8);
        Assert.True(state.Snapshot.Ready);
        Assert.True(state.Snapshot.Checking);
        Assert.Contains("复检", state.Snapshot.Reason);
        Assert.True(state.Complete(recheck, true));
    }

    [Fact]
    public void Both_failed_result_stops_fake_processes_but_pending_and_recovery_do_not()
    {
        var state = new DohHealthState();
        var processes = new FakeProcesses();
        var guard = new ProcessProtection(processes);
        ProtectedExecutable[] apps = [new(@"C:\app.exe", "App")];
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Sweep()
        {
            var health = state.Snapshot;
            guard.Sweep(apps, excluded, health.Ready, health.Reason);
        }
        Sweep(); // Unknown on launch.
        Assert.Equal(1, processes.Terminations);
        state.Complete(state.BeginCheck(), true);
        DohCheckTicket pending = state.BeginCheck();
        Sweep(); Sweep();
        Assert.Equal(1, processes.Terminations);
        state.RecordFailure(pending, "两个 DoH 均失败");
        Sweep();
        Assert.Equal(2, processes.Terminations);
        Assert.True(state.Complete(pending, false, "两个 DoH 均失败"));
        state.Complete(state.BeginCheck(), true);
        Sweep();
        Assert.Equal(2, processes.Terminations);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Superseded_results_cannot_change_new_configuration(bool lateResult)
    {
        var state = new DohHealthState();
        var old = state.BeginCheck();
        state.Invalidate("网卡已切换");
        var current = state.BeginCheck();
        Assert.False(state.RecordFailure(old, "旧失败"));
        Assert.False(state.Complete(old, lateResult, "旧结果"));
        Assert.False(state.Snapshot.Ready);
        Assert.True(state.Complete(current, true));
        Assert.True(state.Snapshot.Ready);
    }

    [Fact]
    public void Canceled_recheck_preserves_last_result_without_accepting_late_completion()
    {
        var state = new DohHealthState();
        state.Complete(state.BeginCheck(), true);
        var pending = state.BeginCheck();
        state.Cancel(pending);
        Assert.True(state.Snapshot.Ready);
        Assert.False(state.Snapshot.Checking);
        Assert.False(state.Complete(pending, false));
    }

    [Fact]
    public void Explicit_round_timeout_protects_and_late_success_cannot_unlock()
    {
        var clock = new Clock();
        var state = new DohHealthState(clock);
        state.Complete(state.BeginCheck(), true);
        var round = state.BeginCheck();
        clock.Advance(14);
        Assert.True(state.Snapshot.Ready);
        clock.Advance(1);
        Assert.False(state.Snapshot.Ready);
        Assert.Contains("检测超时", state.Snapshot.Reason);
        Assert.False(state.Complete(round, true));
        Assert.True(state.Complete(state.BeginCheck(), true));
        clock.Advance(30); // A dead monitor also cannot leave an indefinite allowance.
        Assert.False(state.Snapshot.Ready);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
    private sealed class FakeProcesses : IProcessControl
    {
        public int Terminations;
        public IReadOnlyList<GuardProcess> Capture() => [new(new(1, new DateTime(2026, 10, 1)), 0, @"C:\app.exe")];
        public TerminationResult Terminate(GuardProcess process) { Terminations++; return new(true); }
    }
}
