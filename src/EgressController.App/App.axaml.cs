using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EgressController.App.ViewModels;
using EgressController.Presentation;
using EgressController.Guardian;

namespace EgressController.App;

public partial class App : Application
{
    public AppController Controller { get; private set; } = null!;
    internal static SingleInstanceGuard? InstanceGuard { get; set; }

    private WindowsTrayPresenter? _tray;
    private DispatcherTimer? _trayTimer;
    private readonly ApplicationExitCoordinator _exitCoordinator = new();

    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { Controller = new(); }
            catch (Exception error)
            {
                Services.StartupDiagnostics.Record(error);
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                var journal = Services.StartupDiagnostics.Journal;
                var detail = new TextBox { Text = EgressController.Diagnostics.DiagnosticJournal.Redact(error.ToString()),
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                var open = new Button { Content = "打开日志目录" };
                open.Click += (_, _) =>
                {
                    try
                    {
                        Directory.CreateDirectory(journal.DirectoryPath);
                        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            journal.DirectoryPath) { UseShellExecute = true });
                    }
                    catch (Exception failure)
                    {
                        journal.Report("日志目录", "logs.open", failure.Message, failure);
                        detail.Text += "\n打开日志目录失败：" + failure.Message;
                    }
                };
                var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
                content.Children.Add(new TextBlock { Text = "主程序启动失败，保护名单未清空。", FontSize = 20 });
                content.Children.Add(new SelectableTextBlock { Text = "日志：" + journal.LogPath,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                content.Children.Add(open);
                content.Children.Add(detail);
                foreach (var issue in journal.Snapshot().Where(issue => issue.Code == "log.write"))
                    content.Children.Add(new SelectableTextBlock { Text = issue.Detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                desktop.MainWindow = new Window
                {
                    Title = "EgressController 启动失败", Width = 800, Height = 520,
                    Content = new ScrollViewer { Content = content },
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow { DataContext = new MainViewModel(Controller) };
            desktop.MainWindow = window;
            InstanceGuard?.StartActivationLoop(() => Dispatcher.UIThread.Post(() => ShowWindow(window)));
            try { CreateTray(desktop, window); }
            catch (Exception error) { Controller.ReportFailure("托盘异常", "tray.initialize", error.Message, error); window.TrayAvailable = false; }
            desktop.Exit += (_, _) =>
            {
                _trayTimer?.Stop();
                _tray?.Dispose();
                Controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        bool exiting = false;
        async Task Exit()
        {
            if (exiting) return; exiting = true;
            bool completed = await _exitCoordinator.ExitAsync(
                async () => await Controller.ShutdownAsync(), window.AllowApplicationExit, () => desktop.Shutdown());
            if (!completed) { exiting = false; ShowWindow(window); }
        }
        window.ExitRequested = Exit;
        byte[] artwork = GuardianIcon.CreateBytes(GuardVisualState.Healthy, application: true);
        _tray = new WindowsTrayPresenter("EgressController.Main." + Environment.UserName, "EgressController", artwork,
            "EgressController", () => ShowWindow(window), () => [
                new("打开 EgressController", null, "open", () => { ShowWindow(window); return Task.CompletedTask; }),
                new("退出主程序", "独立守护继续运行", "power", Exit, () => !exiting, true)]);
        _tray.Failed += error => Controller.ReportFailure("托盘异常", "tray.surface", error.Message, error);
        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trayTimer.Tick += (_, _) => _tray.Update(artwork, "EgressController · TUN " + Controller.TunStatus);
        _trayTimer.Start();
    }

    private static void ShowWindow(Window window)
    {
        window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }
}
