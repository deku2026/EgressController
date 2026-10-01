using EgressController.Core.Profile;

namespace EgressController.Core.Tests;

public sealed class MultiAdapterProfileTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void Replacing_role_moves_its_references_but_keeps_other_role_and_port_independent()
    {
        var profile = (new EgressProfileDocument().SetAdapterRoles(A, B).AddPort(7891) with
        {
            DnsAdapterId = A,
            Applications = [new() { DiscoveryKey = "a", Target = EgressRouteTarget.DefaultAdapter },
                new() { DiscoveryKey = "b", Target = EgressRouteTarget.ForAdapter(A) },
                new() { DiscoveryKey = "c", Target = EgressRouteTarget.Default }],
            Domains = [new() { Name = "example.com", Target = EgressRouteTarget.ForAdapter(B) }],
            RuleSets = [new() { Name = "test", Target = EgressRouteTarget.ForAdapter(A) }],
        }).NormalizeAndValidate();
        var changed = profile.SetAdapterRoles(C, B);
        Assert.Equal(7890, changed.UpstreamPort);
        Assert.Equal(C, changed.EffectiveDnsAdapterId);
        Assert.Equal(C, changed.Applications.Single(item => item.DiscoveryKey == "b").Target.AdapterId);
        Assert.Equal(EgressRouteTarget.Default, changed.Applications.Single(item => item.DiscoveryKey == "c").Target);
        Assert.Equal(C, Assert.Single(changed.RuleSets).Target.AdapterId);
        Assert.Equal(B, Assert.Single(changed.Domains).Target.AdapterId);
        Assert.Equal(B, changed.SetDefaultPort(7891).ProxyAdapterId);
        Assert.Equal(C, changed.SetDefaultPort(7891).DefaultAdapterId);
        Assert.Equal(new[] { EgressProfileDocument.DirectAdapterName, EgressProfileDocument.ProxyAdapterName }, changed.Adapters.Select(item => item.Name));
    }

    [Fact]
    public void Roles_can_swap_atomically_and_two_distinct_cards_are_required()
    {
        var profile = (new EgressProfileDocument().SetAdapterRoles(A, B) with
        { DnsAdapterId = B, Domains = [new() { Name = "example.com", Target = EgressRouteTarget.ForAdapter(A) }] });
        var swapped = profile.SetAdapterRoles(B, A);
        Assert.Equal(A, swapped.DnsAdapterId);
        Assert.Equal(B, Assert.Single(swapped.Domains).Target.AdapterId);
        Assert.Equal(2, swapped.Adapters.Count);
        Assert.Null(swapped.AdapterConfigurationError);
        Assert.Throws<ArgumentException>(() => profile.SetAdapterRoles(A, A));
        Assert.Throws<ArgumentException>(() => profile.SetAdapterRoles("invalid", B));
        Assert.NotNull(new EgressProfileDocument().AdapterConfigurationError);
        Assert.NotNull(new EgressProfileDocument().SetAdapterRoles(A, null).AdapterConfigurationError);
    }

    [Fact]
    public void Legacy_two_adapter_profile_preserves_primary_for_proxy_and_esim_for_direct()
    {
        var profile = new EgressProfileDocument
        {
            SchemaVersion = 2, PrimaryAdapterId = A, EsimAdapterId = B,
            Applications = [new() { DiscoveryKey = "browser", Target = new() { Kind = "esim" } }],
        }.NormalizeAndValidate();
        Assert.Equal(4, profile.SchemaVersion);
        Assert.Equal(2, profile.Adapters.Count);
        Assert.Equal(B, profile.DefaultAdapterId);
        Assert.Equal(A, profile.ProxyAdapterId);
        Assert.Equal(EgressRouteTarget.ForAdapter(B), Assert.Single(profile.Applications).Target);
        Assert.Null(profile.PrimaryAdapterId); Assert.Null(profile.EsimAdapterId);
        Assert.Null(profile.AdapterConfigurationError);
        Assert.Equal(profile.Applications, profile.NormalizeAndValidate().Applications);
    }

    [Fact]
    public void Ambiguous_v3_profile_retains_legacy_rules_and_requires_manual_proxy_choice()
    {
        var profile = new EgressProfileDocument
        {
            SchemaVersion = 3, DefaultAdapterId = A,
            Adapters = [new() { Id = A, Name = "one" }, new() { Id = B, Name = "two" }, new() { Id = C, Name = "three" }],
            Applications = [new() { DiscoveryKey = "old", Target = EgressRouteTarget.ForAdapter(C) }],
        }.NormalizeAndValidate();
        Assert.Null(profile.ProxyAdapterId);
        Assert.NotNull(profile.AdapterConfigurationError);
        var selected = profile.SetAdapterRoles(A, B);
        Assert.Equal(2, selected.Adapters.Count);
        Assert.Equal(C, Assert.Single(selected.Applications).Target.AdapterId);
        Assert.Contains("旧网卡", selected.AdapterConfigurationError);
        Assert.Contains("DNS", (selected with { Applications = [], DnsAdapterId = C }).AdapterConfigurationError);
    }

    [Fact]
    public void Explicit_dns_on_a_third_card_does_not_guess_the_proxy_role()
    {
        var profile = new EgressProfileDocument
        {
            SchemaVersion = 3, DefaultAdapterId = A, DnsAdapterId = B,
            Adapters = [new() { Id = A, Name = "one" }, new() { Id = B, Name = "two" }, new() { Id = C, Name = "three" }],
        }.NormalizeAndValidate();
        Assert.Null(profile.ProxyAdapterId);
        Assert.Equal(B, profile.DnsAdapterId);
        Assert.Contains("DNS", profile.SetAdapterRoles(A, C).AdapterConfigurationError);
    }

    [Fact]
    public void V3_with_two_cards_keeps_default_and_uses_other_as_proxy()
    {
        var profile = new EgressProfileDocument { SchemaVersion = 3, DefaultAdapterId = B,
            Adapters = [new() { Id = A, Name = "one" }, new() { Id = B, Name = "two" }] }.NormalizeAndValidate();
        Assert.Equal(B, profile.DefaultAdapterId);
        Assert.Equal(A, profile.ProxyAdapterId);
        Assert.Null(profile.AdapterConfigurationError);
    }

    [Fact]
    public void Adapter_only_profiles_do_not_require_a_socks_port()
    {
        var profile = new EgressProfileDocument { UpstreamPorts = [] }.SetAdapterRoles(A, B);
        Assert.Empty(profile.UpstreamPorts);
        Assert.Equal(1080, profile.AddPort(1080).UpstreamPort);
        Assert.Throws<ArgumentException>(() => (profile with
        { Applications = [new() { DiscoveryKey = "a", Target = EgressRouteTarget.Default }] }).NormalizeAndValidate());
    }
}
