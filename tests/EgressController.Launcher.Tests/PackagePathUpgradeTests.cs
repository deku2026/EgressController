using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class PackagePathUpgradeTests
{
    private const string Family = "OpenAI.Codex_2p2nqsd0c76g0";
    private const string Key = "pkg:" + Family + ":" + Family + "!App";
    private const string Old = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0";
    private const string Current = @"D:\WindowsApps\OpenAI.Codex_26.1016.8622.0_x64__2p2nqsd0c76g0";
    private const string Data = @"C:\Users\Demo\AppData\Local\Packages\" + Family;
    private static string PrimaryExe(string root) => Path.Combine(root, "app", "ChatGPT.exe");
    private static EgressProfileDocument Profile() => new()
    {
        Applications = [new() { DiscoveryKey = Key, DisplayName = "Codex", Target = EgressRouteTarget.DefaultAdapter,
            DirectoryRoots = [Old, Data], ExecutablePaths = [PrimaryExe(Old), Path.Combine(Old, "app", "bin", "helper.exe"), Path.Combine(Data, "runtime.exe")] }],
    };
    private static string? RootFamily(string root) =>
        new[] { Old, Current }.Contains(root, StringComparer.OrdinalIgnoreCase) ? Family : null;
    private static PackagePathUpgrade Plan(EgressProfileDocument? profile = null, Func<string, bool>? exists = null) =>
        PackagePathUpgrade.Plan(profile ?? Profile(), [new(Family, [Current])], RootFamily, exists ?? (_ => true));

    [Fact]
    public void Store_upgrade_preserves_the_route_and_rebases_only_saved_relative_EXEs()
    {
        var original = Profile(); var plan = Plan(original);
        var updated = plan.Apply(original); var app = Assert.Single(updated.Applications);
        Assert.Equal(original.Applications[0].Target, app.Target);
        Assert.Equal(5, app.ExecutablePaths.Count); // Three saved + two same-relative-path additions.
        Assert.Contains(PrimaryExe(Old), app.ExecutablePaths); Assert.Contains(PrimaryExe(Current), app.ExecutablePaths);
        Assert.Contains(Path.Combine(Current, "app", "bin", "helper.exe"), app.ExecutablePaths);
        Assert.DoesNotContain(Path.Combine(Current, "runtime.exe"), app.ExecutablePaths);
        Assert.Equal(original.Applications[0].ExecutablePaths, app.ExecutablePaths.Take(3));
        Assert.Contains(Current, app.DirectoryRoots);
        Assert.False(Plan(updated).HasChanges);
        Assert.Same(updated, plan.Apply(updated));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Missing_or_renamed_files_do_not_expand_membership(bool mainExists, bool helperExists)
    {
        var probes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = Plan(exists: path => { probes.Add(path); return path == PrimaryExe(Current) ? mainExists : helperExists; });
        Assert.Equal(2, probes.Count);
        Assert.DoesNotContain(probes, path => path.EndsWith("brand-new.exe"));
        var paths = plan.Apply(Profile()).Applications[0].ExecutablePaths;
        Assert.Equal(mainExists, paths.Contains(PrimaryExe(Current)));
        Assert.Equal(helperExists, paths.Contains(Path.Combine(Current, "app", "bin", "helper.exe")));
    }

    [Fact]
    public void Exact_family_identity_excludes_other_publishers_and_similar_names()
    {
        foreach (string family in new[] { "OpenAI.Codex_8wekyb3d8bbwe", "OpenAI.CodexOther_2p2nqsd0c76g0", "Claude_pzs8sxrjxfjjc" })
            Assert.False(PackagePathUpgrade.Plan(Profile(), [new(family, [Current])], RootFamily, _ => throw new Exception("Wrong package probed")).HasChanges);
        Assert.False(PackagePathUpgrade.Plan(Profile(), [new(Family, [Current])], _ => "Other_2p2nqsd0c76g0",
            _ => throw new Exception("Invalid root probed")).HasChanges);
    }

    [Fact]
    public void Ambiguous_or_unregistered_versions_wait_for_the_next_check()
    {
        Assert.False(PackagePathUpgrade.Plan(Profile(), [], RootFamily, _ => true).HasChanges);
        Assert.False(PackagePathUpgrade.Plan(Profile(), [new(Family, [Old]), new(Family, [Current])], RootFamily,
            _ => throw new Exception("Ambiguous registration probed")).HasChanges);
    }

    [Fact]
    public void Traversal_sibling_paths_and_data_roots_are_never_rebased()
    {
        var app = Profile().Applications[0] with { ExecutablePaths = [
            Path.Combine(Old, "..", "Other", "helper.exe"), Old + @"-other\helper.exe",
            @"C:\Unrelated\ChatGPT.exe", Path.Combine(Data, "runtime.exe"), Path.Combine(Old, "settings.json")] };
        Assert.False(Plan(Profile() with { Applications = [app] }, _ => throw new Exception("Out-of-scope probe")).HasChanges);
    }

    [Fact]
    public void Selection_and_route_edits_during_the_metadata_query_are_respected()
    {
        var profile = Profile(); var plan = Plan(profile);
        var deselected = profile with { Applications = [] };
        Assert.Same(deselected, plan.Apply(deselected));
        var changed = profile with { Applications = [profile.Applications[0] with { Target = EgressRouteTarget.ForPort(7890) }] };
        Assert.Equal(EgressRouteTarget.ForPort(7890), plan.Apply(changed).Applications[0].Target);
        var removed = profile with { Applications = [profile.Applications[0] with { ExecutablePaths = [] }] };
        Assert.Same(removed, plan.Apply(removed));
    }

    [Fact]
    public void Casing_does_not_duplicate_a_path_or_change_a_package_identity()
    {
        var profile = Profile();
        profile = profile with { Applications = [profile.Applications[0] with { ExecutablePaths =
            profile.Applications[0].ExecutablePaths.Append(PrimaryExe(Current).ToUpperInvariant()).ToArray() }] };
        var plan = PackagePathUpgrade.Plan(profile, [new(Family.ToUpperInvariant(), [Current.ToUpperInvariant()])], RootFamily, _ => true);
        Assert.Single(Assert.Single(plan.Applications).Paths);
    }

    [Theory]
    [InlineData("exe:C:\\Apps\\ChatGPT.exe")]
    [InlineData("pkg:OpenAI.Codex_2p2nqsd0c76g0:Other!App")]
    [InlineData("pkg:OpenAI.Codex:OpenAI.Codex!App")]
    public void Non_package_or_mismatched_AUMID_is_not_eligible(string key)
        => Assert.Null(PackagePathUpgrade.FamilyOfSelection(key));

    [Fact]
    public void Optional_cache_can_recover_from_the_saved_profile_but_new_data_roots_stay_pending()
    {
        var plan = Plan(); var saved = plan.Apply(Profile());
        var target = new LaunchTarget { Id = Key, Name = "Codex", Kind = LaunchKind.PackagedAumid,
            PackageFamily = Family, Aumid = Family + "!App", CanonicalExecutable = PrimaryExe(Old), Command = PrimaryExe(Old),
            InventoryReady = true, OwnedRoots = [Old, Data], OwnedExecutables = Profile().Applications[0].ExecutablePaths };
        var updated = plan.UpdateTarget(target, saved);
        Assert.Equal(PrimaryExe(Current), updated.CanonicalExecutable);
        Assert.True(updated.InventoryReady); Assert.Contains(PrimaryExe(Current), updated.OwnedExecutables);
        var discovered = updated.WithInventory(target.OwnedExecutables, false);
        Assert.True(PackagePathUpgrade.RestoreInventory(discovered, saved).InventoryReady);
        var pending = discovered.WithInventory(target.OwnedExecutables, false, [Old, Current, Data, @"C:\NewData"]);
        Assert.False(PackagePathUpgrade.RestoreInventory(pending, saved).InventoryReady);
    }

    [Fact]
    public void Mock_mode_fences_the_native_registration_source()
    {
        RuntimeSafety.EnableMockOnly();
        Assert.Throws<InvalidOperationException>(() => WindowsPackageUpgradeSource.Read(Profile()));
    }
}
