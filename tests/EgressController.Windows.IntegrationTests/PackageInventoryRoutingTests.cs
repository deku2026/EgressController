using System.Text.RegularExpressions;
using EgressController.App;
using EgressController.App.Preview;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Guardian;
using EgressController.Launcher.Discovery;
using EgressController.SingBox.Configuration;
using EgressController.Windows.Network;

namespace EgressController.Windows.IntegrationTests;

public sealed class PackageInventoryRoutingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EgressController.PackageRouteTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Explicit_package_refresh_routes_cached_runtimes_home_and_gives_guard_the_identical_exact_paths()
    {
        const string family = "Claude_pzs8sxrjxfjjc";
        string install = Path.Combine(_directory, "WindowsApps", "Claude_2.42304.0.0_x64__pzs8sxrjxfjjc");
        string local = Path.Combine(_directory, "Local");
        var roots = PackageExecutableRoots.Resolve(install, null, local, family);
        string main = CreateFile(Path.Combine(install, "app", "Claude.exe"));
        string nested = CreateFile(Path.Combine(roots[1], "LocalCache", "Roaming", "Claude", "claude-code", "2.1.299", "f0584be0fbaf", "claude.exe"));
        string sibling = CreateFile(Path.Combine(local, "Packages", "ClaudeOther_pzs8sxrjxfjjc", "claude.exe"));
        string standalone = CreateFile(Path.Combine(_directory, ".local", "bin", "claude.exe"));
        var target = new LaunchTarget { Id = "claude", Name = "Claude", Kind = LaunchKind.PackagedAumid, PackageFamily = family,
            Aumid = family + "!Claude", CanonicalExecutable = main, OwnedRoots = roots, OwnedExecutables = [main], InventoryReady = false };
        var selection = new EgressApplicationSelection { DiscoveryKey = target.DiscoveryKey, DisplayName = "Claude", DirectoryRoots = [install],
            ExecutablePaths = [main], Target = EgressRouteTarget.DefaultAdapter };
        var preview = new PreviewController();
        var original = preview.Profile with { Applications = [selection], RuleSets = [], Domains = [] };
        const string owner = @"C:\Proxy\mihomo.exe";

        EgressProfileCompilationResult Compile(EgressProfileDocument profile, LaunchTarget app) => new EgressProfileCompiler().Compile(new()
        {
            Profile = profile, Environment = new NetworkEnvironmentResolver().Resolve(profile, preview.Adapters),
            ApplicationRoutes = AppController.ResolveApplicationRoutes(profile, ApplicationInventorySnapshot.Create([app])),
            UpstreamOwnerPaths = [owner], RuleSets = [], ControllerPort = 19090, ControllerSecret = "mock-package-scope-secret",
        });
        static string Route(EgressProfileCompilationResult compiled, string executable)
        {
            foreach (var rule in compiled.Document.Route.Rules)
                if (rule.ProcessPathRegex?.Any(pattern => Regex.IsMatch(executable, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) == true)
                    return rule.Outbound ?? "reject";
            return compiled.Document.Route.Final;
        }

        // Merely discovering another root cannot silently replace the saved snapshot.
        Assert.Equal("upstream-7890", Route(Compile(original, target), nested));
        var indexed = target.WithInventory(ExecutableInventory.CollectChecked(roots, main, TestContext.Current.CancellationToken));
        var updated = original with { Applications = [ApplicationCatalogProjection.Merge(indexed, [selection], selection.Target)] };
        var compiled = Compile(updated, indexed);
        string home = EgressProfileCompiler.AdapterTag(Guid.Parse(updated.DefaultAdapterId!));
        Assert.Equal(home, Route(compiled, nested)); Assert.Equal(home, Route(compiled, nested.ToUpperInvariant()));
        Assert.Equal(home, Route(compiled, main)); Assert.Equal("proxy-direct", Route(compiled, owner));
        Assert.Equal("upstream-7890", Route(compiled, sibling)); Assert.Equal("upstream-7890", Route(compiled, standalone));
        Assert.Equal("upstream-7890", Route(compiled, nested.Replace("2.1.299", "2.1.300"))); // Still exact paths, not folder rules.
        var guard = GuardPolicyMailbox.FromProfile(updated, @"C:\Controller\EgressController.exe", [owner]);
        Assert.Empty(guard.Directories);
        Assert.Contains(guard.Executables, item => item.Path.Equals(nested, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(updated.Applications[0].ExecutablePaths.Order(StringComparer.OrdinalIgnoreCase), guard.Executables.Select(item => item.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(compiled.Sha256, Compile(updated, indexed).Sha256); // New instances do not change configuration.
        Assert.Contains(nested, ApplicationCatalogProjection.Merge(indexed, updated.Applications, EgressRouteTarget.ForPort(7891)).ExecutablePaths);
        Assert.Equal("Claude", Assert.Single(ApplicationCatalogProjection.Project([indexed])).Name);
    }

    private static string CreateFile(string path)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [0]); return path; }
    public void Dispose()
    {
        string full = Path.GetFullPath(_directory), parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EgressController.PackageRouteTests"));
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture path");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
