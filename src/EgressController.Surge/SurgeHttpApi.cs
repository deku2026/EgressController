using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EgressController.Core.Protection;

namespace EgressController.Surge;

public sealed record SurgeNode(string Id, string Name, string Group, bool Testable);
public sealed record SurgeStatus(bool Connected, string Summary, DateTimeOffset ObservedAt);
public sealed record PolicyTestRequest([property: JsonPropertyName("policy_names")] string[] PolicyNames, [property: JsonPropertyName("url")] string Url);
[JsonSerializable(typeof(PolicyTestRequest))]
internal sealed partial class SurgeApiJson : JsonSerializerContext;

/// <summary>Read/test-only HTTP surface. No reload, switch, kill or feature toggles.</summary>
public sealed class SurgeHttpApi : IDisposable
{
    private readonly HttpClient _client;
    public SurgeHttpApi(Uri endpoint, string key, HttpMessageHandler? handler = null)
    {
        if (endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
            throw new ArgumentException("API 地址必须是无路径、凭据或查询参数的 HTTP(S) 地址。");
        if (endpoint.Scheme == "http" && !(IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("远程 Surge API 必须使用 HTTPS；本机 HTTP 请填写 127.0.0.1。");
        _client = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }, true)
            { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        _client.DefaultRequestHeaders.Add("X-Key", key);
    }
    public async Task<SurgeStatus> CheckAsync(CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        try
        {
            using var json = await ReadAsync("v1/policies", token).ConfigureAwait(false);
            bool valid = json.RootElement.TryGetProperty("proxies", out var proxies) && proxies.ValueKind == JsonValueKind.Array;
            return new(valid, valid ? "Surge 已连接" : "API 返回格式无法识别", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        { return new(false, "Surge API 无法连接；检查地址、密钥和 Surge 状态", DateTimeOffset.UtcNow); }
    }
    public async Task<IReadOnlyList<SurgeNode>> NodesAsync(CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        using var json = await ReadAsync("v1/policies?include_external=true", token).ConfigureAwait(false);
        var nodes = new List<SurgeNode>();
        if (json.RootElement.TryGetProperty("proxies", out var proxies))
            foreach (var proxy in proxies.EnumerateArray())
            {
                string? name = proxy.GetString();
                if (name is not null && name is not ("DIRECT" or "REJECT" or "REJECT-DROP")) nodes.Add(new("policy:" + name, name, "", true));
            }
        if (json.RootElement.TryGetProperty("external-policies", out var external))
            foreach (var node in external.EnumerateArray())
                nodes.Add(new(node.GetProperty("identifier").GetString()!, node.GetProperty("name").GetString()!, node.GetProperty("group").GetString()!, false));
        // External identifiers are documented for scripts, not policy_names; unknown is never a timeout.
        return nodes.DistinctBy(n => n.Id).ToArray();
    }
    public async Task<JsonDocument> TestAsync(string[] names, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        using var response = await _client.PostAsJsonAsync("v1/policies/test", new PolicyTestRequest(names, "http://www.gstatic.com/generate_204"), SurgeApiJson.Default.PolicyTestRequest, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
    }
    private async Task<JsonDocument> ReadAsync(string path, CancellationToken token)
    {
        using var response = await _client.GetAsync(path, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
    }
    public void Dispose() => _client.Dispose();
}
