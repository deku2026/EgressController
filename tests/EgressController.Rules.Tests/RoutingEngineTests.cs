using EgressController.Core.Routing;
using EgressController.Rules.Parsing;
using EgressController.Rules.Stores;

namespace EgressController.Rules.Tests;

public class RoutingEngineTests
{
    private static RoutingEngine Engine(DomainSetStore store) => new(store.GetMatcher());

    private static Dictionary<string, IReadOnlyList<CompiledDomainRule>> Set(string name, params string[] lines)
    {
        Assert.True(StrictDomainListParser.TryParse(lines, name, out var rules, out _));
        return new Dictionary<string, IReadOnlyList<CompiledDomainRule>> { [name] = rules! };
    }

    [Fact]
    public void Selected_domain_matches_route_to_esim()
    {
        var store = new DomainSetStore();
        store.ReplaceSelectedSets(Set("geosite/google", "google.com", "+.google.com"));

        var d = Engine(store).Decide("api.google.com");
        Assert.Equal(Egress.Esim, d.Egress);
        Assert.Equal(RouteReason.DomainMatch, d.Reason);
        Assert.True(d.MatchedRule!.Value.Matched);
        Assert.Equal("geosite/google", d.MatchedRule!.Value.RuleSetName);
    }

    [Fact]
    public void Non_matching_host_routes_to_upstream_default()
    {
        var store = new DomainSetStore();
        store.ReplaceSelectedSets(Set("g", "+.google.com"));

        var d = Engine(store).Decide("github.com");
        Assert.Equal(Egress.UpstreamProxy, d.Egress);
        Assert.Equal(RouteReason.DefaultUpstream, d.Reason);
        Assert.Null(d.MatchedRule);
    }

    [Fact]
    public void Empty_store_routes_everything_to_upstream()
    {
        var store = new DomainSetStore();
        Assert.Equal(Egress.UpstreamProxy, Engine(store).Decide("anything.example").Egress);
    }

    [Fact]
    public void Manual_domain_has_priority_over_selected_set_at_equal_specificity()
    {
        var store = new DomainSetStore();
        store.ReplaceSelectedSets(Set("s", "+.openai.com"));
        store.AddManual("openai.com"); // SuffixInclusive too

        var d = Engine(store).Decide("api.openai.com");
        Assert.Equal(Egress.Esim, d.Egress);
        Assert.Equal("manual", d.MatchedRule!.Value.RuleSetName); // manual wins the tie
    }

    [Fact]
    public void Atomic_snapshot_is_consistent_while_replacing_selected_sets()
    {
        var store = new DomainSetStore();
        store.ReplaceSelectedSets(Set("a", "alpha.com"));

        var engine = Engine(store);
        Assert.Equal(Egress.Esim, engine.Decide("alpha.com").Egress);
        Assert.Equal(Egress.UpstreamProxy, engine.Decide("beta.com").Egress);

        // Non-destructive check that adding manual is visible to a freshly-acquired snapshot.
        store.AddManual("gamma.com");
        var engine2 = new RoutingEngine(store.GetMatcher());
        Assert.Equal(Egress.Esim, engine2.Decide("gamma.com").Egress);
    }

    [Fact]
    public void Remove_manual_takes_effect()
    {
        var store = new DomainSetStore();
        store.AddManual("github.com");
        Assert.Equal(Egress.Esim, Engine(store).Decide("github.com").Egress);

        store.RemoveManual("github.com");
        Assert.Equal(Egress.UpstreamProxy, Engine(store).Decide("github.com").Egress);
    }

    [Fact]
    public void Managed_session_id_wins_over_domain()
    {
        var store = new DomainSetStore();
        store.ReplaceSelectedSets(Set("g", "+.google.com"));

        var d = new RoutingEngine(store.GetMatcher()).Decide("example.org", launchSessionId: "sess-1");
        Assert.Equal(Egress.Esim, d.Egress);
        Assert.Equal(RouteReason.ManagedApp, d.Reason);
        Assert.Equal("sess-1", d.LaunchSessionId);
    }
}