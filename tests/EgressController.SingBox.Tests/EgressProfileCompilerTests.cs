using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        JsonElement dnsServers = dns.GetProperty("servers");
        Assert.Equal(3, dnsServers.GetArrayLength());
        JsonElement bootstrap = dnsServers[0];
        Assert.Equal("local", bootstrap.GetProperty("type").GetString());
        Assert.Equal(EgressProfileCompiler.DohBootstrapTag, bootstrap.GetProperty("tag").GetString());

        JsonElement esimCloudflare = dnsServers.EnumerateArray()
            .Single(server => server.GetProperty("tag").GetString() == EgressDohConfiguration.CloudflareTag);
        JsonElement esimDnsPod = dnsServers.EnumerateArray()
            .Single(server => server.GetProperty("tag").GetString() == EgressDohConfiguration.DnsPodTag);
        Assert.Equal("https", esimCloudflare.GetProperty("type").GetString());
        Assert.Equal("cloudflare-dns.com", esimCloudflare.GetProperty("server").GetString());
        Assert.Equal("dns-direct", esimCloudflare.GetProperty("detour").GetString());
        Assert.Equal("doh.pub", esimDnsPod.GetProperty("server").GetString());
        Assert.Equal("doh.pub", esimDnsPod.GetProperty("tls").GetProperty("server_name").GetString());
        Assert.All(dnsServers.EnumerateArray().Where(server => server.GetProperty("type").GetString() == "https"), server =>
        {
            Assert.Equal(EgressProfileCompiler.DohBootstrapTag, server.GetProperty("domain_resolver").GetString());
            Assert.Equal(EgressProfileCompiler.DnsDirectTag, server.GetProperty("detour").GetString());
        });
        Assert.Equal(EgressDohConfiguration.CloudflareTag, dns.GetProperty("final").GetString());
        Assert.Equal("ipv4_only", dns.GetProperty("strategy").GetString());
        Assert.True(dns.GetProperty("reverse_mapping").GetBoolean());
        Assert.Equal("sing-box", inbound.GetProperty("interface_name").GetString());
        Assert.Equal(2, inbound.GetProperty("address").GetArrayLength());
        Assert.Equal("dns-direct", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.Equal("proxy-direct", root.GetProperty("outbounds")[1].GetProperty("tag").GetString());
        Assert.Equal("clash-7890", root.GetProperty("outbounds").EnumerateArray().Single(item => item.GetProperty("type").GetString() == "socks").GetProperty("tag").GetString());
        Assert.False(route.TryGetProperty("rule_set", out _));
        Assert.True(route.GetProperty("auto_detect_interface").GetBoolean());
        Assert.True(route.GetProperty("find_process").GetBoolean());
        Assert.Equal("sniff", route.GetProperty("rules")[0].GetProperty("action").GetString());
        Assert.Equal("dns", route.GetProperty("rules")[1].GetProperty("protocol").GetString());
        Assert.Equal("hijack-dns", route.GetProperty("rules")[1].GetProperty("action").GetString());
        Assert.Equal(6, route.GetProperty("rules")[2].GetProperty("ip_version").GetInt32());
        Assert.Equal("reject", route.GetProperty("rules")[2].GetProperty("action").GetString());
        Assert.True(route.GetProperty("rules")[3].TryGetProperty("process_path_regex", out _));
        Assert.False(route.GetProperty("rules")[3].TryGetProperty("process_name", out _));
        Assert.False(route.GetProperty("rules")[3].TryGetProperty("process_path", out _));
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
            JsonElement[] rules = ActiveRules(json.RootElement);
            JsonElement outbounds = json.RootElement.GetProperty("outbounds");

            Assert.Equal(7, rules.Length);
            Assert.Equal("sniff", rules[0].GetProperty("action").GetString());
            Assert.Equal("hijack-dns", rules[1].GetProperty("action").GetString());
            Assert.Equal("reject", rules[2].GetProperty("action").GetString());
            Assert.Equal("proxy-direct", rules[3].GetProperty("outbound").GetString());
            Assert.False(rules[4].TryGetProperty("process_path", out _));
            Assert.Contains("chrome.exe", rules[4].GetProperty("process_name").EnumerateArray().Select(value => value.GetString()));
            Assert.Contains("chrome", rules[4].GetProperty("process_name").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("openai.com", rules[5].GetProperty("domain_suffix")[0].GetString());
            Assert.Equal("google", rules[6].GetProperty("rule_set")[0].GetString());
            Assert.Equal("clash-7890", json.RootElement.GetProperty("route").GetProperty("final").GetString());
            Assert.Equal(EgressProfileCompiler.DohBootstrapTag, json.RootElement.GetProperty("route").GetProperty("default_domain_resolver").GetString());
            Assert.Equal("dns-direct", outbounds[0].GetProperty("tag").GetString());
            Assert.Equal("proxy-direct", outbounds[1].GetProperty("tag").GetString());
            Assert.Equal("clash-7890", outbounds.EnumerateArray().Single(item => item.GetProperty("type").GetString() == "socks").GetProperty("tag").GetString());
            Assert.Equal("5", outbounds.EnumerateArray().Single(item => item.GetProperty("type").GetString() == "socks").GetProperty("version").GetString());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Global_DoH_routing_can_select_the_fallback_without_changing_business_final()
    {
        EgressProfileCompileInput input = Input(
            new EgressProfileDocument(),
            applicationPaths: new[] { @"C:\Apps\Claude\claude.exe" }) with
        {
            DohRouting = new DohRoutingDecision
            {
                DnsTag = EgressDohConfiguration.DnsPodTag,
            },
        };

        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        JsonElement root = json.RootElement;
        JsonElement dns = root.GetProperty("dns");
        Assert.Equal(EgressDohConfiguration.CloudflareTag, dns.GetProperty("final").GetString());
        Assert.Equal(EgressDohConfiguration.DnsPodMode, root.GetProperty("experimental").GetProperty("clash_api").GetProperty("default_mode").GetString());
        Assert.Equal(EgressDohConfiguration.DnsPodTag, dns.GetProperty("rules").EnumerateArray()
            .Single(rule => rule.TryGetProperty("clash_mode", out var mode) && mode.GetString() == EgressDohConfiguration.DnsPodMode)
            .GetProperty("server").GetString());
        Assert.DoesNotContain(dns.GetProperty("rules").EnumerateArray(), rule =>
            rule.TryGetProperty("process_name", out _));
        Assert.Equal(EgressProfileCompiler.UpstreamSocksTag, root.GetProperty("route").GetProperty("final").GetString());
        Assert.Equal(EgressProfileCompiler.DohBootstrapTag, root.GetProperty("route")
            .GetProperty("default_domain_resolver").GetString());
        JsonElement servers = dns.GetProperty("servers");
        Assert.Equal(EgressProfileCompiler.DnsDirectTag, servers.EnumerateArray()
            .Single(server => server.GetProperty("tag").GetString() == EgressDohConfiguration.DnsPodTag)
            .GetProperty("detour").GetString());
        Assert.DoesNotContain(servers.EnumerateArray(), server =>
            server.GetProperty("tag").GetString() is "dns-clash" or "dns-clash-backup");
    }

    [Fact]
    public void Fail_closed_rejects_selected_apps_but_keeps_recovery_traffic_available()
    {
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(
            Input(new EgressProfileDocument(), applicationPaths: [@"C:\Apps\A\a.exe"], selfPaths: [@"C:\Controller\controller.exe"]) with
            { DohRouting = new DohRoutingDecision { FailClosed = true } }).JsonBytes);
        Assert.Equal("reject", RouteForProcess(json.RootElement, @"C:\Apps\A\a.exe"));
        Assert.Equal("recovery-direct", RouteForProcess(json.RootElement, @"C:\Controller\controller.exe"));
        Assert.DoesNotContain(json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray(),
            rule => rule.TryGetProperty("inbound", out _));
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
        JsonElement[] rules = ActiveRules(json.RootElement);
        Assert.Equal(6, rules.Length);
        Assert.Equal("proxy-direct", rules[3].GetProperty("outbound").GetString());
        Assert.Equal("proxy-direct", RouteForProcess(json.RootElement, owner));
        Assert.False(rules[3].TryGetProperty("process_name", out _));
        Assert.Contains("brave.exe", rules[4].GetProperty("process_name").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void Application_paths_are_compiled_to_stable_process_names()
    {
        const string configuredPath = @"C:\Program Files\Vendor (Preview)\App[1]+.EXE";
        EgressProfileCompilationResult result = new EgressProfileCompiler().Compile(Input(
            new EgressProfileDocument(),
            applicationPaths: new[] { configuredPath }));

        using JsonDocument json = JsonDocument.Parse(result.JsonBytes);
        JsonElement applicationRule = ActiveRules(json.RootElement)[4];
        string[] processNames = applicationRule.GetProperty("process_name").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        Assert.Contains("App[1]+.EXE", processNames);
        Assert.Contains("App[1]+", processNames);
        Assert.False(applicationRule.TryGetProperty("process_path_regex", out _));
    }

    [Fact]
    public void Process_name_matching_keeps_windows_case_variants_for_the_same_application()
    {
        EgressProfileCompilationResult result = new EgressProfileCompiler().Compile(Input(
            new EgressProfileDocument(),
            applicationPaths: new[]
            {
                @"C:\Apps\Claude\claude.exe",
                @"C:\Apps\Claude\Claude.exe",
            }));

        using JsonDocument json = JsonDocument.Parse(result.JsonBytes);
        JsonElement routeRule = ActiveRules(json.RootElement)[4];
        string[] routeNames = routeRule.GetProperty("process_name")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        Assert.Equal(4, routeNames.Length);
        Assert.Contains("Claude", routeNames);
        Assert.Contains("Claude.exe", routeNames);
        Assert.Contains("claude", routeNames);
        Assert.Contains("claude.exe", routeNames);
        Assert.Equal(4, routeNames.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(routeNames, name => name == "CLAUDE.EXE");
        Assert.DoesNotContain(json.RootElement.GetProperty("dns").GetProperty("rules").EnumerateArray(), rule =>
            rule.TryGetProperty("process_name", out _));
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
    public void Offline_esim_rejects_selected_process_and_domain_without_an_esim_outbound()
    {
        EgressProfileCompileInput input = Input(
            new EgressProfileDocument { EsimDomains = new[] { "openai.com" } },
            applicationPaths: new[] { @"C:\Apps\Chrome\chrome.exe" },
            environment: EnvironmentSnapshot(dnsReady: false));

        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        JsonElement root = json.RootElement;
        JsonElement routeRules = root.GetProperty("route").GetProperty("rules");
        JsonElement processRule = routeRules.EnumerateArray().Single(rule => rule.TryGetProperty("process_name", out JsonElement names)
            && names.EnumerateArray().Any(name => name.GetString() == "chrome.exe"));
        JsonElement domainRule = routeRules.EnumerateArray().Single(rule => rule.TryGetProperty("domain_suffix", out _));
        Assert.Equal("reject", processRule.GetProperty("action").GetString());
        Assert.Equal("reject", domainRule.GetProperty("action").GetString());
        Assert.Equal(4, root.GetProperty("outbounds").GetArrayLength());
        Assert.DoesNotContain(root.GetProperty("outbounds").EnumerateArray(), item =>
            item.GetProperty("tag").GetString() == EgressProfileCompiler.DnsDirectTag);
        Assert.DoesNotContain(root.GetProperty("dns").GetProperty("servers").EnumerateArray(), item =>
            item.GetProperty("tag").GetString() == EgressProfileCompiler.DnsTag);
        Assert.Equal(EgressProfileCompiler.DohBootstrapTag, root.GetProperty("dns").GetProperty("final").GetString());
        Assert.All(root.GetProperty("dns").GetProperty("rules").EnumerateArray(), rule =>
            Assert.Equal(EgressProfileCompiler.DohBootstrapTag, rule.GetProperty("server").GetString()));
        Assert.DoesNotContain(routeRules.EnumerateArray(), rule => rule.TryGetProperty("inbound", out _));
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
        // Missing physical connectivity and offline SOCKS owners do not prevent TUN preparation.
        new EgressProfileCompiler().Compile(Input(new EgressProfileDocument(), environment: EnvironmentSnapshot(hasPrimaryAddress: false)));
        new EgressProfileCompiler().Compile(Input(new EgressProfileDocument(), ownerPaths: []));

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

    [Fact]
    public void Multiple_ports_route_processes_domains_and_catalog_sets_to_their_destinations()
    {
        string directory = NewRoot();
        Directory.CreateDirectory(directory);
        string srs = Path.Combine(directory, "google.srs");
        File.WriteAllBytes(srs, [1]);
        try
        {
            var profile = new EgressProfileDocument
            {
                UpstreamPorts = [7892, 7890, 7891],
                UpstreamPort = 7891,
                Domains =
                [
                    new() { Name = "google.com", Target = EgressRouteTarget.Default },
                    new() { Name = "ai.google.com", Target = EgressRouteTarget.ForPort(7892) },
                    new() { Name = "openai.com" },
                ],
                RuleSets = [new() { Name = "google", Target = EgressRouteTarget.ForPort(7890) }],
            };
            EgressProfileCompileInput input = Input(profile, ruleSets: [new("google", srs)],
                ownerPaths: [@"C:\Apps\Proxy\mihomo.exe", @"C:\Apps\Proxy2\xray.exe"]) with
            {
                ApplicationRoutes =
                [
                    new([@"C:\Apps\Chrome\chrome.exe"], EgressRouteTarget.ForPort(7892)),
                    new([@"C:\Apps\Claude\claude.exe"], EgressRouteTarget.DefaultAdapter),
                ],
            };
            using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
            JsonElement root = json.RootElement;
            Assert.Equal("clash-7891", root.GetProperty("route").GetProperty("final").GetString());
            JsonElement[] socks = root.GetProperty("outbounds").EnumerateArray()
                .Where(outbound => outbound.GetProperty("type").GetString() == "socks").ToArray();
            Assert.Equal([7890, 7891, 7892], socks.Select(outbound => outbound.GetProperty("server_port").GetInt32()));
            foreach (JsonElement outbound in socks)
                Assert.Equal($"clash-{outbound.GetProperty("server_port").GetInt32()}", outbound.GetProperty("tag").GetString());

            JsonElement[] rules = ActiveRules(root);
            Assert.Equal("proxy-direct", rules[3].GetProperty("outbound").GetString());
            Assert.Equal("proxy-direct", RouteForProcess(root, @"C:\Apps\Proxy2\xray.exe"));
            Assert.Equal("proxy-direct", RouteForProcess(root, @"C:\Apps\Proxy\mihomo.exe"));
            Assert.Equal("clash-7892", rules.Single(rule => Matches(rule, "process_name", "chrome.exe")).GetProperty("outbound").GetString());
            Assert.Equal("adapter-22222222222222222222222222222222", rules.Single(rule => Matches(rule, "process_name", "claude.exe")).GetProperty("outbound").GetString());
            Assert.Equal("ai.google.com", rules[6].GetProperty("domain_suffix")[0].GetString());
            Assert.Equal("clash-7892", rules[6].GetProperty("outbound").GetString());
            Assert.Equal("clash-7891", rules[7].GetProperty("outbound").GetString());
            Assert.Equal("adapter-22222222222222222222222222222222", rules[8].GetProperty("outbound").GetString());
            Assert.Equal("google", rules[9].GetProperty("rule_set")[0].GetString());
            Assert.Equal("clash-7890", rules[9].GetProperty("outbound").GetString());
        }
        finally
        {
            DeleteRoot(directory);
        }
    }

    [Fact]
    public void Changing_default_updates_only_follow_default_routes()
    {
        var profile = new EgressProfileDocument
        {
            UpstreamPorts = [7890, 7891],
            Domains =
            [
                new() { Name = "fixed.example", Target = EgressRouteTarget.ForPort(7890) },
                new() { Name = "follow.example", Target = EgressRouteTarget.Default },
            ],
        };
        foreach (int port in profile.UpstreamPorts)
        {
            using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(Input(profile.SetDefaultPort(port))).JsonBytes);
            JsonElement route = json.RootElement.GetProperty("route");
            Assert.Equal($"clash-{port}", route.GetProperty("final").GetString());
            Assert.Equal("clash-7890", route.GetProperty("rules").EnumerateArray()
                .Single(rule => Matches(rule, "domain_suffix", "fixed.example")).GetProperty("outbound").GetString());
            Assert.Equal($"clash-{port}", route.GetProperty("rules").EnumerateArray()
                .Single(rule => Matches(rule, "domain_suffix", "follow.example")).GetProperty("outbound").GetString());
        }
    }

    [Fact]
    public void Same_executable_path_cannot_silently_choose_between_different_exits()
    {
        EgressProfileCompileInput input = Input(new EgressProfileDocument()) with
        {
            ApplicationRoutes =
            [
                new([@"C:\Apps\One\electron.exe"], EgressRouteTarget.DefaultAdapter),
                new([@"c:\apps\one\Electron.exe"], EgressRouteTarget.ForPort(7890)),
            ],
        };
        var exception = Assert.Throws<EgressProfileCompilationException>(() => new EgressProfileCompiler().Compile(input));
        Assert.Equal("application.conflict", exception.Code);
        Assert.Contains(@"C:\Apps\One\electron.exe", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Brave_and_Codex_chrome_proxy_helpers_keep_their_own_exits()
    {
        const string brave = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
        const string braveHelper = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\chrome_proxy.exe";
        const string codex = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__example\app\Codex.exe";
        const string codexHelper = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__example\app\chrome_proxy.exe";
        EgressProfileCompileInput input = Input(new EgressProfileDocument
        {
            UpstreamPorts = [7890, 7897],
            UpstreamPort = 7897,
        }) with
        {
            ApplicationRoutes =
            [
                new([brave, braveHelper], EgressRouteTarget.DefaultAdapter),
                new([codex, codexHelper], EgressRouteTarget.ForPort(7890)),
            ],
        };
        var compiler = new EgressProfileCompiler();
        EgressProfileCompilationResult compiled = compiler.Compile(input);
        using JsonDocument json = JsonDocument.Parse(compiled.JsonBytes);
        JsonElement root = json.RootElement;
        Assert.Equal("adapter-22222222222222222222222222222222", RouteForProcess(root, brave));
        Assert.Equal("adapter-22222222222222222222222222222222", RouteForProcess(root, braveHelper.ToUpperInvariant()));
        Assert.Equal("clash-7890", RouteForProcess(root, codex));
        Assert.Equal("clash-7890", RouteForProcess(root, codexHelper.ToUpperInvariant()));
        Assert.Equal("clash-7897", RouteForProcess(root, @"C:\Unrelated\chrome_proxy.exe"));
        Assert.DoesNotContain(root.GetProperty("route").GetProperty("rules").EnumerateArray(),
            rule => Matches(rule, "process_name", "chrome_proxy.exe") || Matches(rule, "process_name", "Chrome_proxy"));
        Assert.Equal(compiled.JsonBytes, compiler.Compile(input with
        {
            ApplicationRoutes = input.ApplicationRoutes.Reverse().ToArray(),
        }).JsonBytes);
    }

    [Fact]
    public void Unchecked_application_with_a_shared_helper_is_not_captured_by_selected_application()
    {
        const string selectedHelper = @"C:\Apps\Selected\chrome_proxy.exe";
        const string uncheckedHelper = @"C:\Apps\Unchecked\chrome_proxy.exe";
        EgressProfileCompileInput input = Input(new EgressProfileDocument(), applicationPaths: [selectedHelper]) with
        {
            KnownApplicationExecutablePaths = [selectedHelper, uncheckedHelper],
        };
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal("adapter-22222222222222222222222222222222", RouteForProcess(json.RootElement, selectedHelper));
        Assert.Equal("clash-7890", RouteForProcess(json.RootElement, uncheckedHelper));
    }

    [Fact]
    public void Shared_helper_paths_are_literal_case_insensitive_and_bounded()
    {
        const string first = @"C:\Apps\Vendor (Preview)\App[1]+ #tools\helper.exe";
        const string second = @"C:\Apps\Other\helper.exe";
        EgressProfileCompileInput input = Input(new EgressProfileDocument()) with
        {
            ApplicationRoutes =
            [
                new([first], EgressRouteTarget.DefaultAdapter),
                new([second], EgressRouteTarget.Default),
            ],
        };
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal("adapter-22222222222222222222222222222222", RouteForProcess(json.RootElement, first.ToUpperInvariant()));
        Assert.Equal("clash-7890", RouteForProcess(json.RootElement, second));
        Assert.Equal("clash-7890", RouteForProcess(json.RootElement, first + ".other.exe"));
        Assert.Equal("clash-7890", RouteForProcess(json.RootElement, first.Replace("App[1]+", "App1")));
        string pattern = ActiveRules(json.RootElement)
            .First(rule => rule.TryGetProperty("process_path_regex", out _)
                && rule.GetProperty("outbound").GetString() == "adapter-22222222222222222222222222222222")
            .GetProperty("process_path_regex")[0].GetString()!;
        Assert.DoesNotContain(@"\ ", pattern);
        Assert.DoesNotContain(@"\#", pattern);
    }

    [Fact]
    public void Proxy_owner_exemption_does_not_capture_another_application_with_the_same_name()
    {
        const string proxy = @"C:\Proxy\worker.exe";
        const string application = @"C:\Apps\AI\worker.exe";
        EgressProfileCompileInput input = Input(new EgressProfileDocument(),
            applicationPaths: [application, proxy], ownerPaths: [proxy]);
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal("proxy-direct", RouteForProcess(json.RootElement, proxy));
        Assert.Equal("adapter-22222222222222222222222222222222", RouteForProcess(json.RootElement, application));
    }

    [Fact]
    public void Shared_helper_keeps_esim_rejection_without_blocking_other_application_rule_generation()
    {
        const string esimHelper = @"C:\Apps\AI\helper.exe";
        const string portHelper = @"C:\Apps\Browser\helper.exe";
        EgressProfileCompileInput input = Input(new EgressProfileDocument(),
            environment: EnvironmentSnapshot(dnsReady: false)) with
        {
            ApplicationRoutes =
            [
                new([esimHelper], EgressRouteTarget.DefaultAdapter),
                new([portHelper], EgressRouteTarget.ForPort(7890)),
            ],
        };
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal("reject", RouteForProcess(json.RootElement, esimHelper));
        Assert.Equal("reject", RouteForProcess(json.RootElement, portHelper));
        // Every selected application is protected, independent of its chosen exit.
    }

    [Fact]
    public void Offline_esim_and_global_doh_protection_do_not_change_selected_port_routes()
    {
        EgressProfileCompileInput input = Input(new EgressProfileDocument
        {
            UpstreamPorts = [7890, 7891],
            Domains =
            [
                new() { Name = "esim.example" },
                new() { Name = "port.example", Target = EgressRouteTarget.ForPort(7891) },
            ],
        }, environment: EnvironmentSnapshot(dnsReady: false));
        using JsonDocument json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        JsonElement[] rules = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
        Assert.DoesNotContain(rules, rule => rule.TryGetProperty("inbound", out _));
        JsonElement esim = rules.Single(rule => Matches(rule, "domain_suffix", "esim.example"));
        Assert.Equal("reject", esim.GetProperty("action").GetString());
        Assert.False(esim.TryGetProperty("outbound", out _));
        Assert.Equal("clash-7891", rules.Single(rule => Matches(rule, "domain_suffix", "port.example")).GetProperty("outbound").GetString());
    }

    [Fact]
    public void Two_adapter_routes_are_distinct_and_legacy_third_adapter_is_rejected()
    {
        Guid a = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid b = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid c = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var profile = new EgressProfileDocument { UpstreamPorts = [] }
            .SetAdapterRoles(a.ToString("D"), b.ToString("D"));
        profile = profile with { DnsAdapterId = b.ToString("D") };
        var environment = new NetworkEnvironmentSnapshot
        {
            ProxyAdapter = Adapter(b, "USB Redmi", "198.51.100.2"),
            DefaultAdapter = Adapter(a, "Ethernet", "192.0.2.1"), DnsAdapter = Adapter(b, "USB Redmi", "198.51.100.2"),
            Adapters = [Adapter(a, "Ethernet", "192.0.2.1"), Adapter(b, "USB Redmi", "198.51.100.2"), Adapter(c, "Wi-Fi", "203.0.113.5")],
        };
        var input = Input(profile, ownerPaths: [], environment: environment) with
        {
            ApplicationRoutes = [new([@"C:\A.exe"], EgressRouteTarget.DefaultAdapter),
                new([@"C:\B.exe"], EgressRouteTarget.ForAdapter(b.ToString("D"))),
                new([@"C:\C.exe"], EgressRouteTarget.ForAdapter(c.ToString("D")))],
        };
        using var json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal(EgressProfileCompiler.AdapterTag(a), RouteForProcess(json.RootElement, @"C:\A.exe"));
        Assert.Equal(EgressProfileCompiler.AdapterTag(b), RouteForProcess(json.RootElement, @"C:\B.exe"));
        Assert.Equal("reject", RouteForProcess(json.RootElement, @"C:\C.exe"));
        JsonElement dns = json.RootElement.GetProperty("outbounds").EnumerateArray().Single(item => item.GetProperty("tag").GetString() == EgressProfileCompiler.DnsDirectTag);
        Assert.Equal("Ethernet", dns.GetProperty("bind_interface").GetString());
        Assert.DoesNotContain(json.RootElement.GetProperty("outbounds").EnumerateArray(), item => item.GetProperty("type").GetString() == "socks");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owner_rule_precedes_app_domain_and_final_routes_even_during_fail_closed(bool failClosed)
    {
        const string proxy = @"C:\Proxy (Preview)\core[1].exe";
        var input = Input(new EgressProfileDocument
        {
            Domains = [new() { Name = "example.com", Target = EgressRouteTarget.Default }],
        }, ownerPaths: [proxy]) with
        {
            ApplicationRoutes = [new([proxy], EgressRouteTarget.Default)],
            DohRouting = new() { FailClosed = failClosed },
        };
        using var json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal("proxy-direct", RouteForProcess(json.RootElement, proxy.ToUpperInvariant()));
        Assert.Equal("clash-7890", RouteForProcess(json.RootElement, @"C:\Other\core[1].exe"));
        Assert.Equal("clash-7890", RouteForProcess(json.RootElement, proxy + ".other.exe"));
        var rules = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal("proxy-direct", rules[3].GetProperty("outbound").GetString());
        Assert.True(Array.FindIndex(rules, rule => rule.TryGetProperty("domain_suffix", out _)) > 3);
    }

    [Fact]
    public void Offline_proxy_is_rejected_without_using_recovery_fallback_or_direct_card()
    {
        const string proxy = @"C:\Proxy\proxy.exe", self = @"C:\Controller\controller.exe";
        var input = Input(new(), ownerPaths: [proxy], selfPaths: [self], applicationPaths: [@"C:\app.exe"],
            environment: EnvironmentSnapshot(hasPrimaryAddress: false));
        using var json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal("reject", RouteForProcess(json.RootElement, proxy));
        Assert.Equal(EgressProfileCompiler.AdapterTag(input.Environment.DefaultAdapter.AdapterId), RouteForProcess(json.RootElement, @"C:\app.exe"));
        Assert.Equal("recovery-direct", RouteForProcess(json.RootElement, self));
        Assert.DoesNotContain(json.RootElement.GetProperty("outbounds").EnumerateArray(),
            item => item.GetProperty("tag").GetString() == "proxy-direct");
    }

    [Fact]
    public void Direct_proxy_and_dns_bind_independently_and_replacing_direct_leaves_proxy_fixed()
    {
        var input = Input(new(), applicationPaths: [@"C:\app.exe"]);
        var originalProxy = input.Environment.ProxyAdapter;
        var replacement = Adapter(Guid.Parse("33333333-3333-3333-3333-333333333333"), "iPhone USB", "203.0.113.8");
        var changed = input with
        {
            Profile = input.Profile.SetAdapterRoles(replacement.AdapterId.ToString("D"), originalProxy.AdapterId.ToString("D")),
            Environment = input.Environment with { DefaultAdapter = replacement, DnsAdapter = replacement },
        };
        using var json = JsonDocument.Parse(new EgressProfileCompiler().Compile(changed).JsonBytes);
        var outbounds = json.RootElement.GetProperty("outbounds").EnumerateArray().ToArray();
        var proxy = outbounds.Single(item => item.GetProperty("tag").GetString() == "proxy-direct");
        Assert.Equal("Ethernet", proxy.GetProperty("bind_interface").GetString());
        Assert.Equal("192.0.2.10", proxy.GetProperty("inet4_bind_address").GetString());
        var dns = outbounds.Single(item => item.GetProperty("tag").GetString() == "dns-direct");
        Assert.Equal("iPhone USB", dns.GetProperty("bind_interface").GetString());
        Assert.Equal(EgressProfileCompiler.AdapterTag(replacement.AdapterId), RouteForProcess(json.RootElement, @"C:\app.exe"));
    }

    [Fact]
    public void Unidentified_port_rejects_app_domain_and_final_route_but_allows_recovery_and_known_owners()
    {
        const string core = @"C:\Proxy\known.exe", self = @"C:\Controller\controller.exe";
        var input = Input(new EgressProfileDocument
        {
            UpstreamPorts = [7890, 7891], Domains = [new() { Name = "example.com", Target = EgressRouteTarget.Default }],
        }, ownerPaths: [core], selfPaths: [self]) with
        {
            ApplicationRoutes = [new([@"C:\App.exe"], EgressRouteTarget.Default),
                new([@"C:\Other.exe"], EgressRouteTarget.ForPort(7891))],
            UnreadyUpstreamPorts = [7890],
        };
        using var json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal("reject", RouteForProcess(json.RootElement, @"C:\App.exe"));
        Assert.Equal("clash-7891", RouteForProcess(json.RootElement, @"C:\Other.exe"));
        Assert.Equal("proxy-direct", RouteForProcess(json.RootElement, core));
        Assert.Equal("recovery-direct", RouteForProcess(json.RootElement, self));
        var rules = json.RootElement.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal("reject", rules.Single(rule => Matches(rule, "domain_suffix", "example.com")).GetProperty("action").GetString());
        Assert.Equal("reject", rules[^1].GetProperty("action").GetString());
        Assert.Single(rules[^1].EnumerateObject()); // Unconditional final rejection, no port recursion.
    }

    private static JsonElement[] ActiveRules(JsonElement root, string? mode = null)
    {
        mode ??= root.GetProperty("experimental").GetProperty("clash_api").GetProperty("default_mode").GetString();
        return root.GetProperty("route").GetProperty("rules").EnumerateArray().Where(rule =>
            !rule.TryGetProperty("clash_mode", out var required) || required.GetString() == mode).ToArray();
    }

    [Fact]
    public void One_config_switches_protection_and_dns_in_place_without_changing_loop_exemptions()
    {
        const string app = @"C:\Apps\a.exe", owner = @"C:\Proxy\core.exe", self = @"C:\Controller\app.exe";
        var input = Input(new(), [app], [owner], [self]) with { DohRouting = new() { FailClosed = true } };
        using var json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        var root = json.RootElement;
        var rules = root.GetProperty("route").GetProperty("rules").EnumerateArray().ToArray();
        int protection = Array.FindIndex(rules, rule => rule.TryGetProperty("clash_mode", out _));
        Assert.True(Array.FindIndex(rules, rule => rule.TryGetProperty("outbound", out var outbound)
            && outbound.GetString() == "proxy-direct") < protection);
        foreach (string mode in new[] { EgressDohConfiguration.ProtectedMode, EgressDohConfiguration.CloudflareMode, EgressDohConfiguration.DnsPodMode })
        {
            Assert.Equal(mode == EgressDohConfiguration.ProtectedMode ? "reject" : EgressProfileCompiler.AdapterTag(input.Environment.DefaultAdapter.AdapterId),
                RouteForProcess(root, app, mode));
            Assert.Equal("proxy-direct", RouteForProcess(root, owner, mode));
            Assert.Equal("recovery-direct", RouteForProcess(root, self, mode));
        }
        var dnsRules = root.GetProperty("dns").GetProperty("rules").EnumerateArray().ToArray();
        foreach (var endpoint in EgressDohConfiguration.Endpoints)
        {
            int probe = Array.FindIndex(dnsRules, rule => Matches(rule, "domain_suffix", endpoint.ProbeSuffix));
            Assert.True(probe >= 0 && probe < Array.FindIndex(dnsRules, rule => rule.TryGetProperty("clash_mode", out _)));
            Assert.Equal(endpoint.Tag, dnsRules[probe].GetProperty("server").GetString());
        }
    }

    [Fact]
    public void Proxy_card_and_port_failure_do_not_disable_a_healthy_direct_app()
    {
        var input = Input(new(), [@"C:\app.exe"], environment: EnvironmentSnapshot(hasPrimaryAddress: false))
            with { UnreadyUpstreamPorts = [7890] };
        using var json = JsonDocument.Parse(new EgressProfileCompiler().Compile(input).JsonBytes);
        Assert.Equal(EgressProfileCompiler.AdapterTag(input.Environment.DefaultAdapter.AdapterId), RouteForProcess(json.RootElement, @"C:\app.exe"));
        Assert.Equal("reject", RouteForProcess(json.RootElement, @"C:\Apps\Mihomo\mihomo.exe"));
        Assert.Equal(EgressDohConfiguration.CloudflareMode, json.RootElement.GetProperty("experimental").GetProperty("clash_api").GetProperty("default_mode").GetString());
    }

    [Fact]
    public void Dns_probes_cannot_be_redirected_to_proxy_card_by_legacy_dns_selection()
    {
        var input = Input(new());
        input = input with { Profile = input.Profile with { DnsAdapterId = input.Profile.ProxyAdapterId },
            Environment = input.Environment with { DnsAdapter = input.Environment.ProxyAdapter } };
        var config = new EgressProfileCompiler().Compile(input).Document;
        var dns = config.Outbounds.Single(outbound => outbound.Tag == EgressProfileCompiler.DnsDirectTag);
        Assert.Equal(input.Environment.DefaultAdapter.Alias, dns.BindInterface);
        Assert.Equal(input.Environment.DefaultAdapter.Ipv4BindAddress!.ToString(), dns.Inet4BindAddress);
    }

    private static bool Matches(JsonElement rule, string field, string value)
        => rule.TryGetProperty(field, out JsonElement values)
            && values.EnumerateArray().Any(item => item.GetString() == value);

    private static string RouteForProcess(JsonElement root, string path, string? mode = null)
    {
        foreach (JsonElement rule in ActiveRules(root, mode))
        {
            bool matchesName = Matches(rule, "process_name", Path.GetFileName(path));
            bool matchesPath = rule.TryGetProperty("process_path_regex", out JsonElement patterns)
                && patterns.EnumerateArray().Any(pattern => Regex.IsMatch(path, pattern.GetString()!,
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            if (matchesName || matchesPath)
            {
                Assert.False(rule.TryGetProperty("process_name", out _) && rule.TryGetProperty("process_path_regex", out _));
                return rule.GetProperty("action").GetString() == "reject" ? "reject" : rule.GetProperty("outbound").GetString()!;
            }
        }
        return root.GetProperty("route").GetProperty("final").GetString()!;
    }

    [Fact]
    public void Api_port_cannot_become_an_upstream_even_when_that_proxy_is_offline()
    {
        EgressProfileCompileInput input = Input(new EgressProfileDocument { UpstreamPorts = [7890, 19090] });
        var exception = Assert.Throws<EgressProfileCompilationException>(() => new EgressProfileCompiler().Compile(input));
        Assert.Equal("controller.port.conflict", exception.Code);
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
            Profile = profile.DefaultAdapterId is not null ? profile : profile with
            {
                Adapters = [new() { Id = "22222222-2222-2222-2222-222222222222", Name = "Redmi" }],
                DefaultAdapterId = "22222222-2222-2222-2222-222222222222",
                ProxyAdapterId = "11111111-1111-1111-1111-111111111111",
            },
            Environment = environment ?? EnvironmentSnapshot(),
            ApplicationRoutes = [new(applicationPaths ?? [], EgressRouteTarget.DefaultAdapter)],
            UpstreamOwnerPaths = ownerPaths ?? new[] { @"C:\Apps\Mihomo\mihomo.exe" },
            SelfExecutablePaths = selfPaths ?? Array.Empty<string>(),
            RuleSets = ruleSets ?? Array.Empty<SingBoxRuleSetInput>(),
            ControllerPort = 19090,
            ControllerSecret = "0123456789abcdef0123456789abcdef",
        };

    private static NetworkEnvironmentSnapshot EnvironmentSnapshot(
        bool hasPrimaryAddress = true,
        bool dnsReady = true)
    {
        Guid primaryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid esimId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        return new NetworkEnvironmentSnapshot
        {
            ProxyAdapter = Adapter(primaryId, "Ethernet", hasPrimaryAddress ? "192.0.2.10" : null),
            DefaultAdapter = Adapter(esimId, "Cellular", dnsReady ? "198.51.100.10" : null, isUp: dnsReady),
            DnsAdapter = Adapter(esimId, "Cellular", dnsReady ? "198.51.100.10" : null, isUp: dnsReady),
        };
    }

    private static AdapterSelection Adapter(Guid id, string alias, string? ipv4, bool isUp = true)
        => new()
        {
            AdapterId = id,
            Alias = alias,
            Luid = 1,
            IfIndex = 10,
            Ipv6IfIndex = 10,
            IsUp = isUp,
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
