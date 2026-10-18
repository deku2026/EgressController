using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using EgressController.Guardian;

namespace EgressController.GuardTray;

public sealed class TrayApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(new EgressController.Presentation.DesktopTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var vm = new GuardManagementViewModel(new WindowsGuardManagementClient(Program.Paths), Program.Diagnostics, Program.Paths.LogDirectory);
            var window = new GuardWindow(vm);
            window.OpenDirectoryRequested += path =>
            {
                try
                {
                    EgressController.Core.Protection.RuntimeSafety.RequireLiveOperations();
                    if (!Directory.Exists(path)) throw new DirectoryNotFoundException("日志目录尚未创建：" + path);
                    using var process = System.Diagnostics.Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception error) { vm.ReportError(error.ToString()); }
            };
            var menu = new NativeMenu();
            void Show() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
            var open = new NativeMenuItem("打开主程序");
            open.Click += (_, _) =>
            {
                try
                {
                    string main = new GuardRegistrationStore(Program.Paths.RegistrationFile).Load()?.MainExecutable
                        ?? throw new InvalidOperationException("主程序路径未登记。");
                    if (!File.Exists(main)) throw new FileNotFoundException("主程序已移动或删除：" + main);
                    using var process = System.Diagnostics.Process.Start(new ProcessStartInfo(main) { UseShellExecute = true });
                }
                catch (Exception error) { vm.ReportError(error.Message); Show(); }
            };
            menu.Items.Add(open);
            var manage = new NativeMenuItem("查看 / 管理守护服务");
            manage.Click += (_, _) => Show();
            menu.Items.Add(manage);
            var start = new NativeMenuItem("启动守护服务");
            start.Click += async (_, _) => { await vm.StartCommand.ExecuteAsync(null); Show(); };
            menu.Items.Add(start);
            var stop = new NativeMenuItem("停止守护服务");
            stop.Click += async (_, _) => { await vm.StopCommand.ExecuteAsync(null); Show(); };
            menu.Items.Add(stop);
            menu.Items.Add(new NativeMenuItemSeparator());
            var exit = new NativeMenuItem("退出小托盘（服务继续运行）");
            exit.Click += (_, _) => { window.AllowExit = true; desktop.Shutdown(); };
            menu.Items.Add(exit);
            var normal = TrayIcons.Create(false, false);
            var red = TrayIcons.Create(true, false);
            var disabled = TrayIcons.Create(false, true);
            var tray = new TrayIcon { Icon = red, IsVisible = true, ToolTipText = "EgressController 独立守护", Menu = menu };
            tray.Clicked += (_, _) => Show();
            var icons = new Avalonia.Controls.TrayIcons { tray };
            TrayIcon.SetIcons(this, icons);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) =>
            {
                await vm.RefreshAsync();
                tray.Icon = vm.Disabled ? disabled : vm.Red ? red : normal;
                tray.ToolTipText = vm.Summary.Length > 100 ? vm.Summary[..100] : vm.Summary;
            };
            timer.Start();
            desktop.Exit += (_, _) => { timer.Stop(); tray.Dispose(); icons.Clear(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
