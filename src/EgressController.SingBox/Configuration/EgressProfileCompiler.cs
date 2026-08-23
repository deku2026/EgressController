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
    public const string DnsTag = "dns-clash";
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

        string[] applications = NormalizePaths(input.ApplicationExecutablePaths, "application.path");
        var selectedRuleSets = NormalizeRuleSets(profile, input.RuleSets);
        ValidateEnvironment(input.Environment);
        ValidateControllerEndpoint(input.ControllerPort, input.ControllerSecret);
        string tunName = NormalizeTunName(input.TunInterfaceName);

        var rules = new List<SingBoxRouteRuleDocument>
        {
            new() { Action = "sniff" },
            new() { Protocol = "dns", Action = "hijack-dns" },
            new() { ProcessName = NormalizeProcessNames(owners), Action = "route", Outbound = PrimaryDirectTag },
        };
        if (applications.Length > 0)
        {
            rules.Add(new SingBoxRouteRuleDocument
            {
                // sing-box compares process_path with a case-sensitive Go map, even on Windows.
                // QueryFullProcessImageName may return different casing than package inventory,
                // so preserve exact-path ownership while making only casing insignificant.
                ProcessPathRegex = CreateWindowsExactPathRegexes(applications),
                Action = "route",
                Outbound = EsimDirectTag,
            });
        }
        if (selectedRuleSets.Count > 0)
        {
            rules.Add(new SingBoxRouteRuleDocument
            {
                RuleSet = selectedRuleSets.Select(item => item.Name).ToArray(),
                Action = "route",
                Outbound = EsimDirectTag,
            });
        }
        if (profile.EsimDomains.Count > 0)
        {
            rules.Add(new SingBoxRouteRuleDocument
            {
                DomainSuffix = profile.EsimDomains,
                Action = "route",
                Outbound = EsimDirectTag,
            });
        }

        var document = new SingBoxConfigDocument
        {
            Log = new SingBoxLogDocument { Output = NormalizeOptionalPath(input.LogPath) },
            Dns = new SingBoxDnsDocument
            {
                Servers = new[]
                {
                    new SingBoxHttpsDnsServerDocument
                    {
                        Tag = DnsTag,
                        Server = "1.1.1.1",
                        ServerPort = 443,
                        Path = "/dns-query",
                        Tls = new SingBoxTlsDocument { ServerName = "cloudflare-dns.com" },
                        Detour = UpstreamSocksTag,
                    },
                },
                Final = DnsTag,
                Strategy = "prefer_ipv4",
            },
            Inbounds = new[]
            {
                new SingBoxTunInboundDocument
                {
                    Tag = TunTag,
                    InterfaceName = tunName,
                    Address = new[] { "172.19.0.1/30" },
                    AutoRoute = true,
                    StrictRoute = true,
                    Stack = "system",
                },
            },
            Outbounds = new SingBoxOutboundDocument[]
            {
                CreateDirect(EsimDirectTag, input.Environment.Esim),
                CreateDirect(PrimaryDirectTag, input.Environment.Primary),
                new SingBoxOutboundDocument
                {
                    Type = "socks",
                    Tag = UpstreamSocksTag,
                    Server = ControllerHost,
                    ServerPort = profile.UpstreamPort,
                    Version = "5",
                },
            },
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
        };

    private static string[] NormalizeProcessNames(IEnumerable<string> paths)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
                throw Failure("upstream.owner.name", "上游 SOCKS5 owner executable name 为空。");
            names.Add(fileName);
            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (!string.IsNullOrWhiteSpace(withoutExtension))
                names.Add(withoutExtension);
        }
        return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] CreateWindowsExactPathRegexes(IEnumerable<string> paths)
        => paths.Select(path => "(?i)^" + EscapeGoRegularExpression(path) + "$").ToArray();

    private static string EscapeGoRegularExpression(string value)
    {
        const string metacharacters = @"\.+*?()|[]{}^$";
        var escaped = new StringBuilder(value.Length + 16);
        foreach (char character in value)
        {
            if (metacharacters.Contains(character, StringComparison.Ordinal))
                escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
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
        ValidateAdapter(environment.Primary, "primary");
        ValidateAdapter(environment.Esim, "esim");
        if (environment.Primary.AdapterId == Guid.Empty || environment.Esim.AdapterId == Guid.Empty)
            throw Failure("adapter.id", "网卡稳定 ID 为空。");
        if (environment.Primary.AdapterId == environment.Esim.AdapterId)
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

    private static void ValidateAdapter(AdapterSelection adapter, string label)
    {
        if (!adapter.IsUp)
            throw Failure($"adapter.{label}", $"{label} 网卡未连接。");
        if (!adapter.HasIpv4 && !adapter.HasIpv6)
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
