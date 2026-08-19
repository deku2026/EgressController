using System.ComponentModel;
using System.Diagnostics;
using EgressController.Core.Models;
using Windows.Win32;

namespace EgressController.Windows.Process;

/// <summary>
/// Starts a selected target and returns the actual root PID used to create the managed session.
/// Managed native/full-trust packaged executables use suspended CreateProcess semantics so their
/// verified root session is registered before any app code runs; ordinary launches use
/// <see cref="ProcessStartInfo"/>. Runtime-specific proxy controls reach the actual app process.
/// Packaged apps without a usable manifest executable fall back to IApplicationActivationManager,
/// which returns the actual package root PID instead of requiring a racy process-scan guess.
/// </summary>
public sealed class WindowsLaunchService
{
    private readonly WindowsProcessIdentityResolver _identity =
        new(new ExecutablePathCanonicalizer());

    /// <summary>Whether the most recent launch used the manifest/target executable directly.</summary>
    public bool DirectExecutableStarted { get; private set; }

    /// <summary>Whether Chromium command-line proxy controls were applied to the last launch.</summary>
    public bool ChromiumProxyArgumentsApplied { get; private set; }

    /// <summary>Whether the managed root was attached to an OS-maintained tracking Job.</summary>
    public bool JobTrackingApplied { get; private set; }

    public LaunchSession Start(
        LaunchTarget target,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        DirectExecutableStarted = false;
        ChromiumProxyArgumentsApplied = false;
        JobTrackingApplied = false;

        return target.Kind switch
        {
            LaunchKind.PackagedAumid => StartPackaged(target, environmentOverrides),
            LaunchKind.DirectExe or LaunchKind.CliNative => StartDirect(target, environmentOverrides),
            _ => throw new InvalidOperationException("该目标是未解析的 wrapper/shortcut，不能安全地建立 Managed 会话。"),
        };
    }

    /// <summary>
    /// Starts a Managed native target suspended, invokes <paramref name="registerBeforeResume"/>
    /// with its verified root identity, and only then lets application code execute. This removes
    /// the root process's first-connection race. AUMID activation cannot be suspended, so its
    /// callback runs immediately after IApplicationActivationManager returns the real root PID.
    /// </summary>
    public LaunchSession StartManaged(
        LaunchTarget target,
        IReadOnlyDictionary<string, string> environmentOverrides,
        Action<LaunchSession> registerBeforeResume)
    {
        WindowsProcessJob? job = null;
        try
        {
            return StartManagedTracked(
                target,
                environmentOverrides,
                (session, tracker) =>
                {
                    job = tracker;
                    registerBeforeResume(session);
                });
        }
        finally
        {
            job?.Dispose();
        }
    }

    /// <summary>
    /// Managed launch variant used by RouterHost. Ownership of a non-null tracking job transfers
    /// to <paramref name="registerBeforeResume"/> when the callback returns successfully.
    /// </summary>
    public LaunchSession StartManagedTracked(
        LaunchTarget target,
        IReadOnlyDictionary<string, string> environmentOverrides,
        Action<LaunchSession, WindowsProcessJob?> registerBeforeResume)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(environmentOverrides);
        ArgumentNullException.ThrowIfNull(registerBeforeResume);
        DirectExecutableStarted = false;
        ChromiumProxyArgumentsApplied = false;
        JobTrackingApplied = false;

