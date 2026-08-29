using System.Net;
using System.Text.Json;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.SingBox.Configuration;

namespace EgressController.Core.Tests;

public sealed class TrafficRoutingContractTests
{
    [Fact]
    public void Route_rules_are_emitted_in_the_fixed_order()
    {
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(Input()).JsonBytes);
        JsonElement rules = json.RootElement.GetProperty("route").GetProperty("rules");

        Assert.Equal(4, rules.GetArrayLength());
        Assert.Equal("sniff", rules[0].GetProperty("action").GetString());
        Assert.Equal("hijack-dns", rules[1].GetProperty("action").GetString());
        Assert.Equal(6, rules[2].GetProperty("ip_version").GetInt32());
        Assert.Equal("reject", rules[2].GetProperty("action").GetString());
        Assert.Equal("primary-direct", rules[3].GetProperty("outbound").GetString());
    }

    [Fact]
    public void Final_route_is_always_the_upstream_socks_outbound()
    {
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(Input()).JsonBytes);

        Assert.Equal("clash-7890", json.RootElement.GetProperty("route").GetProperty("final").GetString());
        Assert.Equal("clash-7890", json.RootElement.GetProperty("outbounds")[2].GetProperty("tag").GetString());
    }

    [Fact]
    public void Application_and_domain_matches_form_one_esim_union()
    {
        EgressProfileCompileInput input = Input() with
        {
            ApplicationExecutablePaths = new[] { @"C:\Apps\Chrome\chrome.exe" },
            Profile = new EgressProfileDocument { EsimDomains = new[] { "openai.com" } },
        };
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        JsonElement rules = json.RootElement.GetProperty("route").GetProperty("rules");

        Assert.Equal(6, rules.GetArrayLength());
        Assert.Equal("esim-direct", rules[4].GetProperty("outbound").GetString());
        Assert.Equal("esim-direct", rules[5].GetProperty("outbound").GetString());
    }

    private static EgressProfileCompileInput Input()
        => new()
        {
            Profile = new EgressProfileDocument(),
            Environment = new NetworkEnvironmentSnapshot
            {
                Primary = Adapter(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Ethernet", "192.0.2.10"),
                Esim = Adapter(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Cellular", "198.51.100.10"),
            },
            ApplicationExecutablePaths = Array.Empty<string>(),
            UpstreamOwnerPaths = new[] { @"C:\Apps\Mihomo\mihomo.exe" },
            RuleSets = Array.Empty<SingBoxRuleSetInput>(),
            ControllerPort = 19090,
            ControllerSecret = "0123456789abcdef0123456789abcdef",
        };

    private static AdapterSelection Adapter(Guid id, string alias, string address)
        => new()
        {
            AdapterId = id,
            Alias = alias,
            Luid = 1,
            IfIndex = 10,
            Ipv6IfIndex = 10,
            IsUp = true,
            AddressState = AdapterAddressState.Ipv4Only,
            Ipv4BindAddress = IPAddress.Parse(address),
        };
}
