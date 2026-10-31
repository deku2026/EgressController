using Avalonia;
using EgressController.Desktop.Presentation;
using EgressController.Desktop.Preview;
namespace EgressController.Desktop;

public static class Program
{
    public static string Scenario { get; private set; } = "overview";
    public static StartupMode Mode { get; private set; } = StartupMode.Production;
    [STAThread]
    public static int Main(string[] args)
    {
        StartupSelection startup;
        try { startup = StartupArguments.Resolve(args); }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        try { StartupArguments.EnsureIsolationCompatible(startup.Mode, PreviewEnvironment.IsIsolated || Environment.GetEnvironmentVariable("EGRESS_MOCK_ONLY") == "1"); }
        catch (InvalidOperationException exception) { Console.Error.WriteLine(exception.Message); return 2; }
        Mode = startup.Mode; Scenario = startup.Scenario;
        if (Mode is StartupMode.Preview or StartupMode.MockSmoke) PreviewEnvironment.Enter();
        if (Mode == StartupMode.MockSmoke) { PreviewChecks.RunAsync().GetAwaiter().GetResult(); Console.WriteLine("Mac desktop mock smoke passed; production initialization was not entered."); return 0; }
        if (ProductionHostLauncher.NeedsHandoff(Mode, System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported, OperatingSystem.IsMacOS()))
        {
            try { return ProductionHostLauncher.ReplaceWithNativeProduct(AppContext.BaseDirectory); }
            catch (IOException error) { Console.Error.WriteLine(error.Message); return 3; }
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<DesktopApplication>().WithInterFont().With(MacFonts.Options()).With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true }).UsePlatformDetect();
    }
}
