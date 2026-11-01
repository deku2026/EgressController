using EgressController.Desktop.ViewModels;
using EgressController.Policy;
namespace EgressController.Desktop.Preview;

public static class PreviewChecks
{
    public static async Task RunAsync()
    {
        PreviewEnvironment.RequireIsolated();
        bool productionDenied = false; try { StartupArguments.EnsureIsolationCompatible(StartupMode.Production, true); } catch (InvalidOperationException) { productionDenied = true; }
        Check(productionDenied, "Production mode accepted an inherited mock-only environment.");
        StartupArguments.EnsureIsolationCompatible(StartupMode.Preview, true); StartupArguments.EnsureIsolationCompatible(StartupMode.Production, false);
        var vm = new ShellViewModel(); Check(vm.ConfigValid, "Initial fixture configuration is invalid: " + vm.CurrentIssue);
        Check(vm.Document.Nodes.Any(x => !x.IsSupported), "Unsupported protocols were silently removed.");
        Check(vm.GlobalChoices.All(choice => vm.Document.Groups.Any(group => group.Id == choice.Id)), "Route choices exposed raw nodes instead of proxy groups.");
        var app = vm.VisibleApps.First(); string[] inventory = app.Model.ExecutablePaths.ToArray(); app.RouteIndex = 2;
        Check(app.Model.Via.Kind == ExitKind.Proxy && app.Model.ExecutablePaths.SequenceEqual(inventory), "Changing route changed saved application membership.");
        string originalGlobal = vm.Document.Defaults.Global.TargetId!;
        app.SelectedProxyTarget = vm.GlobalChoices.First(x => x.Detail == "分组" && x.Id != originalGlobal);
        Check(app.Model.Via.TargetId == app.SelectedProxyTarget?.Id && vm.Document.Defaults.Global.TargetId == originalGlobal, "An application proxy choice changed the global proxy.");
        vm.SelectedApp = app; app.Model.ExecutablePaths.Add(app.BundlePath + "/Contents/MacOS/PreviouslySavedHelper");
        int beforeRefresh = app.Model.ExecutablePaths.Count; vm.RefreshSelectedInventoryCommand.Execute(null);
        Check(app.Model.ExecutablePaths.Count == beforeRefresh + 1 && inventory.All(x => app.Model.ExecutablePaths.Contains(x, StringComparer.Ordinal)) && vm.Core.ApplyCount == 0, "Explicit fixture inventory refresh dropped old paths or applied runtime prematurely.");
        vm.RefreshSelectedInventoryCommand.Execute(null); Check(app.Model.ExecutablePaths.Count == beforeRefresh + 1 && vm.Core.ApplyCount == 0, "Unchanged inventory refresh broadened membership or restarted runtime.");
        var assets = vm.PreparedAssets(); app.RouteIndex = 0; Check(ReferenceEquals(assets, vm.PreparedAssets()), "Route-only changes recompiled DAT data.");
        vm.AddAppsCommand.Execute(null); Check(!vm.ConfirmAppsCommand.CanExecute(null), "New applications were preselected.");
        var selected = vm.VisibleCatalog.First(x => x.CanSelect); selected.IsSelected = true; int count = vm.ApplicationCount; vm.ConfirmAppsCommand.Execute(null);
        Check(vm.ApplicationCount == count + 1 && !vm.AddingApps, "Explicit catalog selection failed.");
        var added = vm.Document.Applications.Last(); Check(added.ExecutablePaths.Count > 0 && added.Via == ExitChoice.NonHome, "Selected app was not given a fixed inventory.");
        string global = vm.Document.Defaults.Global.TargetId!; var stableRows = vm.VisibleApps; var stableFirst = vm.VisibleApps[0]; vm.SelectedApp = stableFirst; await vm.ImportSubscriptionAsync();
        Check(ReferenceEquals(stableRows, vm.VisibleApps) && ReferenceEquals(stableFirst, vm.VisibleApps[0]) && ReferenceEquals(vm.SelectedApp, stableFirst), "Node-only subscription refresh replaced application rows or its selection."); Check(vm.Document.Defaults.Global.TargetId == global && vm.Document.Applications.Last().ExecutablePaths.SequenceEqual(added.ExecutablePaths), "Subscription refresh reset saved choices or paths.");
        vm.SubscriptionUrl = "https://private.example.invalid/not-permitted"; await vm.ImportSubscriptionAsync(); Check(vm.HasIssue, "Preview allowed an arbitrary network URL."); vm.SubscriptionUrl = PreviewFixtures.SubscriptionUrl; await vm.ImportSubscriptionAsync(); Check(!vm.HasIssue, "Recovery retained a current error banner.");
        await vm.ApplyPreviewAsync(); Check(vm.Core.ApplyCount == 1, "Mock apply failed: " + vm.CurrentIssue); await vm.ApplyPreviewAsync(); Check(vm.Core.ApplyCount == 1, "Identical configuration restarted mock core.");
        string? currentGeneration = vm.Core.Current?.Id; int requests = vm.FixtureSubscriptionRequestCount;
        vm.SetFixtureSubscriptionPayload(PreviewFixtures.SubscriptionYaml.Replace("tokyo.example.invalid", "tokyo-updated.example.invalid", StringComparison.Ordinal)); await vm.ImportSubscriptionAsync();
        Check(vm.FixtureSubscriptionRequestCount == requests + 1 && vm.Document.Nodes.Any(node => node.Server == "tokyo-updated.example.invalid") && vm.Document.Defaults.Global.TargetId == global && vm.Core.Current?.Id == currentGeneration, "Explicit subscription refresh reused stale cache or applied runtime automatically.");
        string refreshed = PolicyYaml.Write(vm.Document);
        foreach (string bad in new[] { PreviewFixtures.SubscriptionYaml.Replace("新加坡 · 02", "东京 · 01", StringComparison.Ordinal), "proxies: [invalid" })
        {
            vm.SetFixtureSubscriptionPayload(bad); await vm.ImportSubscriptionAsync();
            Check(vm.HasIssue && PolicyYaml.Write(vm.Document) == refreshed && vm.Document.Defaults.Global.TargetId == global && vm.Core.Current?.Id == currentGeneration, "Fatal subscription refresh changed saved policy or applied generation.");
        }
        vm.SetFixtureSubscriptionPayload(PreviewFixtures.SubscriptionYaml); await vm.ImportSubscriptionAsync(); Check(!vm.HasIssue, "Subscription recovery left the current failure banner.");
        vm.SelectedNonHomeInterface = vm.SelectedHomeInterface; await vm.ApplyPreviewAsync(); Check(vm.HasIssue && vm.Core.ApplyCount == 1, "Invalid interfaces changed the running generation.");
        vm.SelectedNonHomeInterface = vm.InterfaceChoices[1]; vm.CompileDraft(); Check(vm.ConfigValid && !vm.HasIssue, "Valid recovery left an error banner.");
        var originalFirst = vm.VisibleRules.First(); var secondRule = vm.VisibleRules[1]; secondRule.MoveUpCommand.Execute(null); Check(vm.Document.RuleSets[0].Id == secondRule.Model.Id && vm.Document.RuleSets[1].Id == originalFirst.Model.Id, "Explicit rule order changes were not retained.");
        vm.CustomRuleName = "Preview IP"; vm.CustomRuleIsIp = true; vm.CustomRuleEntries = "192.0.2.0/24\n2001:db8:abcd::/48"; vm.CreateCustomRuleCommand.Execute(null); Check(vm.Document.RuleSets.Any(x => x.Name == "Preview IP" && x.IpCidrs.Count == 2), "Custom IP collection failed.");
        vm.CustomRuleName = "Invalid IP"; vm.CustomRuleEntries = "this-is-not-an-ip"; vm.CreateCustomRuleCommand.Execute(null); Check(vm.HasIssue && !vm.Document.RuleSets.Any(x => x.Name == "Invalid IP"), "Invalid CIDR was admitted."); vm.CompileDraft();
        vm.CustomRuleIsIp = false; vm.CustomRuleName = "Invalid Domain"; vm.CustomRuleEntries = "https://example.com"; vm.CreateCustomRuleCommand.Execute(null); Check(vm.HasIssue && !vm.Document.RuleSets.Any(rule => rule.Name == "Invalid Domain"), "A URL was admitted as a domain suffix."); vm.CustomRuleEntries = "bad/path"; vm.CreateCustomRuleCommand.Execute(null); Check(vm.HasIssue && !vm.Document.RuleSets.Any(rule => rule.Name == "Invalid Domain"), "A filesystem-like path was admitted as a domain suffix."); vm.CompileDraft();
        string valid = PolicyYaml.Write(vm.Document); vm.DslText = "version: [invalid"; vm.ApplyDslCommand.Execute(null); Check(vm.HasIssue, "Malformed DSL was accepted."); vm.DslText = valid; vm.ApplyDslCommand.Execute(null); Check(vm.ConfigValid && !vm.HasIssue, "DSL recovery failed.");
        vm.DnsServer = "9.9.9.9"; Check(PolicyYaml.Write(vm.Document).Contains("9.9.9.9", StringComparison.Ordinal), "DNS settings were not persisted in the single DSL model.");
        var dnsCandidate = PolicyYaml.Read(PolicyYaml.Write(vm.Document)).Document!; dnsCandidate.Dns.UpstreamIp = "1.0.0.1"; vm.DslText = PolicyYaml.Write(dnsCandidate); vm.ApplyDslCommand.Execute(null); Check(!vm.HasIssue && vm.DnsServer == "1.0.0.1" && vm.GeneratedConfig.Contains("1.0.0.1", StringComparison.Ordinal), "DSL DNS editing did not update both settings and generated runtime configuration.");
        string sourceSnapshot = PolicyYaml.Write(vm.Document); var sourceCandidate = PolicyYaml.Read(sourceSnapshot).Document!; sourceCandidate.RuleSets[0].Sources = ["geosite/unprepared-preview.yaml"]; vm.DslText = PolicyYaml.Write(sourceCandidate); vm.ApplyDslCommand.Execute(null);
        Check(vm.HasIssue && PolicyYaml.Write(vm.Document) == sourceSnapshot, "Unknown DSL resource was treated as prepared or replaced the old model.");
        sourceCandidate.RuleSets[0].Sources = ["geosite/github.yaml"]; sourceCandidate.RuleSets[0].Domains.Clear(); sourceCandidate.RuleSets[0].IpCidrs.Clear(); vm.DslText = PolicyYaml.Write(sourceCandidate); vm.ApplyDslCommand.Execute(null);
        Check(!vm.HasIssue && vm.ConfigValid && vm.GeneratedConfig.Contains("catalog-geosite-github", StringComparison.Ordinal), "A source-only candidate used the old document resource mapping instead of the prepared source tag.");
        vm.SaveCommand.Execute(null); Check(vm.SavedPreviewPath is { } path && path.StartsWith(PreviewEnvironment.SessionDirectory, StringComparison.Ordinal) && File.Exists(path), "Preview draft was not isolated.");
        vm.ShowLogDirectoryCommand.Execute(null); string logPath = Path.Combine(PreviewEnvironment.SessionDirectory, "preview-events.log"); Check(File.Exists(logPath), "Preview log-directory action failed.");
        if (!OperatingSystem.IsWindows())
        {
            var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Check((File.GetUnixFileMode(PreviewEnvironment.SessionDirectory) & forbidden) == 0 && (File.GetUnixFileMode(vm.SavedPreviewPath!) & forbidden) == 0 && (File.GetUnixFileMode(logPath) & forbidden) == 0, "Preview credentials or diagnostics were written with public Unix permissions.");
        }
        Check(!File.ReadAllText(logPath).Contains("fixture-not-a-real-secret", StringComparison.Ordinal), "Diagnostics exposed subscription credentials.");
        await vm.ApplyPreviewAsync();
        string beforeStop = PolicyYaml.Write(vm.Document); int beforeResume = vm.Core.ApplyCount;
        await vm.TogglePreviewAsync(); Check(!vm.PreviewRunning && vm.Core.Current is null && PolicyYaml.Write(vm.Document) == beforeStop, "Stopping preview did not stop the mock runtime or preserve configuration.");
        await vm.TogglePreviewAsync(); Check(vm.PreviewRunning && vm.Core.Current is not null && vm.Core.ApplyCount == beforeResume + 1 && PolicyYaml.Write(vm.Document) == beforeStop, "Resuming unchanged configuration failed to apply the stopped generation.");
        var onboarding = new ShellViewModel("onboarding"); onboarding.NextSetupCommand.Execute(null); onboarding.NextSetupCommand.Execute(null); Check(onboarding.SetupStep == 1 && !onboarding.NextSetupCommand.CanExecute(null), "First-run setup skipped required subscription import.");
        await onboarding.ImportSubscriptionAsync(); onboarding.NextSetupCommand.Execute(null);
        Check(onboarding.SetupStep == 2 && onboarding.SelectedGlobalTarget is not null, "Subscription did not initialize a default group for retention setup.");
        onboarding.SelectedGlobalTarget = onboarding.GlobalChoices.Single(choice => choice.Name == "手动选择");
        Check(onboarding.SelectedSetupGlobalMember is not null && onboarding.NextSetupCommand.CanExecute(null), "Clash's first declared manual member did not initialize setup.");
        onboarding.NextSetupCommand.Execute(null); onboarding.NextSetupCommand.Execute(null); onboarding.NextSetupCommand.Execute(null);
        Check(!onboarding.OnboardingVisible && onboarding.Core.ApplyCount == 1, "First-run setup did not apply its validated mock configuration.");
        var empty = new ShellViewModel("empty"); await empty.ApplyPreviewAsync(); Check(empty.ConfigValid && empty.Core.ApplyCount == 1, "The empty managed workspace did not prepare mandatory China DAT assets on first apply.");
        bool denied = false; try { PreviewEnvironment.RequireLiveOperations(); } catch (InvalidOperationException) { denied = true; }
        Check(denied, "Live operations are accessible from the preview.");
        foreach (var _ in Enumerable.Range(0, 230)) vm.Events.Insert(0, new("09:42", "bounded fixture", "event", "test")); Check(vm.Events.Count == 200, "Event history is unbounded.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
