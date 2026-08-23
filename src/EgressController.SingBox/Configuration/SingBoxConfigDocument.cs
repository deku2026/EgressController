using System.Text.Json;
using System.Text.Json.Serialization;

namespace EgressController.SingBox.Configuration;

public sealed record SingBoxConfigDocument
{
    [JsonPropertyName("log")]
    public required SingBoxLogDocument Log { get; init; }

    [JsonPropertyName("dns")]
    public required SingBoxDnsDocument Dns { get; init; }

    [JsonPropertyName("inbounds")]
    public required IReadOnlyList<SingBoxTunInboundDocument> Inbounds { get; init; }

    [JsonPropertyName("outbounds")]
    public required IReadOnlyList<SingBoxOutboundDocument> Outbounds { get; init; }

    [JsonPropertyName("route")]
    public required SingBoxRouteDocument Route { get; init; }

    [JsonPropertyName("experimental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SingBoxExperimentalDocument? Experimental { get; init; }

    public byte[] ToJsonBytes()
        => JsonSerializer.SerializeToUtf8Bytes(this, SingBoxConfigJsonContext.Default.SingBoxConfigDocument);
}

public sealed record SingBoxLogDocument
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = "warn";

    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; init; }

    [JsonPropertyName("timestamp")]
    public bool Timestamp { get; init; } = true;
}

public sealed record SingBoxDnsDocument
{
    [JsonPropertyName("servers")]
    public required IReadOnlyList<SingBoxHttpsDnsServerDocument> Servers { get; init; }

    [JsonPropertyName("final")]
    public required string Final { get; init; }

    [JsonPropertyName("strategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Strategy { get; init; }
}

public sealed record SingBoxHttpsDnsServerDocument
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "https";

    [JsonPropertyName("tag")]
    public required string Tag { get; init; }

    [JsonPropertyName("server")]
    public required string Server { get; init; }

    [JsonPropertyName("server_port")]
    public int ServerPort { get; init; } = 443;

    [JsonPropertyName("path")]
    public string Path { get; init; } = "/dns-query";

    [JsonPropertyName("tls")]
    public required SingBoxTlsDocument Tls { get; init; }

    [JsonPropertyName("detour")]
    public required string Detour { get; init; }
}

public sealed record SingBoxTlsDocument
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("server_name")]
    public required string ServerName { get; init; }
}

public sealed record SingBoxTunInboundDocument
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tun";

    [JsonPropertyName("tag")]
    public required string Tag { get; init; }

    [JsonPropertyName("interface_name")]
    public required string InterfaceName { get; init; }

    [JsonPropertyName("address")]
    public required IReadOnlyList<string> Address { get; init; }

    [JsonPropertyName("auto_route")]
    public bool AutoRoute { get; init; } = true;

    [JsonPropertyName("strict_route")]
    public bool StrictRoute { get; init; } = true;

    [JsonPropertyName("stack")]
    public string Stack { get; init; } = "system";
}

public sealed record SingBoxOutboundDocument
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("tag")]
    public required string Tag { get; init; }

    [JsonPropertyName("bind_interface")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BindInterface { get; init; }

    [JsonPropertyName("inet4_bind_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Inet4BindAddress { get; init; }

    [JsonPropertyName("inet6_bind_address")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Inet6BindAddress { get; init; }

    [JsonPropertyName("server")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Server { get; init; }

    [JsonPropertyName("server_port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ServerPort { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
}

public sealed record SingBoxRouteDocument
{
    [JsonPropertyName("rules")]
    public required IReadOnlyList<SingBoxRouteRuleDocument> Rules { get; init; }

    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SingBoxRuleSetDocument>? RuleSet { get; init; }

    [JsonPropertyName("final")]
    public required string Final { get; init; }

    [JsonPropertyName("auto_detect_interface")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoDetectInterface { get; init; }

    [JsonPropertyName("find_process")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FindProcess { get; init; }
}

public sealed record SingBoxRuleSetDocument
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "local";

    [JsonPropertyName("tag")]
    public required string Tag { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "binary";

    [JsonPropertyName("path")]
    public required string Path { get; init; }
}

public sealed record SingBoxRouteRuleDocument
{
    [JsonPropertyName("protocol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Protocol { get; init; }

    [JsonPropertyName("process_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ProcessName { get; init; }

    [JsonPropertyName("process_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ProcessPath { get; init; }

    [JsonPropertyName("rule_set")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RuleSet { get; init; }

    [JsonPropertyName("domain_suffix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DomainSuffix { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("outbound")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Outbound { get; init; }
}

public sealed record SingBoxExperimentalDocument
{
    [JsonPropertyName("clash_api")]
    public required SingBoxClashApiDocument ClashApi { get; init; }
}

public sealed record SingBoxClashApiDocument
{
    [JsonPropertyName("external_controller")]
    public required string ExternalController { get; init; }

    [JsonPropertyName("secret")]
    public required string Secret { get; init; }

    [JsonPropertyName("default_mode")]
    public string DefaultMode { get; init; } = "rule";
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Serialization,
    WriteIndented = true)]
[JsonSerializable(typeof(SingBoxConfigDocument))]
internal sealed partial class SingBoxConfigJsonContext : JsonSerializerContext;
