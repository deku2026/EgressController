using System.Text.Json;
using System.Text.Json.Serialization;

namespace EgressController.SingBox.Api.Models;

public sealed record SingBoxVersionResponse
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("premium")]
    public bool Premium { get; init; }

    [JsonPropertyName("meta")]
    public bool Meta { get; init; }
}

public sealed record SingBoxConfigResponse
{
    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("socks-port")]
    public int SocksPort { get; init; }

    [JsonPropertyName("redir-port")]
    public int RedirPort { get; init; }

    [JsonPropertyName("tproxy-port")]
    public int TProxyPort { get; init; }

    [JsonPropertyName("mixed-port")]
    public int MixedPort { get; init; }

    [JsonPropertyName("allow-lan")]
    public bool AllowLan { get; init; }

    [JsonPropertyName("bind-address")]
    public string BindAddress { get; init; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("mode-list")]
    public List<string> ModeList { get; init; } = [];

    [JsonPropertyName("log-level")]
    public string LogLevel { get; init; } = string.Empty;

    [JsonPropertyName("ipv6")]
    public bool Ipv6 { get; init; }

    [JsonPropertyName("tun")]
    public JsonElement Tun { get; init; }
}

public sealed record SingBoxRulesResponse
{
    [JsonPropertyName("rules")]
    public List<SingBoxRule> Rules { get; init; } = [];
}

public sealed record SingBoxRule
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;

    [JsonPropertyName("proxy")]
    public string Proxy { get; init; } = string.Empty;
}

public sealed record SingBoxConnectionsResponse
{
    [JsonPropertyName("downloadTotal")]
    public long DownloadTotal { get; init; }

    [JsonPropertyName("uploadTotal")]
    public long UploadTotal { get; init; }

    [JsonPropertyName("connections")]
    public List<SingBoxConnection> Connections { get; init; } = [];
}

public sealed record SingBoxConnection
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("metadata")]
    public SingBoxConnectionMetadata Metadata { get; init; } = new();

    [JsonPropertyName("upload")]
    public long Upload { get; init; }

    [JsonPropertyName("download")]
    public long Download { get; init; }

    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; init; }

    [JsonPropertyName("chains")]
    public List<string> Chains { get; init; } = [];

    [JsonPropertyName("rule")]
    public string Rule { get; init; } = string.Empty;

    [JsonPropertyName("rulePayload")]
    public string RulePayload { get; init; } = string.Empty;
}

public sealed record SingBoxConnectionMetadata
{
    [JsonPropertyName("network")]
    public string Network { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("sourceIP")]
    public string SourceIp { get; init; } = string.Empty;

    [JsonPropertyName("destinationIP")]
    public string DestinationIp { get; init; } = string.Empty;

    [JsonPropertyName("sourcePort")]
    public string SourcePort { get; init; } = string.Empty;

    [JsonPropertyName("destinationPort")]
    public string DestinationPort { get; init; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("dnsMode")]
    public string DnsMode { get; init; } = string.Empty;

    [JsonPropertyName("processPath")]
    public string ProcessPath { get; init; } = string.Empty;
}

public sealed record SingBoxTrafficEvent
{
    [JsonPropertyName("up")]
    public long Up { get; init; }

    [JsonPropertyName("down")]
    public long Down { get; init; }
}

public sealed record SingBoxLogEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public string Payload { get; init; } = string.Empty;
}

public sealed record SingBoxDnsResponse
{
    [JsonPropertyName("Status")]
    public int Status { get; init; }

    [JsonPropertyName("Question")]
    public JsonElement Question { get; init; }

    [JsonPropertyName("Server")]
    public string Server { get; init; } = string.Empty;

    [JsonPropertyName("TC")]
    public bool Truncated { get; init; }

    [JsonPropertyName("RD")]
    public bool RecursionDesired { get; init; }

    [JsonPropertyName("RA")]
    public bool RecursionAvailable { get; init; }

    [JsonPropertyName("AD")]
    public bool AuthenticatedData { get; init; }

    [JsonPropertyName("CD")]
    public bool CheckingDisabled { get; init; }

    [JsonPropertyName("Answer")]
    public JsonElement Answer { get; init; }

    [JsonPropertyName("Authority")]
    public JsonElement Authority { get; init; }

    [JsonPropertyName("Additional")]
    public JsonElement Additional { get; init; }
}

public sealed record SingBoxApiErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SingBoxVersionResponse))]
[JsonSerializable(typeof(SingBoxConfigResponse))]
[JsonSerializable(typeof(SingBoxRulesResponse))]
[JsonSerializable(typeof(SingBoxConnectionsResponse))]
[JsonSerializable(typeof(SingBoxTrafficEvent))]
[JsonSerializable(typeof(SingBoxLogEvent))]
[JsonSerializable(typeof(SingBoxDnsResponse))]
[JsonSerializable(typeof(SingBoxApiErrorResponse))]
internal sealed partial class SingBoxApiJsonContext : JsonSerializerContext;
