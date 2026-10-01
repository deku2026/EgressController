using EgressController.Core.Protection;

namespace EgressController.Core.Tests;

public sealed class ProcessProtectionTests
{
    private static readonly DateTime Started = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<string> NoExclusions = new(StringComparer.OrdinalIgnoreCase);
    private static GuardProcess P(uint pid, string path, uint parent = 0, int seconds = 0)
        => new(new(pid, Started.AddSeconds(seconds)), parent, path);

    [Fact]
    public void All_exact_path_instances_and_descendants_are_terminated_without_touching_same_names_elsewhere()
    {
        var fake = new FakeProcesses([
            P(1, @"C:\Apps\A\a.exe"), P(2, @"c:\apps\a\A.EXE"),
            P(3, @"C:\Apps\A\nested\helper.exe"), P(4, @"C:\Other\a.exe"),
            P(5, @"C:\Shared\child.exe", 1, 1), P(6, @"C:\Shared\grandchild.exe", 5, 2)]);
        var guard = new ProcessProtection(fake);
        guard.Sweep([new(@"C:\Apps\A\a.exe", "A"), new(@"C:\Apps\A\nested\helper.exe", "A")], NoExclusions, false, "TUN 未运行");
        Assert.Equal(new uint[] { 1, 2, 3, 5, 6 }, fake.Killed.Select(process => process.Key.Pid).Order());
        Assert.All(guard.Events, entry => Assert.True(entry.Succeeded));
    }

    [Fact]
    public void Healthy_never_terminates_but_retains_descendants_after_parent_exit()
    {
        var fake = new FakeProcesses([P(1, @"C:\a.exe"), P(2, @"C:\child.exe", 1, 1)]);
        var guard = new ProcessProtection(fake);
        ProtectedExecutable[] targets = [new(@"C:\a.exe", "A")];
        guard.Sweep(targets, NoExclusions, true, "ready");
        Assert.Empty(fake.Killed);
        fake.Snapshot = [P(2, @"C:\child.exe", 1, 1)];
        guard.Sweep(targets, NoExclusions, false, "DoH 超时");
        Assert.Equal(2u, Assert.Single(fake.Killed).Key.Pid);
    }

    [Fact]
    public void Permission_failures_are_retried_but_not_repeated_in_the_event_list()
    {
        var fake = new FakeProcesses([P(1, @"C:\a.exe")]) { Result = new(false, "拒绝访问") };
        var guard = new ProcessProtection(fake);
        ProtectedExecutable[] targets = [new(@"C:\a.exe", "A")];
        guard.Sweep(targets, NoExclusions, false, "offline");
        guard.Sweep(targets, NoExclusions, false, "offline");
        Assert.Equal(2, fake.Killed.Count);
        Assert.Equal(1, guard.FailedCount);
        Assert.Equal("拒绝访问", Assert.Single(guard.Events).Error);
        fake.Result = new(true);
        guard.Sweep(targets, NoExclusions, false, "offline");
        Assert.Equal(0, guard.FailedCount);
        Assert.True(guard.Events[0].Succeeded);
    }

    [Fact]
    public void Reused_pid_and_younger_parent_are_not_treated_as_a_known_descendant()
    {
        var fake = new FakeProcesses([P(1, @"C:\a.exe"), P(2, @"C:\child.exe", 1, 1)]);
        var guard = new ProcessProtection(fake);
        ProtectedExecutable[] targets = [new(@"C:\a.exe", "A")];
        guard.Sweep(targets, NoExclusions, true, "ready");
        fake.Snapshot = [P(2, @"C:\unrelated.exe", 0, 50)];
        guard.Sweep(targets, NoExclusions, false, "offline");
        Assert.Empty(fake.Killed);
        fake.Snapshot = [P(1, @"C:\a.exe", 0, 10), P(3, @"C:\older.exe", 1, 2)];
        guard.Sweep(targets, NoExclusions, false, "offline");
        Assert.Equal(1u, Assert.Single(fake.Killed).Key.Pid);
    }

    [Fact]
    public void Recovery_dependencies_are_excluded_even_when_they_are_descendants()
    {
        var fake = new FakeProcesses([P(1, @"C:\a.exe"), P(2, @"C:\proxy.exe", 1, 1)]);
        var guard = new ProcessProtection(fake);
        guard.Sweep([new(@"C:\a.exe", "A"), new(@"C:\proxy.exe", "Proxy")],
            new HashSet<string>([@"C:\proxy.exe"], StringComparer.OrdinalIgnoreCase), false, "offline");
        Assert.Equal(1u, Assert.Single(fake.Killed).Key.Pid);
    }

