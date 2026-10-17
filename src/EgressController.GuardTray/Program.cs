using Avalonia;
using EgressController.Core.Protection;
using EgressController.Guardian;
using EgressController.Diagnostics;

namespace EgressController.GuardTray;

internal static class Program
{
    internal static GuardPaths Paths { get; private set; } = null!;
    internal static DiagnosticJournal Diagnostics { get; private set; } = null!;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--mock-smoke"]) return GuardSmoke.Run();
        RuntimeSafety.RequireLiveOperations();
        string? owner = null;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--owner") owner = args[i + 1];
        Paths = owner is null ? GuardPaths.Current : GuardPaths.ForSid(owner);
        Diagnostics = new DiagnosticJournal(Path.Combine(Paths.TrayLogDirectory, "guard-tray.log"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) Diagnostics.Report("托盘异常", "tray.unhandled", error.Message, error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Diagnostics.Report("托盘后台任务", "tray.task", e.Exception.Message, e.Exception);
        if (args.Length >= 2 && args[0] == "--manage")
        {
            try
            {
                WindowsGuardManagementClient.ExecuteElevated(Paths, Enum.Parse<GuardAction>(args[1], false));
                return 0;
            }
            catch (Exception error)
            {
                Diagnostics.Report("管理员服务操作", "guard.elevated", error.Message, error);
                try { File.WriteAllText(Path.Combine(Paths.DataDirectory, "management-error.log"), DiagnosticJournal.Redact(error.ToString())); } catch { }
                return 1;
            }
        }
        using var instance = new Mutex(true, @"Local\EgressController.GuardTray." + Paths.ServiceName, out bool created);
        if (!created) return 0;
        AppBuilder.Configure<TrayApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        return 0;
    }
}