        return target.Kind switch
        {
            LaunchKind.PackagedAumid => StartPackagedManaged(target, environmentOverrides, registerBeforeResume),
            LaunchKind.DirectExe or LaunchKind.CliNative => StartDirectManaged(target, environmentOverrides, registerBeforeResume),
            _ => throw new InvalidOperationException("该目标是未解析的 wrapper/shortcut，不能安全地建立 Managed 会话。"),
        };
    }

    private LaunchSession StartDirect(
        LaunchTarget target,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        LaunchSession session = StartNative(target, environmentOverrides);
        DirectExecutableStarted = true;
        return session;
    }

    private LaunchSession StartDirectManaged(
        LaunchTarget target,
        IReadOnlyDictionary<string, string> environmentOverrides,
        Action<LaunchSession, WindowsProcessJob?> registerBeforeResume)
    {
        LaunchSession session = StartNativeSuspended(target, environmentOverrides, registerBeforeResume);
        DirectExecutableStarted = true;
        return session;
    }

    /// <summary>
    /// Environment used for a managed native launch. A launched application must enter this
    /// controller before it can be classified by PID; inheriting the desktop's HTTP(S)_PROXY
    /// (often the upstream Clash port) would bypass the controller completely. Descendants inherit
    /// the same local proxy and the accept-time resolver then decides whether each executable is a
    /// scanned Managed component or an ordinary process routed upstream.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LocalProxyEnvironment(int localPort)
    {
        string endpoint = $"http://127.0.0.1:{localPort}";
        const string noProxy = "localhost,127.0.0.1,::1";
        string browserArguments = WindowsRuntimeProxyPolicy.ChromiumArguments(localPort);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HTTP_PROXY"] = endpoint,
            ["HTTPS_PROXY"] = endpoint,
            ["ALL_PROXY"] = endpoint,
            ["NO_PROXY"] = noProxy,
            ["http_proxy"] = endpoint,
            ["https_proxy"] = endpoint,
            ["all_proxy"] = endpoint,
            ["no_proxy"] = noProxy,
            // Newer Node releases require this opt-in before built-in fetch/http clients honor
            // HTTP_PROXY/HTTPS_PROXY. Older releases ignore the variable harmlessly.
            ["NODE_USE_ENV_PROXY"] = "1",
            // WebView2 does not use HTTP_PROXY. This variable is consumed only by WebView2 and
            // is otherwise inert, so all managed native launches can receive it safely.
            ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = browserArguments,
        };
    }

    private LaunchSession StartNative(
        LaunchTarget target,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        ProcessStartInfo psi = BuildNativeStartInfo(target, environmentOverrides);
        System.Diagnostics.Process process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Windows 没有返回启动进程。");
        try
        {
            uint pid = checked((uint)process.Id);
            ProcessIdentity identity = _identity.Resolve(pid)
                ?? throw new InvalidOperationException("进程启动后无法读取其身份，已拒绝建立 Managed 会话。");
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
        finally
        {
            process.Dispose();
        }
    }

    private ProcessStartInfo BuildNativeStartInfo(
        LaunchTarget target,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        string executable = target.CanonicalExecutable ?? target.Command
            ?? throw new InvalidOperationException("目标没有可执行文件路径。");
        if (!File.Exists(executable))
            throw new FileNotFoundException("目标文件不存在。", executable);

        string arguments = target.Arguments ?? string.Empty;
        bool chromiumProxy = TryGetLocalProxyPort(environmentOverrides, out int localPort)
            && WindowsRuntimeProxyPolicy.UsesChromiumCommandLine(executable);
        if (chromiumProxy)
        {
            EnsureNoExistingChromiumInstance(executable);
            arguments = WindowsRuntimeProxyPolicy.AppendChromiumArguments(arguments, localPort);
            ChromiumProxyArgumentsApplied = true;
        }

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(target.WorkingDirectory)
                ? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
                : target.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (target.Environment is not null)
            foreach ((string key, string value) in target.Environment)
                psi.Environment[key] = value;
        if (environmentOverrides is not null)
            foreach ((string key, string value) in environmentOverrides)
            {
                if (key.Equals("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", StringComparison.OrdinalIgnoreCase)
                    && psi.Environment.TryGetValue(key, out string? inherited)
                    && !string.IsNullOrWhiteSpace(inherited))
                {
                    psi.Environment[key] = inherited.Trim() + " " + value;
                }
                else
                {
                    psi.Environment[key] = value;
                }
            }
        return psi;
    }

    private LaunchSession StartNativeSuspended(
        LaunchTarget target,
        IReadOnlyDictionary<string, string> environmentOverrides,
        Action<LaunchSession, WindowsProcessJob?> registerBeforeResume)
    {
        ProcessStartInfo psi = BuildNativeStartInfo(target, environmentOverrides);
        var processLauncher = new WindowsProcessLauncher();
        LaunchedProcess launched = processLauncher.LaunchSuspendedTracked(
            psi.FileName,
            psi.Arguments,
            psi.WorkingDirectory,
            psi.Environment);

        bool resumed = false;
        bool jobTransferred = false;
        WindowsProcessJob? job = null;
        try
        {
            ProcessIdentity identity = _identity.Resolve(launched.Pid)
                ?? throw new InvalidOperationException("挂起进程启动后无法读取其身份，已拒绝建立 Managed 会话。");
            LaunchSession session = NewSession(target, identity);
            job = new WindowsProcessJob(launched.JobHandle);
            registerBeforeResume(session, job);
            jobTransferred = true;
            JobTrackingApplied = true;
            processLauncher.Resume(launched);
            resumed = true;
            return session;
        }
        catch
        {
            if (!resumed)
                KillProcessTree(launched.Pid);
            throw;
        }
        finally
        {
            PInvoke.CloseHandle(launched.ThreadHandle);
            PInvoke.CloseHandle(launched.ProcessHandle);
            if (!jobTransferred)
            {
                if (job is not null)
                    job.Dispose();
                else if (!launched.JobHandle.IsNull)
                    PInvoke.CloseHandle(launched.JobHandle);
            }
        }
    }

    private static void KillProcessTree(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(checked((int)pid));
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static bool TryGetLocalProxyPort(
        IReadOnlyDictionary<string, string>? environmentOverrides,
        out int localPort)
    {
        localPort = 0;
        if (environmentOverrides is null)
            return false;

        string? endpoint = environmentOverrides
            .FirstOrDefault(pair => pair.Key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase))
            .Value;
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            && uri.IsLoopback
            && uri.Port is > 0 and <= ushort.MaxValue
            && (localPort = uri.Port) > 0;
    }

    private void EnsureNoExistingChromiumInstance(string executable)
    {
        string fullPath = new ExecutablePathCanonicalizer().Canonicalize(executable)
            ?? Path.GetFullPath(executable);
        foreach (System.Diagnostics.Process candidate in GetProcessesSafely())
        {
            using (candidate)
            {
                ProcessIdentity? identity;
                try
                {
                    if (candidate.Id == Environment.ProcessId)
                        continue;
                    identity = _identity.Resolve(checked((uint)candidate.Id));
                }
                catch
                {
                    // Process exited or became inaccessible during enumeration.
                    continue;
                }

                if (identity is not null
                    && (string.Equals(identity.ExePathFinal, fullPath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(identity.ExePathObserved, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ExistingChromiumInstanceException(
                        $"检测到该 Chromium/Electron 应用已在运行 (PID {identity.Pid})。请先完全退出旧实例，再从控制器启动；旧实例无法在运行中补注入代理。");
                }
            }
        }
    }

    private LaunchSession StartPackaged(
        LaunchTarget target,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        // A full-trust MSIX manifest's Executable is the real desktop process. Starting it
        // directly is the only reliable way to pass the per-runtime proxy controls to Electron,
        // WebView2, Node, or another packaged desktop runtime. Some packages require activation;
        // those fall back to AUMID activation below and therefore still rely on System Proxy.
        if (!string.IsNullOrWhiteSpace(target.CanonicalExecutable)
            && File.Exists(target.CanonicalExecutable))
        {
            try
            {
                LaunchSession session = StartNative(target, environmentOverrides);
                DirectExecutableStarted = true;
                return session;
            }
            catch (Exception ex) when (ex is not ExistingChromiumInstanceException
                                       && CanFallbackToPackageActivation(ex))
            {
                ChromiumProxyArgumentsApplied = false;
                // The direct executable may require an AppX activation context. It has already
                // been terminated by StartNative when correlation failed, so activation will not
                // leave a duplicate managed root behind.
            }
        }

        return StartPackagedViaAumid(target);
    }

    private LaunchSession StartPackagedManaged(
        LaunchTarget target,
        IReadOnlyDictionary<string, string> environmentOverrides,
        Action<LaunchSession, WindowsProcessJob?> registerBeforeResume)
    {
        // AUMID activation may return an already-running single-instance process. Reject every
        // pre-existing executable from this package before either direct or AUMID launch so an
        // old desktop/web-wrapper instance can never be claimed by a new Managed session.
        EnsureNoExistingPackagedInstance(target);

        if (!string.IsNullOrWhiteSpace(target.CanonicalExecutable)
            && File.Exists(target.CanonicalExecutable))
        {
            bool registrationStarted = false;
            try
            {
                LaunchSession session = StartNativeSuspended(
                    target,
                    environmentOverrides,
                    (prepared, job) =>
                    {
                        registrationStarted = true;
                        registerBeforeResume(prepared, job);
                    });
                DirectExecutableStarted = true;
                return session;
            }
            catch (Exception ex) when (!registrationStarted
                                       && ex is not ExistingChromiumInstanceException
                                       && CanFallbackToPackageActivation(ex))
            {
                ChromiumProxyArgumentsApplied = false;
                // This package requires AppX activation context; activate it through its AUMID.
            }
        }

        (LaunchSession activated, WindowsProcessJob job) = StartPackagedViaAumidManaged(target);
        try
        {
            registerBeforeResume(activated, job);
            JobTrackingApplied = true;
            return activated;
        }
        catch
        {
            job.Dispose();
            KillProcessTree(activated.RootPid);
            throw;
        }
    }

    private static bool CanFallbackToPackageActivation(Exception exception)
        => exception is Win32Exception
            or UnauthorizedAccessException
            or FileNotFoundException
            or InvalidOperationException;

    private sealed class ExistingChromiumInstanceException(string message)
        : InvalidOperationException(message)
    {
    }

    private LaunchSession StartPackagedViaAumid(LaunchTarget target)
        => ActivatePackaged(target, trackWithJob: false).Session;

    private (LaunchSession Session, WindowsProcessJob Job) StartPackagedViaAumidManaged(LaunchTarget target)
    {
        (LaunchSession session, WindowsProcessJob? job) = ActivatePackaged(target, trackWithJob: true);
        return (session, job ?? throw new InvalidOperationException("Packaged root 没有 tracking Job。"));
    }

    private (LaunchSession Session, WindowsProcessJob? Job) ActivatePackaged(
        LaunchTarget target,
        bool trackWithJob)
    {
        if (string.IsNullOrWhiteSpace(target.Aumid))
            throw new InvalidOperationException("Package 缺少 AUMID，无法激活。");

        DateTime activationRequestedAtUtc = DateTime.UtcNow;
        uint rootPid = WindowsPackageActivator.ActivateApplication(target.Aumid, target.Arguments);
        try
        {
            ProcessIdentity identity = WaitForActivatedIdentity(rootPid)
                ?? throw new InvalidOperationException($"Windows 返回了 packaged root PID {rootPid}，但无法读取其进程身份。");
            if (trackWithJob && identity.StartTimeUtc < activationRequestedAtUtc)
            {
                throw new InvalidOperationException(
                    $"AUMID activation 返回了本次请求前已存在的 PID {rootPid}；拒绝把旧实例伪装成 Managed。");
            }
            if (identity.ExePathFinal is null || !IsUnderOwnedRoot(identity.ExePathFinal, target))
            {
                throw new InvalidOperationException(
                    $"AUMID activation 返回的 PID {rootPid} 不在 package OwnedRoot 内；拒绝猜测 Managed root。");
            }

            LaunchSession session = NewSession(target, identity);
            if (!trackWithJob)
                return (session, null);

            var launcher = new WindowsProcessLauncher();
            return (session, launcher.TrackRunningProcess(rootPid));
        }
        catch
        {
            if (trackWithJob)
                KillProcessTree(rootPid);
            throw;
        }
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

    private void EnsureNoExistingPackagedInstance(LaunchTarget target)
    {
        foreach (System.Diagnostics.Process candidate in GetProcessesSafely())
        {
            using (candidate)
            {
                ProcessIdentity? identity;
                try
                {
                    if (candidate.Id == Environment.ProcessId)
                        continue;
                    identity = _identity.Resolve(checked((uint)candidate.Id));
                }
                catch
                {
                    // Process exited or became inaccessible during enumeration.
                    continue;
                }

                if (identity?.ExePathFinal is not null && IsUnderOwnedRoot(identity.ExePathFinal, target))
                {
                    throw new InvalidOperationException(
                        $"检测到该 packaged app 已在运行 (PID {identity.Pid})。请先完全退出旧实例，再从控制器启动；AUMID 激活不能把旧实例伪装成本次 Managed 会话。");
                }
            }
        }
    }

    private static IEnumerable<System.Diagnostics.Process> GetProcessesSafely()
    {
        System.Diagnostics.Process[] processes;
        try { processes = System.Diagnostics.Process.GetProcesses(); }
        catch { yield break; }
        foreach (System.Diagnostics.Process process in processes)
            yield return process;
    }

    private static bool IsUnderOwnedRoot(string path, LaunchTarget target)
    {
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        foreach (string root in target.OwnedRoots)
        {
            string r = root.Replace('/', '\\').TrimEnd('\\');
            if (string.Equals(normalized, r, StringComparison.OrdinalIgnoreCase)
                || (normalized.Length > r.Length
                    && normalized.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                    && normalized[r.Length] == '\\'))
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
