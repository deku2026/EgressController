using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Models;
using EgressController.Core.Profile;

namespace EgressController.SingBox.Configuration;

public sealed record SingBoxRuleSetInput(string Name, string Path);

/// <summary>Runtime-only inputs assembled by AppController after inventory/network resolution.</summary>
public sealed record EgressProfileCompileInput
{
    public required EgressProfileDocument Profile { get; init; }
    public required NetworkEnvironmentSnapshot Environment { get; init; }
    public required IReadOnlyList<string> ApplicationExecutablePaths { get; init; }
    public required IReadOnlyList<string> UpstreamOwnerPaths { get; init; }
    public IReadOnlyList<string> SelfExecutablePaths { get; init; } = Array.Empty<string>();
    public required IReadOnlyList<SingBoxRuleSetInput> RuleSets { get; init; }
    public int ControllerPort { get; init; }
    public string ControllerSecret { get; init; } = string.Empty;
    public string? LogPath { get; init; }
    public string TunInterfaceName { get; init; } = "sing-box";
    public DohRoutingDecision DohRouting { get; init; } = DohRoutingDecision.Default;
}

public sealed record EgressProfileCompilationResult(
    SingBoxConfigDocument Document,
    byte[] JsonBytes,
    string Sha256)
{
    public string JsonText => Encoding.UTF8.GetString(JsonBytes);
}

/// <summary>
/// Pure, deterministic compiler for the product's supported sing-box configuration subset.
/// It owns route order and never infers missing adapter addresses or process owners.
/// </summary>
public sealed class EgressProfileCompiler
{
    public const string TunTag = "tun-in";
    public const string PrimaryDirectTag = "primary-direct";
    public const string EsimDirectTag = "esim-direct";
    public const string UpstreamSocksTag = "clash-7890";
    public const string DnsTag = EgressDohConfiguration.ClashCloudflareTag;
    public const string EsimDnsTag = EgressDohConfiguration.EsimCloudflareTag;
    public const string ControllerHost = "127.0.0.1";

