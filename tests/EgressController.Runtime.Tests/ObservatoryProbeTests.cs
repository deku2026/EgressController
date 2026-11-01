using System.Text.Json.Nodes;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ObservatoryProbeTests
{
    [Theory]
    [InlineData("2026 [Info] app/observatory: the outbound node-1 is alive:0.125", true, 125)]
    [InlineData("[Info] app/observatory: the outbound node-1 is dead: GET request failed: redacted", false, 0)]
    public void ParsesPinnedObserverResults(string text, bool available, double milliseconds)
    {
        Assert.Equal(new ObservatoryResult(available, milliseconds, available ? null : "connection.failed"), ObservatoryProbes.ParseLine(text, "node-1"));
        Assert.Null(ObservatoryProbes.ParseLine(text, "other-node"));
    }
    [Theory]
    [InlineData("[Info] app/observatory: the outbound node-1 is alive:NaN")]
    [InlineData("[Info] app/observatory: the outbound node-1 is alive:-1")]
    [InlineData("the outbound node-1 is alive:0.1")]
    [InlineData("[Info] app/observatory: some error containing the outbound node-1 is alive:0.1")]
    public void UnknownOrInvalidOutputIsNeverHealthy(string text) => Assert.Null(ObservatoryProbes.ParseLine(text, "node-1"));
    [Fact]
    public void ChildEnvironmentCannotMergeInheritedConfigurationsOrOverrideAssets()
    {
        var start = new System.Diagnostics.ProcessStartInfo("/fixture/xray");
        start.Environment["XRAY_LOCATION_CONFDIR"] = "/fixture/untrusted";
        start.Environment["xray.location.confdir"] = "/fixture/also-untrusted";
        start.Environment["xray.location.asset"] = "/fixture/wrong-assets";
        start.Environment["XRAY_TUN_FD"] = "42";
        XrayProcessEnvironment.Configure(start, "/fixture/owned");
        Assert.DoesNotContain("XRAY_LOCATION_CONFDIR", start.Environment.Keys);
        Assert.DoesNotContain("xray.location.confdir", start.Environment.Keys);
        Assert.DoesNotContain("xray.location.asset", start.Environment.Keys);
        Assert.DoesNotContain("XRAY_TUN_FD", start.Environment.Keys);
        Assert.Equal("/fixture/owned", start.Environment["XRAY_LOCATION_ASSET"]);
    }
    [Fact]
    public async Task MockProbeConfigurationHasNoInboundApiOrExternalDatDependency()
    {
        var fake = new CapturingProcess();
        var doc = ConfigurationTests.Policy();
        doc.Groups.Add(new()
        {
            Id = "group",
            Name = "automatic",
            Kind = EgressController.Policy.GroupKind.UrlTest,
            MemberIds = ["node-1"],
            TestUrl = "https://probe.example.invalid/check"
        });
        var result = await new ObservatoryProbes(fake).ProbeAsync(doc, ConfigurationTests.Options(), "/fixture/xray", TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count); Assert.All(result, probe => { Assert.True(probe.Available); Assert.Equal(125, probe.LatencyMilliseconds); });
        Assert.NotNull(fake.Json);
        var json = JsonNode.Parse(fake.Json!)!;
        Assert.Empty(json["inbounds"]!.AsArray()); Assert.Null(json["api"]);
        Assert.DoesNotContain("ext:", fake.Json!);
        Assert.Equal("en5", json["outbounds"]!.AsArray().Single(node => (string?)node!["tag"] == "node-1")!["streamSettings"]!["sockopt"]!["interface"]!.GetValue<string>());
    }
    [Fact]
    public async Task ExplicitNodeTestWorksWithoutAnAutomaticGroup()
    {
        var fake = new CapturingProcess();
        var result = await new ObservatoryProbes(fake).ProbeAsync(ConfigurationTests.Policy(), ConfigurationTests.Options(), "/fixture/xray", TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.Equal("https://www.gstatic.com/generate_204", result[0].TestUrl);
        Assert.True(result[0].Available);
    }
    [Fact]
    public async Task DeniedProbeGatePrecedesAnyFilesystemOrProcessEffect()
    {
        string root = Path.Combine(Path.GetTempPath(), "egress-denied-probe-" + Guid.NewGuid().ToString("N"));
        var process = new XrayObservatoryProcess(() => throw new InvalidOperationException("Mock safety"), root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => process.RunAsync("/fixture/xray", "{}", "node-1", TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task LargeSubscriptionUsesSmallBoundConfigsAndBoundedConcurrentProgress()
    {
        var doc = ConfigurationTests.Policy(); var template = doc.Nodes[0];
        for (int i = 0; i < 120; i++)
        {
            var node = EgressController.Policy.PolicyYaml.Read(EgressController.Policy.PolicyYaml.Write(doc)).Document!.Nodes[0];
            node.Id = "bulk-" + i; node.Name = "Synthetic " + i; doc.Nodes.Add(node);
        }
        // An unrelated broken application must not make healthy nodes appear dead.
        doc.Applications.Add(new() { Id = "broken", Name = "Broken", ExecutablePaths = [], Via = EgressController.Policy.ExitChoice.Proxy("missing") });
        var process = new BatchProcess(); var progress = new ProgressCounter();
        var requests = doc.Nodes.Select(node => new RequiredGroupProbe(node.Id, "https://example.invalid/204")).ToArray();
        var result = await new ObservatoryProbes(process).ProbeAsync(doc, ConfigurationTests.Options(), "/fixture/xray", TestContext.Current.CancellationToken, requested: requests, progress: progress);
        Assert.Equal(121, result.Count); Assert.Equal(121, progress.Count); Assert.All(result, probe => Assert.True(probe.Available));
        Assert.InRange(process.Maximum, 2, 6);
    }
    private sealed class ProgressCounter : IProgress<EgressController.Policy.GroupProbe>
    {
        public int Count; public void Report(EgressController.Policy.GroupProbe result) => Interlocked.Increment(ref Count);
    }
    private sealed class BatchProcess : IObservatoryProcess
    {
        private int _active; public int Maximum;
        public async Task<ObservatoryResult> RunAsync(string executable, string json, string tag, CancellationToken token)
        {
            var root = JsonNode.Parse(json)!; Assert.Empty(root["inbounds"]!.AsArray()); Assert.Null(root["api"]);
            Assert.InRange(root["outbounds"]!.AsArray().Count, 4, 5);
            Assert.DoesNotContain("ext:", json); Assert.DoesNotContain("missing", json);
            Assert.Equal("en5", root["outbounds"]!.AsArray().Single(item => (string?)item!["tag"] == tag)!["streamSettings"]!["sockopt"]!["interface"]!.GetValue<string>());
            int current = Interlocked.Increment(ref _active); lock (this) Maximum = Math.Max(Maximum, current);
            try { await Task.Delay(3, token); return new(true, 20); } finally { Interlocked.Decrement(ref _active); }
        }
    }
    private sealed class CapturingProcess : IObservatoryProcess
    {
        public string? Json { get; private set; }
        public Task<ObservatoryResult> RunAsync(string executable, string json, string tag, CancellationToken cancellationToken)
        { Json = json; return Task.FromResult(new ObservatoryResult(true, 125)); }
    }
}
