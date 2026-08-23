using System.Globalization;
using System.Net;
using System.Text;

namespace EgressController.Core.Profile;

public static class EgressProfileSchema
{
    public const int CurrentVersion = 1;
    public const string ManagedCore = "managed";
    public const string SystemCore = "system";
}

public sealed record EgressCoreSelection
{
    public string Mode { get; init; } = EgressProfileSchema.ManagedCore;
    public string? SystemPath { get; init; }
}

public sealed record EgressApplicationSelection
{
    public required string DiscoveryKey { get; init; }
    public string? ManualExecutablePath { get; init; }
}

/// <summary>
/// The only user-editable network configuration. Runtime process IDs, owner paths, interface
/// indexes, source addresses, API secrets and generated config paths intentionally do not belong
/// in this document.
/// </summary>
public sealed record EgressProfileDocument
{
    public int SchemaVersion { get; init; } = EgressProfileSchema.CurrentVersion;
    public EgressCoreSelection Core { get; init; } = new();
    public int UpstreamPort { get; init; } = 7890;
    public string? PrimaryAdapterId { get; init; }
    public string? EsimAdapterId { get; init; }
    public IReadOnlyList<EgressApplicationSelection> EsimApplications { get; init; } = Array.Empty<EgressApplicationSelection>();
    public IReadOnlyList<string> EsimRuleSets { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EsimDomains { get; init; } = Array.Empty<string>();

    public static EgressProfileDocument Default { get; } = new();

    public EgressProfileDocument NormalizeAndValidate()
    {
        if (SchemaVersion != EgressProfileSchema.CurrentVersion)
        {
            throw new ProfileSchemaException(
                $"不支持的 Profile schemaVersion={SchemaVersion}；需要升级 EgressController 后再打开。",
                SchemaVersion);
        }

        if (UpstreamPort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(UpstreamPort), UpstreamPort, "上游 SOCKS5 端口必须在 1..65535。 ");

        EgressCoreSelection core = NormalizeCore(Core);
        string? primary = NormalizeAdapterId(PrimaryAdapterId, nameof(PrimaryAdapterId));
        string? esim = NormalizeAdapterId(EsimAdapterId, nameof(EsimAdapterId));
        if (primary is not null && esim is not null
            && string.Equals(primary, esim, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("主网卡和 eSIM 网卡不能是同一个接口。", nameof(EsimAdapterId));
        }

        var applications = (EsimApplications ?? Array.Empty<EgressApplicationSelection>())
            .Select(NormalizeApplication)
            .GroupBy(x => x.DiscoveryKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(x => x.ManualExecutablePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(x => x.DiscoveryKey, StringComparer.Ordinal)
            .ToArray();

        string[] ruleSets = NormalizeStringSet(
            EsimRuleSets,
            NormalizeRuleSetName,
            "规则集名称");
        string[] domains = NormalizeStringSet(
            EsimDomains,
            NormalizeDomain,
            "域名");

        return this with
        {
            SchemaVersion = EgressProfileSchema.CurrentVersion,
            Core = core,
            PrimaryAdapterId = primary,
            EsimAdapterId = esim,
            EsimApplications = applications,
            EsimRuleSets = ruleSets,
            EsimDomains = domains,
        };
    }

    public static string NormalizeDomain(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("域名不能为空。", nameof(value));

        string input = value.Trim().TrimEnd('.');
        if (input.Length == 0 || input.Contains('/') || input.Contains('\\') || input.Contains('@')
            || input.Contains(':') || input.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"非法域名：{value}", nameof(value));
        }

        string ascii;
        try
        {
            ascii = new IdnMapping().GetAscii(input);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"非法域名：{value}", nameof(value), ex);
        }

        if (ascii.Length > 253 || IPAddress.TryParse(ascii, out _)
            || Uri.CheckHostName(ascii) != UriHostNameType.Dns)
        {
            throw new ArgumentException($"非法域名：{value}", nameof(value));
        }

        foreach (string label in ascii.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                throw new ArgumentException($"非法域名：{value}", nameof(value));
            }
        }

        return ascii.ToLowerInvariant();
    }

    private static EgressCoreSelection NormalizeCore(EgressCoreSelection? value)
    {
        EgressCoreSelection core = value ?? new EgressCoreSelection();
        string mode = (core.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is not EgressProfileSchema.ManagedCore and not EgressProfileSchema.SystemCore)
            throw new ArgumentException("Core mode 必须是 managed 或 system。", nameof(Core));

        string? systemPath = core.SystemPath;
        if (mode == EgressProfileSchema.SystemCore)
        {
            if (string.IsNullOrWhiteSpace(systemPath) || !Path.IsPathRooted(systemPath))
                throw new ArgumentException("System core 必须是绝对路径。", nameof(Core));
            systemPath = Path.GetFullPath(systemPath.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(systemPath))
        {
            throw new ArgumentException("Managed core 不能携带 systemPath。", nameof(Core));
        }

        return new EgressCoreSelection { Mode = mode, SystemPath = systemPath };
    }

    private static EgressApplicationSelection NormalizeApplication(EgressApplicationSelection? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.DiscoveryKey))
            throw new ArgumentException("应用 DiscoveryKey 不能为空。", nameof(EsimApplications));

        string? path = value.ManualExecutablePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            path = path.Trim();
            if (!Path.IsPathRooted(path))
                throw new ArgumentException("手工 EXE 路径必须是绝对路径。", nameof(EsimApplications));
            path = Path.GetFullPath(path);
        }

        return new EgressApplicationSelection
        {
            DiscoveryKey = value.DiscoveryKey.Trim(),
            ManualExecutablePath = path,
        };
    }

    private static string? NormalizeAdapterId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!Guid.TryParse(value.Trim(), out Guid guid))
            throw new ArgumentException("网卡 ID 必须是稳定 GUID。", parameterName);
        return guid.ToString("D", CultureInfo.InvariantCulture);
    }

    private static string NormalizeRuleSetName(string value)
    {
        string name = value.Trim().Replace('\\', '/').TrimStart('/');
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal)
            || name.Any(char.IsWhiteSpace)
            || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '@' or '!')))
        {
            throw new ArgumentException($"非法规则集名称：{value}", nameof(EsimRuleSets));
        }
        return name.ToLowerInvariant();
    }

    private static string[] NormalizeStringSet(
        IEnumerable<string>? values,
        Func<string, string> normalizer,
        string displayName)
    {
        try
        {
            return (values ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(normalizer)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        catch (ArgumentException ex) when (ex.ParamName is nameof(EsimRuleSets) or nameof(EsimDomains))
        {
            throw new ArgumentException(ex.Message.Replace("规则集名称", displayName, StringComparison.Ordinal)
                .Replace("域名", displayName, StringComparison.Ordinal), ex);
        }
    }
}

public sealed class ProfileSchemaException(string message, int schemaVersion) : InvalidOperationException(message)
{
    public int SchemaVersion { get; } = schemaVersion;
}
