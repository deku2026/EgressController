using Avalonia;

namespace EgressController.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using SingleInstanceGuard? instance = SingleInstanceGuard.Acquire();
        if (instance is null)
            return;

        App.InstanceGuard = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        App.InstanceGuard = null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}
