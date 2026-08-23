using EgressController.Core.Models;
using EgressController.Windows.Process;

namespace EgressController.Windows.IntegrationTests;

public sealed class ProcessLauncherTests
{
    [Fact]
    public void Plain_launch_returns_the_real_root_identity_without_proxy_controls()
    {
        string executable = Path.Combine(Environment.SystemDirectory, "where.exe");
        Assert.True(File.Exists(executable));
        var target = new LaunchTarget
        {
            Id = "plain-where",
            Name = "where",
            Kind = LaunchKind.DirectExe,
            Command = executable,
            CanonicalExecutable = executable,
        };

        var launcher = new WindowsLaunchService();
        LaunchSession session = launcher.StartPlain(target);

        Assert.True(launcher.DirectExecutableStarted);
        Assert.Equal((uint)session.RootPid, session.CandidatePids.Single());
        Assert.Equal(target.Id, session.TargetId);
    }

    [Fact]
    public void Unresolved_targets_are_rejected_before_any_process_is_started()
    {
        var target = new LaunchTarget
        {
            Id = "shortcut",
            Name = "unresolved",
            Kind = LaunchKind.Shortcut,
            Command = "wrapper.lnk",
            ResolutionUnsupported = true,
        };

        Assert.Throws<InvalidOperationException>(() => new WindowsLaunchService().StartPlain(target));
    }

    [Fact]
    public void Packaged_target_uses_aumid_activation_even_when_its_executable_exists()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test process path is unavailable.");
        string ownedRoot = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("Test process directory is unavailable.");
        string? activatedAumid = null;
        string? activatedArguments = null;
        var launcher = new WindowsLaunchService((aumid, arguments) =>
        {
            activatedAumid = aumid;
            activatedArguments = arguments;
            return checked((uint)Environment.ProcessId);
        });
        var target = new LaunchTarget
        {
            Id = "packaged-test",
            Name = "packaged-test",
            Kind = LaunchKind.PackagedAumid,
            Aumid = "Contoso.Sample_123!App",
            Arguments = "--test-argument",
            CanonicalExecutable = executable,
            OwnedRoots = new[] { ownedRoot },
            OwnedExecutables = new[] { executable },
        };

        LaunchSession session = launcher.StartPlain(target);

        Assert.Equal(target.Aumid, activatedAumid);
        Assert.Equal(target.Arguments, activatedArguments);
        Assert.False(launcher.DirectExecutableStarted);
        Assert.Equal((uint)Environment.ProcessId, session.RootPid);
    }
}
