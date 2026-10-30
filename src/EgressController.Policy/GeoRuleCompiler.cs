using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Google.Protobuf;
using YamlDotNet.RepresentationModel;

namespace EgressController.Policy;

public enum RuleDataKind { Domain, Ip, Classical }
public sealed class GeoRuleSet
{
    public string Tag { get; set; } = "";
    public List<DomainRule> Domains { get; set; } = [];
    public List<string> IpCidrs { get; set; } = [];
    public List<PolicyDiagnostic> Diagnostics { get; set; } = [];
    public bool IsSuccess => !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);
}
public sealed record GeoDatBundle(byte[] GeoSite, byte[] GeoIp, string Digest, int DomainCount, int CidrCount);
public sealed record GeoPublishResult(string GenerationDirectory, string Digest);

/// <summary>Meta raw YAML to Xray's public protobuf wire schema; never reads MRS/SRS.</summary>
public static class GeoRuleCompiler
{
    public static GeoRuleSet Parse(string yaml, string tag, RuleDataKind kind)
    {
        var result = new GeoRuleSet { Tag = tag };
        if (!ValidTag(tag)) { result.Diagnostics.Add(new("geodata.tag", "规则标签只能包含字母、数字、点、下划线和短横线。")); return result; }
        try
        {
            var root = YamlNodes.Load(yaml);
            if (root.Get("payload") is not YamlSequenceNode payload) { result.Diagnostics.Add(new("geodata.payload", "规则 YAML 必须包含 payload 数组。")); return result; }
            foreach (var item in payload.Children)
            {
                if (item is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value)) { result.Diagnostics.Add(new("geodata.item", "规则集合包含非标量或空条目。")); continue; }
                var text = scalar.Value.Trim();
                if (kind == RuleDataKind.Ip) { AddIp(result, text); continue; }
                if (kind == RuleDataKind.Classical)
                {
                    var fields = text.Split(',');
                    if (fields.Length is < 2 or > 3 || fields.Length == 3 && fields[2] != "no-resolve") { result.Diagnostics.Add(new("geodata.classical", "该 classical 规则无法等价转换。")); continue; }
                    if (fields.Length == 3) { result.Diagnostics.Add(new("geodata.no-resolve", "集合 DAT 无法保留单条 no-resolve 行为，未静默移除该语义。")); continue; }
                    switch (fields[0])
                    {
                        case "DOMAIN": AddDomain(result, DomainMatchKind.Full, fields[1]); break;
                        case "DOMAIN-SUFFIX": AddDomain(result, DomainMatchKind.Suffix, fields[1]); break;
                        case "DOMAIN-KEYWORD": AddDomain(result, DomainMatchKind.Keyword, fields[1]); break;
                        case "DOMAIN-REGEX": AddDomain(result, DomainMatchKind.Regex, fields[1]); break;
                        case "IP-CIDR": case "IP-CIDR6": case "IP6-CIDR": AddIp(result, fields[1]); break;
                        default: result.Diagnostics.Add(new("geodata.unsupported", "包含不属于域名/IP 集合的 classical 条件；未忽略该规则。")); break;
                    }
                    continue;
                }
                if (text.StartsWith("+.", StringComparison.Ordinal)) AddDomain(result, DomainMatchKind.Suffix, text[2..]);
                else if (text.StartsWith(".", StringComparison.Ordinal) && Uri.CheckHostName(text[1..]) != UriHostNameType.Unknown) AddDomain(result, DomainMatchKind.Regex, "^.+\\." + Regex.Escape(text[1..].ToLowerInvariant()) + "$");
                else if (text.Contains('*'))
                {
                    var labels = text.Split('.');
                    if (labels.Any(x => x != "*" && (x.Contains('*') || x.Length == 0 || Uri.CheckHostName(x) == UriHostNameType.Unknown))) result.Diagnostics.Add(new("geodata.wildcard", "该域名通配符无法保证等价匹配，未改写为后缀。"));
                    else AddDomain(result, DomainMatchKind.Regex, "^" + string.Join("\\.", labels.Select(x => x == "*" ? "[^.]+" : Regex.Escape(x.ToLowerInvariant()))) + "$");
                }
                else if (text.Contains('?') || text.Contains('+')) result.Diagnostics.Add(new("geodata.wildcard", "该域名通配符无法保证等价匹配，未改写为后缀。"));
                else AddDomain(result, DomainMatchKind.Full, text);
            }
            result.Domains = result.Domains.Distinct().ToList(); result.IpCidrs = result.IpCidrs.Distinct(StringComparer.Ordinal).ToList();
            if (result.Domains.Count == 0 && result.IpCidrs.Count == 0) result.Diagnostics.Add(new("geodata.empty", "规则集合没有可执行条目。"));
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or FormatException or ArgumentException or InvalidOperationException)
        { result.Diagnostics.Add(new("geodata.syntax", "规则 YAML 无效或超出安全限制。")); }
        return result;
    }

    public static GeoDatBundle Compile(IEnumerable<GeoRuleSet> input)
    {
        var sets = input.ToList();
        if (sets.Count == 0 || sets.Any(x => !x.IsSuccess || !ValidTag(x.Tag) || x.Domains.Any(domain => !DomainRuleValidation.IsValid(domain)) || x.IpCidrs.Any(cidr => !PolicyValidator.IsCidr(cidr))) || sets.Select(x => x.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sets.Count) throw new InvalidOperationException("Geodata requires unique valid tags and validated rule values.");
        var sites = Encode(writer =>
        {
            foreach (var set in sets.Where(x => x.Domains.Count > 0).OrderBy(x => x.Tag, StringComparer.Ordinal))
                Message(writer, 1, entry =>
                {
                    // Xray uppercases ext tags and its streaming loader compares the code bytes exactly.
                    Text(entry, 1, set.Tag.ToUpperInvariant());
                    foreach (var domain in set.Domains.Distinct().OrderBy(x => x.Kind).ThenBy(x => x.Value, StringComparer.Ordinal))
                        Message(entry, 2, value => { value.WriteTag(1, WireFormat.WireType.Varint); value.WriteEnum(domain.Kind switch { DomainMatchKind.Keyword => 0, DomainMatchKind.Regex => 1, DomainMatchKind.Suffix => 2, _ => 3 }); Text(value, 2, DomainRuleValidation.CompiledValue(domain)); });
                });
        });
        var ips = Encode(writer =>
        {
            foreach (var set in sets.Where(x => x.IpCidrs.Count > 0).OrderBy(x => x.Tag, StringComparer.Ordinal))
                Message(writer, 1, entry =>
                {
                    Text(entry, 1, set.Tag.ToUpperInvariant());
                    foreach (var text in set.IpCidrs.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        var (ip, prefix) = ParseCidr(text);
                        Message(entry, 2, value => { value.WriteTag(1, WireFormat.WireType.LengthDelimited); value.WriteBytes(ByteString.CopyFrom(ip)); value.WriteTag(2, WireFormat.WireType.Varint); value.WriteUInt32((uint)prefix); });
                    }
                });
        });
        var joined = new byte[sites.Length + ips.Length]; sites.CopyTo(joined, 0); ips.CopyTo(joined, sites.Length);
        return new(sites, ips, Convert.ToHexStringLower(SHA256.HashData(joined)), sets.Sum(x => x.Domains.Distinct().Count()), sets.Sum(x => x.IpCidrs.Distinct(StringComparer.Ordinal).Count()));
    }

    /// <summary>Commits both files by a single durable generation-pointer replacement. Old generations remain available.</summary>
    public static async Task<GeoPublishResult> PublishAsync(string root, GeoDatBundle bundle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root) || !ValidTag(bundle.Digest) || bundle.Digest.Length != 64) throw new ArgumentException("Publication requires an absolute directory and validated digest.");
        var joined = new byte[bundle.GeoSite.Length + bundle.GeoIp.Length]; bundle.GeoSite.CopyTo(joined, 0); bundle.GeoIp.CopyTo(joined, bundle.GeoSite.Length);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(joined)), bundle.Digest, StringComparison.Ordinal)) throw new InvalidDataException("Geodata bundle digest mismatch.");
        Directory.CreateDirectory(root);
        if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("Rule directory cannot be a symbolic link.");
        GeoDirectorySync.Flush(root);
        if (Path.GetDirectoryName(root) is { } parent) GeoDirectorySync.Flush(parent);
        var generation = Path.Combine(root, bundle.Digest); var stage = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
        var pointerStage = Path.Combine(root, ".current-" + Guid.NewGuid().ToString("N"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(generation))
            {
                Directory.CreateDirectory(stage);
                await DurableWrite(Path.Combine(stage, "geosite.dat"), bundle.GeoSite, cancellationToken);
                await DurableWrite(Path.Combine(stage, "geoip.dat"), bundle.GeoIp, cancellationToken);
                await Verify(stage, bundle, cancellationToken);
                GeoDirectorySync.Flush(stage);
                cancellationToken.ThrowIfCancellationRequested(); Directory.Move(stage, generation);
                GeoDirectorySync.Flush(root);
            }
            else await Verify(generation, bundle, cancellationToken);
            await DurableWrite(pointerStage, Encoding.ASCII.GetBytes(bundle.Digest + "\n"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pointerStage, Path.Combine(root, "CURRENT"), true);
            GeoDirectorySync.Flush(root);
            return new(generation, bundle.Digest);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (File.Exists(pointerStage)) File.Delete(pointerStage);
        }
    }
    private static async Task DurableWrite(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); stream.Flush(true);
    }
    private static async Task Verify(string directory, GeoDatBundle bundle, CancellationToken token)
    {
        if (new DirectoryInfo(directory).LinkTarget is not null) throw new IOException("Rule generation cannot be a symbolic link.");
        var site = Path.Combine(directory, "geosite.dat"); var ip = Path.Combine(directory, "geoip.dat");
        if (new FileInfo(site).LinkTarget is not null || new FileInfo(ip).LinkTarget is not null || !(await File.ReadAllBytesAsync(site, token)).AsSpan().SequenceEqual(bundle.GeoSite) || !(await File.ReadAllBytesAsync(ip, token)).AsSpan().SequenceEqual(bundle.GeoIp)) throw new InvalidDataException("Rule generation failed integrity validation.");
    }
    private static byte[] Encode(Action<CodedOutputStream> action)
    {
        using var stream = new MemoryStream(); using (var writer = new CodedOutputStream(stream, true)) { action(writer); writer.Flush(); }
        return stream.ToArray();
    }
    private static void Message(CodedOutputStream writer, int field, Action<CodedOutputStream> action) { writer.WriteTag(field, WireFormat.WireType.LengthDelimited); writer.WriteBytes(ByteString.CopyFrom(Encode(action))); }
    private static void Text(CodedOutputStream writer, int field, string text) { writer.WriteTag(field, WireFormat.WireType.LengthDelimited); writer.WriteString(text); }
    private static void AddDomain(GeoRuleSet result, DomainMatchKind kind, string text)
    {
        var rule = new DomainRule(kind, text);
        if (!DomainRuleValidation.IsValid(rule)) result.Diagnostics.Add(new("geodata.domain", "域名规则无效；不能将 URL、路径或无效通配符当作域名。"));
        else result.Domains.Add(new(kind, DomainRuleValidation.CompiledValue(rule)));
    }
    private static void AddIp(GeoRuleSet result, string value) { if (!PolicyValidator.IsCidr(value)) result.Diagnostics.Add(new("geodata.cidr", "IP/CIDR 条目无效。")); else { var (ip, prefix) = ParseCidr(value); result.IpCidrs.Add(new IPAddress(ip) + "/" + prefix); } }
    private static (byte[] Ip, int Prefix) ParseCidr(string value)
    {
        var parts = value.Split('/'); var ip = IPAddress.Parse(parts[0]).GetAddressBytes(); var prefix = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : ip.Length * 8;
        for (var bit = prefix; bit < ip.Length * 8; bit++) ip[bit / 8] &= (byte)~(1 << (7 - bit % 8));
        return (ip, prefix);
    }
    // The pinned core's streaming seek requires a single-byte code-length prefix.
    private static bool ValidTag(string tag) => tag.Length is > 0 and <= 127 && tag.All(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '-' or '.');
}

internal static partial class GeoDirectorySync
{
    internal static void Flush(string directory)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        var descriptor = Open(directory, 0);
        if (descriptor < 0) throw new IOException("Unable to open the rule publication directory for synchronization.");
        try { if (Fsync(descriptor) != 0) throw new IOException("Unable to synchronize the rule publication directory."); }
        finally { _ = Close(descriptor); }
    }
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)] private static partial int Fsync(int descriptor);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int descriptor);
}
