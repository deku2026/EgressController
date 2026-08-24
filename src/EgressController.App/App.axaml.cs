using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EgressController.App.ViewModels;

namespace EgressController.App;

public partial class App : Application
{
    public AppController Controller { get; } = new();
    internal static SingleInstanceGuard? InstanceGuard { get; set; }

    private TrayIcon? _tray;
    private TrayIcons? _trayIcons;
    private DispatcherTimer? _trayTimer;
    private readonly ApplicationExitCoordinator _exitCoordinator = new();

    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow { DataContext = new MainViewModel(Controller) };
            desktop.MainWindow = window;
            InstanceGuard?.StartActivationLoop(() => Dispatcher.UIThread.Post(() => ShowWindow(window)));
            CreateTray(desktop, window);
            desktop.Exit += (_, _) =>
            {
                _trayTimer?.Stop();
                _tray?.Dispose();
                _trayIcons?.Clear();
                Controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("打开 EgressController");
        open.Click += (_, _) => ShowWindow(window);
        var toggle = new NativeMenuItem("启动/停止 TUN");
        toggle.Click += (_, _) => _ = Controller.ToggleTunAsync();
        var exit = new NativeMenuItem("关闭 EgressController");
        exit.Click += async (_, _) =>
        {
            exit.IsEnabled = false;
            await _exitCoordinator.ExitAsync(
                async () => await Controller.StopTunAsync(),
                window.AllowApplicationExit,
                () => desktop.Shutdown());
        };
        menu.Items.Add(open);
        menu.Items.Add(toggle);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        using var iconStream = new MemoryStream(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABJSURBVDhPYxBSMPlPCWZAFyAVww1g2P+fJEx7A/b/xwJek2jAgweYNtPXAHSw/yqJBlDsgoE3AAN8/f/fg1gDCGHqG0AuptgAABRIRg3R7e/MAAAAAElFTkSuQmCC"));
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            Menu = menu,
            ToolTipText = "EgressController · TUN 已停止",
            IsVisible = true,
        };
        _trayIcons = new TrayIcons { _tray };
        TrayIcon.SetIcons(this, _trayIcons);

        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trayTimer.Tick += (_, _) =>
        {
            if (_tray is not null)
                _tray.ToolTipText = "EgressController · TUN " + Controller.TunStatus;
        };
        _trayTimer.Start();
    }

    private static void ShowWindow(Window window)
    {
        window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }
}
