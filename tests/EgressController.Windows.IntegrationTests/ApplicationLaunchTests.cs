using System.Diagnostics;
using EgressController.App;
using EgressController.App.Preview;
using EgressController.App.Services;
using EgressController.App.ViewModels;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Windows.Process;

namespace EgressController.Windows.IntegrationTests;

public sealed class ApplicationLaunchTests
{
    private static LaunchTarget Desktop(string path = @"C:\Apps\Example\Example.exe") => new()
    {
        Id = "main", Name = "Example", CanonicalExecutable = path, Command = @"C:\Apps\Example\helper.exe",
        Arguments = "--profile \"My Profile\"", WorkingDirectory = @"C:\Apps\Example\work",
        Environment = new Dictionary<string, string> { ["EXAMPLE_MODE"] = "test" },
        OwnedExecutables = [@"C:\Apps\Example\helper.exe", path],
    };

    [Fact]
    public void Desktop_launch_uses_the_primary_entry_arguments_and_environment_without_PID_tracking()
    {
        ProcessStartInfo? started = null;
        var launcher = new WindowsLaunchService(info => started = info, (_, _) => throw new Exception("Wrong activation"), _ => true);
        var target = Desktop();
        launcher.Launch(target);
        Assert.NotNull(started);
        Assert.Equal(target.CanonicalExecutable, started.FileName);
        Assert.Equal(target.Arguments, started.Arguments);
        Assert.Equal(target.WorkingDirectory, started.WorkingDirectory);
        Assert.Equal("test", started.Environment["EXAMPLE_MODE"]);
        Assert.False(started.UseShellExecute);
    }

    [Fact]
    public void Packaged_application_uses_AUMID_even_with_recursive_runtime_EXEs()
    {
        string? activated = null;
        var launcher = new WindowsLaunchService(_ => throw new Exception("Package EXE must not launch directly"),
            (aumid, args) => { activated = aumid; Assert.Equal("--test", args); }, _ => throw new Exception("No EXE probing"));
        launcher.Launch(new() { Id = "package", Name = "Claude", Kind = LaunchKind.PackagedAumid,
            Aumid = "Claude_test!App", Arguments = "--test", CanonicalExecutable = @"C:\WindowsApps\Claude.exe",
            OwnedExecutables = [@"C:\Data\claude-code\claude.exe", @"C:\WindowsApps\Claude.exe"] });
        Assert.Equal("Claude_test!App", activated);
    }

    [Theory]
    [InlineData(LaunchKind.CliNative)] [InlineData(LaunchKind.CliWrapperResolved)] [InlineData(LaunchKind.Shortcut)]
    public void CLI_and_unresolved_shortcut_never_reach_native_launch(LaunchKind kind)
    {
        var launcher = new WindowsLaunchService(_ => throw new Exception("No launch"), (_, _) => throw new Exception("No launch"), _ => true);
        Assert.Throws<InvalidOperationException>(() => launcher.Launch(new() { Id = "test", Name = "test", Kind = kind, Command = @"C:\Apps\test.exe" }));
    }

