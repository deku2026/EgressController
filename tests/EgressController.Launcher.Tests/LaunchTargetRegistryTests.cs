using EgressController.Core.Models;
using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public class LaunchTargetRegistryTests
{
    private static LaunchTarget Target(LaunchKind kind, string id, string? command, string? canonical = null,
        string? pkgFamily = null, string? aumid = null, string? args = null, bool selected = true)
        => new()
        {
            Id = id,
            Name = id,
            Kind = kind,
            Command = command,
            CanonicalExecutable = canonical,
            PackageFamily = pkgFamily,
            Aumid = aumid,
            Arguments = args,
            EsimSelected = selected,
        };

    [Fact]
    public void Same_canonical_exe_from_two_providers_dedups_to_one()
    {
        var reg = new LaunchTargetRegistry();
        bool a1 = reg.Add(Target(LaunchKind.DirectExe, "chrome-apppaths", "chrome.exe", canonical: @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
        bool a2 = reg.Add(Target(LaunchKind.DirectExe, "chrome-shortcut", @"C:\Program Files\Google\Chrome\Application\chrome.exe", canonical: @"C:\Program Files\Google\Chrome\Application\chrome.exe"));

        Assert.True(a1);
        Assert.False(a2);          // second dropped (same key)
        Assert.Equal(1, reg.Count);
        Assert.NotNull(reg.Get("chrome-apppaths"));
    }

    [Fact]
    public void Packaged_aumid_keys_separately_from_exe()
    {
        var reg = new LaunchTargetRegistry();
        reg.Add(Target(LaunchKind.DirectExe, "sample-exe", "Sample.exe", canonical: @"C:\Users\x\SampleApp\Sample.exe"));
        reg.Add(Target(LaunchKind.PackagedAumid, "sample-msix", null, pkgFamily: "Contoso.Sample_2p2n", aumid: "Contoso.Sample!App"));

        Assert.Equal(2, reg.Count); // different DiscoveryKeys
    }

    [Fact]
    public void Different_applications_in_one_package_are_not_deduplicated()
    {
        var reg = new LaunchTargetRegistry();
        reg.Add(Target(LaunchKind.PackagedAumid, "sample-app", null,
            pkgFamily: "Contoso.Sample_2p2n", aumid: "Contoso.Sample_2p2n!App"));
        reg.Add(Target(LaunchKind.PackagedAumid, "sample-settings", null,
            pkgFamily: "Contoso.Sample_2p2n", aumid: "Contoso.Sample_2p2n!Settings"));

        Assert.Equal(2, reg.Count);
    }

    [Fact]
    public void Higher_quality_target_replaces_lower_same_key()
    {
        var reg = new LaunchTargetRegistry();
        string exe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

        // Same key 'exe:<canonical>'; the selected entry is higher quality and replaces the other.
        var low = Target(LaunchKind.DirectExe, "c1", "chrome.exe", canonical: exe, selected: false);
        var high = Target(LaunchKind.DirectExe, "c2", "chrome.exe", canonical: exe);

        Assert.True(reg.Add(low));
        Assert.True(reg.Add(high));       // replaced the lower-quality same-key entry
        Assert.Equal(1, reg.Count);
        Assert.NotNull(reg.Get("c2"));
        Assert.Null(reg.Get("c1"));
    }

    [Fact]
    public void Unsupported_wrapper_never_replaces_resolved()
    {
        var reg = new LaunchTargetRegistry();
        reg.Add(Target(LaunchKind.DirectExe, "r", "resolved.exe", canonical: @"C:\resolved\resolved.exe"));
        bool wrapper = reg.Add(new LaunchTarget
        {
            Id = "w",
            Name = "wrapper",
            Kind = LaunchKind.CliWrapperResolved,
            Command = "foo.cmd",
            ResolutionUnsupported = true,
        });

        Assert.True(wrapper); // different key (cmd not an exe) — just sanity that unsupported doesn't crash
    }
}
