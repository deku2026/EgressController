using System.Text.Json.Nodes;
using EgressController.Policy;
namespace EgressController.Policy.Tests;

public sealed class RetainedGroupTests
{
    [Fact]
    public void DirectDefaultStillDownloadsThroughAnObservedProxyAndPreservesInventory()
    {
        var doc = PolicyTests.Document(); var group = doc.Groups.Single(group => group.Id == "fixture-global");
        group.MemberIds.Add(GroupGraph.Direct); group.SelectedMemberId = GroupGraph.Direct;
        string[] paths = doc.Applications[0].ExecutablePaths.ToArray();
        var now = DateTimeOffset.UtcNow; var options = PolicyTests.Options(); options.Now = now;
        Assert.Null(GroupGraph.ResourceNode(doc, [], now));
        options.Probes.Add(new(doc.Nodes[0].Id, true, 42, now, group.TestUrl));
        options.ResourceNodeId = GroupGraph.ResourceNode(doc, options.Probes, now);
        var compiled = XrayConfigCompiler.Compile(doc, options); Assert.True(compiled.IsSuccess);
        var rules = JsonNode.Parse(compiled.Json!)!["routing"]!["rules"]!.AsArray();
        Assert.Equal("nonhome", (string?)rules.Last()!["outboundTag"]);
        Assert.Equal(doc.Nodes[0].Id, (string?)rules[0]!["outboundTag"]);
        Assert.Equal(paths, doc.Applications[0].ExecutablePaths);
        Assert.Null(GroupGraph.ResourceNode(doc, [new(doc.Nodes[0].Id, false, 0, now, group.TestUrl)], now));
        Assert.Null(GroupGraph.ResourceNode(doc, options.Probes, now.AddMinutes(11)));
    }
    [Fact]
    public void NewClashDefaultAndIconSurviveTypedDslWithoutChangingHiddenDependencyGraph()
    {
        const string yaml = """
            proxies:
              - {name: first, type: socks5, server: node.example.invalid, port: 1080}
            proxy-groups:
              - {name: Claude, type: select, proxies: [first, DIRECT], default-selected: DIRECT, icon: https://icons.example.invalid/claude.png}
            """;
        var imported = ClashSubscriptionImporter.Import(yaml, "source"); var group = Assert.Single(imported.Groups);
        Assert.Equal(GroupGraph.Direct, group.SelectedMemberId);
        group.Retained = false; group.StrategyOverridden = true;
        var doc = PolicyTests.Document(); doc.Groups.Add(group);
        var roundTrip = PolicyYaml.Read(PolicyYaml.Write(doc)).Document!;
        var saved = roundTrip.Groups.Single(item => item.Id == group.Id);
        Assert.False(saved.Retained); Assert.True(saved.StrategyOverridden); Assert.Equal(group.Icon, saved.Icon);
        Assert.Equal(group.MemberIds, saved.MemberIds); Assert.Equal(GroupGraph.Direct, saved.SelectedMemberId);
    }
    [Fact]
    public void UnusedLocalRelayCannotBlockRemoteGroupOrBecomeASelectableProxy()
    {
        var doc = PolicyTests.Document();
        doc.Nodes.Add(new() { Id = "local", Name = "local relay", Protocol = NodeProtocol.Socks, Server = "127.0.0.1", Port = 7890 });
        var compiled = XrayConfigCompiler.Compile(doc, PolicyTests.Options());
        Assert.True(compiled.IsSuccess);
        Assert.DoesNotContain(JsonNode.Parse(compiled.Json!)!["outbounds"]!.AsArray(), item => (string?)item!["tag"] == "local");
        var group = doc.Groups.Single(group => group.Id == "fixture-global"); group.MemberIds.Add("local"); group.SelectedMemberId = "local";
        Assert.Contains(XrayConfigCompiler.Compile(doc, PolicyTests.Options()).Diagnostics, item => item.Code == "node.local-proxy");
    }
    [Fact]
    public void NodeOnlySubscriptionGetsStableAutomaticGroup()
    {
        const string yaml = "proxies: [{name: first, type: socks5, server: proxy.example.invalid, port: 1080}]";
        var first = ClashSubscriptionImporter.Import(yaml, "source"); var second = ClashSubscriptionImporter.Import(yaml, "source");
        Assert.Equal(GroupKind.UrlTest, Assert.Single(first.Groups).Kind);
        Assert.Equal(first.Groups[0].Id, second.Groups[0].Id); Assert.Equal(first.Nodes[0].Id, Assert.Single(first.Groups[0].MemberIds));
    }
}
