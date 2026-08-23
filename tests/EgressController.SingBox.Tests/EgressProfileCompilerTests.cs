using System.Net;
using System.Text.Json;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class EgressProfileCompilerTests
{
    [Fact]
    public void Default_profile_emits_the_reference_data_plane_with_loopback_api()
    {
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(Input(new EgressProfileDocument())).JsonBytes);
        JsonElement root = json.RootElement;
        Assert.Equal("warn", root.GetProperty("log").GetProperty("level").GetString());
        JsonElement dns = root.GetProperty("dns");
        JsonElement inbound = root.GetProperty("inbounds")[0];
        JsonElement route = root.GetProperty("route");

        JsonElement clashApi = root.GetProperty("experimental").GetProperty("clash_api");
        Assert.Equal("127.0.0.1:19090", clashApi.GetProperty("external_controller").GetString());
        Assert.Equal("0123456789abcdef0123456789abcdef", clashApi.GetProperty("secret").GetString());
        Assert.Equal("dns-clash", dns.GetProperty("servers")[0].GetProperty("tag").GetString());
        Assert.Equal("clash-7890", dns.GetProperty("servers")[0].GetProperty("detour").GetString());
        Assert.Equal("prefer_ipv4", dns.GetProperty("strategy").GetString());
        Assert.Equal("sing-box", inbound.GetProperty("interface_name").GetString());
        Assert.Single(inbound.GetProperty("address").EnumerateArray());
        Assert.Equal("esim-direct", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.Equal("primary-direct", root.GetProperty("outbounds")[1].GetProperty("tag").GetString());
        Assert.Equal("clash-7890", root.GetProperty("outbounds")[2].GetProperty("tag").GetString());
        Assert.False(route.TryGetProperty("rule_set", out _));
        Assert.False(route.TryGetProperty("auto_detect_interface", out _));
        Assert.False(route.TryGetProperty("find_process", out _));
        Assert.Equal("sniff", route.GetProperty("rules")[0].GetProperty("action").GetString());
        Assert.Equal("dns", route.GetProperty("rules")[1].GetProperty("protocol").GetString());
        Assert.Equal("hijack-dns", route.GetProperty("rules")[1].GetProperty("action").GetString());
        Assert.True(route.GetProperty("rules")[2].TryGetProperty("process_name", out _));
        Assert.False(route.GetProperty("rules")[2].TryGetProperty("process_path", out _));
    }

    [Fact]
    public void Route_order_and_union_are_deterministic()
    {
        string root = NewRoot();
        string srs = Path.Combine(root, "google.srs");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(srs, new byte[] { 1, 2, 3 });
        try
        {
            EgressProfileCompileInput input = Input(
                new EgressProfileDocument
                {
                    EsimRuleSets = new[] { "google" },
                    EsimDomains = new[] { "openai.com" },
                },
                applicationPaths: new[] { @"C:\Apps\Chrome\chrome.exe" },
                ruleSets: new[] { new SingBoxRuleSetInput("google", srs) });

            EgressProfileCompilationResult result = new EgressProfileCompiler().Compile(input);
            using JsonDocument json = JsonDocument.Parse(result.JsonBytes);
            JsonElement rules = json.RootElement.GetProperty("route").GetProperty("rules");
            JsonElement outbounds = json.RootElement.GetProperty("outbounds");

            Assert.Equal(6, rules.GetArrayLength());
            Assert.Equal("sniff", rules[0].GetProperty("action").GetString());
            Assert.Equal("hijack-dns", rules[1].GetProperty("action").GetString());
            Assert.Equal("primary-direct", rules[2].GetProperty("outbound").GetString());
            Assert.Equal(@"C:\Apps\Chrome\chrome.exe", rules[3].GetProperty("process_path")[0].GetString());
            Assert.Equal("google", rules[4].GetProperty("rule_set")[0].GetString());
            Assert.Equal("openai.com", rules[5].GetProperty("domain_suffix")[0].GetString());
            Assert.Equal("clash-7890", json.RootElement.GetProperty("route").GetProperty("final").GetString());
            Assert.Equal("esim-direct", outbounds[0].GetProperty("tag").GetString());
            Assert.Equal("primary-direct", outbounds[1].GetProperty("tag").GetString());
            Assert.Equal("clash-7890", outbounds[2].GetProperty("tag").GetString());
            Assert.Equal("5", outbounds[2].GetProperty("version").GetString());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Same_input_produces_byte_stable_json_and_owner_precedes_overlapping_app()
    {
        string owner = Path.GetFullPath(@"C:\Apps\Proxy\proxy.exe");
        var compiler = new EgressProfileCompiler();
        EgressProfileCompileInput input = Input(
            new EgressProfileDocument(),
            applicationPaths: new[] { owner, @"C:\Apps\Browser\brave.exe" },
            ownerPaths: new[] { owner });

        EgressProfileCompilationResult first = compiler.Compile(input);
        EgressProfileCompilationResult second = compiler.Compile(input);

        Assert.Equal(first.JsonBytes, second.JsonBytes);
        Assert.Equal(first.Sha256, second.Sha256);
        using JsonDocument json = JsonDocument.Parse(first.JsonBytes);
        JsonElement rules = json.RootElement.GetProperty("route").GetProperty("rules");
        Assert.Equal(4, rules.GetArrayLength());
        Assert.Equal("primary-direct", rules[2].GetProperty("outbound").GetString());
        Assert.Contains("proxy.exe", rules[2].GetProperty("process_name").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("proxy", rules[2].GetProperty("process_name").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(2, rules[3].GetProperty("process_path").GetArrayLength());
    }

    [Fact]
    public void Owner_self_overlap_is_rejected_with_precise_code()
    {
        string owner = Path.GetFullPath(@"C:\Apps\EgressController\sing-box.exe");
        EgressProfileCompilationException exception = Assert.Throws<EgressProfileCompilationException>(
            () => new EgressProfileCompiler().Compile(Input(
                new EgressProfileDocument(),
                ownerPaths: new[] { owner },
                selfPaths: new[] { owner })));

        Assert.Equal("upstream.owner.self", exception.Code);
    }

    [Fact]
    public void Controller_endpoint_is_required_for_structured_diagnostics()
    {
        EgressProfileCompilationException exception = Assert.Throws<EgressProfileCompilationException>(
            () => new EgressProfileCompiler().Compile(Input(new EgressProfileDocument()) with
            {
                ControllerPort = 0,
                ControllerSecret = string.Empty,
            }));

        Assert.Equal("controller.port", exception.Code);
    }

    [Fact]
    public void Missing_adapter_address_owner_and_srs_are_rejected()
    {
        EgressProfileCompilationException noAddress = Assert.Throws<EgressProfileCompilationException>(
            () => new EgressProfileCompiler().Compile(Input(
                new EgressProfileDocument(),
                environment: EnvironmentSnapshot(hasPrimaryAddress: false))));
        Assert.Equal("adapter.primary.address", noAddress.Code);

        EgressProfileCompilationException noOwner = Assert.Throws<EgressProfileCompilationException>(
            () => new EgressProfileCompiler().Compile(Input(
                new EgressProfileDocument(),
                ownerPaths: Array.Empty<string>())));
        Assert.Equal("upstream.owner", noOwner.Code);

        string root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            EgressProfileCompilationException noSrs = Assert.Throws<EgressProfileCompilationException>(
                () => new EgressProfileCompiler().Compile(Input(
                    new EgressProfileDocument { EsimRuleSets = new[] { "google" } },
                    ruleSets: Array.Empty<SingBoxRuleSetInput>())));
            Assert.Equal("ruleset.missing", noSrs.Code);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Rule_set_names_support_real_sing_catalog_at_and_bang_suffixes()
    {
        string root = NewRoot();
        string path = Path.Combine(root, "airchina@!cn.srs");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(path, new byte[] { 1 });
        try
        {
            EgressProfileCompilationResult result = new EgressProfileCompiler().Compile(Input(
                new EgressProfileDocument { EsimRuleSets = new[] { "airchina@!cn" } },
                ruleSets: new[] { new SingBoxRuleSetInput("airchina@!cn", path) }));

            using JsonDocument json = JsonDocument.Parse(result.JsonBytes);
            Assert.Equal("airchina@!cn", json.RootElement.GetProperty("route").GetProperty("rule_set")[0].GetProperty("tag").GetString());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static EgressProfileCompileInput Input(
        EgressProfileDocument profile,
        IReadOnlyList<string>? applicationPaths = null,
        IReadOnlyList<string>? ownerPaths = null,
        IReadOnlyList<string>? selfPaths = null,
        IReadOnlyList<SingBoxRuleSetInput>? ruleSets = null,
        NetworkEnvironmentSnapshot? environment = null)
        => new()
        {
            Profile = profile,
            Environment = environment ?? EnvironmentSnapshot(),
            ApplicationExecutablePaths = applicationPaths ?? Array.Empty<string>(),
            UpstreamOwnerPaths = ownerPaths ?? new[] { @"C:\Apps\Mihomo\mihomo.exe" },
            SelfExecutablePaths = selfPaths ?? Array.Empty<string>(),
            RuleSets = ruleSets ?? Array.Empty<SingBoxRuleSetInput>(),
            ControllerPort = 19090,
            ControllerSecret = "0123456789abcdef0123456789abcdef",
        };

    private static NetworkEnvironmentSnapshot EnvironmentSnapshot(bool hasPrimaryAddress = true)
    {
        Guid primaryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid esimId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        return new NetworkEnvironmentSnapshot
        {
            Primary = Adapter(primaryId, "Ethernet", hasPrimaryAddress ? "192.0.2.10" : null),
            Esim = Adapter(esimId, "Cellular", "198.51.100.10"),
        };
    }

    private static AdapterSelection Adapter(Guid id, string alias, string? ipv4)
        => new()
        {
            AdapterId = id,
            Alias = alias,
            Luid = 1,
            IfIndex = 10,
            Ipv6IfIndex = 10,
            IsUp = true,
            AddressState = ipv4 is null ? AdapterAddressState.NoAddress : AdapterAddressState.Ipv4Only,
            Ipv4BindAddress = ipv4 is null ? null : IPAddress.Parse(ipv4),
            Ipv6BindAddress = null,
        };

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "EgressController.CompilerTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