    [Fact]
    public void Missing_main_does_not_fall_back_to_an_existing_helper()
    {
        var target = Desktop();
        var launcher = new WindowsLaunchService(_ => throw new Exception("No fallback"), (_, _) => throw new Exception("No fallback"),
            path => path.EndsWith("helper.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(target.CanonicalExecutable, Assert.Throws<FileNotFoundException>(() => launcher.Launch(target)).FileName);
    }

    [Theory]
    [InlineData(LaunchKind.DirectExe)] [InlineData(LaunchKind.PackagedAumid)]
    public void Launch_policy_rejects_a_new_primary_not_yet_in_the_applied_inventory(LaunchKind kind)
    {
        var target = new LaunchTarget { Id = "app", Name = "App", Kind = kind, Aumid = "Mock_family!App",
            CanonicalExecutable = @"C:\NewVersion\App.exe", InventoryReady = true };
        var profile = new EgressProfileDocument { Applications = [new() { DiscoveryKey = target.DiscoveryKey,
            ExecutablePaths = [@"C:\OldVersion\App.exe"] }] };
        Assert.Contains("保护清单", ApplicationLaunchPolicy.BlockReason(target, profile, true, false, new(true, "ready")));
    }

    [Fact]
    public void Mock_mode_fences_the_production_package_activation_boundary()
    {
        RuntimeSafety.EnableMockOnly();
        Assert.Throws<InvalidOperationException>(() => new WindowsLaunchService().Launch(new()
        { Id = "package", Name = "package", Kind = LaunchKind.PackagedAumid, Aumid = "Mock_test!App" }));
    }

    [Theory]
    [InlineData(false, true, false, true)] [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)] [InlineData(true, true, false, false)]
    public void Unselected_app_TUN_configuration_and_protection_are_rechecked(bool selected, bool tun, bool updating, bool ready)
    {
        var target = Desktop();
        var profile = new EgressProfileDocument { Applications = selected ? [new() { DiscoveryKey = target.DiscoveryKey, ExecutablePaths = target.OwnedExecutables }] : [] };
        Assert.NotNull(ApplicationLaunchPolicy.BlockReason(target, profile, tun, updating, new(ready, "test")));
    }

    [Fact]
    public void Saved_path_matching_is_case_insensitive_and_an_unprotected_main_is_refused()
    {
        var target = Desktop();
        var profile = new EgressProfileDocument { Applications = [new() { DiscoveryKey = target.DiscoveryKey,
            ExecutablePaths = [target.CanonicalExecutable!.ToUpperInvariant()] }] };
        Assert.Null(ApplicationLaunchPolicy.BlockReason(target, profile, true, false, new(true, "ready")));
        profile = profile with { Applications = [profile.Applications[0] with { ExecutablePaths = [target.Command!] }] };
        Assert.NotNull(ApplicationLaunchPolicy.BlockReason(target, profile, true, false, new(true, "ready")));
    }

    [Fact]
    public async Task Button_follows_selection_hides_for_both_CLI_kinds_and_never_mutates_routes_or_inventory()
    {
        var backend = new PreviewController();
        var vm = new AppsViewModel(backend);
        var app = vm.Entries.First();
        var profile = backend.Profile;
        Assert.True(app.ShowLaunch); Assert.True(app.CanLaunch);
        await app.LaunchCommand.ExecuteAsync(null);
        Assert.Equal(app.Target, Assert.Single(backend.LaunchedApplications));
        Assert.Same(profile, backend.Profile); Assert.Equal(0, backend.InventoryBuildCount);
        app.Routing.IsSelected = false;
        Assert.False(app.ShowLaunch); Assert.False(app.LaunchCommand.CanExecute(null));
        await app.LaunchCommand.ExecuteAsync(null);
        Assert.Single(backend.LaunchedApplications);
        var cli = vm.Entries.First(item => item.Target.Kind == LaunchKind.CliNative);
        cli.Routing.IsSelected = true;
        Assert.False(cli.ShowLaunch); Assert.False(cli.CanLaunch);
    }

    [Fact]
    public async Task Busy_launch_survives_list_refresh_and_reports_failure_without_rebuilding_rows()
    {
        var completion = new TaskCompletionSource<ControllerOperationResult>();
        var backend = new PreviewController { LaunchOverride = _ => completion.Task };
        var vm = new AppsViewModel(backend);
        var app = vm.Entries.First();
        Task launching = app.LaunchCommand.ExecuteAsync(null);
        Assert.False(app.CanLaunch); Assert.Equal("启动中", app.LaunchText);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Same(app, vm.Entries.First()); Assert.False(app.CanLaunch);
        completion.SetResult(ControllerOperationResult.Failure("模拟：主程序文件不存在"));
        await launching;
        Assert.True(app.CanLaunch); Assert.True(app.HasRowIssue);
        Assert.Contains("主程序文件不存在", app.RowDetail);
        Assert.Empty(backend.LaunchedApplications);
    }

    [Theory]
    [InlineData("starting")] [InlineData("offline")] [InlineData("failed")]
    public async Task Preview_and_UI_do_not_launch_while_protection_is_active(string scenario)
    {
        var backend = new PreviewController(scenario);
        var app = new AppsViewModel(backend).Entries.First();
        Assert.True(app.ShowLaunch); Assert.False(app.CanLaunch);
        Assert.False((await backend.LaunchApplicationAsync(app.DiscoveryKey)).Succeeded);
        Assert.Empty(backend.LaunchedApplications);
    }
}
