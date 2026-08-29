using EgressController.Core.Models;
using EgressController.Launcher.Discovery;

namespace EgressController.Launcher.Tests;

public sealed class SupportedApplicationCatalogTests
{
    [Theory]
    [InlineData("claude.exe")]
    [InlineData("chrome.exe")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    public void Known_ai_and_browser_process_names_are_supported(string processName)
        => Assert.True(SupportedApplicationCatalog.IsSupportedProcessStem(processName));

    [Theory]
    [InlineData("dotnet.exe")]
    [InlineData("powershell.exe")]
    [InlineData("RevoUPPort.exe")]
    [InlineData("node.exe")]
    public void Generic_and_cli_process_names_are_not_supported(string processName)
        => Assert.False(SupportedApplicationCatalog.IsSupportedProcessStem(processName));

    [Fact]
    public void Unsupported_discovery_kinds_are_never_exposed()
    {
        var target = new LaunchTarget
        {
            Id = "cli:test",
            Name = "Claude CLI",
            Kind = LaunchKind.CliNative,
            Command = @"C:\Tools\claude.exe",
            CanonicalExecutable = @"C:\Tools\claude.exe",
            OwnedExecutables = [@"C:\Tools\claude.exe"],
        };

        Assert.False(SupportedApplicationCatalog.IsSupported(target));
    }
}
