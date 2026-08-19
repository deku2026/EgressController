using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using Windows.Win32.System.Threading;

namespace EgressController.Windows.Process;

/// <summary>A launched, not-yet-resumed process plus its optional owning job.</summary>
internal sealed record LaunchedProcess(uint Pid, HANDLE ProcessHandle, HANDLE ThreadHandle, HANDLE JobHandle);

/// <summary>
/// Launches a DirectExe suspended, assigns it to a Job, and the caller resumes the primary
/// thread — the plan's race-free sequence (plan §Step 09 "DirectExe launch"):
/// CreateProcessW(CREATE_SUSPENDED) → create+assign Job → register session(root pid) → ResumeThread.
/// Test-owned launches can request KILL_ON_JOB_CLOSE; live tracking jobs deliberately do not.
/// </summary>
internal sealed unsafe class WindowsProcessLauncher
{
    public LaunchedProcess LaunchSuspended(string executablePath, string? arguments = null, string? workingDirectory = null)
        => LaunchSuspendedCore(
            executablePath,
            arguments,
            workingDirectory,
            environment: null,
            assignJob: true,
            killOnJobClose: true);

    /// <summary>
    /// Suspended launch used by the live RouterHost. The job is retained for membership queries,
    /// but deliberately has no KILL_ON_JOB_CLOSE limit, so unmanaging or exiting the controller
    /// never terminates the user's application.
    /// </summary>
    public LaunchedProcess LaunchSuspendedTracked(
        string executablePath,
        string? arguments,
        string? workingDirectory,
        IEnumerable<KeyValuePair<string, string?>> environment)
        => LaunchSuspendedCore(
            executablePath,
            arguments,
            workingDirectory,
            environment,
            assignJob: true,
            killOnJobClose: false);

    /// <summary>Attaches an already activated packaged root to a non-terminating tracking job.</summary>
    public WindowsProcessJob TrackRunningProcess(uint processId)
    {
        HANDLE job = CreateJob(killOnJobClose: false);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            HANDLE processHandle = new(process.Handle);
            if (!PInvoke.AssignProcessToJobObject(job, processHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed for activated package root");
            return new WindowsProcessJob(job);
        }
        catch
        {
            PInvoke.CloseHandle(job);
            throw;
        }
    }

    private static LaunchedProcess LaunchSuspendedCore(
        string executablePath,
        string? arguments,
        string? workingDirectory,
        IEnumerable<KeyValuePair<string, string?>>? environment,
        bool assignJob,
        bool killOnJobClose)
    {
        string commandLine = "\"" + executablePath + "\"" + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim());

        STARTUPINFOW si = default;
        si.cb = (uint)sizeof(STARTUPINFOW);
        PROCESS_INFORMATION pi = default;

        char[] cmdBuffer = (commandLine + '\0').ToCharArray();
        char[]? environmentBlock = BuildEnvironmentBlock(environment);
        var cmdSpan = cmdBuffer.AsSpan();

        PROCESS_CREATION_FLAGS flags = PROCESS_CREATION_FLAGS.CREATE_SUSPENDED;
        if (environmentBlock is not null)
            flags |= PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT;

        bool ok;
        fixed (char* environmentPointer = environmentBlock)
        {
            ok = PInvoke.CreateProcess(
                lpApplicationName: null,
                lpCommandLine: ref cmdSpan,
                lpProcessAttributes: null,
                lpThreadAttributes: null,
                bInheritHandles: false,
                dwCreationFlags: flags,
                lpEnvironment: environmentPointer,
                lpCurrentDirectory: workingDirectory,
                lpStartupInfo: in si,
                lpProcessInformation: out pi);
        }

        if (!ok)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessW failed for '{executablePath}'");

        HANDLE job = default;
        try
        {
            if (assignJob)
            {
                job = CreateJob(killOnJobClose);
                if (!PInvoke.AssignProcessToJobObject(job, pi.hProcess))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
            }

            return new LaunchedProcess(pi.dwProcessId, pi.hProcess, pi.hThread, job);
        }
        catch
        {
            try
            {
                using var failed = System.Diagnostics.Process.GetProcessById(checked((int)pi.dwProcessId));
                if (!failed.HasExited)
                    failed.Kill(entireProcessTree: true);
            }
            catch { }
            if (!job.IsNull)
                PInvoke.CloseHandle(job);
            PInvoke.CloseHandle(pi.hThread);
            PInvoke.CloseHandle(pi.hProcess);
            throw;
        }
    }

    private static HANDLE CreateJob(bool killOnJobClose)
    {
        HANDLE job = PInvoke.CreateJobObject((SECURITY_ATTRIBUTES*)null, (PCWSTR)null);
        if (job.IsNull)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        if (!killOnJobClose)
            return job;

        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!PInvoke.SetInformationJobObject(
                    job,
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    &limits,
                    (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
            return job;
        }
        catch
        {
            PInvoke.CloseHandle(job);
            throw;
        }
    }

    /// <summary>Resume the primary thread so the process actually runs.</summary>
    public void Resume(LaunchedProcess process)
    {
        uint previousSuspendCount = PInvoke.ResumeThread(process.ThreadHandle);
        if (previousSuspendCount == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed");
    }

    private static char[]? BuildEnvironmentBlock(
        IEnumerable<KeyValuePair<string, string?>>? environment)
    {
        if (environment is null)
            return null;

        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in environment)
        {
            if (string.IsNullOrEmpty(key) || key.Contains('\0') || key.Contains('='))
                continue;
            if (value?.Contains('\0') == true)
                throw new ArgumentException($"Environment variable '{key}' contains a null character.", nameof(environment));
            values[key] = value ?? string.Empty;
        }

        string block = string.Concat(values.Select(pair => $"{pair.Key}={pair.Value}\0")) + "\0";
        return block.ToCharArray();
    }
}
