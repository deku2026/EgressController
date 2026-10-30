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
        string duplicate = Generated().Replace("\"loglevel\": \"warning\"", "\"loglevel\": \"warning\", \"loglevel\": \"none\"", StringComparison.Ordinal);
        Assert.Equal("duplicate-field", MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(duplicate), "en0", "en5").Code);
        Assert.False(MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes("{"), "en0", "en5").IsValid);
        Assert.False(MacHelperConfigurationValidator.Validate(new byte[MacHelperRequestValidator.MaximumFrameBytes + 1], "en0", "en5").IsValid);
        Assert.False(MacHelperConfigurationValidator.Validate(Encoding.UTF8.GetBytes(Generated()), "en0", "en0").IsValid);
    }

    private static string Generated()
    {
        // Same shape as Runtime.ConfigurationTests, compiled rather than duplicating expected JSON.
        var policy = new MacPolicyDocument
        {
            Interfaces = new() { HomeId = "home-service", NonHomeId = "other-service" },
            Defaults = new() { Global = ExitChoice.Proxy("node-1") },
            Nodes = [new() { Id = "node-1", Name = "Example", SourceId = "example", Server = "proxy.example.invalid", Port = 443,
                Protocol = NodeProtocol.Vless, UserId = "11111111-1111-1111-1111-111111111111", Tls = true }],
            Applications = [new() { Id = "app-1", Name = "Example App", BundlePath = "/Applications/Example.app",
                PrimaryExecutable = "/Applications/Example.app/Contents/MacOS/Example",
                ExecutablePaths = ["/Applications/Example.app/Contents/MacOS/Example", "/Applications/Example.app/Contents/Helpers/OldHelper"], Via = ExitChoice.Home }]
        };
        var result = XrayConfigCompiler.Compile(policy, new() { Interfaces = [new("home-service", "en0"), new("other-service", "en5")] });
        Assert.True(result.IsSuccess, string.Join(';', result.Diagnostics.Select(item => item.Message)));
        return result.Json!;
    }
}
