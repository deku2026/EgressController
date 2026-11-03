using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using EgressController.Core.Storage;
using EgressController.Core.Protection;
using EgressController.Subscriptions;
using EgressController.Surge;

namespace EgressController.Mac;

public sealed class SurgeNodeHealthSource(string root, KeychainSecretStore secrets) : INodeHealthSource
{
    public async Task<HealthSnapshot> ObserveAsync(SubscriptionSettings settings, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        var config = await PrivateFile.ReadJsonAsync(Path.Combine(root, "settings.json"), MacSettingsJson.Default.MacSettings, token).ConfigureAwait(false) ?? new();
        string? key = secrets.Read("surge-api");
        if (string.IsNullOrEmpty(key)) return new(false, false, [], "请先在设置中连接 Surge HTTP API");
        if (!NetworkInterface.GetIsNetworkAvailable()) return new(false, false, [], "本机网络不可用");
        using var api = new SurgeHttpApi(new(config.SurgeEndpoint), key);
        try
        {
            var nodes = await api.NodesAsync(token).ConfigureAwait(false);
            var pattern = new Regex(settings.GroupPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var matching = nodes.Where(n => pattern.IsMatch(n.Name)).ToArray();
            // DIRECT is a control observation. A failed control never blames the subscription.
            using var control = await api.TestAsync(["DIRECT"], token).ConfigureAwait(false);
            bool network = Parse("DIRECT", "DIRECT", control.RootElement, DateTimeOffset.UtcNow).Outcome == ProbeOutcome.Success;
            if (!network) return new(true, false, matching.Select(n => new NodeObservation(n.Id, n.Name, ProbeOutcome.Unknown, DateTimeOffset.UtcNow)).ToArray(), "网络对照检测未成功");
            string[] testable = matching.Where(n => n.Testable).Take(128).Select(n => n.Name).ToArray();
            using var result = testable.Length == 0 ? JsonDocument.Parse("{}") : await api.TestAsync(testable, token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var observations = matching.Select(n => n.Testable && testable.Contains(n.Name, StringComparer.Ordinal)
                ? Parse(n.Id, n.Name, result.RootElement, now) : new NodeObservation(n.Id, n.Name, ProbeOutcome.Unknown, now)).ToArray();
            return new(true, true, observations, "Surge 节点检测完成");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or RegexMatchTimeoutException or TaskCanceledException && !token.IsCancellationRequested)
        { return new(false, false, [], "Surge API 检测不可用；未知结果不触发刷新"); }
    }
    public static NodeObservation Parse(string id, string name, JsonElement response, DateTimeOffset at)
    {
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty(name, out var result) && result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.Number && available.TryGetDouble(out double milliseconds) && milliseconds >= 0 && double.IsFinite(milliseconds))
                return new(id, name, ProbeOutcome.Success, at, milliseconds);
            if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                (error.GetString()!.Contains("timeout", StringComparison.OrdinalIgnoreCase) || error.GetString()!.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
                return new(id, name, ProbeOutcome.Timeout, at);
        }
        return new(id, name, ProbeOutcome.Unknown, at);
    }
}