    public EgressProfileCompilationResult Compile(EgressProfileCompileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        EgressProfileDocument profile = input.Profile.NormalizeAndValidate();
        string[] owners = NormalizePaths(input.UpstreamOwnerPaths, "upstream.owner");
        if (owners.Length == 0)
            throw Failure("upstream.owner", "未解析到上游 SOCKS5 owner executable path。");
        string[] self = NormalizePaths(input.SelfExecutablePaths, "self.path");
        if (self.Any(path => owners.Contains(path, StringComparer.OrdinalIgnoreCase)))
            throw Failure("upstream.owner.self", "上游 SOCKS5 owner 是 EgressController/sing-box 自身，拒绝生成配置。");

        string[] applicationPaths = NormalizePaths(input.ApplicationExecutablePaths, "application.path");
        string[] applications = NormalizeProcessNames(applicationPaths);
        var selectedRuleSets = NormalizeRuleSets(profile, input.RuleSets);
        ValidateEnvironment(input.Environment);
        ValidateControllerEndpoint(input.ControllerPort, input.ControllerSecret);
        string tunName = NormalizeTunName(input.TunInterfaceName);
        DohRoutingDecision dohRouting = input.DohRouting ?? throw Failure("doh.routing", "DoH 路由选择为空。");
        ValidateDohRouting(dohRouting, input.Environment.IsEsimReady);

        var rules = new List<SingBoxRouteRuleDocument>
        {
            new() { Action = "sniff" },
            new() { Protocol = "dns", Action = "hijack-dns" },
            new() { IpVersion = 6, Action = "reject" },
            new() { ProcessName = NormalizeProcessNames(owners), Action = "route", Outbound = PrimaryDirectTag },
        };
        if (dohRouting.FailClosed)
        {
            rules.Insert(0, new SingBoxRouteRuleDocument
            {
                Inbound = [TunTag],
                Action = "reject",
            });
        }
        string esimAction = input.Environment.IsEsimReady ? "route" : "reject";
        if (applications.Length > 0)
        {
            rules.Add(new SingBoxRouteRuleDocument
            {
                ProcessName = applications,
                Action = esimAction,
                Outbound = input.Environment.IsEsimReady ? EsimDirectTag : null,
            });
        }
        if (selectedRuleSets.Count > 0)
        {
            rules.Add(new SingBoxRouteRuleDocument
            {
                RuleSet = selectedRuleSets.Select(item => item.Name).ToArray(),
                Action = esimAction,
                Outbound = input.Environment.IsEsimReady ? EsimDirectTag : null,
            });
        }
        if (profile.EsimDomains.Count > 0)
        {
            rules.Add(new SingBoxRouteRuleDocument
            {
                DomainSuffix = profile.EsimDomains,
                Action = esimAction,
                Outbound = input.Environment.IsEsimReady ? EsimDirectTag : null,
            });
        }

        var dnsRules = new List<SingBoxDnsRuleDocument>();
        foreach (SingBoxDohEndpointDefinition endpoint in AvailableDohEndpoints(input.Environment.IsEsimReady))
        {
            dnsRules.Add(new SingBoxDnsRuleDocument
            {
                DomainSuffix = [endpoint.ProbeSuffix],
                Action = "route",
                Server = endpoint.Tag,
            });
        }
        if (applications.Length > 0)
        {
            dnsRules.Add(new SingBoxDnsRuleDocument
            {
                ProcessName = applications,
                Action = input.Environment.IsEsimReady ? "route" : "reject",
                Server = input.Environment.IsEsimReady ? dohRouting.EsimDnsTag : null,
            });
        }
        if (selectedRuleSets.Count > 0)
        {
            dnsRules.Add(new SingBoxDnsRuleDocument
            {
                RuleSet = selectedRuleSets.Select(item => item.Name).ToArray(),
                Action = input.Environment.IsEsimReady ? "route" : "reject",
                Server = input.Environment.IsEsimReady ? dohRouting.EsimDnsTag : null,
            });
        }
        if (profile.EsimDomains.Count > 0)
        {
            dnsRules.Add(new SingBoxDnsRuleDocument
            {
                DomainSuffix = profile.EsimDomains,
                Action = input.Environment.IsEsimReady ? "route" : "reject",
                Server = input.Environment.IsEsimReady ? dohRouting.EsimDnsTag : null,
            });
        }

        var dnsServers = new List<SingBoxHttpsDnsServerDocument>();
        foreach (SingBoxDohEndpointDefinition endpoint in AvailableDohEndpoints(input.Environment.IsEsimReady))
        {
            dnsServers.Add(new SingBoxHttpsDnsServerDocument
            {
                Tag = endpoint.Tag,
                Server = endpoint.Server,
                ServerPort = endpoint.ServerPort,
                Path = endpoint.Path,
                Tls = new SingBoxTlsDocument { ServerName = endpoint.ServerName },
                Detour = endpoint.Detour,
            });
        }

        var outbounds = new List<SingBoxOutboundDocument>();
        if (input.Environment.IsEsimReady)
            outbounds.Add(CreateDirect(EsimDirectTag, input.Environment.Esim));
        outbounds.Add(CreateDirect(PrimaryDirectTag, input.Environment.Primary));
        outbounds.Add(new SingBoxOutboundDocument
        {
            Type = "socks",
            Tag = UpstreamSocksTag,
            Server = ControllerHost,
            ServerPort = profile.UpstreamPort,
            Version = "5",
        });

        var document = new SingBoxConfigDocument
        {
            Log = new SingBoxLogDocument { Output = NormalizeOptionalPath(input.LogPath) },
            Dns = new SingBoxDnsDocument
            {
                Servers = dnsServers,
                Rules = dnsRules.Count == 0 ? null : dnsRules,
                Final = dohRouting.ClashDnsTag,
                Strategy = "ipv4_only",
            },
            Inbounds = new[]
            {
                new SingBoxTunInboundDocument
                {
                    Tag = TunTag,
                    InterfaceName = tunName,
                    Address = new[] { "172.19.0.1/30", "fdfe:dcba:9876::1/126" },
                    AutoRoute = true,
                    StrictRoute = true,
                    Stack = "system",
                },
            },
            Outbounds = outbounds,
            Route = new SingBoxRouteDocument
            {
                Rules = rules,
                RuleSet = selectedRuleSets.Count == 0 ? null : selectedRuleSets.Select(item => new SingBoxRuleSetDocument
                {
                    Tag = item.Name,
                    Path = item.Path,
                    Type = "local",
                    Format = "binary",
                }).ToArray(),
                Final = UpstreamSocksTag,
                DefaultDomainResolver = dohRouting.ClashDnsTag,
                AutoDetectInterface = true,
                FindProcess = true,
            },
            Experimental = new SingBoxExperimentalDocument
            {
                ClashApi = new SingBoxClashApiDocument
                {
                    ExternalController = $"{ControllerHost}:{input.ControllerPort}",
                    Secret = input.ControllerSecret.Trim(),
                },
            },
        };

        byte[] jsonBytes = document.ToJsonBytes();
        string sha256 = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();
        return new EgressProfileCompilationResult(document, jsonBytes, sha256);
    }

