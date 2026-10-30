using System.Text.Json;
using EgressController.Policy;

namespace EgressController.Runtime;

/// <summary>Prepared references must resolve to real, validated DAT contents before a core is touched.</summary>
public static class RuntimeAssetValidator
{
    public static IReadOnlyList<PolicyDiagnostic> Validate(string json, IReadOnlyDictionary<string, byte[]> assets)
    {
        var diagnostics = new List<PolicyDiagnostic>();
        var inspected = new HashSet<(string File, string Tag, RuleDataKind Kind)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var rule in document.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray())
            {
                foreach (var (property, kind) in new[] { ("domain", RuleDataKind.Domain), ("ip", RuleDataKind.Ip) })
                {
                    if (!rule.TryGetProperty(property, out var values)) continue;
                    foreach (var item in values.EnumerateArray())
                    {
                        string? value = item.GetString();
                        if (value is null || !value.StartsWith("ext:", StringComparison.Ordinal)) continue;
                        string[] parts = value.Split(':');
                        if (parts.Length != 3 || !ConfigurationCoordinator.SafeArtifactName(parts[1]))
                        { diagnostics.Add(new("assets.reference", "规则数据引用无效，未应用配置。")); continue; }
                        if (!inspected.Add((parts[1], parts[2], kind))) continue;
                        if (!assets.TryGetValue(parts[1], out var bytes) || !GeoDatInspector.HasTag(bytes, parts[2], kind))
                            diagnostics.Add(new("assets.not-prepared", "规则数据未完成验证或缺少对应标签，保留上一份配置。"));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { diagnostics.Add(new("assets.config", "生成配置的规则引用结构无效。")); }
        return diagnostics;
    }
}
