using System.Text.Json;
using Google.Protobuf;
using EgressController.Policy;
using YamlDotNet.RepresentationModel;

namespace EgressController.Policy.Tests;

public sealed class PolicyTests
{
    private const string Subscription = """
        mixed-port: 7890
        external-controller: 127.0.0.1:9090
        dns: {enable: true, enhanced-mode: fake-ip}
        tun: {enable: true}
        defaults: &transport {tls: true, network: tcp, client-fingerprint: chrome}
        proxies:
          - {<<: *transport, name: vless-pq, type: vless, server: pq.example.test, port: 443, uuid: 00000000-0000-4000-8000-000000000001, encryption: mlkem768x25519plus.native.1rtt.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, reality-opts: {public-key: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, short-id: aabb}, servername: www.example.test}
          - {name: chained-socks, type: socks5, server: socks.example.test, port: 1080, username: synthetic-user, password: synthetic-password, dialer-proxy: vless-pq, udp: true}
          - {name: unsupported, type: hysteria2, server: unsupported.example.test, port: 443, password: synthetic-password}
        proxy-groups:
          - {name: manual, type: select, proxies: [DIRECT, chained-socks, vless-pq]}
          - {name: latency, type: url-test, proxies: [chained-socks, vless-pq], url: https://example.test/204, interval: 300, tolerance: 25}
          - {name: ordered, type: fallback, proxies: [chained-socks, vless-pq], url: https://example.test/204}
        rules: [UDP,DIRECT]
        """;
    internal static MacPolicyDocument Document()
    {
        var imported = ClashSubscriptionImporter.Import(Subscription, "source");
        var node = imported.Nodes.First(x => x.Name == "vless-pq");
        return new()
        {
            Interfaces = new() { HomeId = "home-stable", NonHomeId = "mobile-stable" },
            Defaults = new() { Global = ExitChoice.Proxy(node.Id) },
            Nodes = imported.Nodes.ToList(),
            Groups = imported.Groups.ToList(),
            Applications = [new() { Id = "editor", Name = "Editor", BundlePath = "/Applications/Editor.app", PrimaryExecutable = "/Applications/Editor.app/Contents/MacOS/Editor", ExecutablePaths = ["/Applications/Editor.app/Contents/MacOS/Editor", "/Applications/Editor.app/Contents/Frameworks/Helper.app/Contents/MacOS/Helper"], Via = ExitChoice.Home }],
            RuleSets = [new() { Id = "service", Name = "Service", Domains = [new(DomainMatchKind.Suffix, "example.test")], IpCidrs = ["203.0.113.0/24"], Via = ExitChoice.Proxy(node.Id) }]
        };
    }
    internal static XrayCompileOptions Options() => new() { Interfaces = [new("home-stable", "en0"), new("mobile-stable", "en7")] };

