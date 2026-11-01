using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Desktop.Preview;
using EgressController.Desktop.ViewModels;
using EgressController.Desktop.Views;
using EgressController.Policy;

namespace EgressController.MacPreview;

/// <summary>Full shell review with synthetic data. No platform adapters or real configuration.</summary>
internal static class UiAuditChecks
{
    public static void Run(string directory)
    {
        PreviewEnvironment.RequireIsolated();
        directory = Path.Combine(directory, "audit"); Directory.CreateDirectory(directory);
        WorkspaceRecovery(directory);
        HomeConnectivityRecovery(directory);
        foreach (bool longLabels in new[] { false, true })
        {
            var document = PreviewFixtures.CreateDocument();
            if (longLabels)
            {
                string label = "很长的名称 · " + new string('W', 100);
                foreach (var app in document.Applications) app.Name = label;
                foreach (var group in document.Groups) group.Name = label;
                foreach (var rule in document.RuleSets) rule.Name = label;
            }
            var backend = new FixtureClientBackend { LongLabels = longLabels, WorkspaceDocument = document };
            var vm = new ShellViewModel(backend); Complete(vm.InitializeAsync());
            var window = new MainWindow { DataContext = vm }; window.Show(); Flush();
            string prefix = longLabels ? "long-" : "";
            for (int page = 0; page < 5; page++)
            {
                vm.PageIndex = page;
                if (page == 4)
                    for (int tab = 0; tab < 5; tab++) { vm.SettingsTab = tab; Sizes(window, directory, prefix + "settings-" + tab); }
                else Sizes(window, directory, prefix + "page-" + page);
                if (page == 1) { vm.SelectedApp = vm.VisibleApps.First(); Sizes(window, directory, prefix + "app-detail"); vm.SelectedApp = null; }
                if (page == 2) { vm.SelectedRule = vm.VisibleRules.First(); Sizes(window, directory, prefix + "rule-detail"); vm.SelectedRule = null; }
            }
            vm.PageIndex = 0; vm.Connections.IsOpen = true; Sizes(window, directory, prefix + "connections"); vm.Connections.IsOpen = false;
            vm.PageIndex = 1; Complete(vm.AddAppsCommand.ExecuteAsync()); Sizes(window, directory, prefix + "app-catalog");
            Check(Find<TextBox>(window, "CatalogSearch").IsFocused && !Find<ListBox>(window, "ManagedAppList").IsEffectivelyEnabled,
                "App catalog failed to focus search or left covered rows interactive.");
            AssertFocusCycle(window, Find<Border>(window, "AppCatalogPanel")); vm.CancelAppsCommand.Execute(null);
            vm.PageIndex = 2; Complete(vm.AddRulesCommand.ExecuteAsync()); Sizes(window, directory, prefix + "rule-catalog");
            Check(Find<TextBox>(window, "RuleCatalogSearch").IsFocused && !vm.RulesWorkspaceEnabled, "Rule catalog did not own focus.");
            AssertFocusCycle(window, Find<Border>(window, "RuleCatalogPanel")); vm.CloseRulesCommand.Execute(null);
            vm.ShowCustomRuleCommand.Execute(null); Sizes(window, directory, prefix + "custom-rule");
            Check(Find<TextBox>(window, "CustomRuleNameBox").IsFocused && !vm.RulesWorkspaceEnabled, "Custom-rule form did not own focus."); vm.CancelCustomRuleCommand.Execute(null);
            vm.PageIndex = 3; vm.ManagingGroups = true; Sizes(window, directory, prefix + "retention");
            Check(!Find<ListBox>(window, "ProxyGroupList").IsEffectivelyEnabled, "Group management left covered selectors interactive.");
            AssertFocusCycle(window, Find<Border>(window, "GroupManagementPanel")); vm.ManagingGroups = false;
            vm.OnboardingVisible = true;
            for (int step = 0; step < 5; step++)
            {
                vm.SetupStep = step; Sizes(window, directory, prefix + "setup-" + step);
                var scroller = Find<ScrollViewer>(window, "SetupContentScroll"); scroller.ScrollToEnd(); Flush();
                if (step is 2 or 3) Check(!scroller.IsVisible, "A list step retained a second outer scrollbar.");
                var title = Find<TextBlock>(window, "SetupStepTitle");
                Check(InWindow(window, title), "Step title escaped viewport after scrolling.");
                Check(InWindow(window, Find<Button>(window, "SetupContinueButton")), "Continue escaped viewport.");
                int before = vm.PageIndex;
                window.KeyPress(Key.D1, RawInputModifiers.Meta, PhysicalKey.Digit1, "1"); Flush();
                Check(vm.PageIndex == before, "Background navigation shortcut escaped the setup modal.");
            }
            vm.SetupStep = 0; Flush();
            Check(Find<ScrollViewer>(window, "SetupContentScroll").Offset.Y == 0, "A new step retained the prior step's scroll offset.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Flush();
            Check(!vm.OnboardingVisible && vm.NodesCommand.CanExecute(null), "Escape did not restore page navigation.");
            window.Close(); Complete(vm.DisposeAsync().AsTask());
        }
        Console.WriteLine("UI audit: every page, five settings tabs, five setup steps, catalogs, custom rule, group management; normal/minimum sizes and long labels. Load/failure/retry/reset and modal input verified.");
    }

    private static void WorkspaceRecovery(string directory)
    {
        var backend = new FixtureClientBackend { LoadGate = new(), LoadIssue = "无法读取网卡信息，请重新载入。已保存的配置保留。" };
        var vm = new ShellViewModel(backend); Task loading = vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm }; window.Show(); Flush();
        Check(vm.IsLoading && !vm.CanEdit && !vm.OnboardingVisible, "Loading exposed editable placeholder setup.");
        Sizes(window, directory, "workspace-loading");
        backend.LoadGate.SetResult(); Complete(loading);
        string saved = PolicyYaml.Write(vm.Document);
        Check(vm.HasWorkspaceLoadIssue && !vm.WorkspaceReady && !vm.OnboardingVisible && !vm.SaveStatus.Contains("已同步", StringComparison.Ordinal), "Load failure presented an empty or healthy workspace.");
        Check(vm.RequestResetCommand.CanExecute(null), "A readable saved draft cannot be reset after discovery failure.");
        Sizes(window, directory, "workspace-failed");
        var retry = Find<Button>(window, "RetryWorkspaceButton");
        retry.Focus(NavigationMethod.Tab); Save(window, directory, "workspace-retry-focus");
        var point = Center(window, retry); window.MouseMove(point); Save(window, directory, "workspace-retry-hover");
        window.MouseDown(point, MouseButton.Left); Save(window, directory, "workspace-retry-pressed");
        backend.LoadIssue = null;
        window.MouseUp(point, MouseButton.Left); Flush();
        Check(backend.LoadCalls == 2 && vm.WorkspaceReady && PolicyYaml.Write(vm.Document) == saved && backend.SaveCalls == 0, "Pointer retry failed or changed saved choices.");
        vm.PageIndex = 3; Flush();
        Check(Find<Button>(window, "ProbeAllButton").IsEffectivelyEnabled, "Loading/retry left the all-probes action disabled.");
        vm.PageIndex = 0;
        Sizes(window, directory, "workspace-recovered");
        window.Close(); Complete(vm.DisposeAsync().AsTask());

        // Deserialization/storage failure before a draft is available cannot save an empty replacement.
        backend = new() { LoadFailure = new IOException("Synthetic unavailable draft") }; vm = new(backend); Complete(vm.InitializeAsync());
        window = new() { DataContext = vm }; window.Show(); Flush();
        Check(vm.HasWorkspaceLoadIssue && !vm.CanResetWorkspace && backend.SaveCalls == 0, "Unknown draft was presented as safe to overwrite.");
        Sizes(window, directory, "workspace-storage-failure");
        backend.LoadFailure = null; Complete(vm.RetryWorkspaceCommand.ExecuteAsync());
        Check(vm.WorkspaceReady && vm.CanResetWorkspace && backend.SaveCalls == 0, "Retry did not restore commands.");
        window.Close(); Complete(vm.DisposeAsync().AsTask());

        // The empty combo contract also applies to unavailable subscription/group choices.
        var box = new ComboBox { Width = 260, ItemsSource = Array.Empty<string>() };
        var host = new Window { Content = box, Width = 340, Height = 100 }; host.Show(); Flush();
        Check(!box.IsEffectivelyEnabled, "Empty ComboBox still accepts clicks and opens an empty popup.");
        host.Close();
    }

