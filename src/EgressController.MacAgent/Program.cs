using EgressController.Core.Protection;
using EgressController.Mac;
using EgressController.Subscriptions;

// No Avalonia, window, tray or automatic launchd installation. Mock gate precedes every production dependency.
if (args.Contains("--mock-smoke", StringComparer.Ordinal))
{
    RuntimeSafety.EnableMockOnly();
    var decision = HealthEvaluator.Evaluate(new(), new(), [], false, false, DateTimeOffset.UtcNow);
    if (decision.Refresh) return 1;
    Console.WriteLine("Agent mock smoke passed: unavailable backend never starts a browser."); return 0;
}
if (!OperatingSystem.IsMacOS()) return 2;
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
string root = MacSettings.DataDirectory;
var secrets = new KeychainSecretStore();
var session = new DesktopSubscriptionSession(root, secrets);
var scheduler = new SubscriptionScheduler(root, session, new SurgeNodeHealthSource(root, secrets));
try
{
    if (args.Contains("--once", StringComparer.Ordinal)) Console.WriteLine(await scheduler.CheckOnceAsync(true, lifetime.Token));
    else await scheduler.RunAsync(lifetime.Token);
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { Console.Error.WriteLine("Agent settings or session unavailable; open EgressController to review."); return 1; }
return 0;
