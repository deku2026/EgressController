using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class InstalledApplicationTests
{
    [Fact]
    public void Vendor_registry_resolves_League_without_enumerating_its_directory()
    {
        var record = new InstalledApplication("vendor:lol", "英雄联盟", @"C:\WeGameApps\lol", @"C:\WeGameApps\lol\TCLS\Client.exe", null, null, "平台登记");
        var app = InstalledApplicationResolver.Resolve(record, _ => true, _ => true, _ => throw new Exception("No directory scan"));
        Assert.Equal(record.Entry, app.CanonicalExecutable); Assert.Equal(record.Location, Assert.Single(app.OwnedRoots));
        Assert.False(app.InventoryReady); Assert.Empty(app.OwnedExecutables); Assert.False(app.RouteSelected);
    }
    [Fact]
    public void Uninstaller_icon_locates_the_directory_but_is_never_the_main_executable()
    {
        var record = new InstalledApplication("steam", "Steam", null, null, @"C:\Games\Steam\uninstall.exe", @"C:\Games\Steam\uninstall.exe", "登记");
        int reads = 0;
        var app = InstalledApplicationResolver.Resolve(record, _ => true, _ => true,
            root => { reads++; Assert.Equal(@"C:\Games\Steam", root); return [root + @"\uninstall.exe", root + @"\steam.exe"]; });
        Assert.Equal(@"C:\Games\Steam\steam.exe", app.CanonicalExecutable); Assert.Equal(1, reads);
    }
    [Theory]
    [InlineData("MsiExec.exe /X{AA}")] [InlineData("rundll32.exe anything")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /X{AA}")]
    public void Shared_uninstall_hosts_do_not_become_application_roots(string uninstall)
    {
        var app = InstalledApplicationResolver.Resolve(new("x", "Component", null, null, null, uninstall, "登记"),
            _ => true, _ => true, _ => throw new Exception("Must not scan shared directories"));
        Assert.Empty(app.OwnedRoots); Assert.Null(app.CanonicalExecutable);
    }
    [Fact]
    public void Steam_libraries_are_read_from_metadata_and_each_game_gets_its_own_root()
    {
        var libraries = GameLibraryManifest.SteamLibraries(@"C:\Steam", "\"libraryfolders\" { \"1\" { \"path\" \"D:\\\\Games\" } }");
        Assert.Contains(@"D:\Games", libraries);
        var game = GameLibraryManifest.SteamGame(@"D:\Games", "\"AppState\" { \"appid\" \"42\" \"name\" \"Example Game\" \"installdir\" \"Example\" }");
        Assert.NotNull(game); Assert.Equal(@"D:\Games\steamapps\common\Example", game.Location);
        Assert.Equal("steam:42", game.Key);
    }
    [Theory]
    [InlineData("..\\..\\Windows")] [InlineData("C:\\Windows")] [InlineData(".")]
    public void Steam_metadata_cannot_escape_the_game_directory(string directory)
        => Assert.Null(GameLibraryManifest.SteamGame(@"C:\Steam", $"\"appid\" \"42\" \"installdir\" \"{directory}\""));
    [Theory]
    [InlineData(@"C:\")] [InlineData(@"C:\Windows")] [InlineData(@"D:\WeGameApps")] [InlineData(@"D:\Steam\steamapps")]
    public void Broad_roots_are_refused(string directory) => Assert.Throws<IOException>(() => ApplicationDirectoryBoundary.RequireScoped(directory));

    public static IEnumerable<object[]> MetadataCombinations => Enumerable.Range(0, 16).Select(flags => new object[] { flags });
    [Theory, MemberData(nameof(MetadataCombinations))]
    public void Registry_field_combinations_never_select_the_uninstaller_or_eagerly_index(int flags)
    {
        const string root = @"D:\Apps\Example", main = @"D:\Apps\Example\Example.exe", uninstall = @"D:\Apps\Example\uninstall.exe";
        var record = new InstalledApplication("registered:example", "Example", (flags & 1) != 0 ? root : null,
            (flags & 2) != 0 ? main : null, (flags & 4) != 0 ? uninstall : null, (flags & 8) != 0 ? uninstall : null, "登记");
        var target = InstalledApplicationResolver.Resolve(record, path => path == main || path == uninstall,
            path => path == root, path => { Assert.Equal(root, path); return [uninstall, main]; });
        Assert.NotEqual(uninstall, target.CanonicalExecutable);
        Assert.Empty(target.OwnedExecutables); Assert.False(target.InventoryReady); Assert.False(target.RouteSelected);
        if (flags == 0 || flags == 4) Assert.Null(target.CanonicalExecutable);
        else Assert.Equal(main, target.CanonicalExecutable);
    }

    [Fact]
    public void Unreadable_install_directory_keeps_the_registered_application_visible()
    {
        var app = InstalledApplicationResolver.Resolve(new("x", "Example", @"D:\Example", null, null, null, "登记"),
            _ => false, _ => true, _ => throw new UnauthorizedAccessException());
        Assert.Equal("Example", app.Name); Assert.Single(app.OwnedRoots); Assert.False(app.InventoryReady);
    }
}
