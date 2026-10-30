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
namespace EgressController.Desktop;

public sealed class DesktopApplication : Application
{
    public DesktopApplication() => Name = "Egress";

    public override void Initialize()
    {
        PreviewEnvironment.RequireIsolated();
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
            desktop.MainWindow = new MainWindow { DataContext = new ShellViewModel(Program.Scenario) };
        base.OnFrameworkInitializationCompleted();
    }
}
