using Google.Protobuf;

namespace EgressController.Policy.Tests;

public sealed class GeoRuleCompilerTests
{
    [Fact]
    public void PreservesDomainFullSuffixKeywordAndRegexWireEnums()
    {
        var set = GeoRuleCompiler.Parse("""
            payload:
              - DOMAIN,exact.example.test
              - DOMAIN-SUFFIX,suffix.example.test
              - DOMAIN-KEYWORD,keyword
              - DOMAIN-REGEX,^regex[.]example[.]test$
            """, "services", RuleDataKind.Classical);
        Assert.True(set.IsSuccess); var bundle = GeoRuleCompiler.Compile([set]);
        var input = new CodedInputStream(bundle.GeoSite); Assert.Equal(10u, input.ReadTag());
        var entry = new CodedInputStream(input.ReadBytes().ToByteArray()); Assert.Equal(10u, entry.ReadTag()); Assert.Equal("SERVICES", entry.ReadString());
        var types = new Dictionary<string, int>();
        while (!entry.IsAtEnd)
        {
            Assert.Equal(18u, entry.ReadTag()); var domain = new CodedInputStream(entry.ReadBytes().ToByteArray()); Assert.Equal(8u, domain.ReadTag()); var type = domain.ReadEnum(); Assert.Equal(18u, domain.ReadTag()); types[domain.ReadString()] = type;
        }
        Assert.Equal(3, types["exact.example.test"]); Assert.Equal(2, types["suffix.example.test"]); Assert.Equal(0, types["keyword"]); Assert.Equal(1, types["^regex[.]example[.]test$"]);
    }
    [Fact]
    public void MetaDomainAndIpPayloadsPreserveSemanticsAndCanonicalizeNetwork()
    {
        var domains = GeoRuleCompiler.Parse("payload: ['+.example.test', exact.example.test]", "domain", RuleDataKind.Domain);
        Assert.Contains(new DomainRule(DomainMatchKind.Suffix, "example.test"), domains.Domains); Assert.Contains(new DomainRule(DomainMatchKind.Full, "exact.example.test"), domains.Domains);
        var ips = GeoRuleCompiler.Parse("payload: ['203.0.113.127/24', '2001:db8::1234/32']", "ip", RuleDataKind.Ip);
        Assert.True(ips.IsSuccess); Assert.Contains("203.0.113.0/24", ips.IpCidrs); Assert.Contains("2001:db8::/32", ips.IpCidrs);
        var bundle = GeoRuleCompiler.Compile([domains, ips]); Assert.Equal(2, bundle.DomainCount); Assert.Equal(2, bundle.CidrCount);
        var list = new CodedInputStream(bundle.GeoIp); Assert.Equal(10u, list.ReadTag()); var entry = new CodedInputStream(list.ReadBytes().ToByteArray()); Assert.Equal(10u, entry.ReadTag()); Assert.Equal("IP", entry.ReadString());
        var lengths = new List<int>();
        while (!entry.IsAtEnd)
        {
            Assert.Equal(18u, entry.ReadTag()); var cidr = new CodedInputStream(entry.ReadBytes().ToByteArray()); Assert.Equal(10u, cidr.ReadTag()); lengths.Add(cidr.ReadBytes().Length); Assert.Equal(16u, cidr.ReadTag()); Assert.Contains(cidr.ReadUInt32(), new uint[] { 24, 32 });
        }
        Assert.Contains(4, lengths); Assert.Contains(16, lengths);
    }
    [Fact]
    public void UnsupportedWildcardAndProcessRuleCannotBeSilentlyWidenedOrIgnored()
    {
        var wildcard = GeoRuleCompiler.Parse("payload: ['*a.example.test']", "test", RuleDataKind.Domain); Assert.False(wildcard.IsSuccess); Assert.Empty(wildcard.Domains);
        var classical = GeoRuleCompiler.Parse("payload: ['PROCESS-NAME,shared-host']", "test", RuleDataKind.Classical); Assert.False(classical.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => GeoRuleCompiler.Compile([classical]));
    }
    [Theory]
    [InlineData("payload: ['https://example.test']", RuleDataKind.Domain)]
    [InlineData("payload: ['+.bad/path']", RuleDataKind.Domain)]
    [InlineData("payload: ['DOMAIN,bad/path']", RuleDataKind.Classical)]
    [InlineData("payload: ['DOMAIN-SUFFIX,*.example.test']", RuleDataKind.Classical)]
    public void ParseRejectsMalformedExactAndSuffixNames(string yaml, RuleDataKind kind)
    {
        var parsed = GeoRuleCompiler.Parse(yaml, "test", kind); Assert.False(parsed.IsSuccess); Assert.Empty(parsed.Domains);
    }
    [Fact]
    public void DotAndStarWildcardsRemainSubdomainOnlyInsteadOfWideningToApex()
    {
        var parsed = GeoRuleCompiler.Parse("payload: ['.example.test', '*.other.test']", "test", RuleDataKind.Domain); Assert.True(parsed.IsSuccess);
        Assert.All(parsed.Domains, x => Assert.Equal(DomainMatchKind.Regex, x.Kind));
        Assert.DoesNotMatch(parsed.Domains[0].Value, "example.test");
        Assert.Matches(parsed.Domains[0].Value, "a.b.example.test");
        Assert.Matches(parsed.Domains[1].Value, "a.other.test");
        Assert.DoesNotMatch(parsed.Domains[1].Value, "a.b.other.test");
    }
    [Fact]
    public void DeterministicDatOrderingAndDuplicateEntries()
    {
        var a = GeoRuleCompiler.Parse("payload: ['+.b.example.test', '+.a.example.test', '+.a.example.test']", "test", RuleDataKind.Domain);
        var b = GeoRuleCompiler.Parse("payload: ['+.a.example.test', '+.b.example.test']", "test", RuleDataKind.Domain);
        Assert.Equal(GeoRuleCompiler.Compile([a]).GeoSite, GeoRuleCompiler.Compile([b]).GeoSite); Assert.Equal(GeoRuleCompiler.Compile([a]).Digest, GeoRuleCompiler.Compile([b]).Digest);
    }
    [Fact]
    public async Task AtomicPublicationPreservesPreviousGenerationOnCancellationAndCorruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "egress-policy-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = GeoRuleCompiler.Compile([GeoRuleCompiler.Parse("payload: ['+.one.example.test']", "test", RuleDataKind.Domain)]);
            var committed = await GeoRuleCompiler.PublishAsync(directory, first, TestContext.Current.CancellationToken); var current = await File.ReadAllTextAsync(Path.Combine(directory, "CURRENT"), TestContext.Current.CancellationToken); Assert.Equal(first.Digest + "\n", current);
            var second = GeoRuleCompiler.Compile([GeoRuleCompiler.Parse("payload: ['+.two.example.test']", "test", RuleDataKind.Domain)]);
            using var cts = new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GeoRuleCompiler.PublishAsync(directory, second, cts.Token)); Assert.Equal(current, await File.ReadAllTextAsync(Path.Combine(directory, "CURRENT"), TestContext.Current.CancellationToken));
            var changedBytes = second.GeoSite.ToArray(); changedBytes[0] ^= 1;
            await Assert.ThrowsAsync<InvalidDataException>(() => GeoRuleCompiler.PublishAsync(directory, second with { GeoSite = changedBytes }, TestContext.Current.CancellationToken)); Assert.Equal(current, await File.ReadAllTextAsync(Path.Combine(directory, "CURRENT"), TestContext.Current.CancellationToken));
            await File.WriteAllTextAsync(Path.Combine(committed.GenerationDirectory, "geosite.dat"), "corrupt", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => GeoRuleCompiler.PublishAsync(directory, first, TestContext.Current.CancellationToken)); Assert.Equal(current, await File.ReadAllTextAsync(Path.Combine(directory, "CURRENT"), TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
