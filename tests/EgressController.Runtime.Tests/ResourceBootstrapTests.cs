using System.Net;
using System.Text.Json.Nodes;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ResourceBootstrapTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BootstrapIsTransientAndFailureRestoresPreviousRuntimeAndStoredPolicy(bool existing, bool commit)
    {
        var token = TestContext.Current.CancellationToken;
        var core = new MockCoreRuntime(); var store = new InMemoryGenerationStore();
        var coordinator = new ConfigurationCoordinator(core, store, new BoundedLifecycleLog());
        var policy = ConfigurationTests.Policy(); var options = ConfigurationTests.Options();
        if (existing) Assert.True((await coordinator.ApplyAsync(policy, options, ConfigurationTests.Assets(), "initial", token)).Succeeded);
        var previous = coordinator.Applied; var savedPaths = policy.Applications[0].ExecutablePaths.ToArray();
        policy.Defaults.ResourceProxyPort = 18765;
        policy.RuleSets.Add(new() { Id = "remote", Name = "Unprepared", Sources = ["geosite/example.yaml"], Via = ExitChoice.Home });
        await coordinator.BeginResourceBootstrapAsync(policy, options, token);
        var root = JsonNode.Parse(core.Current!.XrayJson)!;
        Assert.Equal("socks", (string?)root["inbounds"]![1]!["protocol"]);
        Assert.Equal(18765, (int?)root["inbounds"]![1]!["port"]);
        var rules = root["routing"]!["rules"]!.AsArray();
        Assert.Equal(new[] { "resources", "dns-upstream", "bootstrap-block" }, rules.Select(rule => (string?)rule!["ruleTag"]));
        Assert.Equal("node-1", (string?)rules[0]!["outboundTag"]);
        Assert.Equal("blocked", (string?)rules[2]!["outboundTag"]);
        Assert.DoesNotContain("ext:", core.Current.XrayJson);
        Assert.Equal(previous, await store.ReadCurrentAsync(token));
        if (commit)
        {
            policy.RuleSets.Clear();
            Assert.True((await coordinator.ApplyAsync(policy, options, ConfigurationTests.Assets(), "prepared", token)).Succeeded);
        }
        await coordinator.EndResourceBootstrapAsync(commit);
        Assert.Equal(savedPaths, policy.Applications[0].ExecutablePaths);
        Assert.Equal(commit ? coordinator.Applied : previous, core.Current);
        Assert.Equal(commit ? coordinator.Applied : previous, await store.ReadCurrentAsync(token));
    }

    [Fact]
    public async Task ReadyResourceRouteDoesNotRestartForMetadataOrRulePreparation()
    {
        var core = new MockCoreRuntime(); var coordinator = new ConfigurationCoordinator(core, new InMemoryGenerationStore(), new BoundedLifecycleLog());
        var policy = ConfigurationTests.Policy(); var options = ConfigurationTests.Options();
        await coordinator.ApplyAsync(policy, options, ConfigurationTests.Assets(), "initial", TestContext.Current.CancellationToken);
        policy.Applications[0].Name = "New display name";
        await coordinator.BeginResourceBootstrapAsync(policy, options, TestContext.Current.CancellationToken);
        await coordinator.EndResourceBootstrapAsync(false);
        Assert.Equal(1, core.ApplyCount); Assert.Equal(0, core.RestoreCount);
    }

    [Fact]
    public async Task FailureToStartBootstrapCannotPublishOrLosePreviousInventory()
    {
        var core = new MockCoreRuntime(); var store = new InMemoryGenerationStore();
        var coordinator = new ConfigurationCoordinator(core, store, new BoundedLifecycleLog());
        var policy = ConfigurationTests.Policy(); var options = ConfigurationTests.Options();
        await coordinator.ApplyAsync(policy, options, ConfigurationTests.Assets(), "initial", TestContext.Current.CancellationToken);
        var previous = coordinator.Applied; policy.Defaults.ResourceProxyPort++;
        core.FailApply = true;
        await Assert.ThrowsAsync<IOException>(() => coordinator.BeginResourceBootstrapAsync(policy, options, TestContext.Current.CancellationToken));
        Assert.Equal(previous, core.Current); Assert.Equal(previous, await store.ReadCurrentAsync(TestContext.Current.CancellationToken));
        await coordinator.EndResourceBootstrapAsync(false);
        Assert.Equal(1, core.RestoreCount);
    }

    [Fact]
    public async Task ConcurrentDownloadsRetainTheirPortsAndRedirectsCannotEscapeHttps()
    {
        var ports = new List<int>(); var ready = new List<int>();
        var transport = new XrayResourceTransport((port, _) => { lock (ready) ready.Add(port); return Task.CompletedTask; },
            port => { lock (ports) ports.Add(port); return new HttpClient(new Handler()); });
        ResourceDescriptor Request(int port, string path) => new() { Id = "example", Url = new("https://example.invalid/" + path), Exit = ResourceExitSnapshot.Xray(port) };
        await Task.WhenAll(transport.DownloadAsync(Request(17890, "ok"), TestContext.Current.CancellationToken),
            transport.DownloadAsync(Request(17891, "ok"), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { 17890, 17891 }, ports.Order()); Assert.Equal(new[] { 17890, 17891 }, ready.Order());
        await Assert.ThrowsAsync<IOException>(() => transport.DownloadAsync(Request(17890, "redirect"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CacheHitNeedsNoProxyAndMissingCoreNeverRequestsNetwork()
    {
        var cache = new InMemoryResourceCache(); var transport = new FixtureResourceTransport();
        var request = new ResourceDescriptor { Id = "example", Url = new("https://example.invalid/resource"), Exit = ResourceExitSnapshot.Xray(17890) };
        await cache.WriteAsync(ResourcePreparation.CacheKey(request), [1, 2], TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2 }, await new ResourcePreparation(transport, cache).PrepareAsync(request, _ => true, TestContext.Current.CancellationToken));
        string path = Path.Combine(Path.GetTempPath(), "egress-missing-core-" + Guid.NewGuid().ToString("N"));
        var core = new XrayCoreReleaseManager(transport, cache, path, _ => ValueTask.FromResult(ResourceExitSnapshot.Xray(17890)));
        var error = await Assert.ThrowsAsync<RuntimeFailureException>(() => core.PrepareBundledAsync(Path.Combine(path, "missing.zip"), TestContext.Current.CancellationToken));
        Assert.Equal("core.bundle", error.Code); Assert.Equal(0, transport.RequestCount); Assert.False(Directory.Exists(path));
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsolutePath == "/redirect"
                ? new(HttpStatusCode.Redirect) { Headers = { Location = new("http://example.invalid/unsafe") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
    }
}
