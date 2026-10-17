using Avalonia;
using EgressController.Core.Protection;
using EgressController.Guardian;

namespace EgressController.GuardTray;

internal static class Program
{
    internal static GuardPaths Paths { get; private set; } = null!;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--mock-smoke"]) return GuardSmoke.Run();
        RuntimeSafety.RequireLiveOperations();
        string? owner = null;
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--owner") owner = args[i + 1];
        Paths = owner is null ? GuardPaths.Current : GuardPaths.ForSid(owner);
        if (args.Length >= 2 && args[0] == "--manage")
        {
            try
            {
                WindowsGuardManagementClient.ExecuteElevated(Paths, Enum.Parse<GuardAction>(args[1], false));
                return 0;
            }
            catch (Exception error)
            {
                try { File.WriteAllText(Path.Combine(Paths.DataDirectory, "management-error.log"), error.Message); } catch { }
                return 1;
            }
        }
        using var instance = new Mutex(true, @"Local\EgressController.GuardTray." + Paths.ServiceName, out bool created);
        if (!created) return 0;
        AppBuilder.Configure<TrayApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        return 0;
    }
}
