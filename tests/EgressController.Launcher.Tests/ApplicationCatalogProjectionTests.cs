using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class ApplicationCatalogProjectionTests
{
    private const string Main = @"C:\Apps\Claude\Claude.exe";
    private const string Helper = @"C:\Apps\Claude\resources\claude-ssh-proxy.exe";
    private static LaunchTarget Target(string path, params string[] members) => new()
    { Id = path, Name = Path.GetFileNameWithoutExtension(path), Kind = LaunchKind.DirectExe,
      CanonicalExecutable = path, Command = path, OwnedExecutables = members.Prepend(path).ToArray() };
    private static EgressApplicationSelection Selection(string path, EgressRouteTarget? route = null, params string[] members) => new()
    { DiscoveryKey = "exe:" + path, DisplayName = Path.GetFileNameWithoutExtension(path), ExecutablePaths = members.Prepend(path).ToArray(),
      Target = route ?? EgressRouteTarget.DefaultAdapter };

    [Fact]
    public void Claude_helpers_are_one_main_row_with_fixed_members_and_legacy_keys()
    {
        var rows = ApplicationCatalogProjection.Project([Target(Main, Helper), Target(Helper), Target(Main.ToUpperInvariant())]);
        var row = Assert.Single(rows);
        Assert.Equal("Claude", row.Name);
        Assert.Contains(Helper, row.OwnedExecutables);
        Assert.Contains("exe:" + Helper, row.SelectionKeys);
        Assert.Contains("exe:" + Main.ToUpperInvariant(), row.SelectionKeys);
        var inventory = ApplicationInventorySnapshot.Create(rows);
        var restored = inventory.CaptureSelection("exe:" + Helper, EgressRouteTarget.DefaultAdapter, null, []);
        Assert.Equal(Helper, Assert.Single(restored.ExecutablePaths)); // A conflicting old helper route cannot capture the whole app.
    }

    [Theory]
    [InlineData(@"C:\VS\devenv.exe")]
    [InlineData(@"C:\System\IEDIAGCMD.EXE")]
    [InlineData(@"C:\System\IEXPLORE.EXE")]
    [InlineData(@"C:\System\ImagingDevices.exe")]
    [InlineData(@"C:\Tools\claude-ssh-proxy.exe")]
    public void Any_primary_is_visible_but_nested_AI_does_not_change_its_identity(string primary)
    {
        var target = Assert.Single(ApplicationCatalogProjection.Project([Target(primary, @"C:\VS\copilot.exe", @"C:\VS\msedge.exe")]));
        Assert.Equal(Path.GetFileNameWithoutExtension(primary), target.Name);
        Assert.True(string.IsNullOrEmpty(target.CatalogId));
    }

    [Fact]
    public void Compatible_legacy_selections_merge_but_missing_saved_paths_survive()
    {
        string old = @"C:\Apps\Claude\old\helper.exe";
        var rows = ApplicationCatalogProjection.Project([Target(Main, Helper), Target(Helper)]);
        var result = ApplicationCatalogProjection.Reconcile([Selection(Main, null, old), Selection(Helper)], rows);
        var selected = Assert.Single(result);
        Assert.Equal("exe:" + Main, selected.DiscoveryKey);
        Assert.Contains(old, selected.ExecutablePaths);
        Assert.Contains(Helper, selected.ExecutablePaths);
    }

    [Fact]
    public void Different_saved_exits_are_not_silently_overwritten()
    {
        var rows = ApplicationCatalogProjection.Project([Target(Main, Helper), Target(Helper)]);
        var result = ApplicationCatalogProjection.Reconcile([Selection(Main), Selection(Helper, EgressRouteTarget.ForPort(7891))], rows);
        Assert.Equal(2, result.Count);
        var unified = ApplicationCatalogProjection.Merge(rows[0], result, EgressRouteTarget.DefaultAdapter);
        Assert.Contains(Main, unified.ExecutablePaths);
        Assert.Contains(Helper, unified.ExecutablePaths);
    }

    [Fact]
    public void Unavailable_package_retains_one_cached_row_including_old_helpers()
    {
        EgressApplicationSelection[] saved = [Selection(Helper), Selection(Main, null, Helper), Selection(@"C:\VS\devenv.exe")];
        var rows = ApplicationCatalogProjection.RestoreSavedTargets([], saved);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Name == "Claude");
        var result = ApplicationCatalogProjection.Reconcile(saved, rows);
        Assert.Equal(2, result.Count);
        Assert.Contains(Helper, result.Single(item => item.DisplayName == "Claude").ExecutablePaths);
    }

    [Fact]
    public void Independently_registered_game_is_not_swallowed_by_launcher_inventory()
    {
        string path = @"C:\Steam\steamapps\common\Game\game.exe";
        var launcher = Target(@"C:\Steam\steam.exe", path);
        var game = new LaunchTarget { Id = "steam:1", SavedDiscoveryKey = "steam:1", Name = "Game", CanonicalExecutable = path,
            OwnedExecutables = [path], Source = "Steam 游戏库" };
        var rows = ApplicationCatalogProjection.Project([launcher, game]);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain("steam:1", rows.Single(row => row.Name == "steam").RelatedDiscoveryKeys);
    }

    [Fact]
    public void Package_identity_wins_over_a_legacy_executable_filename()
    {
        var row = Assert.Single(ApplicationCatalogProjection.Project([new() { Id = "codex", Name = "Codex", Kind = LaunchKind.PackagedAumid,
            PackageFamily = "OpenAI.Codex_abcd", CanonicalExecutable = @"C:\WindowsApps\OpenAI.Codex\ChatGPT.exe" }]));
        Assert.Equal("Codex", row.Name);
    }

    [Fact]
    public void Packaged_helpers_fold_without_eagerly_scanning_files()
    {
        var main = new LaunchTarget { Id = "main", Name = "Claude", Kind = LaunchKind.PackagedAumid, PackageFamily = "Claude_test",
            Aumid = "Claude_test!main", CanonicalExecutable = Main, InventoryReady = false };
        var helper = new LaunchTarget { Id = "helper", Name = "claude-ssh-proxy", Kind = LaunchKind.PackagedAumid, PackageFamily = "Claude_test",
            Aumid = "Claude_test!helper", CanonicalExecutable = Helper, InventoryReady = false };
        var row = Assert.Single(ApplicationCatalogProjection.Project([helper, main]));
        Assert.Equal("Claude", row.Name); Assert.Contains(helper.DiscoveryKey, row.RelatedDiscoveryKeys);
        Assert.Empty(row.OwnedExecutables);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Provider_refresh_keeps_category_and_route_identity_aliases_without_rescanning(bool manual)
    {
        var cached = new LaunchTarget { Id = "old", SavedDiscoveryKey = "old:registration", Name = "Chosen name", IsManual = manual,
            CanonicalExecutable = Main, OwnedRoots = [@"C:\Apps\Claude"], OwnedExecutables = [Main, Helper] };
        var discovered = new LaunchTarget { Id = "new", SavedDiscoveryKey = "arp:claude", Name = "Claude", InventoryReady = false,
            CanonicalExecutable = Main, OwnedRoots = [@"C:\Apps\Claude"], RelatedDiscoveryKeys = ["exe:" + Main] };
        var restored = ApplicationCatalogProjection.RestoreCachedInventory(discovered, cached);
        Assert.Contains(cached.DiscoveryKey, restored.SelectionKeys); Assert.Contains(discovered.DiscoveryKey, restored.SelectionKeys);
        Assert.Equal(cached.OwnedExecutables, restored.OwnedExecutables); Assert.True(restored.InventoryReady);
        if (manual) { Assert.Equal(cached.DiscoveryKey, restored.DiscoveryKey); Assert.Equal("Chosen name", restored.Name); }
    }

    [Fact]
    public void Native_CLI_is_separate_from_desktop_and_does_not_enroll_shared_bin()
    {
        string cliPath = @"C:\Users\Demo\.local\bin\claude.exe";
        var source = new FakeCommands([new("claude", cliPath)]);
        var cli = Assert.Single(new WindowsCliDiscovery(source, _ => true, _ => throw new Exception("No directory scan")).Discover());
        var rows = ApplicationCatalogProjection.Project([Target(Main, Helper), cli]);
        Assert.Equal(2, rows.Count);
        var row = rows.Single(item => item.Kind == LaunchKind.CliNative);
        Assert.Equal("Claude CLI", row.Name);
        Assert.Equal(cliPath, Assert.Single(row.OwnedExecutables));
        Assert.Empty(row.OwnedRoots);
    }

    [Fact]
    public void Nested_CLI_keeps_its_own_selection_key_after_legacy_reconciliation()
    {
        string cliPath = @"C:\Apps\Claude\tools\claude.exe";
        var cli = Assert.Single(new WindowsCliDiscovery(new FakeCommands([new("claude", cliPath)]), _ => true).Discover());
        var rows = ApplicationCatalogProjection.Project([Target(Main, cliPath), Target(cliPath), cli]);
        EgressApplicationSelection[] saved = [Selection(Main, null, cliPath), new() { DiscoveryKey = cli.DiscoveryKey,
            DisplayName = cli.Name, ExecutablePaths = [cliPath], Target = EgressRouteTarget.DefaultAdapter }];
        var restored = ApplicationCatalogProjection.RestoreSavedTargets(rows, saved);
        Assert.DoesNotContain(cli.DiscoveryKey, restored.Single(item => item.Kind != LaunchKind.CliNative).SelectionKeys);
        Assert.Equal(2, ApplicationCatalogProjection.Reconcile(saved, restored).Count);
    }

    [Fact]
    public void Shared_interpreter_wrapper_has_notice_instead_of_binding_node()
    {
        var finder = new WindowsCliDiscovery(new FakeCommands([new("gemini", @"C:\bin\gemini.cmd")]), _ => true, _ => []);
        Assert.Empty(finder.Discover());
        Assert.Contains("gemini", Assert.Single(finder.Notices));
    }

    [Fact]
    public void Wrapper_resolution_stays_in_the_known_package_and_ignores_unrelated_binaries()
    {
        string binary = @"C:\bin\node_modules\@openai\codex\vendor\x64\codex.exe";
        var finder = new WindowsCliDiscovery(new FakeCommands([new("codex", @"C:\bin\codex.cmd")]), _ => true,
            _ => [@"C:\unrelated\codex.exe", @"C:\bin\node.exe", binary]);
        Assert.Equal(binary, Assert.Single(finder.Discover()).CanonicalExecutable);
    }

    private sealed class FakeCommands(IReadOnlyList<CliCommandLocation> locations) : ICommandLocationSource
    { public IReadOnlyList<CliCommandLocation> Find(IReadOnlyList<string> names) => locations; }
}
