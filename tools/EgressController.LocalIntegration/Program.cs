using System.Text;
using EgressController.Core.Routing;
using EgressController.Core.Storage;
using EgressController.Subscriptions;
using EgressController.Surge;

// Explicit local opt-in tool. Never included in a solution/CI/mock preview.
if (!args.Contains("--local-only", StringComparer.Ordinal)) throw new ArgumentException("Pass --local-only to run isolated local integration.");
string directory = Environment.GetEnvironmentVariable("EGRESS_LOCAL_TEST_DATA") ?? Path.Combine(Path.GetTempPath(), "egress-local-integration");
PrivateFile.EnsureDirectory(directory);
var settings = new SubscriptionSettings { MihomoPath = Environment.GetEnvironmentVariable("EGRESS_MIHOMO") ?? "", ClientId = "local-integration", ServerEndpoint = "http://127.0.0.1:18081/" };
using var timeout = new CancellationTokenSource(args.Contains("--login", StringComparer.Ordinal) ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(12));
var progress = new Progress<SubscriptionProgress>(p => Console.WriteLine(p.Stage + ": " + p.Detail));
var provider = new TagSubscriptionProvider();
if (args.Contains("--login", StringComparer.Ordinal))
{
    var credentials = new ProviderCredentials(Environment.GetEnvironmentVariable("EGRESS_TAG_USERNAME") ?? "", Environment.GetEnvironmentVariable("EGRESS_TAG_PASSWORD") ?? "");
    var state = await provider.LoginAsync(settings, credentials, Path.Combine(directory, "chrome-profile"), progress, timeout.Token);
    Console.WriteLine("Login state: " + state.Status);
    return state.Status == LoginStatus.Valid ? 0 : 2;
}
if (args.Contains("--check-session", StringComparer.Ordinal))
{
    var state = await provider.CheckSessionAsync(settings, Path.Combine(directory, "chrome-profile"), timeout.Token);
    Console.WriteLine("Session state: " + state.Status); return state.Status == LoginStatus.Valid ? 0 : 2;
}
if (args.Contains("--tag", StringComparer.Ordinal))
{
    using var uploader = new GrpcSubscriptionUploader(new Uri(settings.ServerEndpoint), Environment.GetEnvironmentVariable("SUBVAULT_INGRESS_TEST_TOKEN") ?? "");
    var workflow = new SubscriptionWorkflow(provider, new NativeSubscriptionValidator(settings.SurgeCliPath, settings.MihomoPath), uploader, directory);
    var result = await workflow.RefreshAsync(settings, new("", ""), progress, timeout.Token);
    foreach (var receipt in result) Console.WriteLine($"{receipt.Format}: receipt verified, SHA-256 {receipt.Sha256}, version {receipt.Version}");
    return 0;
}
// Harmless synthetic profiles: validate only; no proxy starts, Surge reload or active profile writes.
string surge = new SurgeConfigCompiler().Compile(new RoutingProfile { Domains = [new("example.com", new())] }, []);
string surgePath = Path.Combine(directory, "fixture.conf"); await PrivateFile.WriteAsync(surgePath, Encoding.UTF8.GetBytes(surge), timeout.Token);
var checkedConfig = await new SurgeCli().CheckAsync(surgePath, timeout.Token);
if (!checkedConfig.Valid) throw new InvalidDataException(checkedConfig.Summary);
Console.WriteLine("Generated Surge profile: offline validation passed");
if (settings.MihomoPath.Length > 0)
{
    byte[] clash = Encoding.UTF8.GetBytes("proxies:\n  - {name: fixture, type: socks5, server: 127.0.0.1, port: 9}\nrules:\n  - MATCH,DIRECT\n");
    string clashPath = Path.Combine(directory, "fixture.yaml"); await PrivateFile.WriteAsync(clashPath, clash, timeout.Token);
    await new NativeSubscriptionValidator(settings.SurgeCliPath, settings.MihomoPath).ValidateAsync(SourceFormat.Clash, clashPath, timeout.Token);
    Console.WriteLine("Synthetic Clash: mihomo -t passed");
    using var uploader = new GrpcSubscriptionUploader(new Uri(settings.ServerEndpoint), Environment.GetEnvironmentVariable("SUBVAULT_INGRESS_TEST_TOKEN") ?? "");
    long version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var first = await uploader.UploadAsync("fixture", "local-integration", SourceFormat.Clash, clash, version, DateTimeOffset.UtcNow, timeout.Token);
    var retry = await uploader.UploadAsync("fixture", "local-integration", SourceFormat.Clash, clash, version, DateTimeOffset.UtcNow, timeout.Token);
    if (first.ServerVersion != retry.ServerVersion) throw new InvalidDataException("Retry was not idempotent");
    Console.WriteLine("Native gRPC server: exact receipt and idempotent retry passed");
}
return 0;
