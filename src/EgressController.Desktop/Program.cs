using Avalonia;
using EgressController.Desktop.Presentation;
using EgressController.Desktop.Preview;
namespace EgressController.Desktop;

public static class Program
{
    public static string Scenario { get; private set; } = "overview";
    [STAThread]
    public static int Main(string[] args)
    {
        PreviewEnvironment.Enter(); // Must precede framework, catalog, storage and runtime initialization.
        if (args.Contains("--live", StringComparer.Ordinal)) throw new InvalidOperationException("This build only supports isolated preview operations.");
        if (args.Contains("--mock-smoke", StringComparer.Ordinal)) { PreviewChecks.RunAsync().GetAwaiter().GetResult(); Console.WriteLine("Mac desktop mock smoke passed; live initialization was not entered."); return 0; }
        int index = Array.IndexOf(args, "--scenario"); if (index >= 0 && index + 1 < args.Length) Scenario = args[index + 1];
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp()
    {
        PreviewEnvironment.RequireIsolated();
        return AppBuilder.Configure<DesktopApplication>().WithInterFont().With(MacFonts.Options()).With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true }).UsePlatformDetect();
    }
}
