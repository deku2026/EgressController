using EgressController.Core.Rules;
using EgressController.Rules.Matcher;
using EgressController.Rules.Parsing;

namespace EgressController.Rules.Tests;

public class DomainMatcherTests
{
    private static DomainMatcher.RuleSetView Set(string name, params string[] lines)
    {
        Assert.True(StrictDomainListParser.TryParse(lines, name, out var rules, out _));
        return new DomainMatcher.RuleSetView(name, rules!);
    }

    private static DomainMatcher Build(params DomainMatcher.RuleSetView[] sets) => new(sets);

    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "www.example.com", false)]
    [InlineData("+.example.com", "example.com", true)]
    [InlineData("+.example.com", "www.example.com", true)]
    [InlineData("+.example.com", "a.b.example.com", true)]
    [InlineData("+.example.com", "evil-example.com", false)]
    [InlineData(".example.com", "www.example.com", true)]
    [InlineData(".example.com", "example.com", false)]
    [InlineData("+.openai.com", "api.openai.com", true)]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("xbox.*.microsoft.com", "xbox.live.microsoft.com", true)]
    [InlineData("xbox.*.microsoft.com", "xbox.microsoft.com", false)]
    [InlineData("*.*.microsoft.com", "a.b.microsoft.com", true)]
    [InlineData("*.*.microsoft.com", "a.microsoft.com", false)]
    public void Truth_table(string ruleText, string host, bool expected)
    {
        var m = Build(Set("r", ruleText));
        Assert.Equal(expected, m.Match(host).Matched);
    }

    [Fact]
    public void No_match_returns_no_match_with_empty_provenance()
    {
        var m = Build(Set("r", "example.com"));
        var r = m.Match("unrelated.org");
        Assert.False(r.Matched);
        Assert.Equal("", r.RuleSetName);
    }

    [Fact]
    public void Match_returns_concrete_provenance()
    {
        var m = Build(Set("geosite/openai", "+.openai.com"),
                      Set("manual", "+.api.openai.com"));
        var r = m.Match("api.openai.com");
        Assert.True(r.Matched);
        // Both sets match at equal specificity; first-encountered (geosite/openai) wins.
        Assert.Equal("geosite/openai", r.RuleSetName);
        Assert.Equal(DomainRuleKind.SuffixInclusive, r.RuleKind);
        Assert.Equal("+.openai.com", r.RuleText);
    }

    [Fact]
    public void Most_specific_rule_wins_within_a_set()
    {
        // Both exact and suffix match api.example.com; exact should be reported (higher spec).
        var m = Build(Set("s", "api.example.com", "+.example.com"));
        var r = m.Match("api.example.com");
        Assert.Equal(DomainRuleKind.Exact, r.RuleKind);
        Assert.Equal("api.example.com", r.RuleText);
    }

    [Fact]
    public void Manual_set_is_queried_after_rule_sets_but_specificity_wins_from_whichever()
    {
        // manual suffix + set exact for a concrete host -> exact is more specific.
        var m = Build(Set("manual", "+.example.com"), Set("geosite/google", "www.example.com"));
        var r = m.Match("www.example.com");
        Assert.Equal("www.example.com", r.RuleText); // exact from geosite (specificity 4 > 3)
    }

    [Fact]
    public void Suffix_never_matches_same_prefix_with_extra_dash()
    {
        // +.amazon.com must not match "evil-amazon.com"
        var m = Build(Set("s", "+.amazon.com", "+.amazonaws.com"));
        Assert.False(m.Match("evil-amazon.com").Matched);
    }

    [Fact]
    public void Query_is_case_insensitive_and_trailing_dot_is_ignored()
    {
        var m = Build(Set("s", "+.example.com"));
        Assert.True(m.Match("WWW.Example.COM.").Matched);
    }
}