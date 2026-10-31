using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class ProductionResourceTests
{
    private static readonly InterfaceBinding Binding = new("stable-nonhome", "en7");
    [Fact]
    public async Task Failed_binding_prevents_any_connect_and_has_no_unbound_retry()
    {
        var binder = new FailingBinder(); var dns = new FixedResolver(); var transport = new BoundHttpsResourceTransport(binder, dns);
        await Assert.ThrowsAsync<IOException>(async () => await transport.ConnectAsync(new DnsEndPoint("example.test", 443), Binding, TestContext.Current.CancellationToken));
        Assert.Equal(1, binder.Calls); Assert.Equal(1, dns.Calls); Assert.Equal(Binding, binder.Binding);
        await Assert.ThrowsAsync<ArgumentException>(() => transport.DownloadAsync(new ResourceDescriptor { Id = "x", Url = new("http://example.test"), Exit = ResourceExitSnapshot.Bound(Binding) }, TestContext.Current.CancellationToken));
        Assert.Equal(1, binder.Calls);
    }
    [Fact]
    public async Task Https_redirects_remain_https_and_bodies_have_hard_limits_without_network()
    {
        var handler = new HttpFixture(); int factories = 0;
        var transport = new BoundHttpsResourceTransport(new FailingBinder(), new FixedResolver(), sockets =>
        {
            factories++; Assert.False(sockets.UseProxy); Assert.False(sockets.AllowAutoRedirect); Assert.False(sockets.UseCookies); Assert.NotNull(sockets.ConnectCallback);
            return handler;
        });
        var descriptor = new ResourceDescriptor { Id = "synthetic", Url = new("https://resource.test/start"), Exit = ResourceExitSnapshot.Bound(Binding), MaximumBytes = 3 };
        Assert.Equal(new byte[] { 1, 2, 3 }, await transport.DownloadAsync(descriptor, TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Calls); Assert.Equal(1, factories);
        var downgrade = new BoundHttpsResourceTransport(new FailingBinder(), new FixedResolver(), _ => new HttpFixture { Downgrade = true });
        await Assert.ThrowsAsync<IOException>(() => downgrade.DownloadAsync(descriptor, TestContext.Current.CancellationToken));
        var oversized = new BoundHttpsResourceTransport(new FailingBinder(), new FixedResolver(), _ => new HttpFixture { Oversize = true });
        await Assert.ThrowsAsync<IOException>(() => oversized.DownloadAsync(descriptor, TestContext.Current.CancellationToken));
        var failed = new BoundHttpsResourceTransport(new FailingBinder(), new FixedResolver(), _ => new HttpFixture { Fail = true });
        var error = await Assert.ThrowsAsync<IOException>(() => failed.DownloadAsync(descriptor, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }
    [Fact]
    public async Task Bound_dns_validates_questions_and_retries_truncated_udp_with_bound_tcp()
    {
        var exchange = new DnsFixture { TruncateUdp = true }; var resolver = new BoundDnsResolver(exchange, IPAddress.Parse("1.1.1.1"));
        var results = await resolver.ResolveAsync("example.test", Binding, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { IPAddress.Parse("192.0.2.8"), IPAddress.Parse("2001:db8::8") }, results);
        Assert.Equal(4, exchange.Calls.Count); Assert.All(exchange.Calls, call => Assert.Equal(Binding, call.Binding));
        Assert.Equal(new[] { false, true, false, true }, exchange.Calls.Select(call => call.Tcp));
        exchange.WrongQuestion = true;
        await Assert.ThrowsAsync<IOException>(async () => await resolver.ResolveAsync("example.test", Binding, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Bound_dns_rejects_compression_cycles_and_does_not_call_default_dns()
    {
        var exchange = new DnsFixture { CompressionLoop = true }; var resolver = new BoundDnsResolver(exchange, IPAddress.Loopback);
        await Assert.ThrowsAsync<IOException>(async () => await resolver.ResolveAsync("example.test", Binding, TestContext.Current.CancellationToken));
        var literal = await resolver.ResolveAsync("192.0.2.1", Binding, TestContext.Current.CancellationToken);
        Assert.Single(literal); Assert.Single(exchange.Calls);
    }
    [Fact]
    public async Task Private_cache_is_hashed_private_durable_and_detects_corruption()
    {
        string root = TempRoot();
        try
        {
            var cache = new PrivateDiskResourceCache(root, 1024); byte[] secret = Encoding.UTF8.GetBytes("synthetic-sensitive-content");
            Assert.Null(await cache.ReadAsync("private-source?token=fixture", TestContext.Current.CancellationToken));
            await cache.WriteAsync("private-source?token=fixture", secret, TestContext.Current.CancellationToken);
            Assert.Equal(secret, await cache.ReadAsync("private-source?token=fixture", TestContext.Current.CancellationToken));
            string path = Assert.Single(Directory.GetFiles(root)); Assert.DoesNotContain("token", path);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
            File.WriteAllBytes(path, new byte[40]); Assert.Null(await cache.ReadAsync("private-source?token=fixture", TestContext.Current.CancellationToken));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.WriteAsync("x", secret, cancel.Token));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Private_cache_rejects_symlinks_and_public_directories()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = TempRoot(); string link = root + "-link";
        try
        {
            Directory.CreateDirectory(root); File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
            var cache = new PrivateDiskResourceCache(root); await Assert.ThrowsAsync<IOException>(() => cache.WriteAsync("x", [1], TestContext.Current.CancellationToken));
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); Directory.CreateSymbolicLink(link, root);
            await Assert.ThrowsAsync<IOException>(() => new PrivateDiskResourceCache(link).WriteAsync("x", [1], TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Subscription_fetch_resolves_provider_on_same_snapshot_and_ignores_route_policy()
    {
        var transport = new FixtureResourceTransport(); var cache = new InMemoryResourceCache(); int snapshots = 0;
        transport.Add(new("https://subscription.test/list"), Encoding.UTF8.GetBytes("""
            mixed-port: 7890
            dns: {enable: true}
            rules: [MATCH,DIRECT]
            proxy-providers:
              upstream: {type: http, url: https://provider.test/list}
            proxy-groups:
              - {name: choose, type: select, use: [upstream]}
            """));
        transport.Add(new("https://provider.test/list"), Encoding.UTF8.GetBytes("proxies: [{name: fixture, type: socks5, server: 192.0.2.1, port: 1080}]"));
        var resources = new ProductionResources(transport, cache, token => { snapshots++; return ValueTask.FromResult(Binding); });
        var source = new SubscriptionSource { Id = "s", Url = "https://subscription.test/list" };
        var result = await resources.FetchSubscriptionAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess); Assert.Single(result.Nodes); Assert.Single(result.Providers); Assert.Single(result.Groups);
        Assert.Contains(result.Nodes[0].Id, result.Groups[0].MemberIds); Assert.Contains(result.Diagnostics, item => item.Code.StartsWith("subscription.ignored.", StringComparison.Ordinal));
        Assert.Equal(1, snapshots); Assert.All(transport.UsedInterfaces, item => Assert.Equal(Binding, item));
        await resources.FetchSubscriptionAsync(source, cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(2, transport.RequestCount);
        await resources.FetchSubscriptionAsync(source, PreparationCachePolicy.Refresh, TestContext.Current.CancellationToken); Assert.Equal(4, transport.RequestCount);
    }
    [Fact]
    public async Task Http_provider_remains_explicitly_unavailable_without_download()
    {
        var transport = new FixtureResourceTransport();
        transport.Add(new("https://subscription.test/list"), Encoding.UTF8.GetBytes("proxy-providers: {x: {type: http, url: http://provider.test/list}}\nproxy-groups: [{name: x, type: select, use: [x]}]"));
        var resources = new ProductionResources(transport, new InMemoryResourceCache(), _ => ValueTask.FromResult(Binding));
        var result = await resources.FetchSubscriptionAsync(new() { Id = "s", Url = "https://subscription.test/list" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.RequestCount); Assert.Contains(result.Providers[0].Diagnostics, diagnostic => diagnostic.Code == "provider.https-required");
        Assert.Contains(result.Groups[0].Diagnostics, diagnostic => diagnostic.Code == "provider.https-required");
    }
    [Fact]
    public async Task Meta_catalog_pins_commit_checks_git_blob_and_compiles_real_semantics()
    {
        var (resources, transport) = RuleFixture();
        var catalog = await resources.LoadRuleCatalogAsync(cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(3, catalog.Entries.Count);
        var result = await resources.PrepareRulesAsync(["geosite/google.yaml"], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Sources.Count); Assert.Single(result.Bindings);
        Assert.Contains(result.Sources["geosite/google.yaml"].Domains, domain => domain.Kind == DomainMatchKind.Keyword);
        Assert.Contains(result.Sources["geosite/google.yaml"].Domains, domain => domain.Kind == DomainMatchKind.Regex);
        Assert.True(GeoDatInspector.HasTag(result.Assets["geosite.dat"], "cn", RuleDataKind.Domain));
        Assert.True(GeoDatInspector.HasTag(result.Assets["geoip.dat"], "cn", RuleDataKind.Ip));
        Assert.True(GeoDatInspector.HasTag(result.Assets["geosite.dat"], result.Bindings[0].Tag, RuleDataKind.Domain));
        Assert.All(transport.UsedInterfaces, item => Assert.Equal(Binding, item));
        Assert.Equal(6, transport.RequestCount); await resources.PrepareRulesAsync(["geosite/google.yaml"], cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(6, transport.RequestCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => resources.PrepareRulesAsync(["geosite/missing.yaml"], cancellationToken: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Meta_rejects_stale_index_bytes_and_truncated_catalog()
    {
        var (resources, transport) = RuleFixture(corrupt: true);
        await Assert.ThrowsAsync<ResourcePreparationException>(() => resources.PrepareRulesAsync([], cancellationToken: TestContext.Current.CancellationToken));
        var truncated = Encoding.UTF8.GetBytes("{\"truncated\":true,\"tree\":[]}");
        Assert.Throws<InvalidDataException>(() => ProductionResources.ParseTree(truncated, "geosite", new string('a', 40)));
        Assert.False(ProductionResources.VerifyGitBlob([1], new string('a', 40)));
    }
    [Fact]
    public async Task Core_is_digest_verified_cache_first_safe_and_never_executed()
    {
        byte[] archive = Zip("xray", MachO()); string archiveHash = Hash(archive); byte[] checksum = Encoding.ASCII.GetBytes("SHA2-256= " + archiveHash + "\n");
        var spec = new XrayCoreReleaseSpecification("26.10.29", new("https://release.test/core.zip"), new("https://release.test/core.zip.dgst"), archiveHash, Hash(checksum));
        var transport = new FixtureResourceTransport(); transport.Add(spec.ArchiveUrl, archive); transport.Add(spec.ChecksumUrl, checksum);
        string root = TempRoot();
        try
        {
            var manager = new XrayCoreReleaseManager(transport, new InMemoryResourceCache(), root, _ => ValueTask.FromResult(Binding), spec);
            var result = await manager.PrepareAsync(TestContext.Current.CancellationToken); Assert.Equal(MachO(), File.ReadAllBytes(result.ExecutablePath)); Assert.Equal(Hash(MachO()), result.Sha256);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(result.ExecutablePath));
            File.WriteAllBytes(result.ExecutablePath, [1]); Assert.Equal(result, await manager.PrepareAsync(TestContext.Current.CancellationToken)); Assert.Equal(MachO(), File.ReadAllBytes(result.ExecutablePath));
            Assert.Equal(2, transport.RequestCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Core_archive_rejects_escape_symlink_wrong_architecture_and_checksum_ambiguity()
    {
        Assert.Throws<InvalidDataException>(() => XrayCoreReleaseManager.ExtractExecutable(Zip("../xray", MachO())));
        Assert.Throws<InvalidDataException>(() => XrayCoreReleaseManager.ExtractExecutable(Zip("xray", new byte[32])));
        Assert.Throws<InvalidDataException>(() => XrayCoreReleaseManager.ExtractExecutable(Zip("xray", MachO(), symlink: true)));
        Assert.False(XrayCoreReleaseManager.VerifyChecksum(Encoding.ASCII.GetBytes("SHA2-256= " + new string('a', 64) + "\nSHA2-256= " + new string('a', 64)), new string('a', 64)));
        Assert.Equal("2d4bfa91aae5f90dee691cb5eb794316510c7f6006973eb46e60d7978bb789aa", XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256);
    }
    private static (ProductionResources Resources, FixtureResourceTransport Transport) RuleFixture(bool corrupt = false)
    {
        string commit = new('a', 40); var transport = new FixtureResourceTransport();
        transport.Add(ProductionResources.MetaCommitUrl, Encoding.UTF8.GetBytes("{\"sha\":\"" + commit + "\"}"));
        byte[] cn = Encoding.UTF8.GetBytes("payload: [DOMAIN-SUFFIX,cn.test]".Replace("[DOMAIN-SUFFIX,cn.test]", "['DOMAIN-SUFFIX,cn.test']"));
        byte[] google = Encoding.UTF8.GetBytes("payload: ['DOMAIN-SUFFIX,google.test', 'DOMAIN-KEYWORD,google', 'DOMAIN-REGEX,^api[0-9]+\\.test$']");
        byte[] ip = Encoding.UTF8.GetBytes("payload: ['192.0.2.0/24']");
        string Entry(string path, byte[] body) => "{\"path\":\"" + path + "\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"" + GitHash(body) + "\",\"size\":" + body.Length + "}";
        transport.Add(new($"https://api.github.com/repos/MetaCubeX/meta-rules-dat/git/trees/{commit}:geo/geosite?recursive=1"), Encoding.UTF8.GetBytes("{\"truncated\":false,\"tree\":[" + Entry("classical/cn.yaml", cn) + "," + Entry("classical/google.yaml", google) + "]}"));
        transport.Add(new($"https://api.github.com/repos/MetaCubeX/meta-rules-dat/git/trees/{commit}:geo/geoip?recursive=1"), Encoding.UTF8.GetBytes("{\"truncated\":false,\"tree\":[" + Entry("cn.yaml", ip) + "]}"));
        transport.Add(new($"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/{commit}/geo/geosite/classical/cn.yaml"), corrupt ? [1] : cn);
        transport.Add(new($"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/{commit}/geo/geosite/classical/google.yaml"), google);
        transport.Add(new($"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/{commit}/geo/geoip/cn.yaml"), ip);
        return (new(transport, new InMemoryResourceCache(), _ => ValueTask.FromResult(Binding)), transport);
    }
    private static string GitHash(byte[] body) { using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); hash.AppendData(Encoding.ASCII.GetBytes("blob " + body.Length + "\0")); hash.AppendData(body); return Convert.ToHexStringLower(hash.GetHashAndReset()); }
    private static string Hash(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));
    private static string TempRoot() => Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "egress-production-resource-test-" + Guid.NewGuid().ToString("N"));
    private static byte[] MachO() { byte[] bytes = new byte[32]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x0100000c); return bytes; }
    private static byte[] Zip(string name, byte[] bytes, bool symlink = false)
    {
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) { var entry = zip.CreateEntry(name); if (symlink) entry.ExternalAttributes = unchecked((int)0xa1ff0000); using var stream = entry.Open(); stream.Write(bytes); }
        return output.ToArray();
    }
    private sealed class FailingBinder : IPhysicalSocketBinder
    { public int Calls; public InterfaceBinding? Binding; public void Bind(Socket socket, InterfaceBinding binding) { Calls++; Binding = binding; throw new IOException("injected"); } }
    private sealed class FixedResolver : IInterfaceDnsResolver
    { public int Calls; public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, InterfaceBinding binding, CancellationToken cancellationToken) { Calls++; return ValueTask.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback, IPAddress.IPv6Loopback]); } }
    private sealed class HttpFixture : HttpMessageHandler
    {
        public int Calls; public bool Downgrade; public bool Oversize; public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; if (Fail) throw new HttpRequestException("synthetic-secret");
            if (Calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(Downgrade ? "http://other.test/body" : "https://other.test/body"); return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Oversize ? [1, 2, 3, 4] : [1, 2, 3]) });
        }
    }
    private sealed class DnsFixture : IBoundDnsExchange
    {
        public bool TruncateUdp; public bool WrongQuestion; public bool CompressionLoop;
        public List<(InterfaceBinding Binding, bool Tcp)> Calls { get; } = [];
        public ValueTask<byte[]> ExchangeAsync(byte[] query, IPAddress server, InterfaceBinding binding, bool tcp, CancellationToken token)
        {
            Calls.Add((binding, tcp)); byte[] header = query.ToArray(); header[2] = (byte)(TruncateUdp && !tcp ? 0x83 : 0x81); header[3] = 0x80; header[7] = 1;
            if (WrongQuestion) header[13] = (byte)'x';
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4)); byte[] ip = IPAddress.Parse(type == 1 ? "192.0.2.8" : "2001:db8::8").GetAddressBytes();
            using var output = new MemoryStream(); output.Write(header);
            int offset = header.Length; output.WriteByte(0xc0); output.WriteByte((byte)(CompressionLoop ? offset : 12)); output.WriteByte(0); output.WriteByte((byte)type); output.WriteByte(0); output.WriteByte(1); output.Write(new byte[4]); output.WriteByte(0); output.WriteByte((byte)ip.Length); output.Write(ip);
            return ValueTask.FromResult(output.ToArray());
        }
    }
}
