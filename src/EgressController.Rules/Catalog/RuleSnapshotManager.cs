using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Rules.Parsing;

namespace EgressController.Rules.Catalog;

public sealed record MigrationResult(
    bool Succeeded,
    ActiveRuleSnapshot? Activated,
    string? Error,
    string? FailedName,
    IReadOnlyDictionary<string, byte[]> DownloadedBodies,
    IReadOnlyDictionary<string, IReadOnlyList<CompiledDomainRule>> RuleSets)
{
    public static MigrationResult Ok(
        ActiveRuleSnapshot s,
        IReadOnlyDictionary<string, byte[]> bodies,
        IReadOnlyDictionary<string, IReadOnlyList<CompiledDomainRule>> ruleSets)
        => new(true, s, null, null, bodies, ruleSets);

    public static MigrationResult Failed(string name, string error)
        => new(
            false,
            null,
            error,
            name,
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<CompiledDomainRule>>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Owns the active rule snapshot and the available catalog, and performs atomic all-or-nothing
/// activation (plan §Step 07). Routing consumes only one commit; a migration that downloads every
/// selected rule set and weaves whichever <b>cannot</b> be strictly parsed or fetched is rejected —
/// the previous active commits must not be replaced by a mixed-ShA mix.
///
/// <para>All network goes through Core.IRemoteFetcher (implemented by Transport), which never
/// honors the Windows System Proxy — safe for the control plane (no recursion into :18080).</para>
/// </summary>
public sealed class RuleSnapshotManager
{
    // The generated corpus contains valid lists larger than 256 KiB (for example cn.list), so
    // the cap must protect memory without rejecting normal upstream output.
    public const int MaxRuleBytes = 8 * 1024 * 1024;

    private readonly IRemoteFetcher _fetcher;
    private readonly object _gate = new();
    private volatile ActiveRuleSnapshot _active = ActiveRuleSnapshot.Empty;
    private volatile RuleCatalog? _available;

    public RuleSnapshotManager(IRemoteFetcher fetcher)
        => _fetcher = fetcher;

    public ActiveRuleSnapshot Active => _active;

    public RuleCatalog? Available => _available;

    public void SetAvailableCatalog(RuleCatalog catalog)
        => _available = catalog;

    /// <summary>Download + strict-validate <b>every</b> selected rule from the target commit; publish atomically.</summary>
    public async Task<MigrationResult> ActivateAsync(IReadOnlyList<string> selectedNames, RuleCatalog target, CancellationToken ct = default)
    {
        if (target.Snapshot.Entries.Count == 0)
            return MigrationResult.Failed("*", "empty target catalog");

        var fetched = new List<(string Name, IReadOnlyList<CompiledDomainRule> Rules, byte[] Body)>(selectedNames.Count);
        foreach (string name in selectedNames)
        {
            (bool ok, IReadOnlyList<CompiledDomainRule>? rules, byte[]? body, string? error) =
                await DownloadAndParseAsync(name, target, ct).ConfigureAwait(false);
            if (!ok)
                return MigrationResult.Failed(name, error!);
            fetched.Add((name, rules!, body!));
        }

        var allRules = new List<CompiledDomainRule>();
        var setNames = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, var rules, _) in fetched)
        {
            setNames.Add(name);
            allRules.AddRange(rules);
        }

        var snapshot = new ActiveRuleSnapshot(
            target.Snapshot.CommitSha, target.Snapshot.TreeSha,
            fetched.Select(f => f.Name).ToArray(), allRules, setNames);

        var bodies = fetched.ToDictionary(
            item => item.Name,
            item => item.Body,
            StringComparer.OrdinalIgnoreCase);
        var ruleSets = fetched.ToDictionary(
            item => item.Name,
            item => item.Rules,
            StringComparer.OrdinalIgnoreCase);

        lock (_gate)
            _active = snapshot;

        return MigrationResult.Ok(snapshot, bodies, ruleSets);
    }

    private async Task<(bool Ok, IReadOnlyList<CompiledDomainRule>? Rules, byte[]? Body, string? Error)> DownloadAndParseAsync(
        string name, RuleCatalog target, CancellationToken ct)
    {
        if (!target.TryGet(name, out RuleCatalogEntry? entry) || entry is null)
            return (false, null, null, $"'{name}' not present in target catalog");

        var uri = RuleDownloadUri(target.Snapshot.CommitSha, entry);
        RemoteFetchResult res;
        try
        {
            res = await _fetcher.FetchAsync(uri, MaxRuleBytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, null, null, $"fetch threw: {ex.Message}");
        }

        if (!res.Succeeded)
            return (false, null, null, $"fetch failed (status {(res.StatusCode?.ToString() ?? "n/a")})");

        byte[] body = res.Body ?? Array.Empty<byte>();
        if (body.Length == 0)
            return (false, null, null, "fetch returned an empty rule list");

        if (entry.BlobSha.Length == 40
            && entry.BlobSha.All(Uri.IsHexDigit)
            && !string.Equals(GitBlobSha1(body), entry.BlobSha, StringComparison.OrdinalIgnoreCase))
            return (false, null, null, "downloaded rule content does not match the Git blob SHA");

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException ex)
        {
            return (false, null, null, "rule list is not valid UTF-8: " + ex.Message);
        }
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        if (LooksLikeHtmlOrError(text))
            return (false, null, null, "server returned HTML/error, not a rule list");

        if (!StrictDomainListParser.TryParse(text.Split('\n'), entry.Name, out var rules, out var failure))
            return (false, null, null, $"unsupported syntax @ line {failure!.LineNumber}: '{failure.LineText}'");

        return (true, rules!, body, null);
    }

    private static bool LooksLikeHtmlOrError(string body)
    {
        string t = body.TrimStart();
        return t.StartsWith('<') || t.StartsWith("404", StringComparison.Ordinal) || t.StartsWith("Not Found", StringComparison.Ordinal);
    }

    /// <summary>Commit-pinned download URL (plan §1.1: never a floating branch URL).</summary>
    public static Uri RuleDownloadUri(string commitSha, string ruleName)
        => new($"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/{commitSha}/geo/geosite/{ruleName}.list");

    public static Uri RuleDownloadUri(string commitSha, RuleCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string path = entry.Path.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Rule catalog path is invalid.", nameof(entry));
        return new Uri($"https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/{commitSha}/{path}");
    }

    private static string GitBlobSha1(byte[] body)
    {
        byte[] header = Encoding.ASCII.GetBytes($"blob {body.Length}\0");
        using var sha1 = SHA1.Create();
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        sha1.TransformFinalBlock(body, 0, body.Length);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }
}
