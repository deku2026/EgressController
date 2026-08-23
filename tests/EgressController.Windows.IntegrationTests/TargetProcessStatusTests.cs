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
}
