using System.Text.Json.Serialization;
using EgressController.Core.Contracts;

namespace EgressController.Subscriptions;

public enum SourceFormat { Clash = 1, Surge = 2 }
public sealed record CapturedSubscription(SourceFormat Format, byte[] Content);
public sealed record ProviderCredentials(string Username, string Password);
public sealed record SubscriptionProgress(string Stage, string Detail, bool NeedsInteraction = false);
public sealed record SubscriptionResult(SourceFormat Format, string Sha256, string ServerVersion, long Version);
public sealed record SubscriptionSettings
{
    public string ProviderId { get; init; } = "tag";
    public string Website { get; init; } = "https://tagss.pro/";
    public string ChromePath { get; init; } = OperatingSystem.IsMacOS() ? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" : @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    public string MihomoPath { get; init; } = "";
    public string SurgeCliPath { get; init; } = "/Applications/Surge.app/Contents/Applications/surge-cli";
    public string ServerEndpoint { get; init; } = "http://127.0.0.1:18081/";
    public string ClientId { get; init; } = Guid.NewGuid().ToString("N");
    public bool AutomaticChecks { get; init; }
    public bool KeepSessionActive { get; init; } = true;
    public string GroupPattern { get; init; } = "(?i)(香港|HK|Hong Kong)";
    public int FailurePercent { get; init; } = 50;
    public int ConsecutiveRounds { get; init; } = 2;
    public int CooldownMinutes { get; init; } = 60;
}
public interface ISubscriptionProvider
{
    Task<IReadOnlyList<CapturedSubscription>> CaptureAsync(SubscriptionSettings settings, ProviderCredentials credentials,
        string browserDirectory, IProgress<SubscriptionProgress> progress, CancellationToken token);
}
public interface ISubscriptionValidator { Task ValidateAsync(SourceFormat format, string path, CancellationToken token); }
public interface ISubscriptionUploader { Task<SubscriptionResult> UploadAsync(string provider, string client, SourceFormat format, byte[] content, long version, DateTimeOffset captured, CancellationToken token); }
[JsonSerializable(typeof(ProviderCredentials))]
[JsonSerializable(typeof(SubscriptionSettings))]
[JsonSerializable(typeof(SubscriptionResult[]))]
public sealed partial class SubscriptionJson : JsonSerializerContext;
