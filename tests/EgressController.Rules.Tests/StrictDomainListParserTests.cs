using EgressController.Core.Rules;
using EgressController.Rules.Parsing;

namespace EgressController.Rules.Tests;

public class StrictDomainListParserTests
{
    private static CompiledDomainRule Single(string line)
    {
        Assert.True(StrictDomainListParser.TryCompileSingle(line, out var rule), $"expected '{line}' to parse");
        return rule;
    }

    [Fact]
    public void Bare_host_is_Exact()
    {
        var r = Single("example.com");
        Assert.Equal(DomainRuleKind.Exact, r.Kind);
        Assert.Equal("example.com", r.Base);
    }

    [Fact]
    public void Plus_dot_is_SuffixInclusive()
    {
        var r = Single("+.example.com");
        Assert.Equal(DomainRuleKind.SuffixInclusive, r.Kind);
        Assert.Equal("example.com", r.Base);
    }

    [Fact]
    public void Leading_dot_is_SubdomainSuffix()
    {
        var r = Single(".example.com");
        Assert.Equal(DomainRuleKind.SubdomainSuffix, r.Kind);
        Assert.Equal("example.com", r.Base);
    }

    [Fact]
    public void Trailing_dot_is_normalized_away()
    {
        Assert.Equal("example.com", Single("example.com.").Base);
    }

    [Fact]
    public void Wildcard_is_LabelWildcard()
    {
        var r = Single("xbox.*.microsoft.com");
        Assert.Equal(DomainRuleKind.LabelWildcard, r.Kind);
        Assert.Equal("xbox.*.microsoft.com", r.Base);
    }

    [Theory]
    [InlineData("a*b.com")]        // partial wildcard label
    [InlineData("example..com")]   // empty label
    [InlineData("foo,bar")]        // comma
    [InlineData("http://x.com")]   // scheme
    [InlineData("-bad.com")]       // leading hyphen
    [InlineData("*")]              // bare star with no fixed label
    public void Unsupported_or_malformed_are_rejected(string line)
    {
        Assert.False(StrictDomainListParser.TryCompileSingle(line, out _), $"expected '{line}' to be rejected");
    }

    [Fact]
    public void Idn_is_normalized_to_punycode()
    {
        var r = Single("例え.テスト");
        Assert.Equal("xn--r8jz45g.xn--zckzah", r.Base);
    }

    [Fact]
    public void Single_label_wildcard_is_valid_wildcard()
    {
        // "*.com" has a fixed label ("com"), so it is a legitimate one-arbitrary-label wildcard.
        var r = Single("*.com");
        Assert.Equal(DomainRuleKind.LabelWildcard, r.Kind);
        Assert.Equal("*.com", r.Base);
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        Assert.True(StrictDomainListParser.TryParse(
            new[] { "# a comment", "; b", "example.com", "", "  ", "+.openai.com" }, "test",
            out var rules, out var failure));
        Assert.Null(failure);
        Assert.Equal(2, rules!.Count);
    }

    [Fact]
    public void Unknown_line_fails_whole_set_no_partial_activation()
    {
        // a valid rule, then an unknown one -> the caller must NOT use the partial set.
        Assert.False(StrictDomainListParser.TryParse(
            new[] { "example.com", "!!untime" }, "bad",
            out var rules, out var failure));
        Assert.NotNull(failure);
        Assert.Equal(2, failure!.LineNumber);
        Assert.Equal("!!untime", failure.LineText);
        Assert.Single(rules!); // partial accumulated but TryParse=false signals rejection
    }

    [Fact]
    public void Manual_default_is_SuffixInclusive()
    {
        var r = StrictDomainListParser.ManualDefault("openai.com", "manual");
        Assert.Equal(DomainRuleKind.SuffixInclusive, r.Kind);
        Assert.Equal("openai.com", r.Base);
        Assert.Equal("+.openai.com", r.OriginalText);
    }
}