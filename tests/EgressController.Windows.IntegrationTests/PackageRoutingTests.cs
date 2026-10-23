using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Guardian;
using EgressController.Launcher.Discovery;
using EgressController.SingBox.Configuration;

namespace EgressController.Windows.IntegrationTests;

public sealed class PackageRoutingTests
{
    // Real planner -> compiler -> guardian policy; no Windows/network/process operations.
    [Theory]
    [InlineData("home", true, true)] [InlineData("home", false, true)]
    [InlineData("home", true, false)] [InlineData("home", false, false)]
    [InlineData("other", true, true)] [InlineData("other", false, true)]
    [InlineData("other", true, false)] [InlineData("other", false, false)]
    [InlineData("port", true, true)] [InlineData("port", false, true)]
    [InlineData("port", true, false)] [InlineData("port", false, false)]
    public void Upgraded_EXE_has_one_saved_route_for_all_domains_and_the_same_guard_membership(string route, bool homeUp, bool proxyReady)
    {
        const string family = "OpenAI.Codex_2p2nqsd0c76g0";
        const string oldRoot = @"C:\Store\OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0";
        const string newRoot = @"D:\Store\OpenAI.Codex_2.0.0.0_x64__2p2nqsd0c76g0";
        string oldExe = Path.Combine(oldRoot, "app", "ChatGPT.exe"), newExe = Path.Combine(newRoot, "app", "ChatGPT.exe");
        const string owner = @"C:\Proxy\mihomo.exe";
        var home = Adapter("22222222-2222-2222-2222-222222222222", "Ethernet", homeUp);
        var other = Adapter("11111111-1111-1111-1111-111111111111", "PRIMARY-WIFI", proxyReady);
        var target = route switch { "home" => EgressRouteTarget.DefaultAdapter, "other" => EgressRouteTarget.ForAdapter(other.AdapterId.ToString()),
            _ => EgressRouteTarget.ForPort(7890) };
        var profile = new EgressProfileDocument
        {
            DefaultAdapterId = home.AdapterId.ToString(), ProxyAdapterId = other.AdapterId.ToString(),
            Adapters = [new() { Id = home.AdapterId.ToString(), Name = home.Alias }, new() { Id = other.AdapterId.ToString(), Name = other.Alias }],
            Applications = [new() { DiscoveryKey = $"pkg:{family}:{family}!App", DisplayName = "Codex",
                DirectoryRoots = [oldRoot], ExecutablePaths = [oldExe], Target = target }],
            Domains = new[] { "chatgpt.com", "google.com", "cloudflare.com" }.Select(name => new EgressNamedRoute
                { Name = name, Target = EgressRouteTarget.Default }).ToArray(),
        };
        var plan = PackagePathUpgrade.Plan(profile, [new(family, [newRoot])], _ => family, _ => true);
        profile = plan.Apply(profile);
        var guard = GuardPolicyMailbox.FromProfile(profile, @"C:\Controller\EgressController.exe", [owner]);
        Assert.Equal(profile.Applications[0].ExecutablePaths.Order(), guard.Executables.Select(item => item.Path).Order());
        Assert.Empty(guard.Directories); // No wildcard or process-name matching.
        var input = new EgressProfileCompileInput
        {
            Profile = profile, Environment = new() { DefaultAdapter = home, ProxyAdapter = other, DnsAdapter = home },
            ApplicationRoutes = profile.Applications.Select(item => new SingBoxApplicationRouteInput(item.ExecutablePaths, item.Target)).ToArray(),
            RuleSets = [],
            UpstreamOwnerPaths = [owner], UnreadyUpstreamPorts = proxyReady ? [] : [7890],
            ControllerPort = 19090, ControllerSecret = "0123456789abcdef0123456789abcdef",
        };
        var compiler = new EgressProfileCompiler();
        var compiled = compiler.Compile(input);
        using var json = JsonDocument.Parse(compiled.JsonBytes);
        var rules = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
        var processRules = rules.Where(item => item.TryGetProperty("process_path_regex", out _)).ToArray();
        foreach (string path in new[] { oldExe, newExe, newExe.ToUpperInvariant(), newExe.ToLowerInvariant() })
        {
            var matched = Assert.Single(processRules, rule => Matches(rule, path));
            bool ready = route == "home" ? homeUp : proxyReady;
            Assert.Equal(ready ? "route" : "reject", matched.GetProperty("action").GetString());
            if (ready) Assert.Equal(route == "port" ? "upstream-7890" : EgressProfileCompiler.AdapterTag(route == "home" ? home.AdapterId : other.AdapterId),
                matched.GetProperty("outbound").GetString());
            // Process routing wins regardless of whether these three domains matched catalog/custom rules.
            Assert.True(Array.IndexOf(rules, matched) < Array.FindIndex(rules, rule => rule.TryGetProperty("domain_suffix", out _)));
        }
        Assert.DoesNotContain(processRules, rule => Matches(rule, @"C:\Unrelated\ChatGPT.exe"));
        Assert.DoesNotContain(processRules, rule => Matches(rule, Path.Combine(newRoot, "brand-new-helper.exe")));
        var ownerRule = Assert.Single(processRules, rule => Matches(rule, owner));
        Assert.Equal(proxyReady ? "route" : "reject", ownerRule.GetProperty("action").GetString());
        if (proxyReady) Assert.Equal("proxy-direct", ownerRule.GetProperty("outbound").GetString());
        // Applying/checking the same update cannot produce another runtime change.
        Assert.Same(profile, plan.Apply(profile));
        Assert.Equal(compiled.Sha256, compiler.Compile(input).Sha256);
    }

    private static bool Matches(JsonElement rule, string path) => rule.GetProperty("process_path_regex").EnumerateArray()
        .Any(pattern => Regex.IsMatch(path, pattern.GetString()!));
    private static AdapterSelection Adapter(string id, string alias, bool up) => new()
    {
        AdapterId = Guid.Parse(id), Alias = alias, Luid = 1, IfIndex = 10, Ipv6IfIndex = 10, IsUp = up,
        AddressState = up ? AdapterAddressState.Ipv4Only : AdapterAddressState.NoAddress,
        Ipv4BindAddress = up ? IPAddress.Parse("192.0.2.10") : null,
    };
}
