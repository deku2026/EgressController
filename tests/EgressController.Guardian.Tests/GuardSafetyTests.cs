using System.ComponentModel;
using EgressController.Core.Protection;

namespace EgressController.Guardian.Tests;

public sealed class GuardSafetyTests
{
    [Fact]
    public void Pipe_server_is_bound_to_scm_pid_command_and_system_account_without_debug_privilege()
    {
        var paths = new GuardPaths("owner", "guard", "guard", @"C:\mock\data", @"C:\mock\install");
        var service = new GuardServiceInfo(paths.ServiceName, GuardServiceState.Running, true, 300,
            paths.ServiceCommandLine, "LocalSystem");
        GuardPipeAccess.ValidateServer(paths, 300, service);
        GuardPipeAccess.ValidateServer(paths, 300, service with { State = GuardServiceState.Starting });
        GuardServiceInfo[] invalid = [service with { Name = "other" }, service with { Pid = 301 },
            service with { State = GuardServiceState.Stopped }, service with { Account = "owner" },
            service with { BinaryPath = @"C:\mock\fake.exe" }];
        foreach (var value in invalid)
            Assert.Throws<UnauthorizedAccessException>(() => GuardPipeAccess.ValidateServer(paths, 300, value));
        Assert.Throws<UnauthorizedAccessException>(() => GuardPipeAccess.ValidateServer(paths, 0, service));
    }
    [Fact]
    public void Ordinary_tray_access_matches_acl_without_pipe_creation_or_impersonation_rights()
    {
        const uint pipeCreateInstance = 4, genericReadWrite = 0xc0000000;
        Assert.Equal(0u, GuardPipeAccess.ClientRights & (pipeCreateInstance | genericReadWrite));
        Assert.Equal(3u, GuardPipeAccess.ClientRights & 3); // Both read-data and write-data.
        Assert.Contains($"(A;;0x{GuardPipeAccess.ClientRights:x};;;owner)", GuardPipeAccess.Descriptor("owner"));
        Assert.Contains("(D;;GA;;;NU)", GuardPipeAccess.Descriptor("owner"));
        Assert.Equal(0x00110000u, GuardPipeAccess.ClientFlags & 0x001f0000); // SQOS + Identification, not Impersonation.
    }
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
    public void Local_system_guard_limits_all_saved_path_matches_to_owner_sid()
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
        Assert.Equal(1, guard.Sweep([new(@"C:\apps\AI\new\worker.exe", "AI")], new HashSet<string>(), false, "mock"));
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
