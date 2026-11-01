using System.Text.Json;
using EgressController.Policy;

namespace EgressController.Policy.Tests;

public sealed class GroupExitTests
{
    [Theory]
    [InlineData("global")]
    [InlineData("application")]
    [InlineData("rule")]
    public void RoutesRetainLegacyNodeReferencesForExplicitGroupRepair(string location)
    {
        var doc = PolicyTests.Document(); string id = doc.Nodes[0].Id;
        if (location == "global") doc.Defaults.Global = ExitChoice.Proxy(id);
        if (location == "application") doc.Applications[0].Via = ExitChoice.Proxy(id);
        if (location == "rule") doc.RuleSets[0].Via = ExitChoice.Proxy(id);
        Assert.Contains(PolicyValidator.Validate(doc), diagnostic => diagnostic.Code == "proxy.group-required");
        Assert.False(XrayConfigCompiler.Compile(doc, PolicyTests.Options()).IsSuccess);
        var read = PolicyYaml.Read(PolicyYaml.Write(doc)); Assert.False(read.IsSuccess);
        Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Code == "proxy.group-required");
        var retained = location == "global" ? read.Document!.Defaults.Global : location == "application" ? read.Document!.Applications[0].Via : read.Document!.RuleSets[0].Via;
        Assert.Equal(ExitChoice.Proxy(id), retained);
    }
    [Fact]
    public void ExplicitManualGroupNodeMembershipRemainsSupportedForEveryRouteKind()
    {
        var doc = PolicyTests.Document();
        var selected = doc.Groups.Single(group => group.Id == "fixture-global"); doc.Applications[0].Via = ExitChoice.Proxy(selected.Id);
        Assert.Equal(doc.Nodes[0].Id, selected.SelectedMemberId);
        var compiled = XrayConfigCompiler.Compile(doc, PolicyTests.Options()); Assert.True(compiled.IsSuccess);
        using var json = JsonDocument.Parse(compiled.Json!);
        var routes = json.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray()
            .Where(rule => rule.GetProperty("ruleTag").GetString() is "global" or "app-editor" or "domains-service" or "ips-service").ToArray();
        Assert.Equal(4, routes.Length);
        foreach (var rule in routes)
            Assert.Equal(doc.Nodes[0].Id, rule.GetProperty("outboundTag").GetString());
    }
    [Fact]
    public void ServiceProxiesAnchorPreservesDeclaredGroupAndNodeMemberOrderAndInitialClashSelection()
    {
        const string yaml = """
            ServiceProxies: &ServiceProxies [automatic, first, second]
            proxies:
              - {name: first, type: socks5, server: first.example.test, port: 1080}
              - {name: second, type: socks5, server: second.example.test, port: 1080}
            proxy-groups:
              - {name: automatic, type: url-test, proxies: [second, first], url: https://probe.example.test/check}
              - {name: service, type: select, proxies: *ServiceProxies}
              - {name: another-service, type: select, proxies: *ServiceProxies}
            rules: [MATCH,DIRECT]
            """;
        var imported = ClashSubscriptionImporter.Import(yaml, "fixture-source"); Assert.True(imported.IsSuccess);
        var automatic = imported.Groups.Single(group => group.Name == "automatic");
        string first = imported.Nodes.Single(node => node.Name == "first").Id, second = imported.Nodes.Single(node => node.Name == "second").Id;
        foreach (var group in imported.Groups.Where(group => group.Kind == GroupKind.Select))
        { Assert.Equal(new[] { automatic.Id, first, second }, group.MemberIds); Assert.Equal(automatic.Id, group.SelectedMemberId); }
        Assert.Equal(new[] { second, first }, automatic.MemberIds);
        Assert.Contains(imported.Diagnostics, diagnostic => diagnostic.Code == "subscription.ignored.rules");
    }
}
