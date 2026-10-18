using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EgressController.App;
using EgressController.App.Preview;
using EgressController.App.ViewModels;
using EgressController.Core.Protection;
using EgressController.Presentation;

namespace EgressController.UiPreview;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        RuntimeSafety.EnableMockOnly();
        Environment.SetEnvironmentVariable("EGRESS_MOCK_ONLY", "1");
        int screenshotFlag = Array.IndexOf(args, "--screenshots");
        if (screenshotFlag >= 0)
        {
            if (screenshotFlag + 1 >= args.Length) throw new ArgumentException("Specify a screenshot output directory.");
            AppBuilder.Configure<PreviewApplication>().With(DesktopFonts.Options()).UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            Capture(Path.GetFullPath(args[screenshotFlag + 1]));
            return 0;
        }
        return AppBuilder.Configure<PreviewApplication>().With(DesktopFonts.Options()).UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    private static void Capture(string directory)
    {
        Directory.CreateDirectory(directory);
        var bindingLog = new BindingLog(); Avalonia.Logging.Logger.Sink = bindingLog;
        PreviewChecks.Run();
        RenderChecks.Buttons(directory);
        RenderChecks.GuardIcons(directory);
        foreach (var scenario in new[] { "ready", "offline", "starting", "empty", "telemetry", "failed" })
        {
            var controller = new PreviewController(scenario);
            var vm = new MainViewModel(controller);
            var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            window.Show();
            for (int i = 0; i < 60; i++) { controller.AdvanceSample(); vm.Refresh(); }
            vm.Connections.SelectedRow = vm.Connections.Rows.FirstOrDefault();
            IEnumerable<string> pages = scenario == "ready" ? vm.Pages.Select(page => page.Key) : ["overview", "network", "apps", "guardian", "protection", "events", "connections", "traffic"];
            foreach (string page in pages)
            {
                window.SelectPage(page);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
                frame.Save(Path.Combine(directory, $"{scenario}-{page}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            if (scenario == "ready")
            {
                window.Width = 1100; window.Height = 760;
                foreach (string page in vm.Pages.Select(page => page.Key))
                {
                    window.SelectPage(page); Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No compact frame.");
                    frame.Save(Path.Combine(directory, "compact-" + page + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
                // Exercise real view-model commands against the mock boundary.
                vm.Apps.Entries[0].Routing.IsSelected = false;
                if (controller.Profile.Applications.Any(item => item.DiscoveryKey == vm.Apps.Entries[0].DiscoveryKey))
                    throw new InvalidOperationException("Preview application toggle failed.");
                vm.Connections.CloseAllCommand.Execute(null);
                if (controller.ConnectionHistory.ActiveCount != 0) throw new InvalidOperationException("Preview connection action failed.");
            }
            var navigation = window.FindControl<ListBox>("Navigation")!;
            window.SelectPage("overview"); Dispatcher.UIThread.RunJobs();
            ((ListBoxItem)navigation.ContainerFromIndex(0)!).Focus(Avalonia.Input.NavigationMethod.Tab);
            window.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null); window.KeyRelease(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            if (vm.SelectedPage.Key != "network") throw new InvalidOperationException("Keyboard navigation failed: " + vm.SelectedPage.Key);
            window.FindControl<Button>("OpenLogsButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            if (vm.SelectedPage.Key != "events") throw new InvalidOperationException("Preview log-directory action escaped the mock UI.");
            window.Close();
        }
        foreach (string state in new[] { "starting", "guard-stopped", "guard-unavailable", "guard-main-absent", "guard-records" })
        {
            var backend = new PreviewController(state); backend.Guard.RefreshAsync().GetAwaiter().GetResult();
            var companion = new EgressController.GuardTray.GuardWindow(backend.Guard, preview: true) { AllowExit = true };
            companion.Show(); RenderChecks.Save(companion, Path.Combine(directory, "companion-" + state + ".png"));
            companion.Close();
        }
        var trayController = new PreviewController(); trayController.Guard.RefreshAsync().GetAwaiter().GetResult();
        var trayWindow = new EgressController.GuardTray.GuardWindow(trayController.Guard, preview: true) { AllowExit = true };
        trayWindow.Show(); RenderChecks.Save(trayWindow, Path.Combine(directory, "guard-window.png"));
        trayWindow.Width = 680; trayWindow.Height = 520;
        RenderChecks.Save(trayWindow, Path.Combine(directory, "guard-window-compact.png"));
        string? requestedDirectory = null;
        trayWindow.OpenDirectoryRequested += path => requestedDirectory = path;
        var serviceDetails = trayWindow.GetVisualDescendants().OfType<Expander>().First();
        serviceDetails.IsExpanded = true; Dispatcher.UIThread.RunJobs();
        trayWindow.GetVisualDescendants().OfType<Button>().First(button => button.Content as string == "打开服务日志目录")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        if (requestedDirectory != trayController.Guard.LogDirectory) throw new InvalidOperationException("Guard log-directory request failed.");
        bool openRequested = false;
        trayWindow.OpenMainRequested += () => openRequested = true;
        trayWindow.GetVisualDescendants().OfType<Button>().First(button => button.Content as string == "打开主程序")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        if (!openRequested) throw new InvalidOperationException("Companion main-launch request did not reach its host.");
        trayWindow.Close();
        bindingLog.Verify();
        bool blocked = false;
        try { RuntimeSafety.RequireLiveOperations(); }
        catch (InvalidOperationException) { blocked = true; }
        if (!blocked) throw new InvalidOperationException("Preview live-operation guard is not enabled.");
        Console.WriteLine("Rendered all preview pages and states. Mock actions passed; live operations are blocked.");
    }
}

public sealed class PreviewApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme()); Styles.Add(new DesktopTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var controller = new PreviewController();
            var vm = new MainViewModel(controller);
            desktop.MainWindow = new MainWindow { DataContext = vm, Title = "EgressController — UI 开发模式" };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => controller.AdvanceSample(); timer.Start();
            desktop.Exit += (_, _) => timer.Stop();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
