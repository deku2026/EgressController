using System.Diagnostics;
using EgressController.Core.Models;
using EgressController.Core.Protection;

namespace EgressController.Windows.Process;

/// <summary>
/// Activates the catalog's primary entry. Launching does not create a routing session, inspect
/// descendants, or terminate a short-lived activation process. Windows may reuse an existing app.
/// </summary>
public sealed class WindowsLaunchService
{
    private readonly Action<ProcessStartInfo> _startExecutable;
    private readonly Action<string, string?> _activatePackaged;
    private readonly Func<string, bool> _fileExists;

    public WindowsLaunchService() : this(StartExecutable, ActivatePackaged, File.Exists) { }

    internal WindowsLaunchService(Action<ProcessStartInfo> startExecutable,
        Action<string, string?> activatePackaged, Func<string, bool> fileExists)
    {
        _startExecutable = startExecutable;
        _activatePackaged = activatePackaged;
        _fileExists = fileExists;
    }

    public void Launch(LaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.CanLaunch || target.Kind is not (LaunchKind.DirectExe or LaunchKind.PackagedAumid))
            throw new InvalidOperationException("尚未确定应用主入口，请刷新列表或指定主程序 EXE。");

        if (target.Kind == LaunchKind.PackagedAumid)
        {
            // Always use the registered application identity, even when a package EXE exists.
            _activatePackaged(target.Aumid!, target.Arguments);
            return;
        }

        string executable = target.CanonicalExecutable ?? target.Command!;
        if (!Path.IsPathFullyQualified(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("主程序必须是已确定的 EXE 绝对路径。");
        if (!_fileExists(executable))
            throw new FileNotFoundException("主程序文件不存在，请刷新列表或重新指定主程序。", executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = target.Arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(target.WorkingDirectory)
                ? Path.GetDirectoryName(executable)! : target.WorkingDirectory,
            UseShellExecute = false,
        };
        if (target.Environment is not null)
            foreach (var (key, value) in target.Environment) startInfo.Environment[key] = value;
        _startExecutable(startInfo);
    }

    private static void StartExecutable(ProcessStartInfo startInfo)
    {
        RuntimeSafety.RequireLiveOperations();
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows 未接受启动请求。");
        // Do not wait for a PID/window or kill a launcher that hands off to an existing instance.
    }

    private static void ActivatePackaged(string aumid, string? arguments)
    {
        RuntimeSafety.RequireLiveOperations();
        _ = WindowsPackageActivator.ActivateApplication(aumid, arguments);
    }
}
