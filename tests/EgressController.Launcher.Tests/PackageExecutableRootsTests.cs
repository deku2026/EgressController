using EgressController.Core.Models;
using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class PackageExecutableRootsTests : IDisposable
{
    private const string Family = "Claude_pzs8sxrjxfjjc";
    private readonly string _fixture = Path.Combine(Path.GetTempPath(), "EgressController.PackageRootsTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Claude_pzs8sxrjxfjjc")]
    [InlineData("CLAUDE_PZS8SXRJXFJJC")]
    [InlineData("OpenAI.Codex_2p2nqsd0c76g0")]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe")]
    public void Any_valid_family_maps_to_its_own_data_root(string family)
    {
        var roots = PackageExecutableRoots.Resolve(@"D:\AppStore\VersionedPackage", null, @"C:\Users\Demo\AppData\Local", family);
        Assert.Equal(2, roots.Count);
        Assert.Equal(Path.Combine(@"C:\Users\Demo\AppData\Local\Packages", family), roots[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Claude")]
    [InlineData("Claude_2.42304.0.0_x64_pzs8sxrjxfjjc")]
    [InlineData("Claude_2.42304.0.0_x64__pzs8sxrjxfjjc")]
    [InlineData("..\\Other_pzs8sxrjxfjjc")]
    [InlineData("Claude_pzs8sxrjxfjjc\\..\\Other")]
    [InlineData("Claude_*")]
    public void Full_package_names_prefixes_and_traversal_are_not_family_identifiers(string family)
        => Assert.Throws<ArgumentException>(() => PackageExecutableRoots.DataRoot(@"C:\Users\Demo\AppData\Local", family));

    [Fact]
    public void Install_version_or_volume_changes_do_not_change_the_package_data_identity()
    {
        const string local = @"E:\Profiles\Demo\Local";
        var old = PackageExecutableRoots.Resolve(@"C:\WindowsApps\Claude_1.0_x64__pzs8sxrjxfjjc", null, local, Family);
        var current = PackageExecutableRoots.Resolve(@"D:\WindowsApps\Claude_2.0_x64__pzs8sxrjxfjjc", null, local, Family);
        Assert.Equal(old[1], current[1]); Assert.NotEqual(old[0], current[0]);
        Assert.Equal(2, PackageExecutableRoots.Resolve(old[0], old[0].ToUpperInvariant(), local, Family).Count);
    }

    [Fact]
    public void Exact_two_roots_find_nested_runtimes_without_enrolling_other_packages_or_standalone_cli()
    {
        string install = Path.Combine(_fixture, "WindowsApps", "Claude_2.42304.0.0_x64__pzs8sxrjxfjjc");
        string local = Path.Combine(_fixture, "Local");
        var roots = PackageExecutableRoots.Resolve(install, install, local, Family);
        string main = CreateFile(Path.Combine(install, "app", "Claude.exe"));
        string old = CreateFile(Path.Combine(roots[1], "LocalCache", "Roaming", "Claude", "claude-code", "2.1.295", "old-hash", "claude.exe"));
        string current = CreateFile(Path.Combine(roots[1], "LocalCache", "Roaming", "Claude", "claude-code", "2.1.299", "new-hash", "CLAUDE.EXE"));
        string other = CreateFile(Path.Combine(local, "Packages", "ClaudeOther_pzs8sxrjxfjjc", "claude.exe"));
        string otherPublisher = CreateFile(Path.Combine(local, "Packages", "Claude_8wekyb3d8bbwe", "claude.exe"));
        string cli = CreateFile(Path.Combine(_fixture, ".local", "bin", "claude.exe"));
        CreateFile(Path.Combine(roots[1], "LocalState", "settings.json"));
        var paths = ExecutableInventory.CollectChecked(roots, main, TestContext.Current.CancellationToken);
        Assert.Equal(3, paths.Count); Assert.Contains(main, paths); Assert.Contains(old, paths); Assert.Contains(current, paths);
        Assert.DoesNotContain(other, paths); Assert.DoesNotContain(otherPublisher, paths); Assert.DoesNotContain(cli, paths);
        string later = CreateFile(Path.Combine(roots[1], "LocalCache", "Roaming", "Claude", "claude-code", "2.1.300", "next-hash", "claude.exe"));
        Assert.DoesNotContain(later, paths); // Snapshot, not a directory wildcard.
        Assert.Contains(later, ExecutableInventory.CollectChecked(roots, main, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Declared_data_root_can_be_absent_then_appear_before_an_explicit_refresh()
    {
        string install = Path.Combine(_fixture, "Install"), local = Path.Combine(_fixture, "Local");
        string main = CreateFile(Path.Combine(install, "app.exe"));
        var roots = PackageExecutableRoots.Resolve(install, null, local, Family);
        Assert.Equal(2, roots.Count); Assert.False(Directory.Exists(roots[1]));
        Assert.Single(ExecutableInventory.CollectChecked(ExecutableInventory.AvailableRoots(roots), main, TestContext.Current.CancellationToken));
        string runtime = CreateFile(Path.Combine(roots[1], "LocalState", "bin", "runtime.exe"));
        Assert.Contains(runtime, ExecutableInventory.CollectChecked(ExecutableInventory.AvailableRoots(roots), main, TestContext.Current.CancellationToken));
        Assert.Throws<IOException>(() => ExecutableInventory.AvailableRoots([main]));
    }

    [Fact]
    public void Adding_the_data_root_invalidates_old_cache_without_discarding_saved_paths()
    {
        const string install = @"C:\Store\Claude_2.4_x64__pzs8sxrjxfjjc";
        var cached = new LaunchTarget { Id = "claude", Name = "Claude", Kind = LaunchKind.PackagedAumid, PackageFamily = Family,
            CanonicalExecutable = Path.Combine(install, "Claude.exe"), OwnedRoots = [install], OwnedExecutables = [Path.Combine(install, "Claude.exe")] };
        var discovered = cached.WithInventory([], false, PackageExecutableRoots.Resolve(install, null, @"C:\Demo\Local", Family));
        var restored = ApplicationCatalogProjection.RestoreCachedInventory(discovered, cached);
        Assert.False(restored.InventoryReady); Assert.Equal(cached.OwnedExecutables, restored.OwnedExecutables);
        Assert.Equal(2, restored.OwnedRoots.Count);
        Assert.True(PackageExecutableRoots.Covers([install], Path.Combine(install, "nested")));
        Assert.False(PackageExecutableRoots.Covers([install], install + "-another"));
    }

    private static string CreateFile(string path)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [0]); return path; }
    public void Dispose()
    {
        string full = Path.GetFullPath(_fixture), expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EgressController.PackageRootsTests"));
        if (!string.Equals(Path.GetDirectoryName(full), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture path");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
