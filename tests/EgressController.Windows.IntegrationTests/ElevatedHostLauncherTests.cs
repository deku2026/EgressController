using System.Diagnostics;
using EgressController.App.Services;

namespace EgressController.Windows.IntegrationTests;

public sealed class ElevatedHostLauncherTests
{
    [Fact]
    public void Elevated_app_starts_host_without_a_second_UAC_handoff()
    {
        ProcessStartInfo startInfo = ElevatedHostLauncher.CreateStartInfo(
            @"C:\EgressController\EgressController.ElevatedHost.exe",
            "--pipe test --client-pid 1 --data-root C:\\EgressController",
            isElevated: true);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.NotEqual("runas", startInfo.Verb, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Development_fallback_still_requests_UAC_when_app_is_not_elevated()
    {
        ProcessStartInfo startInfo = ElevatedHostLauncher.CreateStartInfo(
            @"C:\EgressController\EgressController.ElevatedHost.exe",
            "--pipe test --client-pid 1 --data-root C:\\EgressController",
            isElevated: false);

        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb, StringComparer.OrdinalIgnoreCase);
    }
}
