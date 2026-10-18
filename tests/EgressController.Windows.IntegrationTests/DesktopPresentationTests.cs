using Avalonia;
using EgressController.App;
using EgressController.App.Preview;
using EgressController.App.Services;
using EgressController.App.ViewModels;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Launcher.Discovery;
using EgressController.Presentation;
using EgressController.Windows.Shell;

namespace EgressController.Windows.IntegrationTests;

// Geometry, metadata and injected artwork only. No native tray, EXE extraction or app launch.
public sealed class DesktopPresentationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Tray_menu_uses_owning_monitor_scale_exactly_once(double scale)
    {
        var anchor = new NotificationAnchor(new(-400, 1020, -376, 1044), new(-1920, 0, 0, 1020), scale);
        var position = TrayMenuPlacement.Place(anchor, new Size(320, 180));
        Assert.Equal(anchor.Icon.Top - (int)Math.Ceiling(180 * scale) - (int)Math.Round(5 * scale), position.Y);
        Assert.InRange(position.X, -1920, -(int)Math.Ceiling(320 * scale));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1900, 0)]
    [InlineData(0, 1050)]
    [InlineData(1900, 1050)]
    public void Tray_menu_is_clamped_for_all_taskbar_edges(int x, int y)
    {
        var position = TrayMenuPlacement.Place(new(new(x, y, x + 20, y + 20), new(40, 40, 1880, 1040), 1.5), new Size(320, 300));
        Assert.InRange(position.X, 40, 1400);
        Assert.InRange(position.Y, 40, 590);
    }

    [Fact]
    public void Invalid_scale_falls_back_to_one()
    {
        var anchor = new NotificationAnchor(new(500, 900, 520, 920), new(0, 0, 1000, 900), double.NaN);
        Assert.Equal(795, TrayMenuPlacement.Place(anchor, new Size(200, 100)).Y);
    }

    [Fact]
    public void Native_notification_structures_match_the_Windows_x64_ABI()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal((976, 40, 80), WindowsNotificationIcon.StructureSizes);
    }

    [Fact]
    public void Ico_parser_rejects_out_of_range_payloads()
    {
        Assert.Throws<InvalidDataException>(() => WindowsNotificationIcon.SelectImage([], 16));
        byte[] malformed = new byte[22]; malformed[2] = 1; malformed[4] = 1;
        BitConverter.GetBytes(int.MaxValue).CopyTo(malformed, 14);
        BitConverter.GetBytes(22).CopyTo(malformed, 18);
        Assert.Throws<InvalidDataException>(() => WindowsNotificationIcon.SelectImage(malformed, 16));
    }

    [Fact]
    public void Native_boundaries_refuse_mock_mode_before_any_os_call()
    {
        RuntimeSafety.EnableMockOnly();
        Assert.Throws<InvalidOperationException>(() => new WindowsNotificationIcon("mock", [], ""));
        Assert.Throws<InvalidOperationException>(() => AppIconLoader.Load(@"C:\fake.exe"));
        Assert.Throws<InvalidOperationException>(() => AppIconLoader.Describe(@"C:\fake.exe"));
        Assert.Throws<InvalidOperationException>(() => new PowerShellCommandLocationSource().Find(["claude"]));
    }

    [Fact]
    public void Helper_visual_reuses_owner_name_icon_and_key_case_insensitively()
    {
        int reads = 0;
        using var visuals = new ApplicationVisualCatalog(true, _ => { reads++; return null; });
        var owner = new LaunchTarget { Id = "claude", CatalogId = "claude", Name = "Claude", CanonicalExecutable = @"C:\Claude\Claude.exe",
            OwnedExecutables = [@"C:\Claude\Claude.exe", @"C:\Claude\resources\claude-ssh-proxy.exe"] };
        visuals.Update([owner], new());
        var main = visuals.ForApplication(owner);
        var helper = visuals.ForProcess(@"C:\CLAUDE\RESOURCES\CLAUDE-SSH-PROXY.EXE");
        Assert.Equal("Claude", helper.Name); Assert.Equal(main.Key, helper.Key); Assert.Equal(1, reads);
        Assert.Same(helper, visuals.ForProcess(@"c:\claude\resources\claude-ssh-proxy.exe"));
    }

    [Fact]
    public void Newly_resolved_owner_updates_existing_visual_without_touching_routing()
    {
        using var visuals = new ApplicationVisualCatalog(true, _ => throw new IOException("Bad icon"));
        string path = @"C:\bin\claude.exe";
        var before = visuals.ForProcess(path);
        var target = new LaunchTarget { Id = path, Name = "Claude CLI", Kind = LaunchKind.CliNative,
            CanonicalExecutable = path, CatalogId = "claude", OwnedExecutables = [path] };
        visuals.Update([target], new());
        var after = visuals.ForProcess(path);
        Assert.Same(before, after); Assert.Equal("Claude CLI", after.Name); Assert.True(after.IsCli); Assert.Null(after.Icon);
    }

    [Fact]
    public void Routes_display_live_adapter_names_and_device_descriptions_not_runtime_tags()
    {
        var preview = new PreviewController();
        string tag = "adapter-" + Guid.Parse(preview.Profile.DefaultAdapterId!).ToString("N");
        Assert.Equal("Ethernet", RouteNames.Outbound(tag, preview.Profile, preview.Adapters));
        Assert.Contains("Apple Mobile", RouteNames.Outbound(tag, preview.Profile, preview.Adapters, true));
        Assert.Equal("代理 · 127.0.0.1:7891", RouteNames.Outbound("upstream-7891", preview.Profile, preview.Adapters));
        Assert.DoesNotContain("adapter-", RouteNames.Outbound(tag, preview.Profile, []));
        var vm = new ConnectionsViewModel(preview) { Query = "Apple Mobile" };
        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, row => Assert.Equal("Ethernet", row.Route));
    }
}
