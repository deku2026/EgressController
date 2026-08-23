using System.Diagnostics;
using EgressController.Core.Models;

namespace EgressController.Windows.Process;

/// <summary>
/// Starts a selected Windows target with its ordinary environment. Network routing is transparent
/// through sing-box TUN; this service never adds proxy variables, browser arguments, or a local
/// proxy listener to a child process.
/// </summary>
public sealed class WindowsLaunchService
{
    private readonly WindowsProcessIdentityResolver _identity =
        new(new ExecutablePathCanonicalizer());
    private readonly Func<string, string?, uint> _activatePackaged;

    public WindowsLaunchService()
        : this(WindowsPackageActivator.ActivateApplication)
    {
    }

    internal WindowsLaunchService(Func<string, string?, uint> activatePackaged)
        => _activatePackaged = activatePackaged ?? throw new ArgumentNullException(nameof(activatePackaged));

    public bool DirectExecutableStarted { get; private set; }

    public LaunchSession StartPlain(LaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        DirectExecutableStarted = false;
        return target.Kind switch
        {
            LaunchKind.PackagedAumid => StartPackaged(target),
            LaunchKind.DirectExe or LaunchKind.CliNative => StartDirect(target),
            _ => throw new InvalidOperationException("该目标尚未解析为可安全启动的 Windows 应用。"),
        };
    }

    private LaunchSession StartDirect(LaunchTarget target)
    {
        string executable = target.CanonicalExecutable ?? target.Command
            ?? throw new InvalidOperationException("目标没有可执行文件路径。");
        if (!File.Exists(executable))
            throw new FileNotFoundException("目标文件不存在。", executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = target.Arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(target.WorkingDirectory)
                ? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
                : target.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (target.Environment is not null)
        {
            foreach ((string key, string value) in target.Environment)
                startInfo.Environment[key] = value;
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows 没有返回启动进程。");
        try
        {
            ProcessIdentity identity = _identity.Resolve(checked((uint)process.Id))
                ?? throw new InvalidOperationException("进程启动后无法读取其身份。");
            DirectExecutableStarted = true;
            return NewSession(target, identity);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
    }

    private LaunchSession StartPackaged(LaunchTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Aumid))
            throw new InvalidOperationException("Package 缺少 AUMID，无法启动。");

        // Activating by AUMID returns the app instance that fulfilled the launch contract.
        // Starting the package EXE directly can return a short-lived delegation process and
        // leaves status tracking to an unsafe machine-wide same-path guess.
        uint rootPid = _activatePackaged(target.Aumid, target.Arguments);
        ProcessIdentity identity = WaitForActivatedIdentity(rootPid)
            ?? throw new InvalidOperationException($"Windows 返回了 packaged root PID {rootPid}，但无法读取其进程身份。");
        if (identity.ExePathFinal is null || !IsUnderOwnedRoot(identity.ExePathFinal, target))
            throw new InvalidOperationException("Package 激活返回的进程不在目标 OwnedRoot 内。");
        return NewSession(target, identity);
    }

    private ProcessIdentity? WaitForActivatedIdentity(uint processId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            ProcessIdentity? identity = _identity.Resolve(processId);
            if (identity is not null)
                return identity;
            Thread.Sleep(40);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    private static bool IsUnderOwnedRoot(string path, LaunchTarget target)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        foreach (string root in target.OwnedRoots)
        {
            string normalizedRoot = root.Replace('/', '\\').TrimEnd('\\');
            if (string.Equals(normalized, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || (normalized.Length > normalizedRoot.Length
                    && normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    && normalized[normalizedRoot.Length] == '\\'))
                return true;
        }
        return false;
    }

    private static LaunchSession NewSession(LaunchTarget target, ProcessIdentity identity)
        => new()
        {
            SessionId = Guid.NewGuid(),
            TargetId = target.Id,
            RootPid = identity.Pid,
            RootStartTimeUtc = identity.StartTimeUtc,
            StartedAtUtc = DateTime.UtcNow,
            CandidatePids = new HashSet<uint> { identity.Pid },
            ActiveOwnedPids = new HashSet<uint> { identity.Pid },
            ActiveOwnedProcessStartTimes = new Dictionary<uint, DateTime>
            {
                [identity.Pid] = identity.StartTimeUtc,
            },
        };
}
