using EgressController.Windows.Process;
using EgressController.Core.Models;
using Windows.Win32;

namespace EgressController.Windows.IntegrationTests;

/// <summary>Live smoke of the race-free suspended launch sequence (Step 09).</summary>
public class ProcessLauncherTests
{
    [Fact]
    public void Managed_native_launch_environment_uses_the_local_router_for_all_proxy_names()
    {
        IReadOnlyDictionary<string, string> environment = WindowsLaunchService.LocalProxyEnvironment(18081);

        Assert.Equal("http://127.0.0.1:18081", environment["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:18081", environment["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:18081", environment["ALL_PROXY"]);
        Assert.Equal(environment["HTTP_PROXY"], environment["http_proxy"]);
        Assert.Equal(environment["HTTPS_PROXY"], environment["https_proxy"]);
        Assert.Equal(environment["ALL_PROXY"], environment["all_proxy"]);
        Assert.Equal("localhost,127.0.0.1,::1", environment["NO_PROXY"]);
        Assert.Equal(environment["NO_PROXY"], environment["no_proxy"]);
        Assert.Equal("1", environment["NODE_USE_ENV_PROXY"]);
        Assert.Contains("--proxy-server=http://127.0.0.1:18081", environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"]);
        Assert.Contains("--disable-quic", environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"]);
    }

    [Fact]
    public void Chromium_runtime_detection_and_arguments_are_vendor_neutral()
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        string executable = Path.Combine(root, "SampleDesktop.exe");
        try
        {
            File.WriteAllBytes(executable, []);
            File.WriteAllBytes(Path.Combine(root, "icudtl.dat"), []);
            File.WriteAllBytes(Path.Combine(root, "resources.pak"), []);
            File.WriteAllBytes(Path.Combine(root, "resources", "app.asar"), []);

            Assert.True(WindowsRuntimeProxyPolicy.UsesChromiumCommandLine(executable));
            string arguments = WindowsRuntimeProxyPolicy.AppendChromiumArguments("--sample", 18081);
            Assert.StartsWith("--sample ", arguments);
            Assert.Contains("--proxy-server=http://127.0.0.1:18081", arguments);
            Assert.Contains("--proxy-bypass-list=", arguments);
            Assert.Contains("--disable-quic", arguments);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Chromium_detection_only_descends_into_versioned_browser_payloads()
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController", Guid.NewGuid().ToString("N"));
        string unrelated = Path.Combine(root, "unrelated-component");
        string versioned = Path.Combine(root, "128.0.6613.85");
        Directory.CreateDirectory(unrelated);
        Directory.CreateDirectory(versioned);
        string executable = Path.Combine(root, "BrowserLauncher.exe");
        try
        {
            File.WriteAllBytes(executable, []);
            WriteChromiumPayload(unrelated);
            Assert.False(WindowsRuntimeProxyPolicy.UsesChromiumCommandLine(executable));

            WriteChromiumPayload(versioned);
            Assert.True(WindowsRuntimeProxyPolicy.UsesChromiumCommandLine(executable));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Full_trust_packaged_target_starts_manifest_executable_directly()
    {
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(File.Exists(cmdExe));

        var target = new LaunchTarget
        {
            Id = "pkg-test:full-trust",
            Name = "Full-trust package test",
            Kind = LaunchKind.PackagedAumid,
            Command = cmdExe,
            CanonicalExecutable = cmdExe,
            PackageFamily = "EgressController.Test_00000",
            Aumid = "EgressController.Test_00000!App",
            Arguments = "/d /c ping 127.0.0.1 -n 6 >nul",
            OwnedRoots = new[] { Environment.SystemDirectory },
            OwnedExecutables = new[] { cmdExe },
        };

        var launcher = new WindowsLaunchService();
        LaunchSession? session = null;
        try
        {
            session = launcher.Start(target, WindowsLaunchService.LocalProxyEnvironment(18081));
            Assert.True(launcher.DirectExecutableStarted);
            Assert.True(session.RootPid > 0);
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(checked((int)session.RootPid));
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }
    }

    [Fact]
    public void Managed_packaged_launch_refuses_to_claim_a_preexisting_package_process()
    {
        string root = Path.Combine(Path.GetTempPath(), "EgressController", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "PackagedDesktop.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);

        System.Diagnostics.Process? existing = null;
        try
        {
            existing = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = "/d /c ping 127.0.0.1 -n 8 >nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(existing);

            var target = new LaunchTarget
            {
                Id = "pkg-test:existing",
                Name = "Existing package test",
                Kind = LaunchKind.PackagedAumid,
                Command = executable,
                CanonicalExecutable = executable,
                PackageFamily = "EgressController.Existing_00000",
                Aumid = "EgressController.Existing_00000!App",
                OwnedRoots = [root],
                OwnedExecutables = [executable],
                Managed = true,
            };

            var launcher = new WindowsLaunchService();
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                launcher.StartManaged(
                    target,
                    WindowsLaunchService.LocalProxyEnvironment(18081),
                    _ => Assert.Fail("An existing package process must not become a new session.")));

            Assert.Contains("已在运行", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (existing is not null)
            {
                try
                {
                    if (!existing.HasExited)
                        existing.Kill(entireProcessTree: true);
                    existing.WaitForExit(5000);
                }
                catch { }
                existing.Dispose();
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Managed_direct_launch_registers_verified_root_before_application_code_runs()
    {
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string marker = Path.Combine(Path.GetTempPath(), "EgressController", Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        var target = new LaunchTarget
        {
            Id = "managed-suspended-test",
            Name = "Managed suspended test",
            Kind = LaunchKind.DirectExe,
            Command = cmdExe,
            CanonicalExecutable = cmdExe,
            Arguments = $"/d /c >\"{marker}\" echo %HTTP_PROXY%",
            OwnedRoots = [Environment.SystemDirectory],
            OwnedExecutables = [cmdExe],
            Managed = true,
        };

        var launcher = new WindowsLaunchService();
        bool callbackRan = false;
        System.Diagnostics.Process? process = null;
        try
        {
            LaunchSession session = launcher.StartManaged(
                target,
                WindowsLaunchService.LocalProxyEnvironment(18081),
                prepared =>
                {
                    callbackRan = true;
                    Assert.Equal(prepared.RootPid, prepared.ActiveOwnedPids.Single());
                    process = System.Diagnostics.Process.GetProcessById(checked((int)prepared.RootPid));
                    Assert.False(process.HasExited);
                });

            Assert.True(callbackRan);
            Assert.Equal(session.RootPid, session.ActiveOwnedPids.Single());
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(8000));
            Assert.Equal("http://127.0.0.1:18081", File.ReadAllText(marker).Trim());
        }
        finally
        {
            process?.Dispose();
            if (File.Exists(marker))
                File.Delete(marker);
        }
    }

    [Fact]
    public async Task Managed_tracking_job_reports_the_root_and_does_not_kill_on_dispose()
    {
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var target = new LaunchTarget
        {
            Id = "managed-job-test",
            Name = "Managed Job test",
            Kind = LaunchKind.DirectExe,
            Command = cmdExe,
            CanonicalExecutable = cmdExe,
            Arguments = "/d /c ping 127.0.0.1 -n 8 >nul",
            OwnedRoots = [Environment.SystemDirectory],
            OwnedExecutables = [cmdExe],
            Managed = true,
        };

        var launcher = new WindowsLaunchService();
        WindowsProcessJob? job = null;
        System.Diagnostics.Process? process = null;
        try
        {
            LaunchSession session = launcher.StartManagedTracked(
                target,
                WindowsLaunchService.LocalProxyEnvironment(18081),
                (prepared, tracker) =>
                {
                    job = Assert.IsType<WindowsProcessJob>(tracker);
                    Assert.Contains(prepared.RootPid, job.SnapshotProcessIds());
                });

            Assert.True(launcher.JobTrackingApplied);
            process = System.Diagnostics.Process.GetProcessById(checked((int)session.RootPid));
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.Contains(session.RootPid, job!.SnapshotProcessIds());

            job.Dispose();
            job = null;
            Assert.False(process.HasExited);
        }
        finally
        {
            job?.Dispose();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                process.Dispose();
            }
        }
    }

    [Fact]
    public async Task Launches_suspended_assigns_job_then_resume_runs_and_exits()
    {
        var launcher = new WindowsProcessLauncher();
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var launched = launcher.LaunchSuspended(cmdExe, "/d /c exit 0", workingDirectory: Environment.SystemDirectory);
        try
        {
            Assert.True(launched.Pid > 0);

            using var proc = System.Diagnostics.Process.GetProcessById((int)launched.Pid);
            // While suspended, the child must NOT have run/exited yet.
            Assert.False(proc.WaitForExit(400), "process exited while still suspended (CREATE_SUSPENDED not honored)");

            launcher.Resume(launched);
            Assert.True(proc.WaitForExit(8000), "process did not run to completion after resume");
            proc.Refresh();
            Assert.True(proc.HasExited, "process did not report exited after resume+run");
        }
        finally
        {
            // Release the job + process/thread handles (child already exited; KILL_ON_JOB_CLOSE no-op).
            PInvoke.CloseHandle(launched.ProcessHandle);
            PInvoke.CloseHandle(launched.ThreadHandle);
            PInvoke.CloseHandle(launched.JobHandle);
        }
    }

    private static void WriteChromiumPayload(string directory)
    {
        File.WriteAllBytes(Path.Combine(directory, "icudtl.dat"), []);
        File.WriteAllBytes(Path.Combine(directory, "resources.pak"), []);
        File.WriteAllBytes(Path.Combine(directory, "chrome_100_percent.pak"), []);
    }
}
