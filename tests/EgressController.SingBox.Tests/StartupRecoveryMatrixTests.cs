using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using EgressController.Rules.Artifacts;
using EgressController.Rules.Catalog;
using EgressController.SingBox.Cli;
using EgressController.SingBox.Configuration;
using EgressController.SingBox.Core;
using EgressController.SingBox.Runtime;
using EgressController.State.SingBox;
using EgressController.Transport.Upstream;

namespace EgressController.SingBox.Tests;

/// <summary>2 TUN × 4 links × 6 proxy × 4 rule selection/cache × 3 core cache × 2 port changes × 3 cancellation = 3,456 cases.</summary>
public sealed class StartupRecoveryMatrixTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool conflict in new[] { false, true })
            foreach (bool home in new[] { false, true })
                foreach (bool proxy in new[] { false, true })
                    foreach (int portState in Enumerable.Range(0, 6)) // healthy, absent, refused, wrong protocol, timeout, HTTP failure
                        foreach (int rules in Enumerable.Range(0, 4)) // verified, missing, corrupt, no selected rules
                            foreach (int core in Enumerable.Range(0, 3))
                                foreach (bool changePort in new[] { false, true })
                                    foreach (int cancellation in Enumerable.Range(0, 3)) // none, before preparation, before apply
                                        yield return [conflict, home, proxy, portState, rules, core, changePort, cancellation];
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Startup_and_recovery_obey_all_enumerated_states(bool occupied, bool homeUp, bool proxyUp,
        int portState, int ruleCache, int coreCache, bool changePort, int cancellation)
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController.StartupMatrix", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = new SingBoxStateStore(root);
            var env = Environment(homeUp, proxyUp);
            int? savedPort = portState == 1 ? null : 7890;
            int? desiredPort = savedPort;
            int fault = portState;
            bool conflict = occupied;
            var requests = new List<int>();
            var transport = new DefaultProxyDownloadTransport(() => savedPort, port =>
            {
                requests.Add(port);
                return new HttpClient(new DownloadHandler(() => fault));
            });
            using var artifactsFetcher = new HttpRemoteFetcher(transport);
            var artifacts = new RuleArtifactStore(Path.Combine(root, "rules-cache"), artifactsFetcher);
            string ruleFile = Path.Combine(artifacts.RootDirectory, "rules", Commit, "openai.srs");
            if (ruleCache is 0 or 2) { Directory.CreateDirectory(Path.GetDirectoryName(ruleFile)!); File.WriteAllBytes(ruleFile, ruleCache == 0 ? RuleBody : "bad"u8.ToArray()); }
            if (coreCache != 1)
            {
                string exe = Path.Combine(root, "core", "1.13.19", "sing-box.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                File.WriteAllBytes(exe, coreCache == 0 ? CoreBody : "bad"u8.ToArray());
                state.SaveCurrent(new() { Version = "1.13.19", ExecutablePath = exe, Sha256 = Convert.ToHexString(SHA256.HashData(CoreBody)), VerifiedAtUtc = DateTimeOffset.UtcNow });
            }
            var manager = new SingBoxCoreManager(root, new SingBoxReleaseClient(transport), new MockCli(), state);
            var host = new MockHost();
            await using var service = new SingBoxService(host, state);
            CancellationTokenSource? cancelAtApply = null;
            SingBoxRuntimeCandidate? candidate = null;
            EgressProfileDocument Profile() => new EgressProfileDocument
            {
                DefaultAdapterId = env.DefaultAdapter.AdapterId.ToString(),
                ProxyAdapterId = env.ProxyAdapter.AdapterId.ToString(),
                UpstreamPorts = desiredPort is int port ? [port] : [],
                UpstreamPort = desiredPort ?? 7890,
                RuleSets = ruleCache == 3 ? [] : [new() { Name = "openai", Target = EgressRouteTarget.DefaultAdapter }]
            }.NormalizeAndValidate();
            async Task<SingBoxRuntimeCandidate> Prepare(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (conflict) throw new SingBoxServiceException("tun.conflict", "mock occupied TUN");
                using var scope = transport.UseDefaultPort(desiredPort);
                var rule = ruleCache == 3 ? null : await artifacts.EnsureAsync(Snapshot, "openai", token);
                if (rule is { Succeeded: false }) throw new SingBoxServiceException("rules.download", rule.Error!);
                var core = await manager.PrepareAsync(new(), token);
                var compilation = new EgressProfileCompiler().Compile(new()
                {
                    Profile = Profile(),
                    Environment = env,
                    UpstreamOwnerPaths = desiredPort is null ? [] : [@"C:\Mock\mihomo.exe"],
                    ApplicationRoutes = [new([@"C:\Mock\ChatGPT.exe"], EgressRouteTarget.DefaultAdapter)],
                    UnreadyUpstreamPorts = desiredPort is int p && fault == 2 ? [p] : [],
                    RuleSets = rule is null ? [] : [new("openai", rule.Path!)],
                    ControllerPort = 19090,
                    ControllerSecret = "0123456789abcdef0123456789abcdef"
                });
                using var json = JsonDocument.Parse(compilation.JsonBytes);
                var appRule = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray()
                    .Single(item => item.TryGetProperty("process_path_regex", out var paths) && paths.GetRawText().Contains("ChatGPT", StringComparison.Ordinal));
                Assert.Equal(homeUp ? "route" : "reject", appRule.GetProperty("action").GetString());
                string config = Path.Combine(root, "config-" + compilation.Sha256 + ".json");
                EgressProfileCompiler.WriteNext(config, compilation);
                candidate = SingBoxRuntimeCandidate.From(core, config, compilation.Sha256, 19090, "0123456789abcdef0123456789abcdef");
                cancelAtApply?.Cancel(); token.ThrowIfCancellationRequested();
                return candidate;
            }
            var first = await service.StartAsync(Prepare, TestContext.Current.CancellationToken);
            bool shouldStart = !occupied && (portState == 0 || (ruleCache is 0 or 3) && coreCache == 0);
            Assert.Equal(shouldStart, first.Succeeded);
            Assert.Equal(shouldStart ? 1 : 0, host.Starts);
            if (occupied || (ruleCache is 0 or 3) && coreCache == 0) Assert.Empty(requests);
            Assert.All(requests, port => Assert.Equal(7890, port));
            var health = new NetworkHealthState();
            int generation = health.ObserveEnvironment(env, desiredPort);
            health.Complete(generation, NetworkHealthCheck.DirectInternet, new(homeUp, "mock"), TestContext.Current.CancellationToken);
            health.Complete(generation, NetworkHealthCheck.ProxyInternet, new(proxyUp && portState == 0, "mock"), TestContext.Current.CancellationToken);
            Assert.Equal(shouldStart && homeUp && proxyUp && (desiredPort is null || portState == 0),
                ProtectionReadiness.Evaluate(first.Succeeded, first.ErrorMessage, first.Succeeded, null, null, health.Readiness).Ready);

            // Fix the external fault without changing either adapter. Changing the default port
            // is an independent matrix dimension; preparation must use the uncommitted new value.
            conflict = false; fault = 0; desiredPort = changePort ? 7891 : 7890;
            int before = host.Starts, requestCount = requests.Count;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            if (cancellation == 1) cancel.Cancel();
            if (cancellation == 2) cancelAtApply = cancel;
            var recovery = await service.ApplyAsync(Prepare, cancel.Token);
            if (cancellation != 0)
            {
                Assert.Equal("operation.cancelled", recovery.ErrorCode);
                Assert.Equal(before, host.Starts);
                cancelAtApply = null;
                recovery = await service.ApplyAsync(Prepare, TestContext.Current.CancellationToken);
            }
            Assert.True(recovery.Succeeded, recovery.ErrorMessage);
            Assert.All(requests.Skip(requestCount), port => Assert.Equal(desiredPort, port));
            Assert.Equal(SingBoxServiceState.Running, service.Status.State);
            int stableStarts = host.Starts;
            Assert.False((await service.ApplyCandidateAsync(candidate!, TestContext.Current.CancellationToken)).CoreChanged);
            Assert.Equal(stableStarts, host.Starts);
            generation = health.ObserveEnvironment(env, desiredPort);
            health.Complete(generation, NetworkHealthCheck.DirectInternet, new(homeUp, "mock"), TestContext.Current.CancellationToken);
            health.Complete(generation, NetworkHealthCheck.ProxyInternet, new(proxyUp, "mock"), TestContext.Current.CancellationToken);
            Assert.Equal(homeUp && proxyUp, ProtectionReadiness.Evaluate(true, null, true, null, null, health.Readiness).Ready);
            // A real takeover failure remains authoritative, independently of healthy network probes.
            service.ReportTakeoverFailure("mock lost TUN");
            Assert.False(ProtectionReadiness.Evaluate(false, "mock lost TUN", true, null, null, health.Readiness).Ready);
        }
        finally { Directory.Delete(root, true); }
    }

    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly byte[] RuleBody = "mock-srs"u8.ToArray(), CoreBody = "mock-native-core"u8.ToArray();
    private static readonly byte[] Zip = Archive();
    private static readonly SingBoxRuleCatalogSnapshot Snapshot = new(Commit, new string('b', 40),
        [new("openai", "geo/geosite/openai.srs", Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes($"blob {RuleBody.Length}\0").Concat(RuleBody).ToArray())).ToLowerInvariant(), RuleBody.Length)]);
    private static byte[] Archive()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var entry = zip.CreateEntry("sing-box-1.13.19-windows-amd64/sing-box.exe").Open()) entry.Write(CoreBody);
        return output.ToArray();
    }
    private static NetworkEnvironmentSnapshot Environment(bool home, bool proxy)
    {
        AdapterSelection Adapter(string id, string name, bool up, string ip) => new()
        {
            AdapterId = Guid.Parse(id),
            Alias = name,
            Luid = 1,
            IfIndex = 10,
            Ipv6IfIndex = 10,
            IsUp = up,
            AddressState = up ? AdapterAddressState.Ipv4Only : AdapterAddressState.NoAddress,
            Ipv4BindAddress = up ? IPAddress.Parse(ip) : null
        };
        var direct = Adapter("11111111-1111-1111-1111-111111111111", "Ethernet", home, "192.0.2.10");
        return new() { DefaultAdapter = direct, DnsAdapter = direct, ProxyAdapter = Adapter("22222222-2222-2222-2222-222222222222", "Wi-Fi", proxy, "198.51.100.10") };
    }
    private sealed class DownloadHandler(Func<int> fault) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (fault() == 2) throw new HttpRequestException("mock refused");
            if (fault() == 3) throw new HttpRequestException("mock invalid SOCKS5 greeting");
            if (fault() == 4) throw new TaskCanceledException("mock ConnectTimeout");
            if (fault() == 5) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            string path = request.RequestUri!.AbsolutePath;
            HttpContent body = path.EndsWith(".srs") ? new ByteArrayContent(RuleBody)
                : path.EndsWith(".zip") ? new ByteArrayContent(Zip)
                : new StringContent($$"""{"tag_name":"v1.13.19","assets":[{"name":"sing-box-1.13.19-windows-amd64.zip","browser_download_url":"https://mock.invalid/core.zip","size":{{Zip.Length}}}]}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        }
    }
    private sealed class MockCli : ISingBoxCli
    {
        public Task<SingBoxVersionInfo> GetVersionAsync(string path, CancellationToken token = default) => Task.FromResult(new SingBoxVersionInfo { Version = new(1, 13, 19), RawOutput = "mock" });
        public Task<SingBoxCommandResult> CheckAsync(string path, string config, CancellationToken token = default) => Task.FromResult(new SingBoxCommandResult { ExitCode = 0, StandardOutput = "", StandardError = "" });
    }
    private sealed class MockHost : ISingBoxProcessClient
    {
        public int Starts;
        public event Action<SingBoxOutputEvent>? Output { add { } remove { } }
        public Task<SingBoxProcessStatus> StartAsync(SingBoxRuntimeCandidate candidate, bool restart, CancellationToken token = default) { Starts++; return GetStatusAsync(token); }
        public Task<SingBoxProcessStatus> GetStatusAsync(CancellationToken token = default) => Task.FromResult(new SingBoxProcessStatus(true, "running", 1234, 0, null, null));
        public Task<SingBoxProcessStatus> StopAsync(CancellationToken token = default) => Task.FromResult(new SingBoxProcessStatus(true, "stopped", null, 0, null, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
