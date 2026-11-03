using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EgressController.Mac;
using EgressController.MacApp.Preview;
using EgressController.MacApp.ViewModels;
using EgressController.Presentation;
using EgressController.SubscriptionUI;
using EgressController.Subscriptions;

namespace EgressController.MacApp;

public partial class App : Application
{
    public static bool PreviewMode { get; set; }
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this); Styles.Add(DesktopTheme.CreateBaseTheme()); Styles.Add(new DesktopTheme());
    }
    private static async Task RunSchedulerAsync(SubscriptionScheduler scheduler, CancellationToken token)
    { try { await scheduler.RunAsync(token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { } }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IMacWorkspace workspace = PreviewMode ? new MockWorkspace() : new MacWorkspace(MacSettings.DataDirectory);
            ISubscriptionSession subscriptions = PreviewMode ? new PreviewSubscriptionSession() : new DesktopSubscriptionSession(MacSettings.DataDirectory, new KeychainSecretStore());
            SubscriptionScheduler? scheduler = null;
            if (!PreviewMode)
            {
                scheduler = new SubscriptionScheduler(MacSettings.DataDirectory, subscriptions, new SurgeNodeHealthSource(MacSettings.DataDirectory, new KeychainSecretStore()));
                subscriptions = new DesktopSubscriptionSession(MacSettings.DataDirectory, new KeychainSecretStore(), health: (_, token) => scheduler.CheckOnceAsync(false, token));
            }
            var lifetime = new CancellationTokenSource();
            var vm = new MacMainViewModel(workspace, new SubscriptionViewModel(subscriptions));
            var window = new MainWindow { DataContext = vm }; desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Opened += async (_, _) => { await vm.InitializeAsync(); if (scheduler is not null) _ = RunSchedulerAsync(scheduler, lifetime.Token); };
            TrayIcon? tray = null;
            if (!PreviewMode)
            {
                var menu = new NativeMenu();
                var show = new NativeMenuItem("打开 EgressController"); show.Click += (_, _) => { window.Show(); window.Activate(); };
                var subscriptionsItem = new NativeMenuItem("机场订阅"); subscriptionsItem.Click += (_, _) => { vm.Navigate("subscriptions"); window.Show(); window.Activate(); };
                var logs = new NativeMenuItem("打开日志目录") { Command = vm.OpenLogsCommand };
                var quit = new NativeMenuItem("退出 EgressController"); quit.Click += (_, _) => desktop.Shutdown();
                menu.Add(show); menu.Add(subscriptionsItem); menu.Add(logs); menu.Add(new NativeMenuItemSeparator()); menu.Add(quit);
                using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EgressController.MacApp/Assets/App.png"));
                tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = "EgressController · Surge 管理", Menu = menu, IsVisible = true };
                tray.Clicked += (_, _) => { window.Show(); window.Activate(); };
                TrayIcon.SetIcons(this, new TrayIcons { tray });
            }
            window.Closed += (_, _) => { lifetime.Cancel(); tray?.Dispose(); vm.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
