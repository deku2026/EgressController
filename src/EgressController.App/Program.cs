using Avalonia;

namespace EgressController.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--mock-smoke"])
        {
            Environment.ExitCode = EgressController.Guardian.GuardSmoke.Run();
            return;
        }
        EgressController.Core.Protection.RuntimeSafety.RequireLiveOperations();
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
            .LogToTrace();
}
