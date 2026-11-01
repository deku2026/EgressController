using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using EgressController.Desktop.Presentation;
using EgressController.Desktop.Preview;
using EgressController.Desktop.ViewModels;
using EgressController.Desktop.Views;
using EgressController.Desktop.Production;
using Avalonia.Threading;
namespace EgressController.Desktop;

public sealed class DesktopApplication : Application
{
    public DesktopApplication() => Name = "Egress";

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(new MacTheme());

        if (OperatingSystem.IsMacOS())
        {
            // Avalonia 12 exports this application menu in AfterSetup. Application is not
            // a TopLevel, so replacing its menu in OnFrameworkInitializationCompleted is too late.
            // Attach the menu itself here; Avalonia supplies the application-name holder.
            var settings = new NativeMenuItem
            {
                Header = "设置…",
                Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta)
            };
            settings.Click += (_, _) =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                    { MainWindow.DataContext: ShellViewModel vm })
                    vm.SettingsCommand.Execute(null);
            };
            var quit = new NativeMenuItem
            {
                Header = "退出 Egress",
                Gesture = new KeyGesture(Key.Q, KeyModifiers.Meta)
            };
            quit.Click += (_, _) =>
            {
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.TryShutdown();
            };
            var applicationMenu = new NativeMenu();
            applicationMenu.Add(settings);
            applicationMenu.Add(new NativeMenuItemSeparator());
            applicationMenu.Add(quit);
            NativeMenu.SetMenu(this, applicationMenu);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Argument dispatch enters mock safety before framework startup. A plain launch composes
            // production services; opening the window alone never starts TUN or installs the helper.
            var vm = PreviewEnvironment.IsIsolated ? new ShellViewModel(Program.Scenario) : new ShellViewModel(new ProductionClientBackend());
            var window = new MainWindow { DataContext = vm }; desktop.MainWindow = window;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            bool polling = false, closing = false, closed = false;
            timer.Tick += async (_, _) =>
            {
                if (polling || closing) return; polling = true;
                try { await vm.RefreshStatusAsync(); await vm.MaintainAutomaticGroupsAsync(); } finally { polling = false; }
            };
            window.Opened += async (_, _) =>
            {
                await vm.InitializeAsync();
                if (!vm.IsPreview && !closing) timer.Start();
            };
            window.Closing += async (_, args) =>
            {
                if (closed) return; args.Cancel = true; if (closing) return; closing = true; timer.Stop();
                try { await vm.DisposeAsync(); }
                finally { closed = true; window.Close(); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