    [Fact]
    public void Deselection_and_recovery_stop_termination_without_starting_any_process()
    {
        var fake = new FakeProcesses([P(1, @"C:\a.exe")]);
        var guard = new ProcessProtection(fake);
        guard.Sweep([new(@"C:\a.exe", "A")], NoExclusions, true, "ready");
        guard.Sweep([], NoExclusions, false, "offline");
        Assert.Empty(fake.Killed);
    }

    [Fact]
    public void Deselecting_an_app_drops_its_tree_even_when_another_app_has_the_same_display_name()
    {
        var fake = new FakeProcesses([P(1, @"C:\A\app.exe"), P(2, @"C:\B\app.exe"), P(3, @"C:\child.exe", 1, 1)]);
        var guard = new ProcessProtection(fake);
        guard.Sweep([new(@"C:\A\app.exe", "Browser"), new(@"C:\B\app.exe", "Browser")], NoExclusions, true, "ready");
        guard.Sweep([new(@"C:\B\app.exe", "Browser")], NoExclusions, false, "offline");
        Assert.Equal(2u, Assert.Single(fake.Killed).Key.Pid);
    }

    [Fact]
    public void Uninspectable_matching_names_are_reported_without_guessing_a_path_or_terminating()
    {
        var fake = new FakeProcesses([]) { InspectionFailures = [new(12, "app.exe", "权限不足")] };
        var guard = new ProcessProtection(fake);
        guard.Sweep([new(@"C:\A\app.exe", "A")], NoExclusions, false, "offline");
        guard.Sweep([new(@"C:\A\app.exe", "A")], NoExclusions, false, "offline");
        Assert.Empty(fake.Killed);
        Assert.Equal(1, guard.FailedCount);
        Assert.False(Assert.Single(guard.Events).Succeeded);
    }

    [Theory]
    [InlineData(false, false, null, false, "TUN")]
    [InlineData(true, true, null, true, "配置")]
    [InlineData(true, false, "Redmi 已断开", true, "Redmi")]
    [InlineData(true, false, null, false, "DoH")]
    public void Every_unready_condition_keeps_protection(bool tun, bool busy, string? network, bool doh, string expected)
    {
        var state = ProtectionReadiness.Evaluate(tun, null, busy, null, network, new(doh, false, doh ? "ready" : "等待 DoH"));
        Assert.False(state.Ready);
        Assert.Contains(expected, state.Reason);
    }

    [Fact]
    public void Unknown_health_cannot_unlock_and_confirmed_health_can()
    {
        Assert.False(ProtectionReadiness.Evaluate(true, null, false, null, null, new(false, true, "首次检测")).Ready);
        Assert.True(ProtectionReadiness.Evaluate(true, null, false, null, null, new(true, true, "复检中")).Ready);
    }

    [Fact]
    public async Task Supervisor_retries_failures_without_overlapping_startup_or_blocking_guard()
    {
        int starts = 0;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var supervisor = new ForcedTunSupervisor(async _ => { Interlocked.Increment(ref starts); await pending.Task; throw new IOException("TUN 占用"); });
        supervisor.Tick(false, false, CancellationToken.None);
        Task first = supervisor.Pending;
        supervisor.Tick(false, false, CancellationToken.None);
        Assert.Same(first, supervisor.Pending);
        pending.SetResult(); await first;
        Assert.Equal(1, starts);
        Assert.Equal("TUN 占用", supervisor.LastError);
        supervisor.Tick(false, false, CancellationToken.None);
        await supervisor.Pending;
        Assert.Equal(2, starts);
        supervisor.Tick(true, false, CancellationToken.None);
        supervisor.Tick(false, true, CancellationToken.None);
        Assert.Equal(2, starts);
    }

    private sealed class FakeProcesses(IReadOnlyList<GuardProcess> initial) : IProcessControl
    {
        public IReadOnlyList<GuardProcess> Snapshot { get; set; } = initial;
        public IReadOnlyList<ProcessInspectionFailure> InspectionFailures { get; init; } = [];
        public List<GuardProcess> Killed { get; } = [];
        public TerminationResult Result { get; set; } = new(true);
        public IReadOnlyList<GuardProcess> Capture() => Snapshot;
        public TerminationResult Terminate(GuardProcess process) { Killed.Add(process); return Result; }
    }
}