    [Fact]
    public void ImportPreservesAnchorsPqAndDialerAndIgnoresClashGlobalSettings()
    {
        var imported = ClashSubscriptionImporter.Import(Subscription, "source");
        Assert.True(imported.IsSuccess); Assert.Equal(3, imported.Nodes.Count); Assert.Equal(3, imported.Groups.Count);
        var pq = imported.Nodes.First(x => x.Name == "vless-pq");
        Assert.True(pq.Tls); Assert.StartsWith("mlkem768x25519plus.", pq.Encryption); Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", pq.RealityPublicKey);
        Assert.Equal(pq.Id, imported.Nodes.First(x => x.Name == "chained-socks").DialerProxyId);
        Assert.False(imported.Nodes.First(x => x.Name == "unsupported").IsSupported);
        Assert.All(imported.Groups, x => Assert.Null(x.SelectedMemberId));
        foreach (var key in new[] { "dns", "tun", "rules", "mixed-port", "external-controller" }) Assert.Contains(imported.Diagnostics, x => x.Code == "subscription.ignored." + key);
    }
    [Fact]
    public void DslRoundTripRetainsFixedInventoryAndAllCredentialAndGroupFields()
    {
        var doc = Document(); doc.Groups[0].SelectedMemberId = doc.Nodes[0].Id;
        var yaml = PolicyYaml.Write(doc); var read = PolicyYaml.Read(yaml);
        Assert.True(read.IsSuccess, string.Join(',', read.Diagnostics.Select(x => x.Code)));
        Assert.Equal(yaml, PolicyYaml.Write(read.Document!));
        Assert.Equal(doc.Applications[0].ExecutablePaths, read.Document!.Applications[0].ExecutablePaths);
        Assert.Equal(doc.Nodes[0].Encryption, read.Document.Nodes[0].Encryption);
        Assert.False(read.Document.Nodes[2].IsSupported);
    }
    [Fact]
    public void DnsTunAndProviderDefinitionsRoundTripAndDriveGeneratedConfiguration()
    {
        var doc = Document(); doc.Dns.UpstreamIp = "9.9.9.9"; doc.Tun.Name = "utun42"; doc.Tun.Ipv4Cidr = "10.123.0.1/30"; doc.Tun.Mtu = 1400;
        doc.Providers.Add(new() { Id = "provider-test", Name = "Fixture provider", SourceId = "source", Url = "https://provider.example.test/nodes.yaml", Diagnostics = [new("provider.pending", "等待显式获取。")] });
        var yaml = PolicyYaml.Write(doc); var parsed = PolicyYaml.Read(yaml); Assert.True(parsed.IsSuccess);
        Assert.Equal(yaml, PolicyYaml.Write(parsed.Document!)); Assert.Equal("9.9.9.9", parsed.Document!.Dns.UpstreamIp); Assert.Equal("utun42", parsed.Document.Tun.Name); Assert.Equal(1400, parsed.Document.Tun.Mtu);
        var provider = Assert.Single(parsed.Document.Providers); Assert.Equal("source", provider.SourceId); Assert.Equal("https://provider.example.test/nodes.yaml", provider.Url); Assert.Contains(provider.Diagnostics, x => x.Code == "provider.pending");
        var generated = XrayConfigCompiler.Compile(parsed.Document, Options()); Assert.True(generated.IsSuccess);
        using var json = JsonDocument.Parse(generated.Json!); Assert.Equal("9.9.9.9", json.RootElement.GetProperty("dns").GetProperty("servers")[0].GetString());
        var tun = json.RootElement.GetProperty("inbounds")[0].GetProperty("settings"); Assert.Equal("utun42", tun.GetProperty("name").GetString()); Assert.Equal(1400, tun.GetProperty("mtu").GetInt32()); Assert.Equal("10.123.0.1/30", tun.GetProperty("gateway")[0].GetString());
    }
    [Fact]
    public void LegacyDslWithoutDnsAndTunUsesExplicitTypedDefaultsOnNextSave()
    {
        var lines = PolicyYaml.Write(Document()).Split('\n'); var filtered = new List<string>(); var skipping = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("dns:", StringComparison.Ordinal) || line.StartsWith("tun:", StringComparison.Ordinal)) { skipping = true; continue; }
            if (skipping && line.Length > 0 && !char.IsWhiteSpace(line[0])) skipping = false;
            if (!skipping) filtered.Add(line);
        }
        var parsed = PolicyYaml.Read(string.Join('\n', filtered)); Assert.True(parsed.IsSuccess); Assert.Equal("1.1.1.1", parsed.Document!.Dns.UpstreamIp); Assert.Equal("utun10", parsed.Document.Tun.Name); Assert.Contains("dns:", PolicyYaml.Write(parsed.Document));
    }
    [Theory]
    [InlineData("dns: {upstream_ip: https://dns.example.test/query}")]
    [InlineData("dns: {upstream_ip: 127.0.0.1}")]
    [InlineData("dns: {upstream_ip: 0.0.0.0}")]
    [InlineData("dns: {upstream_ip: 1.1.1.1, hidden: value}")]
    [InlineData("tun: {name: en0, ipv4_cidr: 198.18.0.1/30, mtu: 1500}")]
    [InlineData("tun: {name: utun10, ipv4_cidr: '2001:db8::1/64', mtu: 1500}")]
    [InlineData("tun: {name: utun10, ipv4_cidr: 198.18.0.1/30, mtu: 1279}")]
    [InlineData("tun: {name: utun10, ipv4_cidr: 198.18.0.1/30, mtu: invalid}")]
    [InlineData("providers: [{id: provider, name: fixture, source_id: source, url: https://provider.example.test, arbitrary: field}]")]
    public void DnsTunAndProvidersHaveStrictSchemaAndSemanticValidation(string fragment)
    {
        var baseline = new YamlStream(); baseline.Load(new StringReader(PolicyYaml.Write(Document())));
        var replacement = new YamlStream(); replacement.Load(new StringReader(fragment));
        var root = (YamlMappingNode)baseline.Documents[0].RootNode;
        foreach (var pair in ((YamlMappingNode)replacement.Documents[0].RootNode).Children) root.Children[pair.Key] = pair.Value;
        var output = new StringWriter(); baseline.Save(output, false);
        var parsed = PolicyYaml.Read(output.ToString()); Assert.False(parsed.IsSuccess);
        Assert.Contains(parsed.Diagnostics, x => x.Code.StartsWith("dns.", StringComparison.Ordinal) || x.Code.StartsWith("tun.", StringComparison.Ordinal) || x.Code.StartsWith("dsl.", StringComparison.Ordinal));
    }
    [Fact]
    public void CompilerEnforcesThreeExitsAndHasNoProxyOrControlListeners()
    {
        var result = XrayConfigCompiler.Compile(Document(), Options()); Assert.True(result.IsSuccess, string.Join(',', result.Diagnostics.Select(x => x.Code)));
        using var json = JsonDocument.Parse(result.Json!); var root = json.RootElement;
        Assert.False(root.TryGetProperty("api", out _)); Assert.False(root.TryGetProperty("fakedns", out _));
        var inbound = Assert.Single(root.GetProperty("inbounds").EnumerateArray()); Assert.Equal("tun", inbound.GetProperty("protocol").GetString()); Assert.Equal("", inbound.GetProperty("settings").GetProperty("autoOutboundsInterface").GetString()); Assert.False(inbound.TryGetProperty("listen", out _)); Assert.False(inbound.TryGetProperty("port", out _));
        Assert.Equal("IPOnDemand", root.GetProperty("routing").GetProperty("domainStrategy").GetString());
        var rules = root.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal("dns-upstream", rules[0].GetProperty("ruleTag").GetString());
        Assert.Equal("system-dns", rules[1].GetProperty("ruleTag").GetString());
        var app = rules.First(x => x.GetProperty("ruleTag").GetString() == "app-editor"); Assert.Equal("home", app.GetProperty("outboundTag").GetString()); Assert.Equal(2, app.GetProperty("process").GetArrayLength());
        Assert.All(app.GetProperty("process").EnumerateArray(), x => Assert.False(x.GetString()!.EndsWith('/')));
        Assert.Equal("global", rules[^1].GetProperty("ruleTag").GetString()); Assert.StartsWith("node-", rules[^1].GetProperty("outboundTag").GetString());
        Assert.Equal("blocked", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        foreach (var outbound in root.GetProperty("outbounds").EnumerateArray().Where(x => x.GetProperty("protocol").GetString() != "blackhole"))
            Assert.Equal(outbound.GetProperty("tag").GetString() == "home" ? "en0" : "en7", outbound.GetProperty("streamSettings").GetProperty("sockopt").GetProperty("interface").GetString());
        Assert.DoesNotContain("unsupported.example.test", result.Json!);
    }
    [Fact]
    public void RuleDomainAndIpConditionsAreOrAndOrderedBeforeBuiltInChina()
    {
        using var json = JsonDocument.Parse(XrayConfigCompiler.Compile(Document(), Options()).Json!);
        var rules = json.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        var domains = Array.FindIndex(rules, x => x.GetProperty("ruleTag").GetString() == "domains-service");
        var ips = Array.FindIndex(rules, x => x.GetProperty("ruleTag").GetString() == "ips-service");
        var china = Array.FindIndex(rules, x => x.GetProperty("ruleTag").GetString() == "china-domains");
        Assert.True(domains < ips && ips < china); Assert.False(rules[domains].TryGetProperty("ip", out _)); Assert.False(rules[ips].TryGetProperty("domain", out _));
    }
    [Fact]
    public void LocalNetworkRulesDoNotDependOnAnImplicitPrivateTagInTheGeneratedDat()
    {
        using var json = JsonDocument.Parse(XrayConfigCompiler.Compile(Document(), Options()).Json!);
        var rules = json.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().ToArray();
        var local = rules.Single(rule => rule.GetProperty("ruleTag").GetString() == "local-network");
        Assert.Equal(XrayConfigCompiler.LocalNetworkCidrs, local.GetProperty("ip").EnumerateArray().Select(x => x.GetString()!).ToArray());
        foreach (var rule in rules)
            foreach (var field in new[] { "domain", "ip" })
                if (rule.TryGetProperty(field, out var values))
                    foreach (var value in values.EnumerateArray())
                    {
                        Assert.False(value.GetString()!.StartsWith("geoip:", StringComparison.Ordinal));
                        Assert.False(value.GetString()!.StartsWith("geosite:", StringComparison.Ordinal));
                    }
    }
    [Fact]
    public void DatTagIdentitiesRespectTheCoreUppercaseAndLengthNamespace()
    {
        var doc = Document();
        doc.RuleSets.Add(new() { Id = "SERVICE", Name = "Conflicting tag", Domains = [new(DomainMatchKind.Full, "other.example.test")] });
        Assert.Contains(PolicyValidator.Validate(doc), x => x.Code == "geodata.tag"); Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
        doc.RuleSets.RemoveAt(doc.RuleSets.Count - 1); doc.RuleSets[0].Id = new string('a', 128);
        Assert.Contains(PolicyValidator.Validate(doc), x => x.Code == "geodata.tag");
    }
    [Theory]
    [InlineData("/Applications/Editor.app/")]
    [InlineData("/Applications/Editor.app/../other")]
    [InlineData("/Applications//Editor.app/Contents/MacOS/Editor")]
    [InlineData("Editor")]
    public void RuntimeDirectoryWildcardAndNoncanonicalPathsAreRejected(string path)
    {
        var doc = Document(); doc.Applications[0].ExecutablePaths = [path]; doc.Applications[0].PrimaryExecutable = null;
        Assert.Contains(PolicyValidator.Validate(doc), x => x.Code == "application.path"); Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
    }
    [Fact]
    public void MissingSavedPathsAreKeptAndRouteChangesNeverRebuildInventory()
    {
        var doc = Document(); doc.Applications[0].ExecutablePaths.Add("/Applications/Editor-v1.app/Contents/MacOS/Editor"); var before = doc.Applications[0].ExecutablePaths.ToArray();
        doc.Applications[0].Via = ExitChoice.NonHome;
        Assert.True(XrayConfigCompiler.Compile(doc, Options()).IsSuccess); Assert.Equal(before, doc.Applications[0].ExecutablePaths);
    }
    [Fact]
    public void MissingPhysicalInterfaceCannotFallBack()
    {
        var opts = Options(); opts.Interfaces.RemoveAt(1);
        var result = XrayConfigCompiler.Compile(Document(), opts); Assert.False(result.IsSuccess); Assert.Null(result.Json); Assert.Contains(result.Diagnostics, x => x.Code == "interfaces.unavailable");
    }
    [Fact]
    public void ManualGroupsRequireSelectionAndDirectCanNeverBeGlobalProxy()
    {
        var doc = Document(); var group = doc.Groups.First(x => x.Kind == GroupKind.Select); doc.Defaults.Global = ExitChoice.Proxy(group.Id);
        Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
        group.SelectedMemberId = "builtin:direct"; Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
        group.SelectedMemberId = doc.Nodes.First(x => x.Name == "chained-socks").Id;
        var compiled = XrayConfigCompiler.Compile(doc, Options()); Assert.True(compiled.IsSuccess, string.Join(',', compiled.Diagnostics.Select(x => x.Code)));
        using var json = JsonDocument.Parse(compiled.Json!); var socks = json.RootElement.GetProperty("outbounds").EnumerateArray().First(x => x.GetProperty("protocol").GetString() == "socks");
        Assert.Equal(doc.Nodes[0].Id, socks.GetProperty("streamSettings").GetProperty("sockopt").GetProperty("dialerProxy").GetString());
    }
    [Fact]
    public void UrlTestToleranceFallbackOrderAndStaleProbesAreExplicit()
    {
        var doc = Document(); var now = DateTimeOffset.Parse("2026-10-30T12:00:00Z"); var a = doc.Nodes[0].Id; var b = doc.Nodes[1].Id;
        var probes = new[] { new GroupProbe(a, true, 20, now, "https://example.test/204"), new GroupProbe(b, true, 30, now, "https://example.test/204") };
        var latency = doc.Groups.First(x => x.Kind == GroupKind.UrlTest); latency.SelectedMemberId = b;
        Assert.Equal(b, GroupSelector.Resolve(latency.Id, doc, probes, now).NodeId);
        latency.ToleranceMilliseconds = 5; Assert.Equal(a, GroupSelector.Resolve(latency.Id, doc, probes, now).NodeId);
        var fallback = doc.Groups.First(x => x.Kind == GroupKind.Fallback); Assert.Equal(b, GroupSelector.Resolve(fallback.Id, doc, probes, now).NodeId);
        Assert.False(GroupSelector.Resolve(fallback.Id, doc, probes, now.AddHours(1)).IsSuccess);
        Assert.False(GroupSelector.Resolve(fallback.Id, doc, probes, now.AddSeconds(-1)).IsSuccess);
        Assert.False(GroupSelector.Resolve(fallback.Id, doc, [new(a, true, 1, now, "https://different.example.test/204")], now).IsSuccess);
    }
    [Fact]
    public void ReferencedUnsupportedNodeAndDialerCycleFailClosed()
    {
        var doc = Document(); doc.Defaults.Global = ExitChoice.Proxy(doc.Nodes[2].Id); Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
        doc.Defaults.Global = ExitChoice.Proxy(doc.Nodes[0].Id); doc.Nodes[0].DialerProxyId = doc.Nodes[1].Id;
        Assert.Contains(PolicyValidator.Validate(doc), x => x.Code == "proxy.cycle"); Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
    }
    [Fact]
    public void ProviderReferencesStayPendingUntilSuppliedWithoutNetworkAccess()
    {
        const string yaml = "proxy-providers:\n  remote: {type: http, url: https://example.test/provider.yaml}\nproxy-groups:\n - {name: group, type: select, use: [remote]}";
        var pending = ClashSubscriptionImporter.Import(yaml, "source"); Assert.Single(pending.Providers); Assert.Contains(pending.Groups[0].Diagnostics, x => x.Code == "provider.pending");
        var loaded = ClashSubscriptionImporter.Import(yaml, "source", new Dictionary<string, string> { ["remote"] = "proxies: [{name: proxy, type: socks5, server: example.test, port: 1080}]" });
        Assert.Single(loaded.Nodes); Assert.Equal("source", loaded.Providers[0].SourceId); Assert.Equal(loaded.Nodes[0].Id, Assert.Single(loaded.Groups[0].MemberIds));
    }
    [Fact]
    public void MalformedYamlErrorsNeverEchoCredentialExcerpts()
    {
        const string secret = "sensitive-password-do-not-log";
        var imported = ClashSubscriptionImporter.Import("proxies: [ {password: " + secret, "source");
        Assert.False(imported.IsSuccess); Assert.DoesNotContain(secret, string.Join(' ', imported.Diagnostics.Select(x => x.Message)));
        Assert.False(PolicyYaml.Read("version: 1\nnodes: [ {password: " + secret).IsSuccess);
    }
    [Fact]
    public void RecursiveAliasesAreRejected()
    {
        Assert.False(ClashSubscriptionImporter.Import("loop: &loop {self: *loop}\nproxies: []", "source").IsSuccess);
    }
    [Theory]
    [InlineData("applications: {}")]
    [InlineData("applications: [{id: editor, enabled: perhaps}]")]
    [InlineData("interfaces: {home: x, nonhome: y, silent_typo: z}")]
    [InlineData("rule_sets: [{id: set, domains: [bad-shape]}]")]
    public void DslRejectsUnknownFieldsAndWrongTypes(string fragment)
    {
        var result = PolicyYaml.Read("version: 1\n" + fragment);
        Assert.False(result.IsSuccess); Assert.Contains(result.Diagnostics, x => x.Code.StartsWith("dsl.", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("https://example.test")]
    [InlineData("bad/path")]
    [InlineData("*.example.test")]
    [InlineData(" example.test")]
    [InlineData("example.test.")]
    [InlineData("bad..example.test")]
    [InlineData("-bad.example.test")]
    [InlineData("192.0.2.1")]
    public void FullAndSuffixRequireHostnamesInPolicyDslAndDirectDatCompile(string value)
    {
        foreach (var kind in new[] { DomainMatchKind.Full, DomainMatchKind.Suffix })
        {
            var doc = Document(); doc.RuleSets[0].Domains = [new(kind, value)];
            Assert.Contains(PolicyValidator.Validate(doc), x => x.Code == "rule.domain");
            Assert.False(PolicyYaml.Read(PolicyYaml.Write(doc)).IsSuccess);
            Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
            Assert.Throws<InvalidOperationException>(() => GeoRuleCompiler.Compile([new() { Tag = "test", Domains = [new(kind, value)] }]));
        }
    }
    [Fact]
    public void FullSuffixAndIdnCompileToTheirOwnCanonicalSemantics()
    {
        var doc = Document(); doc.RuleSets[0].Domains = [new(DomainMatchKind.Full, "EXACT.Example.Test"), new(DomainMatchKind.Suffix, "例子.测试")];
        var result = XrayConfigCompiler.Compile(doc, Options()); Assert.True(result.IsSuccess);
        Assert.Contains("full:exact.example.test", result.Json!); Assert.Contains("domain:xn--fsqu00a.xn--0zwm56d", result.Json!);
        var bundle = GeoRuleCompiler.Compile([new() { Tag = "test", Domains = doc.RuleSets[0].Domains }]); Assert.True(GeoDatInspector.HasTag(bundle.GeoSite, "test", RuleDataKind.Domain));
    }
    [Theory]
    [InlineData("blocked")]
    [InlineData("home")]
    [InlineData("nonhome")]
    [InlineData("dns")]
    public void ReservedOutboundTagsCannotBecomeProxyIdentities(string id)
    {
        var doc = Document(); doc.Nodes[0].Id = id; doc.Defaults.Global = ExitChoice.Proxy(id); doc.RuleSets.Clear();
        Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
    }
    [Theory]
    [InlineData("vmess", "uuid: 00000000-0000-4000-8000-000000000001, cipher: auto, tls: true", "vmess")]
    [InlineData("ss", "cipher: aes-128-gcm, password: synthetic-password", "shadowsocks")]
    [InlineData("trojan", "password: synthetic-password", "trojan")]
    [InlineData("socks5", "username: synthetic-user, password: synthetic-password", "socks")]
    [InlineData("http", "username: synthetic-user, password: synthetic-password, tls: true", "http")]
    public void EquivalentNativeProtocolsCompileWithTheirRequiredFields(string type, string fields, string protocol)
    {
        var imported = ClashSubscriptionImporter.Import("proxies: [{name: synthetic, type: " + type + ", server: proxy.example.test, port: 443, " + fields + "}]", "synthetic");
        var node = Assert.Single(imported.Nodes); Assert.True(node.IsSupported);
        var doc = Document(); doc.Nodes = [node]; doc.Groups.Clear(); doc.RuleSets.Clear(); doc.Defaults.Global = ExitChoice.Proxy(node.Id);
        var compiled = XrayConfigCompiler.Compile(doc, Options()); Assert.True(compiled.IsSuccess, string.Join(',', compiled.Diagnostics.Select(x => x.Code)));
        using var json = JsonDocument.Parse(compiled.Json!); Assert.Contains(json.RootElement.GetProperty("outbounds").EnumerateArray(), x => x.GetProperty("protocol").GetString() == protocol);
    }
    [Fact]
    public void SubscriptionReorderAndRenameKeepEndpointIds()
    {
        const string a = "proxies: [{name: original, type: socks5, server: proxy.example.test, port: 1080}, {name: another, type: socks5, server: other.example.test, port: 1080}]";
        const string b = "proxies: [{name: another, type: socks5, server: other.example.test, port: 1080}, {name: renamed, type: socks5, server: proxy.example.test, port: 1080}]";
        var before = ClashSubscriptionImporter.Import(a, "source"); var after = ClashSubscriptionImporter.Import(b, "source");
        Assert.Equal(before.Nodes[0].Id, after.Nodes[1].Id); Assert.Equal(before.Nodes[1].Id, after.Nodes[0].Id);
    }
    [Theory]
    [InlineData("skip-cert-verify: true", "node.insecure-unsupported")]
    [InlineData("encryption: mlkem768x25519plus.wrong.1rtt.short", "node.encryption")]
    [InlineData("smux: {enabled: true}", "node.unsupported-option")]
    [InlineData("unrecognized-behavior: true", "node.unknown-option")]
    public void UnsupportedSecurityAndMuxOptionsCannotReachAppliedConfiguration(string extra, string code)
    {
        var imported = ClashSubscriptionImporter.Import("proxies: [{name: proxy, type: vless, server: proxy.example.test, port: 443, uuid: 00000000-0000-4000-8000-000000000001, tls: true, " + extra + "}]", "source");
        var node = Assert.Single(imported.Nodes); Assert.False(node.IsSupported); Assert.Contains(node.Diagnostics, x => x.Code == code);
        var doc = Document(); doc.Nodes = [node]; doc.RuleSets.Clear(); doc.Groups.Clear(); doc.Defaults.Global = ExitChoice.Proxy(node.Id);
        Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
    }
    [Fact]
    public void ExternalRuleRequiresPreparedDatBinding()
    {
        var doc = Document(); doc.RuleSets[0].Sources = ["geosite/service.yaml"];
        Assert.False(XrayConfigCompiler.Compile(doc, Options()).IsSuccess);
        var opts = Options(); opts.PreparedRuleSources = [new("geosite/service.yaml", "service", "custom-geosite.dat")]; Assert.True(XrayConfigCompiler.Compile(doc, opts).IsSuccess);
    }
    [Fact]
    public void PrivateAttachmentCanBeVerifiedInMemoryWithoutPersistingOrPrintingSecrets()
    {
        var path = Environment.GetEnvironmentVariable("POLICY_PRIVATE_FIXTURE"); if (path is null) return;
        var imported = ClashSubscriptionImporter.Import(File.ReadAllText(path), "private-source");
        Assert.True(imported.IsSuccess); Assert.True(imported.Nodes.Count > 20); Assert.True(imported.Groups.Count > 5);
        var supported = imported.Nodes.Count(x => x.IsSupported);
        Assert.True(supported >= 4, "Expected the synthetic-equivalent VLESS/REALITY and SOCKS proxy-chain shapes to remain supported.");
        foreach (var node in imported.Nodes.Where(x => x.IsSupported))
        {
            var doc = Document(); doc.Nodes = imported.Nodes.ToList(); doc.Groups = imported.Groups.ToList(); doc.Defaults.Global = ExitChoice.Proxy(node.Id); doc.RuleSets.Clear();
            var compiled = XrayConfigCompiler.Compile(doc, Options());
            Assert.True(compiled.IsSuccess, "Private compile diagnostic codes: " + string.Join(',', compiled.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.Code).Distinct()));
        }
        Console.WriteLine($"Private fixture: {imported.Nodes.Count} nodes, {imported.Groups.Count} groups, {supported} supported nodes; diagnostic codes: {string.Join(',', imported.Diagnostics.Select(x => x.Code).Distinct())}");
    }
}
