using EgressController.Core.Profile;

namespace EgressController.Core.Tests;

public sealed class MultiAdapterProfileTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    [Fact]
    public void Adapter_and_port_defaults_remain_independent_and_explicit_routes_stay_fixed()
    {
        var profile = (new EgressProfileDocument().AddAdapter(A, "Redmi").AddAdapter(B, "Ethernet").AddPort(7891) with
        {
            Applications = [new() { DiscoveryKey = "a", Target = EgressRouteTarget.DefaultAdapter },
                new() { DiscoveryKey = "b", Target = EgressRouteTarget.ForAdapter(A) },
                new() { DiscoveryKey = "c", Target = EgressRouteTarget.Default }],
        }).NormalizeAndValidate();
        var changed = profile.SetDefaultAdapter(B);
        Assert.Equal(7890, changed.UpstreamPort);
        Assert.Equal(B, changed.EffectiveDnsAdapterId);
        Assert.Equal(A, changed.Applications.Single(item => item.DiscoveryKey == "b").Target.AdapterId);
        Assert.Equal(B, changed.SetDefaultPort(7891).DefaultAdapterId);
    }

    [Fact]
    public void Legacy_two_adapter_profile_migrates_routes_without_losing_selections()
    {
        var profile = new EgressProfileDocument
        {
            SchemaVersion = 2, PrimaryAdapterId = A, EsimAdapterId = B,
            Applications = [new() { DiscoveryKey = "browser", Target = new() { Kind = "esim" } }],
        }.NormalizeAndValidate();
        Assert.Equal(3, profile.SchemaVersion);
        Assert.Equal(2, profile.Adapters.Count);
        Assert.Equal(B, profile.DefaultAdapterId);
        Assert.Equal(EgressRouteTarget.ForAdapter(B), Assert.Single(profile.Applications).Target);
        Assert.Null(profile.PrimaryAdapterId); Assert.Null(profile.EsimAdapterId);
        Assert.Equal(profile.Applications, profile.NormalizeAndValidate().Applications);
    }

    [Fact]
    public void Referenced_adapters_and_invalid_selections_cannot_be_removed_or_silently_replaced()
    {
        var profile = new EgressProfileDocument().AddAdapter(A, "Redmi").AddAdapter(B, "Ethernet");
        Assert.Throws<ArgumentException>(() => profile.RemoveAdapter(A));
        Assert.Single(profile.RemoveAdapter(B).Adapters);
        Assert.Throws<ArgumentException>(() => (profile with { DnsAdapterId = B }).RemoveAdapter(B));
        Assert.Throws<ArgumentException>(() => profile.AddAdapter(A, "duplicate"));
        Assert.Throws<ArgumentException>(() => profile.SetDefaultAdapter(Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public void Adapter_only_profiles_do_not_require_a_socks_port()
    {
        var profile = new EgressProfileDocument { UpstreamPorts = [] }.AddAdapter(A, "Redmi");
        Assert.Empty(profile.UpstreamPorts);
        Assert.Equal(1080, profile.AddPort(1080).UpstreamPort);
        Assert.Throws<ArgumentException>(() => (profile with
        { Applications = [new() { DiscoveryKey = "a", Target = EgressRouteTarget.Default }] }).NormalizeAndValidate());
    }
}
