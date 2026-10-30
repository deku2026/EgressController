using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgressController.Policy;

namespace EgressController.Runtime;

public sealed record MetaRuleCatalogEntry(string Id, string Name, RuleDataKind Kind, Uri Url, string GitBlobSha, int Size);
public sealed record MetaRuleCatalog(string Commit, IReadOnlyList<MetaRuleCatalogEntry> Entries);
public sealed record PreparedRuleResources(IReadOnlyList<RuleSourceBinding> Bindings, IReadOnlyDictionary<string, byte[]> Assets,
    IReadOnlyDictionary<string, GeoRuleSet> Sources, string ChinaTag = "cn", string ChinaGeoSiteFile = "geosite.dat", string ChinaGeoIpFile = "geoip.dat");

/// <summary>Production resource operations share one immutable non-home snapshot per operation, never caller policy from Clash.</summary>
public sealed class ProductionResources(IResourceTransport transport, IValidatedResourceCache cache,
    Func<CancellationToken, ValueTask<InterfaceBinding>> bindingProvider)
{
    public static Uri MetaCommitUrl { get; } = new("https://api.github.com/repos/MetaCubeX/meta-rules-dat/commits/meta");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ClashImportResult> FetchSubscriptionAsync(SubscriptionSource source,
        PreparationCachePolicy policy = PreparationCachePolicy.CacheFirst, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Id) || !SafeHttps(source.Url, out var url)) throw new ArgumentException("Subscriptions require a stable identity and HTTPS URL.");
        var binding = await bindingProvider(cancellationToken).ConfigureAwait(false);
        var prepare = new ResourcePreparation(transport, cache);
        var body = await prepare.PrepareAsync(Descriptor("subscription-" + source.Id, url!, binding, policy, 8 * 1024 * 1024),
            bytes => ValidSubscription(bytes, source.Id), cancellationToken).ConfigureAwait(false);
        string yaml = StrictUtf8.GetString(body);
        var first = ClashSubscriptionImporter.Import(yaml, source.Id);
        if (first.Providers.Count > 64) throw new InvalidDataException("Subscription provider count exceeds the limit.");
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in first.Providers)
        {
            if (provider.Diagnostics.Any(diagnostic => diagnostic.Code != "provider.pending" && diagnostic.Severity == DiagnosticSeverity.Error) || !SafeHttps(provider.Url, out var providerUrl)) continue;
            byte[] payload = await prepare.PrepareAsync(Descriptor("provider-" + provider.Id, providerUrl!, binding, policy, 8 * 1024 * 1024),
                bytes => ValidSubscription(bytes, source.Id), cancellationToken).ConfigureAwait(false);
            payloads.Add(provider.Id, StrictUtf8.GetString(payload));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var imported = ClashSubscriptionImporter.Import(yaml, source.Id, payloads);
        foreach (var provider in imported.Providers.Where(item => !SafeHttps(item.Url, out _)))
        {
            var diagnostic = new PolicyDiagnostic("provider.https-required", "生产节点来源必须使用 HTTPS；未通过其他出口或协议重试。", ObjectId: provider.Id);
            provider.Diagnostics.Add(diagnostic);
            foreach (var group in imported.Groups.Where(group => group.ProviderIds.Contains(provider.Id))) group.Diagnostics.Add(diagnostic);
        }
        return imported with { Diagnostics = imported.Diagnostics.Concat(imported.Providers.SelectMany(item => item.Diagnostics)).Distinct().ToArray() };
    }

    public async Task<MetaRuleCatalog> LoadRuleCatalogAsync(PreparationCachePolicy policy = PreparationCachePolicy.CacheFirst,
        CancellationToken cancellationToken = default)
    {
        var binding = await bindingProvider(cancellationToken).ConfigureAwait(false);
        return await CatalogAsync(binding, policy, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreparedRuleResources> PrepareRulesAsync(IEnumerable<string> requestedSources,
        PreparationCachePolicy policy = PreparationCachePolicy.CacheFirst, CancellationToken cancellationToken = default)
    {
        var requested = requestedSources.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length > 256) throw new ArgumentException("Too many rule sources.");
        var binding = await bindingProvider(cancellationToken).ConfigureAwait(false);
        var catalog = await CatalogAsync(binding, policy, cancellationToken).ConfigureAwait(false);
        var entries = catalog.Entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var sources = new Dictionary<string, GeoRuleSet>(StringComparer.Ordinal); var sets = new List<GeoRuleSet>();
        var bindings = new List<RuleSourceBinding>(); var prepare = new ResourcePreparation(transport, cache);
        var china = new GeoRuleSet { Tag = "cn" };
        foreach (string source in requested.Concat(["geosite/cn.yaml", "geoip/cn.yaml"]).Distinct(StringComparer.Ordinal))
        {
            string id = CatalogId(source);
            if (!entries.TryGetValue(id, out var entry)) throw new InvalidDataException("Requested rule source is not in the verified Meta catalog.");
            bool isChina = id is "geosite:cn" or "geoip:cn";
            string tag = isChina ? "cn" : "src-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..24];
            byte[] bytes = await prepare.PrepareAsync(Descriptor("meta-raw-" + id, entry.Url, binding, policy, 8 * 1024 * 1024),
                value => VerifyGitBlob(value, entry.GitBlobSha) && ParseValid(value, tag, entry.Kind), cancellationToken).ConfigureAwait(false);
            var parsed = GeoRuleCompiler.Parse(StrictUtf8.GetString(bytes), tag, entry.Kind);
            sources.Add(source, parsed);
            if (isChina) { china.Domains.AddRange(parsed.Domains); china.IpCidrs.AddRange(parsed.IpCidrs); }
            else sets.Add(parsed);
            if (requested.Contains(source, StringComparer.Ordinal)) bindings.Add(new(source, tag, parsed.Domains.Count > 0 ? "geosite.dat" : null, parsed.IpCidrs.Count > 0 ? "geoip.dat" : null));
        }
        sets.Add(china);
        cancellationToken.ThrowIfCancellationRequested(); var bundle = GeoRuleCompiler.Compile(sets);
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["geosite.dat"] = bundle.GeoSite, ["geoip.dat"] = bundle.GeoIp };
        if (!GeoDatInspector.HasTag(bundle.GeoSite, "cn", RuleDataKind.Domain) || !GeoDatInspector.HasTag(bundle.GeoIp, "cn", RuleDataKind.Ip))
            throw new InvalidDataException("Prepared China defaults failed DAT validation.");
        return new(bindings, assets, sources);
    }

    private async Task<MetaRuleCatalog> CatalogAsync(InterfaceBinding binding, PreparationCachePolicy policy, CancellationToken token)
    {
        var prepare = new ResourcePreparation(transport, cache);
        var commitBytes = await prepare.PrepareAsync(Descriptor("meta-commit", MetaCommitUrl, binding, policy, 1024 * 1024),
            bytes => { try { _ = Commit(bytes); return true; } catch (Exception e) when (e is JsonException or InvalidDataException) { return false; } }, token).ConfigureAwait(false);
        string commit = Commit(commitBytes); var entries = new List<MetaRuleCatalogEntry>();
        foreach (string category in new[] { "geosite", "geoip" })
        {
            Uri indexUrl = new($"https://api.github.com/repos/MetaCubeX/meta-rules-dat/git/trees/{commit}:geo/{category}?recursive=1");
            byte[] tree = await prepare.PrepareAsync(Descriptor("meta-index-" + category, indexUrl, binding, policy, 8 * 1024 * 1024),
                bytes => { try { _ = ParseTree(bytes, category, commit); return true; } catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException) { return false; } }, token).ConfigureAwait(false);
            entries.AddRange(ParseTree(tree, category, commit));
        }
        if (entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count) throw new InvalidDataException("Meta catalog has duplicate identities.");
        return new(commit, entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray());
    }
    private static string Commit(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes); if (!json.RootElement.TryGetProperty("sha", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } sha || !ValidGitSha(sha)) throw new InvalidDataException("Meta commit metadata is invalid."); return sha;
    }
    public static IReadOnlyList<MetaRuleCatalogEntry> ParseTree(byte[] bytes, string category, string commit)
    {
        if (bytes.Length > 8 * 1024 * 1024 || category is not ("geosite" or "geoip") || !ValidGitSha(commit)) throw new InvalidDataException("Invalid Meta index request.");
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
        if (!root.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False || !root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array || tree.GetArrayLength() > 30000) throw new InvalidDataException("Meta index is incomplete or invalid.");
        var entries = new List<MetaRuleCatalogEntry>();
        foreach (var item in tree.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "blob") continue;
            string path = item.GetProperty("path").GetString() ?? "";
            // Classical domain files preserve full/suffix/keyword/regexp. IP files are raw CIDR payloads.
            string prefix = category == "geosite" ? "classical/" : "";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".yaml", StringComparison.Ordinal)) continue;
            string name = path[prefix.Length..^5];
            if (name.Length is < 1 or > 180 || name is "." or ".." || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '@' or '!' or '+')) continue;
            string sha = item.GetProperty("sha").GetString() ?? ""; int size = item.GetProperty("size").GetInt32();
            if (!ValidGitSha(sha) || size is < 1 or > 8 * 1024 * 1024 || item.GetProperty("mode").GetString() != "100644") throw new InvalidDataException("Meta source metadata is invalid.");
            Uri url = new($"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/{commit}/geo/{category}/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
            entries.Add(new(category + ":" + name, name, category == "geosite" ? RuleDataKind.Classical : RuleDataKind.Ip, url, sha, size));
        }
        if (entries.Count == 0) throw new InvalidDataException("Meta source index is empty.");
        return entries;
    }
    public static bool VerifyGitBlob(byte[] bytes, string expectedSha)
    {
        if (!ValidGitSha(expectedSha)) return false;
        // Git's public tree identifies raw blobs with SHA1("blob " + length + NUL + content).
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0")); hash.AppendData(bytes);
        return string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedSha, StringComparison.OrdinalIgnoreCase);
    }
    private static bool ParseValid(byte[] bytes, string tag, RuleDataKind kind)
    { try { return GeoRuleCompiler.Parse(StrictUtf8.GetString(bytes), tag, kind).IsSuccess; } catch (DecoderFallbackException) { return false; } }
    private static bool ValidSubscription(byte[] bytes, string sourceId)
    { try { return ClashSubscriptionImporter.Import(StrictUtf8.GetString(bytes), sourceId).IsSuccess; } catch (DecoderFallbackException) { return false; } }
    private static bool ValidGitSha(string sha) => sha.Length == 40 && sha.All(char.IsAsciiHexDigit);
    private static string CatalogId(string source)
    {
        string[] parts = source.Split('/');
        if (parts.Length != 2 || parts[0] is not ("geosite" or "geoip") || !parts[1].EndsWith(".yaml", StringComparison.Ordinal) || parts[1].Length <= 5)
            throw new InvalidDataException("Rule sources require a geosite/name.yaml or geoip/name.yaml catalog path.");
        string name = parts[1][..^5];
        if (name is "." or ".." || name.Length > 180 || !name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '@' or '!' or '+'))
            throw new InvalidDataException("Rule source path is invalid.");
        return parts[0] + ":" + name;
    }
    private static bool SafeHttps(string text, out Uri? url) => Uri.TryCreate(text, UriKind.Absolute, out url) && url.Scheme == "https" && url.UserInfo.Length == 0 && url.Fragment.Length == 0;
    private static ResourceDescriptor Descriptor(string id, Uri url, InterfaceBinding binding, PreparationCachePolicy policy, int maximum) => new()
    { Id = id, Url = url, Interface = binding, CachePolicy = policy, MaximumBytes = maximum, Timeout = TimeSpan.FromSeconds(60) };
}
