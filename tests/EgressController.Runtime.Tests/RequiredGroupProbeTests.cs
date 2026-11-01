using System.Text.Json;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class RequiredGroupProbeTests
{
    private const string InnerUrl = "https://inner.example.invalid/check", OuterUrl = "https://outer.example.invalid/check";
    [Fact]
    public async Task ManualSelectionDoesNotProbeUnrelatedAutomaticGroupsOrTheirHundredNodes()
    {
        var document = ConfigurationTests.Policy();
        for (int index = 2; index <= 102; index++) document.Nodes.Add(Node("node-" + index));
        document.Groups.Add(Auto("unrelated", InnerUrl, document.Nodes.Select(node => node.Id).ToArray()));
        var options = ConfigurationTests.Options(); var process = new CapturingProcess();
        Assert.False(RequiredGroupProbes.NeedsProbes(document, options));
        Assert.Empty(await new ObservatoryProbes(process).ProbeAsync(document, options, "/fixture/core", TestContext.Current.CancellationToken, requiredOnly: true));
        Assert.Empty(process.Requests);
    }
    [Fact]
    public void OnlyReachableEnabledRoutesContributeExactUrlSpecificMissingPairs()
    {
        var document = ConfigurationTests.Policy(); document.Nodes.Add(Node("node-2"));
        var group = Auto("required", InnerUrl, "node-1", "node-2"); document.Groups.Add(group);
        document.RuleSets.Add(new() { Id = "disabled", Enabled = false, Domains = [new(DomainMatchKind.Full, "example.invalid")], Via = ExitChoice.Proxy(group.Id) });
        var options = ConfigurationTests.Options(); options.Now = DateTimeOffset.UtcNow;
        Assert.Empty(RequiredGroupProbes.Missing(document, options));
        document.Applications[0].Via = ExitChoice.Proxy(group.Id);
        options.Probes.Add(new("node-1", true, 40, options.Now, InnerUrl));
        options.Probes.Add(new("node-2", true, 20, options.Now, OuterUrl));
        Assert.Equal(new RequiredGroupProbe("node-2", InnerUrl), Assert.Single(RequiredGroupProbes.Missing(document, options)));
        options.Probes.Add(new("node-2", false, 0, options.Now, InnerUrl));
        Assert.Empty(RequiredGroupProbes.Missing(document, options)); // Fresh failure stays unavailable; explicit user retest can replace it.
    }
    [Theory]
    [InlineData(-601, 10)]
    [InlineData(1, 10)]
    [InlineData(0, -1)]
    public void ExpiredFutureOrInvalidMeasurementsDoNotSuppressRequiredProbe(int seconds, double latency)
    {
        var document = ConfigurationTests.Policy(); var group = Auto("required", InnerUrl, "node-1"); document.Groups.Add(group); document.Defaults.Global = ExitChoice.Proxy(group.Id);
        var options = ConfigurationTests.Options(); options.Now = DateTimeOffset.UtcNow;
        options.Probes.Add(new("node-1", true, latency, options.Now.AddSeconds(seconds), InnerUrl));
        Assert.Single(RequiredGroupProbes.Missing(document, options));
    }
    [Fact]
    public async Task NestedAutomaticGroupsMeasureDependenciesBeforeOnlyTheResolvedOuterCandidate()
    {
        var document = ConfigurationTests.Policy(); document.Nodes.Add(Node("node-2")); document.Nodes.Add(Node("unused"));
        document.Groups.Add(Auto("inner", InnerUrl, "node-1", "node-2")); document.Groups.Add(Auto("outer", OuterUrl, "inner"));
        document.Groups.Add(Auto("unrelated", "https://unused.example.invalid/check", "unused")); document.Defaults.Global = ExitChoice.Proxy("outer");
        var process = new CapturingProcess(); var options = ConfigurationTests.Options();
        var results = await new ObservatoryProbes(process).ProbeAsync(document, options, "/fixture/core", TestContext.Current.CancellationToken, requiredOnly: true);
        Assert.Equal(3, process.Requests.Count); Assert.Equal(3, results.Count); Assert.Empty(options.Probes);
        Assert.Equal(2, process.Requests.Count(request => request.Url == InnerUrl));
        Assert.Equal(("node-2", OuterUrl), Assert.Single(process.Requests, request => request.Url == OuterUrl));
        Assert.DoesNotContain(process.Requests, request => request.Tag == "unused");
        var final = ConfigurationTests.Options(); final.Probes = results.ToList(); final.Now = results.Max(result => result.ObservedAt);
        Assert.Equal("node-2", GroupSelector.Resolve("outer", document, final.Probes, final.Now, final.ProbeMaxAge).NodeId);
        Assert.Empty(RequiredGroupProbes.Missing(document, final));
    }
    [Fact]
    public async Task AutomaticDialerDependenciesArePreparedBeforeMeasuringTheirClientNode()
    {
        var document = ConfigurationTests.Policy(); document.Nodes.Add(Node("node-2")); document.Nodes[0].DialerProxyId = "dialer";
        document.Groups.Add(Auto("dialer", InnerUrl, "node-2")); document.Groups.Add(Auto("outer", OuterUrl, "node-1")); document.Defaults.Global = ExitChoice.Proxy("outer");
        var options = ConfigurationTests.Options(); Assert.Equal(new RequiredGroupProbe("node-2", InnerUrl), Assert.Single(RequiredGroupProbes.Missing(document, options)));
        var process = new CapturingProcess();
        await new ObservatoryProbes(process).ProbeAsync(document, options, "/fixture/core", TestContext.Current.CancellationToken, requiredOnly: true);
        Assert.Equal(new[] { ("node-2", InnerUrl), ("node-1", OuterUrl) }, process.Requests);
        Assert.True(process.SawExplicitDialer);
    }
    [Fact]
    public async Task RequiredOnlyPreservesFreshObservationsWhileExplicitUserProbeStillMeasuresAll()
    {
        var document = ConfigurationTests.Policy(); document.Nodes.Add(Node("node-2")); document.Groups.Add(Auto("automatic", InnerUrl, "node-1", "node-2"));
        document.Defaults.Global = ExitChoice.Proxy("automatic"); var options = ConfigurationTests.Options(); options.Now = DateTimeOffset.UtcNow;
        var cached = new GroupProbe("node-1", true, 70, options.Now, InnerUrl); options.Probes.Add(cached);
        var process = new CapturingProcess(); var results = await new ObservatoryProbes(process).ProbeAsync(document, options, "/fixture/core", TestContext.Current.CancellationToken, requiredOnly: true);
        Assert.Equal(("node-2", InnerUrl), Assert.Single(process.Requests)); Assert.Contains(cached, results); Assert.Single(options.Probes);
        process.Requests.Clear();
        await new ObservatoryProbes(process).ProbeAsync(document, options, "/fixture/core", TestContext.Current.CancellationToken);
        Assert.Equal(4, process.Requests.Count); // Each group's configured URL, including the manual group.
    }
    [Fact]
    public void InvalidLegacyNodeExitAndMissingManualSelectionDoNotTriggerWidenedNetworkWork()
    {
        var document = ConfigurationTests.Policy(); document.Groups.Add(Auto("automatic", InnerUrl, "node-1"));
        document.Defaults.Global = ExitChoice.Proxy("node-1"); Assert.Empty(RequiredGroupProbes.Missing(document, ConfigurationTests.Options()));
        document.Defaults.Global = ExitChoice.Proxy("group-1"); document.Groups[0].SelectedMemberId = null;
        Assert.Empty(RequiredGroupProbes.Missing(document, ConfigurationTests.Options()));
    }
    private static ProxyGroup Auto(string id, string url, params string[] members) => new() { Id = id, Name = id, Kind = GroupKind.UrlTest, TestUrl = url, MemberIds = members.ToList() };
    private static ProxyNode Node(string id) => new() { Id = id, Name = id, SourceId = "fixture", Protocol = NodeProtocol.Socks, Server = "proxy.example.invalid", Port = 1080 };
    private sealed class CapturingProcess : IObservatoryProcess
    {
        public List<(string Tag, string Url)> Requests { get; } = [];
        public bool SawExplicitDialer;
        public Task<ObservatoryResult> RunAsync(string executable, string json, string outboundTag, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(json); string url = document.RootElement.GetProperty("observatory").GetProperty("probeURL").GetString()!;
            lock (Requests) Requests.Add((outboundTag, url));
            if (document.RootElement.GetProperty("outbounds").EnumerateArray().Any(outbound => outbound.TryGetProperty("streamSettings", out var stream)
                && stream.GetProperty("sockopt").TryGetProperty("dialerProxy", out _))) SawExplicitDialer = true;
            return Task.FromResult(new ObservatoryResult(true, outboundTag == "node-2" ? 10 : 70));
        }
    }
}
