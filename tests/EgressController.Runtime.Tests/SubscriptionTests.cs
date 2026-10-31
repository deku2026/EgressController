using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class SubscriptionTests
{
    [Fact]
    public void RefreshPreservesIndependentExitsInventoriesAndExplicitGroupChoice()
    {
        var policy = ConfigurationTests.Policy();
        policy.Groups.Add(new() { Id = "group", Name = "Global", SourceId = "example", Kind = GroupKind.Select, MemberIds = ["node-1"], SelectedMemberId = "node-1" });
        policy.Defaults.Global = ExitChoice.Proxy("group");
        string before = PolicyYaml.Write(policy);
        var incomingNode = ConfigurationTests.Policy().Nodes[0]; incomingNode.Name = "Renamed server";
        var incomingGroup = new ProxyGroup { Id = "group", SourceId = "example", Name = "Global renamed", Kind = GroupKind.Select, MemberIds = ["node-1"] };
        var merged = SubscriptionReconciler.Merge(policy, "example", new([incomingNode], [incomingGroup], [], []));
        Assert.Equal(before, PolicyYaml.Write(policy));
        Assert.Equal("group", merged.Defaults.Global.TargetId);
        Assert.Equal("node-1", merged.Groups[0].SelectedMemberId);
        Assert.Null(incomingGroup.SelectedMemberId);
        Assert.Equal(policy.Applications[0].ExecutablePaths, merged.Applications[0].ExecutablePaths);
        Assert.Equal(ExitChoice.Home, merged.Applications[0].Via);
        Assert.Equal("Renamed server", merged.Nodes[0].Name);
    }

    [Fact]
    public void MissingNodesRemainExplicitlyUnavailableWithoutResettingTheSelection()
    {
        var policy = ConfigurationTests.Policy();
        var replacement = ConfigurationTests.Policy().Nodes[0]; replacement.Id = "node-2";
        var merged = SubscriptionReconciler.Merge(policy, "example", new([replacement], [], [], []));
        Assert.Equal("group-1", merged.Defaults.Global.TargetId);
        var missing = merged.Nodes.Single(node => node.Id == "node-1");
        Assert.False(missing.IsSupported);
        Assert.Contains(missing.Diagnostics, item => item.Code == "subscription.removed");
    }

    [Fact]
    public void ARefreshCannotRewriteAnotherSubscriptionScope()
    {
        var node = ConfigurationTests.Policy().Nodes[0]; node.SourceId = "other";
        Assert.Throws<ArgumentException>(() => SubscriptionReconciler.Merge(ConfigurationTests.Policy(), "example", new([node], [], [], [])));
    }

    [Fact]
    public void ProviderDefinitionsSurviveRefreshAndFullDslRoundTrip()
    {
        var current = ConfigurationTests.Policy();
        current.Providers.Add(new() { Id = "provider-1", SourceId = "example", Name = "Provider", Url = "https://nodes.example.invalid/old" });
        var node = ConfigurationTests.Policy().Nodes[0];
        var imported = new ClashImportResult([node], [], [new() { Id = "provider-1", SourceId = "example", Name = "Provider", Url = "https://nodes.example.invalid/new" }], []);
        var merged = SubscriptionReconciler.Merge(current, "example", imported);
        Assert.Equal("https://nodes.example.invalid/old", current.Providers.Single().Url);
        Assert.Equal("https://nodes.example.invalid/new", PolicyYaml.Read(PolicyYaml.Write(merged)).Document!.Providers.Single().Url);
    }

    [Theory]
    [InlineData("subscription.syntax")]
    [InlineData("subscription.duplicate-name")]
    public void FatalImportNeverRemovesThePreviousSelection(string error)
    {
        var current = ConfigurationTests.Policy();
        string before = PolicyYaml.Write(current);
        var partial = new ClashImportResult([], [], [], [new(error, "Invalid subscription structure.")]);
        Assert.Throws<InvalidOperationException>(() => SubscriptionReconciler.Merge(current, "example", partial));
        Assert.Equal(before, PolicyYaml.Write(current));
    }
}
