using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EgressController.Runtime;

public sealed record PublishedRuntimeAssets(string Json, IReadOnlyDictionary<string, byte[]> Files);

/// <summary>Xray's routing RPC carries lazy DAT references. The running process must resolve every generation
/// through one stable asset directory, with immutable names so a failed update can still restore old rules.</summary>
public static class RuntimeAssetPublication
{
    public static PublishedRuntimeAssets Prepare(string json, IReadOnlyDictionary<string, byte[]> verifiedFiles)
    {
        if (RuntimeAssetValidator.Validate(json, verifiedFiles).Count != 0)
            throw new InvalidDataException("Runtime assets are not validated.");
        var config = JsonNode.Parse(json)!.AsObject();
        var published = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var rule in config["routing"]!["rules"]!.AsArray())
        foreach (string property in new[] { "domain", "ip" })
        {
            if (rule?[property] is not JsonArray values) continue;
            for (int index = 0; index < values.Count; index++)
            {
                string value = values[index]!.GetValue<string>();
                if (!value.StartsWith("ext:", StringComparison.Ordinal)) continue;
                string[] parts = value.Split(':');
                byte[] bytes = verifiedFiles[parts[1]];
                string name = Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".dat";
                published.TryAdd(name, bytes);
                values[index] = $"ext:{name}:{parts[2]}";
            }
        }
        return new(config.ToJsonString(), published);
    }
}

/// <summary>Only fixed failure categories leave the API output reader; raw configuration/path data is discarded.</summary>
public sealed class RoutingUpdateDiagnosis
{
    private string _code = "rpc.rejected";
    public string Code => Volatile.Read(ref _code);
    public void Observe(string line)
    {
        if (line.Length > 4096) return;
        string? code = line.Contains("no such file", StringComparison.OrdinalIgnoreCase) ? "assets.missing"
            : line.Contains("failed to load", StringComparison.OrdinalIgnoreCase) ? "assets.load-failed"
            : line.Contains("deadline exceeded", StringComparison.OrdinalIgnoreCase) ? "rpc.timeout"
            : line.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ? "rpc.unavailable"
            : line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "rpc.permission" : null;
        if (code is not null) Volatile.Write(ref _code, code);
    }
}
