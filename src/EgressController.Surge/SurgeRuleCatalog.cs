using System.Text.Json;
using System.Text.Json.Serialization;
using EgressController.Core.Storage;
using EgressController.Transport.Upstream;

namespace EgressController.Surge;

public sealed record SurgeRule(string Id, string Name, string Url);
[JsonSerializable(typeof(SurgeRule[]))]
internal sealed partial class RuleCatalogJson : JsonSerializerContext;
public sealed class SurgeRuleCatalog(IHttpDownloadTransport transport, string cachePath)
{
    public async Task<IReadOnlyList<SurgeRule>> LoadAsync(bool refresh, CancellationToken token)
    {
        if (!refresh)
        {
            try
            {
                var cached = await PrivateFile.ReadJsonAsync(cachePath, RuleCatalogJson.Default.SurgeRuleArray, token).ConfigureAwait(false);
                if (cached is { Length: > 0 } && cached.All(Valid)) return cached;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        var uri = new Uri("https://api.github.com/repos/blackmatrix7/ios_rule_script/git/trees/master?recursive=1");
        return await transport.ExecuteAsync<IReadOnlyList<SurgeRule>>(uri, async (client, ct) =>
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) throw new IOException("规则目录过大。");
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream(); byte[] chunk = new byte[16384];
            int count;
            while ((count = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            { if (buffer.Length + count > 32 * 1024 * 1024) throw new IOException("规则目录过大。"); await buffer.WriteAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false); }
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.GetProperty("truncated").GetBoolean()) throw new IOException("规则源返回不完整目录；保留上次有效目录。");
            var rules = document.RootElement.GetProperty("tree").EnumerateArray()
                .Select(n => n.GetProperty("path").GetString()!).Where(path => path.StartsWith("rule/Surge/", StringComparison.Ordinal) && path.EndsWith(".list", StringComparison.Ordinal) && path.Split('/').Length == 4)
                .Select(path => new SurgeRule(path, Path.GetFileNameWithoutExtension(path), "https://raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/" + path))
                .Where(Valid).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (rules.Length == 0) throw new IOException("规则目录为空；保留上次有效目录。");
            await PrivateFile.WriteJsonAsync(cachePath, rules, RuleCatalogJson.Default.SurgeRuleArray, ct).ConfigureAwait(false);
            return rules;
        }, token).ConfigureAwait(false);
    }
    private static bool Valid(SurgeRule rule) => !string.IsNullOrWhiteSpace(rule.Name) && rule.Id.StartsWith("rule/Surge/", StringComparison.Ordinal) &&
        rule.Url == "https://raw.githubusercontent.com/blackmatrix7/ios_rule_script/master/" + rule.Id && !rule.Id.Contains("..", StringComparison.Ordinal) && !rule.Id.Any(c => char.IsControl(c) || c is ',' or '#' or ';');
}
