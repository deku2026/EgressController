using System.ComponentModel;
using EgressController.Core.Protection;

namespace EgressController.Guardian.Tests;

public sealed class GuardSafetyTests
{
    [Fact]
    public void Service_controls_are_fenced_in_mock_only_mode()
    {
        var paths = new GuardPaths("mock", "mock", "mock", @"C:\mock\data", @"C:\mock\install");
        var platform = new WindowsGuardPlatform(paths, @"C:\mock");
        Action[] liveActions = [() => platform.Query(), () => platform.Install(new(), Fixture.Policy), platform.Start,
            () => platform.Stop(false), () => platform.Stop(true), () => platform.SetAutoStart(true), platform.Uninstall,
            () => platform.ConfigureTray(true), platform.StartTray, () => GuardServiceHost.Run(paths),
            () => _ = GuardWindowsIdentity.BootId, () => GuardWindowsIdentity.ProcessSid(1),
            () => new WindowsGuardFacts().IsAlive(Fixture.Main, Fixture.Policy.MainExecutable)];
        foreach (var live in liveActions) Assert.Throws<InvalidOperationException>(live);
    }

    [Fact]
    public async Task Named_pipe_client_is_fenced_before_opening_any_pipe()
    {
        await using var client = new GuardPipeClient(new("mock", "mock", "mock", @"C:\mock", @"C:\mock"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Local_system_guard_limits_all_matches_and_descendants_to_owner_sid()
    {
        var inner = new FakeProcesses { Snapshot = [Fixture.Process(10), Fixture.Process(11)] };
        var scoped = new UserScopedProcessControl(inner, "owner", pid => pid == 10 ? "owner" : "other user");
        var guard = new ProcessProtection(scoped);
        guard.Sweep(Fixture.Policy.Executables, new HashSet<string>(), false, "mock");
        Assert.Equal(10u, Assert.Single(inner.Killed).Pid);
        Assert.False(scoped.Terminate(Fixture.Process(11)).Succeeded);
    }

    [Fact]
    public void Unreadable_owner_is_reported_for_recursive_path_without_killing_unverified_pid()
    {
        var inner = new FakeProcesses { Snapshot = [Fixture.Process(10, @"C:\apps\AI\new\worker.exe")] };
        var scoped = new UserScopedProcessControl(inner, "owner", _ => throw new Win32Exception(5));
        var guard = new ProcessProtection(scoped);
        Assert.Equal(1, guard.Sweep([], new HashSet<string>(), false, "mock", Fixture.Policy.Directories));
        Assert.Empty(inner.Killed); Assert.False(Assert.Single(guard.Events).Succeeded);
    }

    [Fact]
    public void Disappeared_process_is_not_an_endless_permission_error()
    {
        var inner = new FakeProcesses { Snapshot = [Fixture.Process()] };
        var scoped = new UserScopedProcessControl(inner, "owner", _ => throw new Win32Exception(87));
        Assert.Empty(scoped.Capture()); Assert.Empty(scoped.InspectionFailures);
        Assert.True(scoped.Terminate(Fixture.Process()).AlreadyExited);
    }

    [Fact]
    public void Native_aot_smoke_scenario_uses_only_fakes_and_private_temporary_sqlite()
        => Assert.Equal(0, GuardSmoke.Run());
}
