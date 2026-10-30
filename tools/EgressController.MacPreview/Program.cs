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

namespace EgressController.MacPreview;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        PreviewEnvironment.Enter();
        PreviewChecks.RunAsync().GetAwaiter().GetResult();
        int flag = Array.IndexOf(args, "--screenshots"); if (flag < 0) flag = Array.IndexOf(args, "--output");
        if (flag < 0 || flag + 1 >= args.Length) throw new ArgumentException("Use --screenshots <absolute-output-directory>.");
        string directory = Path.GetFullPath(args[flag + 1]); Directory.CreateDirectory(directory);
        AppBuilder.Configure<DesktopApplication>().WithInterFont().With(MacFonts.Options()).UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var log = new BindingLog(); Logger.Sink = log;
        foreach (string scenario in new[] { "overview", "apps", "rules", "nodes", "settings", "dsl", "add-apps", "add-rules", "onboarding", "empty", "error", "stopped", "unavailable" })
        {
            var vm = new ShellViewModel(scenario); var window = new MainWindow { DataContext = vm, Width = 1260, Height = 860 }; window.Show();
            Save(window, Path.Combine(directory, scenario + "-normal.png"));
            window.Width = 980; window.Height = 700; Save(window, Path.Combine(directory, scenario + "-compact.png"));
            if (scenario == "onboarding")
            {
                vm.NextSetupCommand.Execute(null); vm.ImportSubscriptionAsync().GetAwaiter().GetResult(); Save(window, Path.Combine(directory, "setup-step-2.png"));
                vm.NextSetupCommand.Execute(null); Save(window, Path.Combine(directory, "setup-step-3.png"));
                vm.SelectedGlobalTarget = vm.GlobalChoices.Single(choice => choice.Name == "手动选择"); Flush(); Check(!Find<Button>(window, "SetupContinueButton").IsEffectivelyEnabled, "First-run manual group did not disable Continue until an explicit member choice."); Save(window, Path.Combine(directory, "setup-manual-selection-required.png"));
                vm.SelectedSetupGlobalMember = vm.SetupGlobalMemberChoices[1]; Flush(); Check(Find<Button>(window, "SetupContinueButton").IsEffectivelyEnabled, "Explicit manual group selection did not enable Continue."); Save(window, Path.Combine(directory, "setup-manual-selection-ready.png"));
                vm.SelectedGlobalTarget = vm.GlobalChoices.First(); vm.NextSetupCommand.Execute(null); Save(window, Path.Combine(directory, "setup-step-4.png"));
                vm.NextSetupCommand.Execute(null); Save(window, Path.Combine(directory, "setup-step-5.png"));
            }
            window.Close();
        }
        Interactions(directory);
        Icon(directory);
        log.Verify();
        Console.WriteLine("Mac preview: 13 states at normal/compact sizes, five setup steps, pointer/keyboard interactions, route inventory, DSL/subscription/apply recovery, and binding checks passed. Live operations blocked.");
        return 0;
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
