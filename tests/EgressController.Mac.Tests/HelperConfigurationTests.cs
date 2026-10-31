// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json.Nodes;
using EgressController.Mac;
using EgressController.Policy;

namespace EgressController.Mac.Tests;

public sealed class HelperConfigurationTests
{
    [Fact]
    public void ActualCompilerConfigurationCrossesTheIndependentHelperBoundary()
    {
        var generated = Generated();
        var result = MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(generated), "en0", "en5");
        Assert.True(result.IsValid, result.Code + ": " + result.Message);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("external-listener")]
    [InlineData("udp")]
    [InlineData("resource-direct")]
    [InlineData("tun-proxy-leak")]
    [InlineData("missing-resource-route")]
    [InlineData("dns-tun-leak")]
    [InlineData("empty-rules")]
    public void BootstrapListenerAndRoutesAreStrictlyBounded(string mutation)
    {
        var root = JsonNode.Parse(Generated(bootstrap: true))!;
        var rules = root["routing"]!["rules"]!.AsArray();
        switch (mutation)
        {
            case "external-listener": root["inbounds"]![1]!["listen"] = "0.0.0.0"; break;
            case "udp": root["inbounds"]![1]!["settings"]!["udp"] = true; break;
            case "resource-direct": rules[0]!["outboundTag"] = "nonhome"; break;
            case "tun-proxy-leak": rules.Last()!["outboundTag"] = "node-1"; break;
            case "missing-resource-route": rules.RemoveAt(0); break;
            case "dns-tun-leak": rules[1]!["inboundTag"] = new JsonArray("dns-query", "tun"); break;
            case "empty-rules": rules.Clear(); break;
        }
        var result = MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(root.ToJsonString()), "en0", "en5");
        Assert.Equal(mutation == "normal", result.IsValid);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("local-listener")]
    [InlineData("proxy-on-home")]
    [InlineData("direct-swapped")]
    [InlineData("missing-interface")]
    [InlineData("auto-interface")]
    [InlineData("direct-fallback")]
    [InlineData("global-direct")]
    [InlineData("dialer-direct")]
    [InlineData("dialer-cycle")]
    [InlineData("dns-fallback")]
    [InlineData("dns-on-home")]
    [InlineData("process-directory")]
    [InlineData("log-file")]
    [InlineData("certificate-file")]
    public void TamperedRuntimeCannotBroadenThePrivilegedPlan(string tamper)
    {
        var config = JsonNode.Parse(Generated())!.AsObject();
        var outbounds = config["outbounds"]!.AsArray();
        var node = outbounds.Single(item => item!["tag"]!.GetValue<string>() == "node-1")!;
        var home = outbounds.Single(item => item!["tag"]!.GetValue<string>() == "home")!;
        switch (tamper)
        {
            case "api": config["api"] = new JsonObject { ["tag"] = "control" }; break;
            case "local-listener": config["inbounds"]!.AsArray().Add((JsonNode)new JsonObject { ["protocol"] = "socks", ["port"] = 7890 }); break;
            case "proxy-on-home": node["streamSettings"]!["sockopt"]!["interface"] = "en0"; break;
            case "direct-swapped": home["streamSettings"]!["sockopt"]!["interface"] = "en5"; break;
            case "missing-interface": node["streamSettings"]!["sockopt"]!.AsObject().Remove("interface"); break;
            case "auto-interface": config["inbounds"]![0]!["settings"]!["autoOutboundsInterface"] = "auto"; break;
            case "direct-fallback": var direct = home.DeepClone(); direct["tag"] = "fallback"; outbounds.Add(direct); break;
            case "global-direct": config["routing"]!["rules"]!.AsArray().Last()!["outboundTag"] = "home"; break;
            case "dialer-direct": node["streamSettings"]!["sockopt"]!["dialerProxy"] = "nonhome"; break;
            case "dialer-cycle": node["streamSettings"]!["sockopt"]!["dialerProxy"] = "node-1"; break;
            case "dns-fallback": config["dns"]!["disableFallback"] = false; break;
            case "dns-on-home": outbounds.Single(item => item!["tag"]!.GetValue<string>() == "dns")!["streamSettings"]!["sockopt"]!["interface"] = "en0"; break;
            case "process-directory": config["routing"]!["rules"]!.AsArray().Single(item => item!["ruleTag"]!.GetValue<string>() == "app-app-1")!["process"] = new JsonArray("/Applications/Example.app/"); break;
            case "log-file": config["log"]!["error"] = "/etc/sensitive-file"; break;
            case "certificate-file": node["streamSettings"]!["tlsSettings"]!["certificates"] = new JsonArray(new JsonObject { ["certificateFile"] = "/etc/sensitive-file" }); break;
        }
        var result = MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(config.ToJsonString()), "en0", "en5");
        Assert.False(result.IsValid, tamper);
    }

    [Fact]
    public void DuplicateFieldsAndMalformedFramesAreRejectedBeforePrivilege()
    {
        string duplicate = Generated().Replace("\"log\": {", "\"log\": {}, \"log\": {", StringComparison.Ordinal);
        Assert.Equal("duplicate-field", MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(duplicate), "en0", "en5").Code);
        Assert.False(MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes("{"), "en0", "en5").IsValid);
        Assert.False(MacHelperConfigurationValidator.Validate(new byte[MacHelperRequestValidator.MaximumFrameBytes + 1], "en0", "en5").IsValid);
        Assert.False(MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(Generated()), "en0", "en0").IsValid);
    }

    [Theory]
    [InlineData(true, 8443)]
    [InlineData(false, 80)]
    public void ActualShadowsocksHttpCompilerShapeCrossesTheBoundary(bool tfo, int port)
    {
        var result = MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(GeneratedObfs(tfo, port)), "en0", "en5");
        Assert.True(result.IsValid, result.Code + ": " + result.Message);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("tls")]
    [InlineData("response")]
    [InlineData("header-type")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("host-injection")]
    [InlineData("host-port")]
    [InlineData("extra-header")]
    [InlineData("fast-open-string")]
    [InlineData("direct-fast-open")]
    public void HttpDisguiseCannotExpandTheHelperPlan(string tamper)
    {
        var config = JsonNode.Parse(GeneratedObfs(true, 8443))!;
        var node = config["outbounds"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == "node-1")!;
        var stream = node["streamSettings"]!;
        var header = stream["rawSettings"]!["header"]!;
        switch (tamper)
        {
            case "protocol": node["protocol"] = "http"; break;
            case "tls": stream["security"] = "tls"; break;
            case "response": header["response"] = new JsonObject(); break;
            case "header-type": header["type"] = "srtp"; break;
            case "method": header["request"]!["method"] = "CONNECT"; break;
            case "path": header["request"]!["path"] = new JsonArray("/", "/other"); break;
            case "host-injection": header["request"]!["headers"]!["Host"] = new JsonArray("example.test\r\nX-Secret: credential:8443"); break;
            case "host-port": header["request"]!["headers"]!["Host"] = new JsonArray("camouflage.example.test:443"); break;
            case "extra-header": header["request"]!["headers"]!["Authorization"] = new JsonArray("forbidden"); break;
            case "fast-open-string": stream["sockopt"]!["tcpFastOpen"] = "true"; break;
            case "direct-fast-open": config["outbounds"]!.AsArray().Single(item => item!["tag"]!.GetValue<string>() == "home")!["streamSettings"]!["sockopt"]!["tcpFastOpen"] = true; break;
        }
        Assert.False(MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(config.ToJsonString()), "en0", "en5").IsValid, tamper);
    }

    private static string GeneratedObfs(bool tfo, int port)
    {
        var policy = new MacPolicyDocument
        {
            Interfaces = new() { HomeId = "home-service", NonHomeId = "other-service" },
            Defaults = new() { Global = ExitChoice.Proxy("selected-group") },
            Groups = [new() { Id = "selected-group", Name = "Synthetic selection", SourceId = "synthetic", Kind = GroupKind.Select, MemberIds = ["node-1"], SelectedMemberId = "node-1" }],
            Nodes = [new() { Id = "node-1", Name = "Synthetic SS", SourceId = "synthetic", Server = "proxy.example.invalid", Port = port,
                Protocol = NodeProtocol.Shadowsocks, Cipher = "aes-128-gcm", Password = "synthetic-only", Plugin = "obfs", PluginMode = "http",
                PluginHost = "camouflage.example.test", TcpFastOpen = tfo }]
        };
        var result = XrayConfigCompiler.Compile(policy, new() { Interfaces = [new("home-service", "en0"), new("other-service", "en5")] });
        Assert.True(result.IsSuccess, string.Join(';', result.Diagnostics.Select(item => item.Message)));
        return result.Json!;
    }

    private static string Generated(bool bootstrap = false)
    {
        // Same shape as Runtime.ConfigurationTests, compiled rather than duplicating expected JSON.
        var policy = new MacPolicyDocument
        {
            Interfaces = new() { HomeId = "home-service", NonHomeId = "other-service" },
            Defaults = new() { Global = ExitChoice.Proxy("selected-group") },
            Groups = [new() { Id = "selected-group", Name = "Synthetic selection", SourceId = "example", Kind = GroupKind.Select, MemberIds = ["node-1"], SelectedMemberId = "node-1" }],
            Nodes = [new() { Id = "node-1", Name = "Example", SourceId = "example", Server = "proxy.example.invalid", Port = 443,
                Protocol = NodeProtocol.Vless, UserId = "11111111-1111-1111-1111-111111111111", Tls = true }],
            Applications = [new() { Id = "app-1", Name = "Example App", BundlePath = "/Applications/Example.app",
                PrimaryExecutable = "/Applications/Example.app/Contents/MacOS/Example",
                ExecutablePaths = ["/Applications/Example.app/Contents/MacOS/Example", "/Applications/Example.app/Contents/Helpers/OldHelper"], Via = ExitChoice.Home }]
        };
        var result = XrayConfigCompiler.Compile(policy, new() { ResourceBootstrap = bootstrap, Interfaces = [new("home-service", "en0"), new("other-service", "en5")] });
        Assert.True(result.IsSuccess, string.Join(';', result.Diagnostics.Select(item => item.Message)));
        return result.Json!;
    }
}
