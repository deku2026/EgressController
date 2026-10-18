using EgressController.App.Preview;
using EgressController.App.ViewModels;

namespace EgressController.UiPreview;

internal static class PreviewChecks
{
    internal static void Run()
    {
        static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
        static void Complete(Task task)
        {
            Check(task.IsCompleted, "Mock commands must not await production I/O.");
            task.GetAwaiter().GetResult();
        }
        var backend = new PreviewController();
        var vm = new MainViewModel(backend);
        vm.Apps.Query = "ChatGPT"; Check(vm.Apps.Entries.Count == 1, "Application search");
        vm.Apps.Entries[0].Routing.IsSelected = false;
        Check(backend.Profile.Applications.Count == 2, "Application deselection");
        vm.Apps.Entries[0].Routing.IsSelected = true;
        Check(backend.Profile.Applications.Count == 3, "Application selection");
        vm.Apps.Query = "";
        Complete(vm.Apps.ScanCommand.ExecuteAsync(null));
        Complete(vm.Apps.ClearAllCommand.ExecuteAsync(null)); Check(backend.Profile.Applications.Count == 0, "Clear applications");
        Complete(vm.Apps.SelectAllCommand.ExecuteAsync(null)); Check(backend.Profile.Applications.Count == 10, "Select applications");

        var ports = vm.Overview.Ports;
        ports.PortText = "not-a-port"; Complete(ports.AddCommand.ExecuteAsync(null));
        Check(backend.Profile.UpstreamPorts.Count == 2, "Invalid port changes configuration");
        ports.PortText = "1080"; Complete(ports.AddCommand.ExecuteAsync(null));
        Check(backend.Profile.UpstreamPorts.Contains(1080), "Add port");
        Complete(backend.SetDefaultUpstreamPortAsync(1080)); Check(backend.Profile.UpstreamPort == 1080, "Default port");
        Complete(backend.RemoveUpstreamPortAsync(7891)); Check(!backend.Profile.UpstreamPorts.Contains(7891), "Remove port");
        var adapters = vm.Overview.NetworkAdapters; string? original = backend.Profile.DefaultAdapterId;
        adapters.SwapCommand.Execute(null); Complete(adapters.SaveCommand.ExecuteAsync(null));
        Check(backend.Profile.DefaultAdapterId != original, "Swap adapters");
        adapters.SwapCommand.Execute(null); adapters.CancelCommand.Execute(null);
        Check(adapters.SelectedDirect?.Id == backend.Profile.DefaultAdapterId, "Cancel adapter edits");

        vm.Domains.Query = "google"; Complete(vm.Domains.ClearAllCommand.ExecuteAsync(null));
        Check(!backend.SelectedRuleNames.Contains("google"), "Disable rule set");
        Complete(vm.Domains.SelectAllCommand.ExecuteAsync(null)); Check(backend.SelectedRuleNames.Contains("google"), "Enable rule set");
        vm.Domains.ManualDomain = "example.com"; Complete(vm.Domains.AddManualCommand.ExecuteAsync(null));
        Check(backend.ManualDomains.Contains("example.com"), "Add domain");
        Complete(backend.RemoveManualDomainAsync("example.com")); Check(!backend.ManualDomains.Contains("example.com"), "Remove domain");
        Complete(vm.Domains.RefreshCatalogCommand.ExecuteAsync(null));

        vm.Traffic.Refresh();
        Check(vm.Traffic.Days.Count == 30 && vm.Traffic.TotalBytes > 0, "Monthly traffic");
        long recorded = vm.Traffic.TotalBytes;
        vm.Traffic.Query = "Claude"; Check(vm.Traffic.Applications.Count == 2, "Monthly application filter");
        vm.Traffic.Query = "";
        Complete(vm.Connections.CloseAllCommand.ExecuteAsync(null)); Check(backend.ConnectionHistory.ActiveCount == 0, "Close connections");
        vm.Traffic.Refresh(); Check(vm.Traffic.TotalBytes == recorded, "Closing connections must retain monthly use");
        vm.Connections.ClearHistoryCommand.Execute(null); Check(backend.ConnectionHistory.ClosedCount == 0, "Clear connection history");
        vm.Network.ClearProtectionHistoryCommand.Execute(null); Check(backend.ProtectionEvents.Count == 0, "Clear protection history");

        Complete(backend.Guard.StopCommand.ExecuteAsync(null)); Check(backend.Guard.Disabled && !backend.Guard.Healthy, "Stopped guardian must not be green");
        Complete(backend.Guard.StartCommand.ExecuteAsync(null)); Check(backend.Guard.Healthy, "Start guardian");
        Complete(backend.Guard.ForceStopCommand.ExecuteAsync(null)); Check(backend.Guard.Disabled, "Force stop");
        Complete(backend.Guard.UninstallCommand.ExecuteAsync(null)); Check(backend.Guard.CanInstall, "Uninstall guardian");
        Complete(backend.Guard.InstallCommand.ExecuteAsync(null)); Check(backend.Guard.Healthy, "Install guardian");
        Complete(backend.Guard.StopAndDisableCommand.ExecuteAsync(null)); Check(!backend.Guard.AutoStart, "Disable startup");
        Complete(backend.Guard.ClearHistoryCommand.ExecuteAsync(null));
        var offline = new PreviewController("offline"); Complete(offline.Guard.RefreshAsync());
        Check(!offline.Protection.Ready && offline.Guard.Healthy, "Network verdict leaked into guardian");
        Complete(offline.CheckInternetHealthAsync()); Check(offline.Protection.Ready, "Manual network recheck");
        var starting = new PreviewController("starting"); Complete(starting.Guard.RefreshAsync());
        Check(!starting.IsTunRunning && starting.Guard.Red, "Starting TUN guard badge");
        Check(!starting.CheckInternetHealthAsync().Result.Succeeded && !starting.IsTunRunning, "Probe fabricated a TUN");
        var telemetry = new PreviewController("telemetry"); Complete(telemetry.Guard.RefreshAsync());
        Check(telemetry.Protection.Ready && telemetry.Guard.Healthy, "Display sampling changed protection");
    }
}