    private static void HomeConnectivityRecovery(string directory)
    {
        foreach (var size in new[] { (1260, 860), (960, 660) })
        {
            var backend = new FixtureClientBackend { SimulateHomeFailure = true, HomeFailureCode = "interface.inactive" };
            var vm = new ShellViewModel(backend); Complete(vm.InitializeAsync()); vm.SetupStep = 0; vm.OnboardingVisible = true;
            var window = new MainWindow { DataContext = vm, Width = size.Item1, Height = size.Item2 }; window.Show(); Flush();
            var button = Find<Button>(window, "HomeConnectivityButton"); button.BringIntoView(); Flush(); button.Focus(NavigationMethod.Tab);
            var point = Center(window, button); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Flush();
            var help = Find<Border>(window, "HomeConnectivityHelp"); var scroll = Find<ScrollViewer>(window, "SetupContentScroll");
            Check(backend.HomeChecks == 1 && vm.HomeCheckFailed && help.IsEffectivelyVisible, "Pointer home check did not reveal USB guidance.");
            var location = help.TranslatePoint(new Point(), scroll)!.Value;
            Check(location.Y >= -1 && location.Y + help.Bounds.Height <= scroll.Viewport.Height + 1, "USB guidance is clipped by the setup viewport.");
            Check(InWindow(window, Find<TextBlock>(window, "SetupStepTitle")) && InWindow(window, Find<Button>(window, "SetupContinueButton")), "Home failure displaced fixed heading/footer.");
            Check(!Find<Button>(window, "SetupContinueButton").IsEffectivelyEnabled, "Failed check allowed continuation.");
            Check(button.IsFocused, $"Home check lost keyboard focus: {(window.FocusManager?.GetFocusedElement() as Control)?.Name}, button enabled={button.IsEffectivelyEnabled}.");
            Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, "USB help overflows horizontally.");
            Save(window, directory, "home-usb-failed-" + size.Item1);
            backend.SimulateHomeFailure = false; point = Center(window, button);
            Check(InWindow(window, button), "Retry button was scrolled out of the window.");
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Flush();
            Check(backend.HomeChecks == 2 && vm.HomeCheckReady && !help.IsEffectivelyVisible && Find<Button>(window, "SetupContinueButton").IsEffectivelyEnabled,
                "Successful pointer retry retained advice or left Continue disabled.");
            Check(backend.Core.RestartCount == 0, "Home check started TUN."); Save(window, directory, "home-usb-recovered-" + size.Item1);
            window.Close(); Complete(vm.DisposeAsync().AsTask());
        }
    }

    private static void Sizes(Window window, string directory, string name)
    {
        foreach (var size in new[] { (1260, 860), (960, 660) })
        {
            window.Width = size.Item1; window.Height = size.Item2; Flush();
            // Grid overlap is legal XAML. Check actual form geometry, not only successful compilation.
            var dns = window.GetVisualDescendants().OfType<TextBox>().SingleOrDefault(x => x.Name == "DnsUpstreamBox");
            if (name.Contains("settings-1", StringComparison.Ordinal)) Check(dns is not null, "DNS form was not materialized.");
            if (dns?.IsEffectivelyVisible == true)
            {
                var form = dns.GetVisualAncestors().OfType<Grid>().First();
                var label = form.Children.OfType<TextBlock>().Single();
                Check(dns.Bounds.X >= label.Bounds.Right + 8 && dns.Bounds.Right <= form.Bounds.Width + 1,
                    "DNS label and input overlap or extend beyond the form.");
            }
            foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.IsEffectivelyVisible && !x.GetVisualAncestors().OfType<TextBox>().Any()))
                Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"Unexpected horizontal overflow: {name}, {scroll.Name}, {scroll.Extent.Width}/{scroll.Viewport.Width}.");
            Save(window, directory, name + "-" + size.Item1);
        }
    }
    private static bool InWindow(Window window, Control control)
    { var p = control.TranslatePoint(new Point(), window)!.Value; return p.X >= 0 && p.Y >= 0 && p.X + control.Bounds.Width <= window.ClientSize.Width + 1 && p.Y + control.Bounds.Height <= window.ClientSize.Height + 1; }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);
    private static Point Center(Window window, Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    private static void AssertFocusCycle(Window window, Control overlay)
    {
        for (int index = 0; index < 30; index++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Flush();
            var focused = window.FocusManager?.GetFocusedElement() as Control;
            Check(focused is not null && (focused == overlay || focused.GetVisualAncestors().Contains(overlay)), "Tab escaped an open page overlay.");
        }
    }
    private static void Complete(Task task)
    { var deadline = DateTime.UtcNow.AddSeconds(15); while (!task.IsCompleted) { Flush(); if (DateTime.UtcNow > deadline) throw new TimeoutException("Isolated UI task stalled."); Thread.Sleep(1); } task.GetAwaiter().GetResult(); Flush(); }
    private static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Save(Window window, string directory, string name) { Flush(); using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("UI frame unavailable."); frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions()); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
