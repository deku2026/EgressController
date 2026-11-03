using Avalonia;
using EgressController.Core.Protection;
using EgressController.Presentation;
namespace EgressController.MacApp;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        App.PreviewMode = args.Contains("--preview", StringComparer.Ordinal) || args.Contains("--mock-smoke", StringComparer.Ordinal);
        if (App.PreviewMode) { RuntimeSafety.EnableMockOnly(); Environment.SetEnvironmentVariable("EGRESS_MOCK_ONLY", "1"); }
        if (args.Contains("--mock-smoke", StringComparer.Ordinal))
        {
            var compiler = new EgressController.Surge.SurgeConfigCompiler();
            string config = compiler.Compile(new EgressController.Core.Routing.RoutingProfile { Applications = [new("fixture", "Fixture", "/Applications/Fixture.app/", true, new())] }, []);
            if (!config.Contains("PROCESS-NAME,/Applications/Fixture.app/,DIRECT", StringComparison.Ordinal) || !config.Contains("FINAL,DIRECT", StringComparison.Ordinal)) return 1;
            if (EgressController.Subscriptions.HealthEvaluator.Evaluate(new(), new(), [], false, false, DateTimeOffset.UtcNow).Refresh) return 1;
            Console.WriteLine("macOS mock smoke passed: compiler, health gate, no production initialization"); return 0;
        }
        return AppBuilder.Configure<App>().WithInterFont().With(DesktopFonts.Options()).UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
