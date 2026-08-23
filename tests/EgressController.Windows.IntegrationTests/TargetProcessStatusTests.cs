using System.Diagnostics;
using EgressController.App;
using EgressController.Core.Models;

namespace EgressController.Windows.IntegrationTests;

public sealed class TargetProcessStatusTests
{
    [Fact]
    public async Task Exited_launch_root_is_reported_as_not_running_not_as_a_startup_error()
    {
        string? commandShell = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandShell) || !File.Exists(commandShell))
            Assert.Skip("Windows command shell is unavailable.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.TargetStatusTests", Guid.NewGuid().ToString("N"));
        string launchRoot = Path.Combine(root, "launch");
        Directory.CreateDirectory(launchRoot);
        string launchExecutable = Path.Combine(launchRoot, "egress-status-test-shell.exe");
        File.Copy(commandShell, launchExecutable);
        try
        {
            await using var controller = new AppController(root);
            LaunchTarget target = controller.AddExecutable(launchExecutable, "status-test-shell");
            controller.LaunchTarget(target.Id);

            LaunchSession session = Assert.Single(controller.Sessions.All());
            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(checked((int)session.RootPid));
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }

            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            string status;
            do
            {
                status = controller.GetTargetStatus(target.Id);
                if (status == "未运行")
                    break;
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Equal("未运行", status);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Background_process_with_the_same_executable_is_not_reported_as_a_running_app()
    {
        string? commandShell = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandShell) || !File.Exists(commandShell))
            Assert.Skip("Windows command shell is unavailable.");

        string root = Path.Combine(Path.GetTempPath(), "EgressController.TargetStatusTests", Guid.NewGuid().ToString("N"));
        string launchRoot = Path.Combine(root, "launch");
        Directory.CreateDirectory(launchRoot);
        string launchExecutable = Path.Combine(launchRoot, "egress-background-status-test.exe");
        File.Copy(commandShell, launchExecutable);
        System.Diagnostics.Process? background = null;
        try
        {
            await using var controller = new AppController(root);
            LaunchTarget target = controller.AddExecutable(launchExecutable, "background-status-test");
            controller.LaunchTarget(target.Id);

            LaunchSession session = Assert.Single(controller.Sessions.All());
            Kill(session.RootPid);
            await WaitForExitAsync(session.RootPid);

            background = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = launchExecutable,
                Arguments = "/d /c ping.exe -n 30 127.0.0.1 > nul",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            Assert.NotNull(background);
            Assert.False(background.HasExited);

            Assert.Equal("未运行", controller.GetTargetStatus(target.Id));
        }
        finally
        {
            await StopAsync(background);
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    private static async Task StopAsync(System.Diagnostics.Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 10 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), CancellationToken.None);
            }
        }
    }

    private static void Kill(uint processId)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
    }

    private static async Task WaitForExitAsync(uint processId)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(checked((int)processId));
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Process {processId} did not exit.");
    }
}
