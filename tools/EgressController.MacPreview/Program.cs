using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.Desktop;
using EgressController.Desktop.Presentation;
using EgressController.Desktop.Presentation.Controls;
using EgressController.Desktop.Preview;
using EgressController.Desktop.ViewModels;
using EgressController.Desktop.Views;
using EgressController.Policy;

namespace EgressController.MacPreview;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        PreviewEnvironment.Enter();
        bool connectionChecksOnly = args.Contains("--connection-checks", StringComparer.Ordinal);
        if (!connectionChecksOnly) PreviewChecks.RunAsync().GetAwaiter().GetResult();
        int flag = Array.IndexOf(args, "--screenshots"); if (flag < 0) flag = Array.IndexOf(args, "--output");
        if (flag < 0 || flag + 1 >= args.Length) throw new ArgumentException("Use --screenshots <absolute-output-directory>.");
        string directory = Path.GetFullPath(args[flag + 1]); Directory.CreateDirectory(directory);
        AppBuilder.Configure<DesktopApplication>().WithInterFont().With(MacFonts.Options()).UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var log = new BindingLog(); Logger.Sink = log;
        ConnectionAttemptChecks.Run(directory);
        ConnectionWorkspaceChecks.Run(directory);
        if(connectionChecksOnly){log.Verify();Console.WriteLine("Connection-attempt phases, cancel/retry and production wording; workspace input, truthful sparse facts, sorting, virtualization and stable refresh checks passed; live operations blocked.");return 0;}
        PassiveReconciliation(directory);
        if(args.Contains("--passive-checks",StringComparer.Ordinal)){log.Verify();Console.WriteLine("Passive binding reconciliation preserved full policy and clean state.");return 0;}
        foreach (string scenario in new[] { "overview", "apps", "rules", "nodes", "settings", "dsl", "add-apps", "add-rules", "onboarding", "empty", "error", "stopped", "unavailable" })
        {
            var vm = new ShellViewModel(scenario); var window = new MainWindow { DataContext = vm, Width = 1260, Height = 860 }; window.Show();
            Save(window, Path.Combine(directory, scenario + "-normal.png"));
            window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, scenario + "-compact.png"));
            if (scenario == "onboarding")
            {
                vm.NextSetupCommand.Execute(null); vm.ImportSubscriptionAsync().GetAwaiter().GetResult(); Save(window, Path.Combine(directory, "setup-step-2.png"));
                vm.NextSetupCommand.Execute(null); Save(window, Path.Combine(directory, "setup-step-3.png"));
                vm.SelectedGlobalTarget = vm.GlobalChoices.Single(choice => choice.Name == "手动选择"); Flush(); Check(Find<Button>(window, "SetupContinueButton").IsEffectivelyEnabled, "First-run manual group did not use its declared default member."); Save(window, Path.Combine(directory, "setup-manual-selection-required.png"));
                vm.SelectedSetupGlobalMember = vm.SetupGlobalMemberChoices[1]; Flush(); Check(Find<Button>(window, "SetupContinueButton").IsEffectivelyEnabled, "Explicit manual group selection did not enable Continue."); Save(window, Path.Combine(directory, "setup-manual-selection-ready.png"));
                vm.SelectedGlobalTarget = vm.GlobalChoices.First(); vm.NextSetupCommand.Execute(null); Save(window, Path.Combine(directory, "setup-step-4.png"));
                vm.NextSetupCommand.Execute(null); Save(window, Path.Combine(directory, "setup-step-5.png"));
            }
            window.Close();
        }
        ResetInteractions(directory);
        GroupInteractions(directory);
        BootstrapSearchInteractions(directory);
        Interactions(directory);
        ProductionCompositionChecks.Run(directory);
        Icon(directory);
        log.Verify();
        Console.WriteLine("Mac preview: connection-attempt phases/cancel/retry/production wording, connection filtering/sorting, truthful sparse observations, virtualization and viewport preservation; 13 states at normal/compact sizes, five setup steps, pointer/keyboard interactions, route inventory, DSL/subscription/apply recovery, binding checks, injected production first-run/native-PNG/empty-telemetry/error-redaction checks passed. Live operations blocked.");
        return 0;
    }
    private static void ResetInteractions(string directory)
    {
        var vm = new ShellViewModel("settings"); var window = new MainWindow { DataContext = vm, Width = 1260, Height = 860 }; window.Show(); Flush();
        var trigger = Find<Button>(window, "SettingsResetButton"); trigger.BringIntoView(); Flush();
        Save(window, Path.Combine(directory, "reset-settings-normal.png"));
        Click(window, trigger); Check(vm.ResetRequested, "Settings reset did not open its confirmation.");
        var cancel = Find<Button>(window, "CancelResetButton");
        Check(cancel.IsFocused, "Reset confirmation must initially focus the non-destructive choice.");
        Save(window, Path.Combine(directory, "reset-confirm-normal.png"));
        window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, "reset-confirm-compact.png"));
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Flush(); Check(!vm.ResetRequested && vm.Document.Nodes.Count > 0, "Escape did not preserve current setup.");
        vm.OnboardingVisible = true; vm.SetupStep = 4;
        vm.Document.Interfaces.NonHomeId = vm.Document.Interfaces.HomeId; vm.CompileDraft(); Flush();
        var errorTrigger = Find<Button>(window, "SetupResetButton");
        Save(window, Path.Combine(directory, "reset-error-compact.png")); Click(window, errorTrigger);
        var confirm = Find<Button>(window, "ConfirmResetButton"); var point = Center(window, confirm);
        window.MouseMove(point); Save(window, Path.Combine(directory, "reset-hover.png"));
        window.MouseDown(point, MouseButton.Left); Save(window, Path.Combine(directory, "reset-pressed.png"));
        window.MouseMove(new Point(2, 2)); window.MouseUp(new Point(2, 2), MouseButton.Left); Flush();
        confirm.Focus(NavigationMethod.Tab); Save(window, Path.Combine(directory, "reset-focused.png"));
        confirm.IsEnabled = false; Save(window, Path.Combine(directory, "reset-disabled.png")); confirm.IsEnabled = true;
        Click(window, confirm);
        Check(!vm.ResetRequested && vm.OnboardingVisible && vm.SetupStep == 0 && vm.Document.Nodes.Count == 0 && vm.SelectedHomeInterface is null,
            "Confirm reset did not return to a blank first setup step.");
        Check(!vm.ConnectionAttempt.IsVisible && !vm.HasIssue && !vm.PreviewRunning, "Reset left stale progress or runtime status.");
        Save(window, Path.Combine(directory, "reset-first-step-compact.png"));
        window.Width = 1260; window.Height = 860; Save(window, Path.Combine(directory, "reset-first-step-normal.png")); window.Close();
    }
    private static void GroupInteractions(string directory)
    {
        var vm = new ShellViewModel("nodes"); var window = new MainWindow { DataContext = vm, Width = 1260, Height = 860 }; window.Show(); Flush();
        vm.ApplyPreviewAsync().GetAwaiter().GetResult();
        var group = vm.AllGroups.Single(group => group.Name == "Claude"); group.Expanded = true; Flush();
        var list = Find<ListBox>(window, "ProxyGroupList"); list.ScrollIntoView(group); Flush();
        Save(window, Path.Combine(directory, "groups-expanded-normal.png"));
        window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, "groups-expanded-compact.png"));
        list.ScrollIntoView(group); Flush();
        var direct = window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.DataContext is GroupMemberRow { Id: GroupGraph.Direct });
        direct.BringIntoView(); Flush();
        int restarts = vm.Core.RestartCount; var point = Center(window, direct);
        window.MouseMove(point); Save(window, Path.Combine(directory, "groups-member-hover.png"));
        window.MouseDown(point, MouseButton.Left); Save(window, Path.Combine(directory, "groups-member-pressed.png")); window.MouseUp(point, MouseButton.Left); Flush();
        Check(group.Model.SelectedMemberId == GroupGraph.Direct && vm.Core.RestartCount == restarts && !vm.HasChanges, "A group selection did not hot-apply independently of TUN restart: selected=" + group.Model.SelectedMemberId + ", restarts=" + vm.Core.RestartCount + "/" + restarts + ", dirty=" + vm.HasChanges);
        var selected = window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.DataContext is GroupMemberRow { Id: GroupGraph.Direct });
        selected.Focus(NavigationMethod.Tab); Save(window, Path.Combine(directory, "groups-member-focused.png"));
        selected.IsEnabled = false; Save(window, Path.Combine(directory, "groups-member-disabled.png")); selected.IsEnabled = true;
        vm.OnboardingVisible = true; vm.SetupStep = 2; Save(window, Path.Combine(directory, "groups-retention-compact.png"));
        window.Width = 1260; window.Height = 860; Save(window, Path.Combine(directory, "groups-retention-normal.png"));
        window.Close();
    }
    private static void BootstrapSearchInteractions(string directory)
    {
        var vm = new ShellViewModel("nodes"); var window = new MainWindow { DataContext = vm, Width = 1260, Height = 860 }; window.Show(); Flush();
        var search = Find<TextBox>(window, "NodeSearch"); search.Focus(); window.KeyTextInput("东京"); Flush();
        Check(vm.VisibleNodes.Count == 1, "Node search did not receive compiled keyboard input.");
        Save(window, Path.Combine(directory, "nodes-search-normal.png")); window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, "nodes-search-compact.png"));
        search.Text = "no-match"; Flush(); Check(vm.NoNodesMatch, "Node search must show an explicit empty state."); Save(window, Path.Combine(directory, "nodes-search-empty.png"));
        search.Text = "";
        var expander = Find<Expander>(window, "SubscriptionExpander");
        expander.IsExpanded = true; Flush(); Save(window, Path.Combine(directory, "nodes-local-import-compact.png"));
        expander.IsExpanded = false; vm.PageIndex = 1; vm.AddAppsCommand.Execute(null); Flush();
        var catalogSearch = Find<TextBox>(window, "CatalogSearch"); catalogSearch.Focus(); window.KeyTextInput("Safari"); Flush();
        Check(vm.VisibleCatalog.Count == 1 && vm.VisibleCatalog[0].Name == "Safari", "Safari search did not retain the application row.");
        Save(window, Path.Combine(directory, "safari-search-compact.png")); window.Width = 1260; window.Height = 860; Save(window, Path.Combine(directory, "safari-search-normal.png"));
        vm.AddingApps = false; vm.OnboardingVisible = true; vm.SetupStep = 3; vm.CatalogQuery = ""; Flush();
        var originalRows = vm.VisibleCatalog.ToArray(); var selected = originalRows.First(row => row.CanSelect && row.Name != "Safari"); selected.IsSelected = true;
        var setupSearch = Find<TextBox>(window, "SetupApplicationSearch"); setupSearch.Focus(); window.KeyTextInput("Safari"); Flush();
        Check(vm.VisibleCatalog.Count == 1 && vm.VisibleCatalog[0].Name == "Safari" && selected.IsSelected,
            "First-run application search is missing or changed existing checkbox selections.");
        Save(window, Path.Combine(directory, "setup-safari-search-normal.png")); window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, "setup-safari-search-compact.png"));
        setupSearch.Text = "no-matching-app"; Flush(); Check(vm.NoCatalogMatch, "First-run application search needs an empty state.");
        setupSearch.Text = ""; Flush(); Check(vm.VisibleCatalog.SequenceEqual(originalRows) && selected.IsSelected && vm.Core.ApplyCount == 0,
            "Clearing first-run search must preserve rows/order/selection without changing runtime.");
        window.Close();
    }

    private static void PassiveReconciliation(string directory)
    {
        var vm=new ShellViewModel();string saved=PolicyYaml.Write(vm.Document);
        Check(!vm.HasChanges,"Passive reconciliation test must begin with a clean saved workspace.");
        var window=new MainWindow{DataContext=vm,Width=1260,Height=860};window.Show();Flush();
        Check(PolicyYaml.Write(vm.Document)==saved&&!vm.HasChanges,"Attaching compiled UI bindings changed saved policy or marked the workspace dirty.");
        Click(window,Find<Button>(window,"AppsNav"));Click(window,Find<Button>(window,"AddAppsButton"));
        Check(vm.AddingApps&&vm.SelectedCatalogCount==0,"Application catalog did not open without selection.");
        Check(PolicyYaml.Write(vm.Document)==saved&&!vm.HasChanges,"Opening the catalog changed policy or marked it dirty before an explicit selection: policySame="+(PolicyYaml.Write(vm.Document)==saved)+", dirty="+vm.HasChanges);
        vm.AddAppsCommand.ExecuteAsync().GetAwaiter().GetResult();Flush();
        Check(PolicyYaml.Write(vm.Document)==saved&&!vm.HasChanges,"Refreshing discovery metadata changed policy or marked the workspace dirty.");
        var cancel=window.GetVisualDescendants().OfType<Button>().First(button=>button.IsEffectivelyVisible&&button.Content as string=="取消");Click(window,cancel);
        Check(!vm.AddingApps&&PolicyYaml.Write(vm.Document)==saved&&!vm.HasChanges,"Canceling application discovery changed saved policy or dirty state.");
        // A previously saved but now-unresolved proxy must remain unresolved, never select the first option.
        vm.Document.Defaults.Global=ExitChoice.Proxy("saved-missing-global");vm.VisibleApps[0].Model.Via=ExitChoice.Proxy("saved-missing-app-target");vm.VisibleRules[0].Model.Via=ExitChoice.Proxy("saved-missing-rule-target");
        string unresolved=PolicyYaml.Write(vm.Document);vm.AddAppsCommand.ExecuteAsync().GetAwaiter().GetResult();Flush();
        Check(PolicyYaml.Write(vm.Document)==unresolved&&!vm.HasChanges,"Passive refresh replaced an unresolved saved proxy choice: global="+vm.Document.Defaults.Global.TargetId+", app="+vm.VisibleApps[0].Model.Via.TargetId+", rule="+vm.VisibleRules[0].Model.Via.TargetId+", dirty="+vm.HasChanges);
        Check(vm.SelectedGlobalTarget?.Id=="saved-missing-global"&&vm.VisibleApps[0].SelectedProxyTarget?.Id=="saved-missing-app-target"&&vm.VisibleRules[0].SelectedProxyTarget?.Id=="saved-missing-rule-target","Unresolved proxy placeholders did not reflect the saved identities.");
        Save(window,Path.Combine(directory,"passive-discovery-clean-state.png"));window.Close();
    }
    private static void Interactions(string directory)
    {
        var vm = new ShellViewModel("apps"); var window = new MainWindow { DataContext = vm, Width = 1260, Height = 860 }; window.Show(); Flush();
        var add = Find<Button>(window, "AddAppsButton"); var point = Center(window, add);
        Save(window, Path.Combine(directory, "controls-normal.png")); window.MouseMove(point); Save(window, Path.Combine(directory, "controls-hover.png"));
        window.MouseDown(point, MouseButton.Left); Save(window, Path.Combine(directory, "controls-pressed.png")); window.MouseUp(new Point(220, 10), MouseButton.Left); Flush();
        add.Focus(NavigationMethod.Tab); Save(window, Path.Combine(directory, "controls-focused.png"));
        var search = Find<TextBox>(window, "AppSearch"); search.Focus(); window.KeyTextInput("Claude"); Flush();
        Check(vm.AppQuery == "Claude" && vm.VisibleApps.Count == 1, "Compiled search binding did not receive keyboard input.");
        var inventory = vm.VisibleApps[0].Model.ExecutablePaths.ToArray();
        var picker = window.GetVisualDescendants().OfType<RoutePicker>().First(x => x.IsEffectivelyVisible);
        var home = picker.GetVisualDescendants().OfType<Button>().First(x => x.Name == "HomeButton"); Click(window, home);
        Check(vm.VisibleApps[0].RouteIndex == 0 && vm.VisibleApps[0].Model.ExecutablePaths.SequenceEqual(inventory), "Pointer route selection did not preserve exact inventory.");
        Save(window, Path.Combine(directory, "apps-search-route.png"));
        home.Focus(NavigationMethod.Tab); var sameRow = vm.VisibleApps[0]; vm.SelectedApp = sameRow;
        vm.ImportSubscriptionAsync().GetAwaiter().GetResult(); Flush();
        Check(ReferenceEquals(sameRow, vm.VisibleApps[0]) && ReferenceEquals(vm.SelectedApp, sameRow) && home.IsFocused, "Subscription metadata refresh replaced application rows or keyboard focus.");
        search.Text = ""; Flush(); Click(window, add); Check(vm.AddingApps, "Add application action failed.");
        var confirm = Find<Button>(window, "ConfirmAddAppsButton"); Check(!confirm.IsEffectivelyEnabled, "Unselected catalog confirmation should be disabled."); Save(window, Path.Combine(directory, "controls-disabled.png"));
        var catalogSearch = Find<TextBox>(window, "CatalogSearch"); catalogSearch.Focus(); window.KeyTextInput("Steam"); Flush();
        Check(vm.VisibleCatalog.Count == 1 && vm.VisibleCatalog[0].Name == "Steam", "Installed application search excluded a non-AI application.");
        var check = window.GetVisualDescendants().OfType<CheckBox>().First(x => x.IsEffectivelyVisible); Click(window, check);
        Check(vm.SelectedCatalogCount == 1, "Catalog checkbox selection failed."); int old = vm.ApplicationCount; Click(window, confirm);
        Check(vm.ApplicationCount == old + 1 && !vm.AddingApps, "Explicit application selection did not publish a saved inventory.");
        var list = Find<ListBox>(window, "ManagedAppList"); list.SelectedItem = vm.VisibleApps.First(); Flush(); Save(window, Path.Combine(directory, "apps-selected-inventory.png"));
        Click(window, Find<Button>(window, "RulesNav")); Check(vm.PageIndex == 2, "Sidebar navigation failed."); Click(window, Find<Button>(window, "AddRulesButton")); Flush();
        var ruleAdd = window.GetVisualDescendants().OfType<Button>().First(x => x.IsEffectivelyVisible && x.IsEffectivelyEnabled && x.Content as string == "添加集合"); int ruleCount = vm.RuleCount; Click(window, ruleAdd); Check(vm.RuleCount == ruleCount + 1 && !vm.AddingRules, "Rule catalog addition failed.");
        vm.PageIndex = 3; vm.SelectedGroup = vm.GroupChoices.First(x => x.Name == "手动选择"); vm.SelectedGroupMember = vm.GroupMemberChoices[1]; Flush(); Check(vm.SelectedGroupModel?.SelectedMemberId == vm.GroupMemberChoices[1].Id, "Manual group selection did not update unified policy."); Save(window, Path.Combine(directory, "nodes-manual-selection.png"));
        vm.PageIndex = 4; vm.SettingsTab = 2; vm.DslText = "version: [invalid"; vm.ApplyDslCommand.Execute(null); Flush(); Check(vm.HasIssue, "Invalid DSL did not show a current error."); Save(window, Path.Combine(directory, "dsl-error.png"));
        vm.ResetDslCommand.Execute(null); vm.ApplyDslCommand.Execute(null); Flush(); Check(!vm.HasIssue, "Corrected DSL retained a current error."); Save(window, Path.Combine(directory, "dsl-recovered.png"));
        vm.SettingsTab = 4; Click(window, Find<Button>(window, "LogDirectoryButton")); Save(window, Path.Combine(directory, "logs-directory.png"));
        Check(ArtworkContrast.IsLightArtwork(new byte[] { 255, 255, 255, 255 }, 4, 1, 1), "Pale artwork did not receive adaptive dark backing.");
        window.Close();
        ViewportRefresh(directory);
    }
    private static void ViewportRefresh(string directory)
    {
        var vm = new ShellViewModel("apps"); var window = new MainWindow { DataContext = vm, Width = 980, Height = 700 }; window.Show(); Flush();
        var list = Find<ListBox>(window, "ManagedAppList"); vm.SelectedApp = vm.VisibleApps[0]; Flush();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Check(scroll.Extent.Height > scroll.Viewport.Height, "Refresh viewport check requires meaningful scrolling content.");
        scroll.Offset = new Vector(0, 100); Flush();
        double middle = Center(window, list).Y;
        var route = list.GetVisualDescendants().OfType<RoutePicker>().OrderBy(control => Math.Abs(Center(window, control).Y - middle)).First();
        var button = route.GetVisualDescendants().OfType<Button>().First(); button.Focus(NavigationMethod.Tab); Flush();
        Check(button.IsFocused, "The viewport refresh check did not establish keyboard focus on a visible row.");
        Vector offset = scroll.Offset; Check(offset.Y > 0, "Refresh viewport check must begin at a nonzero scroll offset."); var rows = vm.VisibleApps; var selection = vm.SelectedApp;
        vm.ImportSubscriptionAsync().GetAwaiter().GetResult(); Flush();
        Check(ReferenceEquals(rows, vm.VisibleApps) && ReferenceEquals(selection, vm.SelectedApp) && button.IsFocused && scroll.Offset == offset, "Subscription refresh changed list rows, selection, keyboard focus or viewport: rows=" + ReferenceEquals(rows, vm.VisibleApps) + ", selection=" + ReferenceEquals(selection, vm.SelectedApp) + ", focus=" + button.IsFocused + ", offset=" + offset + " -> " + scroll.Offset);
        Save(window, Path.Combine(directory, "apps-refresh-preserved-viewport.png")); window.Close();
    }
    private static void Icon(string directory)
    {
        var icon = new AppIcon { Width = 1024, Height = 1024 }; icon.Measure(new Size(1024, 1024)); icon.Arrange(new Rect(0, 0, 1024, 1024));
        using var bitmap = new RenderTargetBitmap(new PixelSize(1024, 1024), new Vector(96, 96)); bitmap.Render(icon); bitmap.Save(Path.Combine(directory, "AppIcon.png"), new PngBitmapEncoderOptions());
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().First(x => x.Name == name);
    private static Point Center(Window window, Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Missing visible control coordinates.");
    private static void Click(Window window, Control control) { var p = Center(window, control); window.MouseMove(p); window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); Flush(); }
    private static void Flush() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Save(Window window, string path) { Flush(); using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame."); frame.Save(path, new PngBitmapEncoderOptions()); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
internal sealed class BindingLog : ILogSink
{
    private readonly List<string> _failures = [];
    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == "Binding";
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values) { if (IsEnabled(level, area)) _failures.Add(messageTemplate + " " + string.Join(" | ", values)); }
    public void Verify() { if (_failures.Count > 0) throw new InvalidOperationException("Binding failures: " + string.Join("\n", _failures.Distinct())); }
}
