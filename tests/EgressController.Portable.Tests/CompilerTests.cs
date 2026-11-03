using EgressController.Core.Routing;
using EgressController.Surge;

namespace EgressController.Portable.Tests;
public sealed class CompilerTests
{
    private static readonly ResolvedAdapter[] Live = [new("stable", "Wi-Fi", "en7", true, "192.0.2.2")];
    private static RoutingProfile Profile => new() { Adapters = [new("stable", "Wi-Fi")], Ports = [new(7890, "local", "/Apps/Proxy.app/Contents/MacOS/core", "stable")], Applications = [new("a", "App", "/Apps/Test.app/", true, RouteDestination.Socks(7890))] };
    [Fact] public void Stable_adapter_identity_and_core_bypass_precede_application_routes()
    {
        string text = new SurgeConfigCompiler().Compile(Profile, Live);
        Assert.Contains("interface = en7, allow-other-interface = false", text);
        Assert.True(text.IndexOf("PROCESS-NAME,/Apps/Proxy.app/Contents/MacOS/core", StringComparison.Ordinal) < text.IndexOf("PROCESS-NAME,/Apps/Test.app/", StringComparison.Ordinal));
        Assert.Contains("PROCESS-NAME,/Apps/Test.app/,EC-Local-7890", text);
        Assert.DoesNotContain("ParentPid", text);
    }
    [Fact] public void Missing_saved_adapter_never_substitutes_same_interface_name()
        => Assert.Throws<InvalidOperationException>(() => new SurgeConfigCompiler().Compile(Profile, [Live[0] with { Id = "replacement" }]));
    [Theory]
    [InlineData("/Apps/A.app/\nFINAL,DIRECT",true)] [InlineData("/Apps/../B.app/",true)] [InlineData("/usr/bin/node",false)] [InlineData("/Apps/A.app",true)]
    public void Rejects_unbounded_paths_and_injection(string path, bool bundle) => Assert.Throws<InvalidDataException>(() => SurgeConfigCompiler.ValidatePath(path,bundle));
    [Fact] public void Conflicting_core_route_is_rejected() => Assert.Throws<InvalidDataException>(() => new SurgeConfigCompiler().Compile(Profile with { Applications = [new("a","core","/Apps/Proxy.app/Contents/MacOS/core",false,RouteDestination.Socks(7890))] }, Live));
    [Fact] public void Subscription_cannot_inject_other_sections() => Assert.Throws<InvalidDataException>(() => new SurgeConfigCompiler().Compile(Profile, Live, "[Script]\nx = http-response"));
    [Fact] public void Bracketed_ipv6_in_a_proxy_definition_is_preserved()
        => Assert.Contains("[2001:db8::1]", new SurgeConfigCompiler().Compile(Profile, Live, "Fixture = socks5, [2001:db8::1], 1080"));
    [Fact] public void Names_do_not_change_route_membership()
    {
        string before = new SurgeConfigCompiler().Compile(Profile, Live);
        string after = new SurgeConfigCompiler().Compile(Profile with { Applications = [Profile.Applications[0] with { Name = "renamed", Category = "AI" }] }, Live);
        Assert.Equal(before, after);
    }
}
