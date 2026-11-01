using System.Text;
using EgressController.Policy;
using EgressController.Runtime;
namespace EgressController.Runtime.Tests;

public sealed class HotRoutingTests
{
    [Fact]
    public void SwitchingMembersAndCompletingBootstrapAreRouteOnlyButInterfaceAndNodeEditsAreNot()
    {
        var doc = ConfigurationTests.Policy(); doc.Nodes.Add(new() { Id = "node-2", Name = "second", Protocol = NodeProtocol.Socks, Server = "second.example.invalid", Port = 1080 });
        var group = doc.Groups.Single(); group.MemberIds.Add("node-2");
        var options = ConfigurationTests.Options(); options.ResourceBootstrap = true;
        string before = XrayConfigCompiler.Compile(doc, options).Json!;
        options.ResourceBootstrap = false;
        string complete = XrayConfigCompiler.Compile(doc, options).Json!;
        Assert.True(RuntimeRoutingUpdate.CanReplace(before, complete));
        group.SelectedMemberId = "node-2";
        string changed = XrayConfigCompiler.Compile(doc, options).Json!;
        Assert.True(RuntimeRoutingUpdate.CanReplace(complete, changed));
        doc.Nodes[1].Port++;
        Assert.False(RuntimeRoutingUpdate.CanReplace(changed, XrayConfigCompiler.Compile(doc, options).Json!));
        doc.Nodes[1].Port--; options.Interfaces[0] = options.Interfaces[0] with { BsdName = "en9" };
        Assert.False(RuntimeRoutingUpdate.CanReplace(changed, XrayConfigCompiler.Compile(doc, options).Json!));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOrCanceledUpdateRestoresPreviousTableWithoutCallerCancellation(bool canceled)
    {
        var calls = new List<string>(); using var cancellation = new CancellationTokenSource();
        var failure = await Assert.ThrowsAsync<RuntimeFailureException>(() => RuntimeRoutingUpdate.ReplaceAsync("old", "new", (table, token) =>
        {
            calls.Add(table);
            if (table == "new") { cancellation.Cancel(); if (canceled) throw new OperationCanceledException(); throw new IOException(); }
            Assert.False(token.CanBeCanceled); return Task.CompletedTask;
        }, cancellation.Token));
        Assert.Equal("routing.update", failure.Code); Assert.Equal(new[] { "new", "old" }, calls);
    }
    [Fact]
    public async Task FailedRollbackReportsUnavailableInsteadOfClaimingOldRoute()
    {
        var failure = await Assert.ThrowsAsync<RuntimeFailureException>(() => RuntimeRoutingUpdate.ReplaceAsync("old", "new", (_, _) => throw new IOException(), TestContext.Current.CancellationToken));
        Assert.Equal("recovery.failed", failure.Code);
    }
    [Fact]
    public async Task CacheOnlyValidatesBytesWithoutTransportAndMissDoesNotLeaveFailureHistory()
    {
        var transport = new FixtureResourceTransport(); var cache = new InMemoryResourceCache(); var prepare = new ResourcePreparation(transport, cache);
        var resource = new ResourceDescriptor { Id = "fixture", Url = new("https://resource.example.invalid/rule"), Exit = ResourceExitSnapshot.Xray(18990), CachePolicy = PreparationCachePolicy.CacheOnly };
        await Assert.ThrowsAsync<ResourceCacheMissException>(() => prepare.PrepareAsync(resource, bytes => bytes.SequenceEqual("ok"u8.ToArray()), TestContext.Current.CancellationToken));
        await cache.WriteAsync(ResourcePreparation.CacheKey(resource), Encoding.UTF8.GetBytes("corrupt"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ResourceCacheMissException>(() => prepare.PrepareAsync(resource, bytes => bytes.SequenceEqual("ok"u8.ToArray()), TestContext.Current.CancellationToken));
        await cache.WriteAsync(ResourcePreparation.CacheKey(resource), "ok"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal("ok"u8.ToArray(), await prepare.PrepareAsync(resource, bytes => bytes.SequenceEqual("ok"u8.ToArray()), TestContext.Current.CancellationToken));
        Assert.Empty(prepare.History); Assert.Equal(0, transport.RequestCount);
    }
    [Fact]
    public void StartupDiagnosisDoesNotConsumeRoutineConnectionErrorsOrRetainTheirContents()
    {
        var diagnosis = new CoreStartupDiagnosis(); diagnosis.Observe("failed to process connection password=private permission denied"); Assert.Equal("core.exited", diagnosis.Code);
        diagnosis.Observe("Failed to start: failed to listen: address already in use"); Assert.Equal("proxy.port-in-use", diagnosis.Code);
        diagnosis.Reset(); Assert.Equal("core.exited", diagnosis.Code);
    }
    [Fact]
    public void HotSwitchKeepsExistingConnectionGenerationWhileNewConnectionsUseNewPolicy()
    {
        var now = DateTimeOffset.UtcNow; string before = new('a', 64), after = new('b', 64);
        var observer = new XrayConnectionObserver(before);
        observer.ObserveLine("[Info] [42] proxy/tun: processing from tcp:192.0.2.4:54321 to tcp:203.0.113.8:443", now);
        observer.SetGeneration(after);
        observer.ObserveLine("[Info] [42] app/dispatcher: Hit route rule: [global] so taking detour [node-1] for [tcp:203.0.113.8:443]", now);
        observer.ObserveLine("from tcp:192.0.2.4:54322 accepted tcp:203.0.113.8:443 [tun -> nonhome]", now);
        var snapshot = observer.Snapshot(now); Assert.Equal(after, snapshot.GenerationId);
        Assert.Equal(before, snapshot.Records.Single(row => row.SourcePort == 54321).GenerationId);
        Assert.Equal(after, snapshot.Records.Single(row => row.SourcePort == 54322).GenerationId);
    }
    [Fact]
    public void SubscriptionRefreshPreservesRetentionAndExplicitModeOverride()
    {
        var doc = ConfigurationTests.Policy(); var group = doc.Groups.Single(); group.SourceId = "source"; group.Retained = false; group.StrategyOverridden = true; group.MemberIds.Add(GroupGraph.Direct); group.SelectedMemberId = GroupGraph.Direct;
        var incoming = new ProxyGroup { Id = group.Id, Name = "renamed", SourceId = "source", Kind = GroupKind.UrlTest, MemberIds = group.MemberIds.Where(id => id != GroupGraph.Direct).ToList() };
        doc.Nodes[0].SourceId = "source";
        var updated = SubscriptionReconciler.Merge(doc, "source", new(doc.Nodes, [incoming], [], []));
        var saved = updated.Groups.Single(); Assert.False(saved.Retained); Assert.Equal(group.Kind, saved.Kind); Assert.Equal(group.SelectedMemberId, saved.SelectedMemberId); Assert.Contains(GroupGraph.Direct, saved.MemberIds);
    }
}
