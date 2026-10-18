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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Services.StartupDiagnostics.Record(error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Services.StartupDiagnostics.Record(e.Exception);
        using SingleInstanceGuard? instance = SingleInstanceGuard.Acquire();
        if (instance is null)
            return;

        App.InstanceGuard = instance;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception error) { Services.StartupDiagnostics.Record(error); throw; }
        finally { App.InstanceGuard = null; }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
