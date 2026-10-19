using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using EgressController.Guardian;
using EgressController.Presentation;

namespace EgressController.GuardTray;

public sealed class TrayApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(DesktopTheme.CreateBaseTheme());
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
            void Show() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
            void OpenMain()
            {
                try
                {
                    EgressController.Core.Protection.RuntimeSafety.RequireLiveOperations();
                    string main = new GuardRegistrationStore(Program.Paths.RegistrationFile).Load()?.MainExecutable
                        ?? throw new InvalidOperationException("主程序路径未登记。");
                    if (!File.Exists(main)) throw new FileNotFoundException("主程序已移动或删除：" + main);
                    using var process = System.Diagnostics.Process.Start(new ProcessStartInfo(main) { UseShellExecute = true });
                }
                catch (Exception error) { vm.ReportError(error.Message); Show(); }
            }
            window.OpenMainRequested += OpenMain;
            window.ExitRequested += () => { window.AllowExit = true; desktop.Shutdown(); };
            var artwork = TrayIcons.Create();
            var nativeArtwork = Enum.GetValues<GuardVisualState>().ToDictionary(state => state, state => GuardianIcon.CreateBytes(state));
            window.Icon = artwork[GuardVisualState.Waiting];
            WindowsTrayPresenter? tray = null;
            try { tray = new WindowsTrayPresenter(Program.Paths.ServiceName, "Egress Guard", nativeArtwork[GuardVisualState.Waiting],
                "EgressController 独立守护", Show, () => GuardTrayCommands.Create(vm, OpenMain, Show,
                    () => { window.AllowExit = true; desktop.Shutdown(); }));
                tray.Failed += error => { vm.ReportError(error.Message); Show(); };
            }
            catch (Exception error) { window.TrayAvailable = false; vm.ReportError("通知区图标未能创建：" + error.Message); Show(); }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) =>
            {
                await vm.RefreshAsync();
                tray?.Update(nativeArtwork[vm.VisualState], vm.Summary.Length > 100 ? vm.Summary[..100] : vm.Summary);
                window.Icon = artwork[vm.VisualState];
            };
            timer.Start();
            desktop.Exit += (_, _) => { timer.Stop(); tray?.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
