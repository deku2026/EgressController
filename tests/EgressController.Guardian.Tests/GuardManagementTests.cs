namespace EgressController.Guardian.Tests;

public sealed class GuardManagementTests
{
    private static GuardRegistration Initial => new()
    {
        OwnerSid = "S-1-5-21-1-2-3-1001", MainExecutable = Fixture.Policy.MainExecutable, InstallDirectory = @"C:\mock\guard",
    };
    [Fact]
    public void First_launch_installs_starts_and_enables_startup_only_once()
    {
        var store = new Registrations(); var platform = new Platform();
        var manager = new GuardManagement(store, platform, "boot1");
        manager.EnsureForMain(Initial, Fixture.Policy);
        Assert.True(store.Value!.AutoStart);
        Assert.Equal(["install", "tray:True", "start", "show-tray"], platform.Calls);
        platform.Calls.Clear(); manager.EnsureForMain(Initial, Fixture.Policy);
        Assert.Equal(["show-tray"], platform.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manual_stop_and_force_stop_are_persisted_before_action_and_survive_main_relaunch(bool force)
    {
        var store = new Registrations { Value = Initial with { Installed = true } };
        var platform = new Platform { OnStop = () => Assert.Equal("boot1", store.Value!.StoppedOnBoot) };
        var manager = new GuardManagement(store, platform, "boot1");
        manager.Stop(force);
        Assert.Equal(force ? "force-stop" : "stop", Assert.Single(platform.Calls));
        platform.Calls.Clear(); manager.EnsureForMain(Initial, Fixture.Policy);
        Assert.Equal(["show-tray"], platform.Calls);
        manager.Start();
        Assert.Null(store.Value!.StoppedOnBoot);
        Assert.Equal(GuardServiceState.Running, platform.Info.State);
    }
    [Fact]
    public void Next_boot_respects_auto_start_but_does_not_reenable_disabled_setting()
    {
        var store = new Registrations { Value = Initial with { Installed = true, StoppedOnBoot = "boot1" } };
        var platform = new Platform();
        new GuardManagement(store, platform, "boot2").EnsureForMain(Initial, Fixture.Policy);
        Assert.Contains("start", platform.Calls);
        platform.Calls.Clear();
        store.Value = store.Value with { AutoStart = false };
        new GuardManagement(store, platform, "boot3").EnsureForMain(Initial, Fixture.Policy);
        Assert.DoesNotContain("start", platform.Calls);
        Assert.False(store.Value.AutoStart);
    }
    [Fact]
    public void Disabling_auto_start_does_not_stop_running_service()
    {
        var store = new Registrations { Value = Initial with { Installed = true } };
        var platform = new Platform { Info = new("mock", GuardServiceState.Running, true, 1, "mock.exe", "mock") };
        var manager = new GuardManagement(store, platform, "boot1");
        manager.SetAutoStart(false);
        Assert.Equal(["auto:False", "tray:False"], platform.Calls);
        Assert.Equal(GuardServiceState.Running, platform.Info.State);
        Assert.False(store.Value!.AutoStart);
    }
    [Fact]
    public void Stop_and_disable_then_uninstall_do_not_get_automatically_undone()
    {
        var store = new Registrations { Value = Initial with { Installed = true } }; var platform = new Platform();
        var manager = new GuardManagement(store, platform, "boot1");
        manager.Stop(disableAutoStart: true);
        Assert.False(store.Value!.AutoStart);
        Assert.Equal(["auto:False", "tray:False", "stop"], platform.Calls);
        platform.Calls.Clear(); manager.Uninstall();
        Assert.True(store.Value.Uninstalled);
        Assert.Equal(["tray:False", "uninstall"], platform.Calls);
        platform.Calls.Clear(); new GuardManagement(store, platform, "boot2").EnsureForMain(Initial, Fixture.Policy);
        Assert.Empty(platform.Calls);
        Assert.Throws<InvalidOperationException>(() => manager.Start());
    }
    [Fact]
    public void Missing_service_after_install_is_reported_not_silently_recreated()
    {
        var store = new Registrations { Value = Initial with { Installed = true } };
        var platform = new Platform { Info = new("mock", GuardServiceState.Missing, true, 0, "mock.exe", "mock") };
        Assert.Throws<InvalidOperationException>(() => new GuardManagement(store, platform, "boot1").EnsureForMain(Initial, Fixture.Policy));
        Assert.Empty(platform.Calls);
    }
    [Fact]
    public void Failed_stop_keeps_user_intent_and_reports_error()
    {
        var store = new Registrations { Value = Initial with { Installed = true } };
        var platform = new Platform { OnStop = () => throw new TimeoutException("mock hung service") };
        Assert.Throws<TimeoutException>(() => new GuardManagement(store, platform, "boot1").Stop());
        Assert.Equal("boot1", store.Value!.StoppedOnBoot);
    }
    [Fact]
    public void Failed_intent_persistence_does_not_issue_stop()
    {
        var store = new Registrations { Value = Initial with { Installed = true }, FailWrites = true };
        var platform = new Platform();
        Assert.Throws<IOException>(() => new GuardManagement(store, platform, "boot1").Stop());
        Assert.Empty(platform.Calls);
    }
    [Fact]
    public void Partial_auto_start_failure_keeps_requested_preference_for_repair()
    {
        var store = new Registrations { Value = Initial with { Installed = true } };
        var platform = new Platform { FailTray = true };
        Assert.Throws<IOException>(() => new GuardManagement(store, platform, "boot1").SetAutoStart(false));
        Assert.False(store.Value!.AutoStart);
        Assert.False(platform.Info.AutoStart);
    }
    internal sealed class Registrations : IGuardRegistrationStore
    {
        public GuardRegistration? Value;
        public bool FailWrites;
        public GuardRegistration? Load() => Value;
        public void Save(GuardRegistration value) { if (FailWrites) throw new IOException("mock disk full"); Value = value; }
    }
    internal sealed class Platform : IGuardServicePlatform
    {
        public List<string> Calls = [];
        public GuardServiceInfo Info = new("mock", GuardServiceState.Stopped, true, 0, "mock.exe", "mock");
        public Action? OnStop;
        public bool FailTray;
        public GuardServiceInfo Query() => Info;
        public void Install(GuardRegistration registration, GuardPolicy policy) => Calls.Add("install");
        public void Start() { Calls.Add("start"); Info = Info with { State = GuardServiceState.Running }; }
        public void Stop(bool force) { Calls.Add(force ? "force-stop" : "stop"); OnStop?.Invoke(); Info = Info with { State = GuardServiceState.Stopped }; }
        public void SetAutoStart(bool enabled) { Calls.Add("auto:" + enabled); Info = Info with { AutoStart = enabled }; }
        public void Uninstall() { Calls.Add("uninstall"); Info = Info with { State = GuardServiceState.Missing }; }
        public void ConfigureTray(bool enabled) { Calls.Add("tray:" + enabled); if (FailTray) throw new IOException("mock registry failure"); }
        public void StartTray() => Calls.Add("show-tray");
    }
}