    public static string CreateControllerSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static void WriteNext(string path, EgressProfileCompilationResult result)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("config.next path is required", nameof(path));
        ArgumentNullException.ThrowIfNull(result);
        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, result.JsonBytes);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // The previous config remains the last usable candidate.
            }
        }
    }

    private static SingBoxOutboundDocument CreateDirect(string tag, AdapterSelection adapter)
        => new()
        {
            Type = "direct",
            Tag = tag,
            BindInterface = NormalizeRequired(adapter.Alias, "adapter.alias"),
            Inet4BindAddress = adapter.Ipv4BindAddress?.ToString(),
            Inet6BindAddress = adapter.Ipv6BindAddress?.ToString(),
        };

    private static IEnumerable<SingBoxDohEndpointDefinition> AvailableDohEndpoints(bool esimReady)
        => EgressDohConfiguration.Endpoints.Where(endpoint => EgressDohConfiguration.IsAvailable(endpoint, esimReady));

    private static void ValidateDohRouting(DohRoutingDecision routing, bool esimReady)
    {
        if (EgressDohConfiguration.Find(routing.ClashDnsTag) is not { RoutePlane: DohRoutePlane.Clash })
            throw Failure("doh.routing.clash", "7890 DoH 路由选择无效。");
        if (esimReady
            && EgressDohConfiguration.Find(routing.EsimDnsTag) is not { RoutePlane: DohRoutePlane.Esim })
        {
            throw Failure("doh.routing.esim", "eSIM DoH 路由选择无效。");
        }
    }

    private static string[] NormalizeProcessNames(IEnumerable<string> paths)
    {
        // sing-box receives the process name from Windows. Keep the casing variants explicit:
        // process_name matching is owned by sing-box and must not rely on the host filesystem's
        // case-insensitivity. Ordinal de-duplication also makes the generated JSON prove which
        // spellings are accepted instead of silently collapsing them on the C# side.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
                throw Failure("upstream.owner.name", "上游 SOCKS5 owner executable name 为空。");
            AddProcessNameVariants(names, fileName);
            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (!string.IsNullOrWhiteSpace(withoutExtension))
                AddProcessNameVariants(names, withoutExtension);
        }
        return names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddProcessNameVariants(HashSet<string> names, string value)
    {
        names.Add(value);

        string lower = value.ToLowerInvariant();
        names.Add(lower);

        if (value.Length > 0)
        {
            string title = char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
            names.Add(title);
        }
    }

    private static List<SingBoxRuleSetInput> NormalizeRuleSets(
        EgressProfileDocument profile,
        IReadOnlyList<SingBoxRuleSetInput> available)
    {
        var byName = new Dictionary<string, SingBoxRuleSetInput>(StringComparer.Ordinal);
        foreach (SingBoxRuleSetInput item in available ?? Array.Empty<SingBoxRuleSetInput>())
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Path))
                throw Failure("ruleset.path", "SRS 规则集输入不完整。");
            string name = item.Name.Trim().ToLowerInvariant();
            string path = NormalizeRequiredPath(item.Path, "ruleset.path");
            if (!path.EndsWith(".srs", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw Failure("ruleset.missing", $"找不到已验证 SRS：{name}。");
            if (new FileInfo(path).Length == 0)
                throw Failure("ruleset.missing", $"SRS 为空：{name}。");
            if (!byName.TryAdd(name, new SingBoxRuleSetInput(name, path)))
                throw Failure("ruleset.duplicate", $"SRS 规则集重复：{name}。");
        }

        var selected = new List<SingBoxRuleSetInput>(profile.EsimRuleSets.Count);
        foreach (string name in profile.EsimRuleSets)
        {
            if (!byName.TryGetValue(name, out SingBoxRuleSetInput? item))
                throw Failure("ruleset.missing", $"Profile 选择的 SRS 尚未下载：{name}。");
            selected.Add(item);
        }
        return selected;
    }

    private static void ValidateEnvironment(NetworkEnvironmentSnapshot environment)
    {
        if (environment is null)
            throw Failure("adapter.environment", "网络环境为空。");
        ValidateAdapter(environment.Primary, "primary", requireAddress: true);
        if (environment.Esim.AdapterId != Guid.Empty)
            ValidateAdapter(environment.Esim, "esim", requireAddress: false);
        if (environment.Primary.AdapterId == Guid.Empty)
            throw Failure("adapter.id", "网卡稳定 ID 为空。");
        if (environment.Esim.AdapterId != Guid.Empty
            && environment.Primary.AdapterId == environment.Esim.AdapterId)
            throw Failure("adapter.same", "主网卡和 eSIM 网卡不能相同。");
    }

    private static void ValidateControllerEndpoint(int port, string secret)
    {
        if (port is < 1 or > ushort.MaxValue)
            throw Failure("controller.port", "Clash API 端口必须是 1 到 65535。");
        if (string.IsNullOrWhiteSpace(secret))
            throw Failure("controller.secret", "Clash API secret 不能为空。");
        if (secret.Length > 512 || secret.Any(char.IsControl))
            throw Failure("controller.secret", "Clash API secret 长度或字符无效。");
    }

    private static void ValidateAdapter(AdapterSelection adapter, string label, bool requireAddress)
    {
        if (!adapter.IsUp)
        {
            if (requireAddress)
                throw Failure($"adapter.{label}", $"{label} 网卡未连接。");
            return;
        }
        if (requireAddress && !adapter.HasIpv4 && !adapter.HasIpv6)
            throw Failure($"adapter.{label}.address", $"{label} 网卡没有可用 IPv4/IPv6 地址。");
        if (string.IsNullOrWhiteSpace(adapter.Alias))
            throw Failure($"adapter.{label}.alias", $"{label} 网卡没有运行时名称。");
    }

    private static string[] NormalizePaths(IEnumerable<string> values, string code)
    {
        try
        {
            return (values ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (ArgumentException ex)
        {
            throw Failure(code, "包含非法 executable path。", ex);
        }
    }

    private static string NormalizeRequiredPath(string value, string code)
    {
        try
        {
            if (!Path.IsPathRooted(value))
                throw Failure(code, "路径必须是绝对路径。");
            return Path.GetFullPath(value.Trim());
        }
        catch (ArgumentException ex)
        {
            throw Failure(code, "路径非法。", ex);
        }
    }

    private static string? NormalizeOptionalPath(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeRequiredPath(value, "log.path");

    private static string NormalizeRequired(string value, string code)
        => string.IsNullOrWhiteSpace(value) ? throw Failure(code, "值不能为空。") : value.Trim();

    private static string NormalizeTunName(string value)
    {
        string name = NormalizeRequired(value, "tun.name");
        if (name.Length > 64 || name.Any(char.IsControl))
            throw Failure("tun.name", "TUN 名称无效。");
        return name;
    }

    private static EgressProfileCompilationException Failure(string code, string message, Exception? inner = null)
        => new(message, code, inner);
}

public sealed class EgressProfileCompilationException(string message, string code, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public string Code { get; } = code;
}
