using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using EgressController.App.ViewModels;

namespace EgressController.App;

public partial class App : Application
{
    public RouterHost Host { get; private set; } = new();
    internal static SingleInstanceGuard? InstanceGuard { get; set; }

    private TrayIcon? _tray;
    private TrayIcons? _trayIcons;
    private DispatcherTimer? _trayTimer;

    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = new MainViewModel(Host) };
            desktop.MainWindow = window;
            EventHandler<EsimConnectivityChangedEventArgs> connectivityHandler = (_, args) =>
            {
                if (!args.IsOnline)
                    Dispatcher.UIThread.Post(() => _ = ShowEsimWarningAsync(window, args));
            };
            Host.EsimConnectivityChanged += connectivityHandler;

            // Subscribe to fail-closed alerts before the background start can observe an
            // initially disconnected eSIM.
            System.Threading.Tasks.Task.Run(Host.Start);
            InstanceGuard?.StartActivationLoop(() => Dispatcher.UIThread.Post(() => ShowWindow(window)));
            CreateTray(desktop, window);
            desktop.Exit += (_, _) =>
            {
                Host.EsimConnectivityChanged -= connectivityHandler;
                _trayTimer?.Stop();
                _tray?.Dispose();
                _trayIcons?.Clear();
                Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("打开 EgressController");
        open.Click += (_, _) => ShowWindow(window);
        var stop = new NativeMenuItem("停止路由并恢复代理");
        stop.Click += (_, _) => Host.StopRouting();
        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => desktop.Shutdown();
        menu.Items.Add(open);
        menu.Items.Add(stop);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        using var iconStream = new MemoryStream(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABJSURBVDhPYxBSMPlPCWZAFyAVww1g2P+fJEx7A/b/xwJek2jAgweYNtPXAHSw/yqJBlDsgoE3AAN8/f/fg1gDCGHqG0AuptgAABRIRg3R7e/MAAAAAElFTkSuQmCC"));
        _tray = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            Menu = menu,
            ToolTipText = "EgressController · 启动中",
            IsVisible = true,
        };
        _trayIcons = new TrayIcons { _tray };
        TrayIcon.SetIcons(this, _trayIcons);

        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trayTimer.Tick += (_, _) =>
        {
            if (_tray is not null)
                _tray.ToolTipText = "EgressController · " + Host.SystemProxySummary;
        };
        _trayTimer.Start();
    }

    private static void ShowWindow(Window window)
    {
        window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }

    private static async Task ShowEsimWarningAsync(
        MainWindow window,
        EsimConnectivityChangedEventArgs args)
    {
        try
        {
            ShowWindow(window);
            await window.ShowEsimDisconnectedWarningAsync(args);
        }
        catch
        {
            // The fail-closed gate is owned by RouterHost and remains active even if the window
            // is closing while the notification is being displayed.
        }
    }
}
