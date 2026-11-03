using System.Text.Json.Serialization;

namespace EgressController.Mac;

public sealed record MacSettings
{
    public string SurgeEndpoint { get; init; } = "http://127.0.0.1:6171/";
    public string SurgeCliPath { get; init; } = "/Applications/Surge.app/Contents/Applications/surge-cli";
    public string ChromePath { get; init; } = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
    public string ServerEndpoint { get; init; } = "http://127.0.0.1:18081/";
    public string ProviderId { get; init; } = "tag";
    public string ClientId { get; init; } = Guid.NewGuid().ToString("N");
    public bool HealthChecksEnabled { get; init; }
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EgressController");
}
[JsonSerializable(typeof(MacSettings))]
public sealed partial class MacSettingsJson : JsonSerializerContext;
