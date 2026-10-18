using EgressController.Diagnostics;

namespace EgressController.App.Services;

internal static class StartupDiagnostics
{
    internal static DiagnosticJournal Journal { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EgressController", "logs", "startup.log"));
    public static void Record(Exception error) => Journal.Report("主程序启动/未处理异常", "app.unhandled", error.Message, error);
}
