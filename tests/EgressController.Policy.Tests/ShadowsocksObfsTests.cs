using System.Text;
using System.Text.Json;
using EgressController.Policy;

namespace EgressController.Policy.Tests;

public sealed class ShadowsocksObfsTests
{
    private static string Subscription(string extra = "", string mode = "http", string host = "camouflage.example.test", string tfo = "true") => $$$"""
        defaults: &plugin {mode: {{{mode}}}, host: {{{host}}}}
        proxies:
          - {name: synthetic-obfs, type: ss, server: ss.example.test, port: 8443, cipher: aes-128-gcm, password: synthetic-password, udp: true, tfo: {{{tfo}}}, plugin: obfs, plugin-opts: {<<: *plugin{{{extra}}}}}
        proxy-groups:
          - {name: select, type: select, proxies: [synthetic-obfs]}
        """;
    private static MacPolicyDocument Policy(ClashImportResult imported)
    {
        var doc = PolicyTests.Document(); doc.RuleSets.Clear();
        doc.Nodes = imported.Nodes.ToList(); doc.Groups = imported.Groups.ToList(); PolicyTests.SelectGlobalNode(doc, doc.Nodes[0].Id);
        return doc;
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObfsHttpAndExplicitTfoRoundTripIntoNativeCoreSettings(bool tfo)
    {
        var imported = ClashSubscriptionImporter.Import(Subscription(tfo: tfo ? "true" : "false"), "fixture-source");
        var node = Assert.Single(imported.Nodes); Assert.True(node.IsSupported); Assert.Equal("obfs", node.Plugin);
        Assert.Equal("http", node.PluginMode); Assert.Equal("camouflage.example.test", node.PluginHost); Assert.Equal(tfo, node.TcpFastOpen);
        var document = Policy(imported); string yaml = PolicyYaml.Write(document); var read = PolicyYaml.Read(yaml); Assert.True(read.IsSuccess);
        var saved = Assert.Single(read.Document!.Nodes); Assert.Equal(node.Plugin, saved.Plugin); Assert.Equal(node.PluginMode, saved.PluginMode);
        Assert.Equal(node.PluginHost, saved.PluginHost); Assert.Equal(node.TcpFastOpen, saved.TcpFastOpen); Assert.Equal(node.Password, saved.Password);
        var result = XrayConfigCompiler.Compile(read.Document!, PolicyTests.Options()); Assert.True(result.IsSuccess);
        using var json = JsonDocument.Parse(result.Json!);
        var outbound = json.RootElement.GetProperty("outbounds").EnumerateArray().Single(item => item.GetProperty("tag").GetString() == node.Id);
        Assert.Equal("shadowsocks", outbound.GetProperty("protocol").GetString()); Assert.Equal("aes-128-gcm", outbound.GetProperty("settings").GetProperty("method").GetString());
        var stream = outbound.GetProperty("streamSettings"); Assert.Equal("raw", stream.GetProperty("network").GetString());
        Assert.Equal("en7", stream.GetProperty("sockopt").GetProperty("interface").GetString());
        Assert.Equal(tfo, stream.GetProperty("sockopt").GetProperty("tcpFastOpen").GetBoolean());
        Assert.False(stream.TryGetProperty("wsSettings", out _)); Assert.False(stream.TryGetProperty("security", out _));
        var header = stream.GetProperty("rawSettings").GetProperty("header"); Assert.Equal("http", header.GetProperty("type").GetString());
        Assert.Equal("camouflage.example.test:8443", header.GetProperty("request").GetProperty("headers").GetProperty("Host")[0].GetString());
        Assert.Equal(2, json.RootElement.GetProperty("inbounds").GetArrayLength()); Assert.False(json.RootElement.TryGetProperty("api", out _));
        Assert.Equal("tcp,udp", json.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray().Last().GetProperty("network").GetString());
    }
    [Fact]
    public void GeneratedNativeHeaderIsAcceptedBySimpleObfsWireRulesAndPreservesCiphertext()
    {
        var imported = ClashSubscriptionImporter.Import(Subscription(), "fixture-source");
        var result = XrayConfigCompiler.Compile(Policy(imported), PolicyTests.Options()); Assert.True(result.IsSuccess);
        using var json = JsonDocument.Parse(result.Json!);
        var request = json.RootElement.GetProperty("outbounds").EnumerateArray().Single(item => item.GetProperty("tag").GetString() == imported.Nodes[0].Id)
            .GetProperty("streamSettings").GetProperty("rawSettings").GetProperty("header").GetProperty("request");
        // Reproduce pinned HeaderWriter serialization, independently check simple-obfs server's actual admission rules.
        string prefix = request.GetProperty("method").GetString() + " " + request.GetProperty("path")[0].GetString() + " HTTP/" + request.GetProperty("version").GetString() + "\r\n";
        foreach (var property in request.GetProperty("headers").EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            prefix += property.Name + ": " + property.Value[0].GetString() + "\r\n";
        prefix += "\r\n";
        byte[] ciphertext = [0x00, 0xff, 0x31, 0x0d, 0x0a, 0x0d, 0x0a, 0x72];
        byte[] wire = Encoding.ASCII.GetBytes(prefix).Concat(ciphertext).ToArray();
        Assert.True(SimpleObfsAccepts(wire, "camouflage.example.test")); Assert.Equal(ciphertext, StripFirstHeader(wire));
        Assert.False(SimpleObfsAccepts(Encoding.ASCII.GetBytes(prefix.Replace("websocket", "other", StringComparison.Ordinal)), "camouflage.example.test"));
        Assert.False(SimpleObfsAccepts(wire, "wrong.example.test"));
        var serverResponse = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nServer: nginx\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n").Concat(ciphertext).ToArray();
        Assert.Equal(ciphertext, StripFirstHeader(serverResponse)); // Neither direction adds WebSocket frames or HTTP body encoding.
    }
    [Theory]
    [InlineData(", path: /ignored", "http", "camouflage.example.test", "true", "node.plugin-options")]
    [InlineData("", "tls", "camouflage.example.test", "true", "node.plugin")]
    [InlineData("", "http", "'https://camouflage.example.test/'", "true", "node.plugin-host")]
    [InlineData("", "http", "'camouflage.example.test:443'", "true", "node.plugin-host")]
    [InlineData("", "http", "'camouflage.example.test/path'", "true", "node.plugin-host")]
    [InlineData("", "http", "camouflage.example.test", "invalid", "node.tfo")]
    public void UnsupportedPluginOptionsCannotBecomePlainShadowsocks(string extra, string mode, string host, string tfo, string code)
    {
        var imported = ClashSubscriptionImporter.Import(Subscription(extra, mode, host, tfo), "fixture-source");
        var node = Assert.Single(imported.Nodes); Assert.False(node.IsSupported); Assert.Contains(node.Diagnostics, diagnostic => diagnostic.Code == code);
        var result = XrayConfigCompiler.Compile(Policy(imported), PolicyTests.Options()); Assert.False(result.IsSuccess); Assert.Null(result.Json);
    }
    [Theory]
    [InlineData("v2ray-plugin")]
    [InlineData("unknown-plugin")]
    public void OtherPluginsArePreservedAndExplicitlyRejected(string plugin)
    {
        var imported = ClashSubscriptionImporter.Import(Subscription().Replace("plugin: obfs", "plugin: " + plugin, StringComparison.Ordinal), "fixture-source");
        Assert.Equal(plugin, imported.Nodes[0].Plugin); Assert.False(imported.Nodes[0].IsSupported);
        var read = PolicyYaml.Read(PolicyYaml.Write(Policy(imported))); Assert.Equal(plugin, read.Document!.Nodes[0].Plugin);
        Assert.False(XrayConfigCompiler.Compile(Policy(imported), PolicyTests.Options()).IsSuccess);
    }
    [Fact]
    public void ManualDslCannotBypassPluginValidationAndUnspecifiedTfoRemainsUnspecified()
    {
        var doc = Policy(ClashSubscriptionImporter.Import(Subscription(), "fixture-source")); doc.Nodes[0].PluginHost = "camouflage.example.test\r\nInjected: value";
        Assert.False(XrayConfigCompiler.Compile(doc, PolicyTests.Options()).IsSuccess); Assert.False(PolicyYaml.Read(PolicyYaml.Write(doc)).IsSuccess);
        doc.Nodes[0].PluginHost = "camouflage.example.test"; doc.Nodes[0].TcpFastOpen = null;
        string yaml = PolicyYaml.Write(doc); Assert.DoesNotContain("tcp_fast_open:", yaml);
        Assert.Null(PolicyYaml.Read(yaml).Document!.Nodes[0].TcpFastOpen);
        Assert.False(PolicyYaml.Read(yaml.Replace("plugin_mode: http", "plugin_mode: http\n  tcp_fast_open: invalid", StringComparison.Ordinal)).IsSuccess);
    }
    [Fact]
    public void HttpPort80AndStableIdentitiesKeepTheirOriginalMeaning()
    {
        string body = Subscription().Replace("port: 8443", "port: 80", StringComparison.Ordinal);
        var first = ClashSubscriptionImporter.Import(body, "fixture-source");
        var second = ClashSubscriptionImporter.Import(body.Replace("synthetic-obfs", "renamed", StringComparison.Ordinal).Replace("tfo: true", "tfo: false", StringComparison.Ordinal), "fixture-source");
        Assert.Equal(first.Nodes[0].Id, second.Nodes[0].Id);
        var result = XrayConfigCompiler.Compile(Policy(first), PolicyTests.Options()); Assert.True(result.IsSuccess);
        using var json = JsonDocument.Parse(result.Json!);
        Assert.Equal("camouflage.example.test", json.RootElement.GetProperty("outbounds").EnumerateArray().Single(item => item.GetProperty("tag").GetString() == first.Nodes[0].Id)
            .GetProperty("streamSettings").GetProperty("rawSettings").GetProperty("header").GetProperty("request").GetProperty("headers").GetProperty("Host")[0].GetString());
    }
    private static bool SimpleObfsAccepts(byte[] request, string expectedHost)
    {
        string[] headers = Encoding.ASCII.GetString(request).Split("\r\n", StringSplitOptions.None);
        return headers[0].StartsWith("GET / ", StringComparison.Ordinal) && headers[0].EndsWith("HTTP/1.1", StringComparison.Ordinal)
            && headers.Any(header => header.Equals("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            && headers.Any(header => header.StartsWith("Host: " + expectedHost, StringComparison.OrdinalIgnoreCase)
                && (header.Length == 6 + expectedHost.Length || header[6 + expectedHost.Length] == ':'));
    }
    private static byte[] StripFirstHeader(byte[] wire)
    {
        for (int index = 0; index + 3 < wire.Length; index++)
            if (wire[index] == 13 && wire[index + 1] == 10 && wire[index + 2] == 13 && wire[index + 3] == 10) return wire[(index + 4)..];
        throw new InvalidDataException("Synthetic response is incomplete.");
    }
}
