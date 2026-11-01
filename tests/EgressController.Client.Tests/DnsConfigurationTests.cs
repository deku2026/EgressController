using EgressController.Desktop.Preview;
using EgressController.Desktop.ViewModels;
using EgressController.Policy;

namespace EgressController.Client.Tests;

public sealed class DnsConfigurationTests
{
    [Fact]
    public async Task SubscriptionFailureCanBeRepairedInSetupWithoutResettingChoicesOrStartingCore()
    {
        PreviewEnvironment.Enter(); var document = PreviewFixtures.CreateDocument();
        document.Dns.HttpsUrl = "https://1.1.1.1/dns-query"; document.Dns.UpstreamIp = "1.1.1.1";
        var backend = new FixtureClientBackend { WorkspaceDocument = document, SimulateSubscriptionDnsFailure = true };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync(); shell.SetupStep = 1;
        Assert.Equal("https://1.1.1.1/dns-query", shell.DnsServer); // Saved choices are never silently migrated.
        var inventories = shell.Document.Applications.Select(a => string.Join('|', a.ExecutablePaths)).ToArray();
        string home = shell.Document.Interfaces.HomeId, nonhome = shell.Document.Interfaces.NonHomeId;
        await shell.ImportSubscriptionCommand.ExecuteAsync();
        Assert.Contains("DNS 查询", shell.CurrentIssue); Assert.Contains("超时", shell.CurrentIssue); Assert.DoesNotContain("subscription-fixture", shell.CurrentIssue);
        shell.UseAliDnsCommand.Execute(null); Assert.Equal(DnsEndpoint.DefaultHttpsUrl, shell.DnsServer); Assert.Equal(DnsEndpoint.DefaultIp, shell.Document.Dns.UpstreamIp);
        Assert.False(shell.HasIssue); Assert.Equal(home, shell.Document.Interfaces.HomeId); Assert.Equal(nonhome, shell.Document.Interfaces.NonHomeId);
        backend.SimulateSubscriptionDnsFailure = false; await shell.ImportSubscriptionCommand.ExecuteAsync();
        Assert.False(shell.HasIssue); Assert.NotEmpty(shell.Document.Nodes); Assert.Equal(0, backend.Core.RestartCount);
        Assert.Equal(inventories, shell.Document.Applications.Select(a => string.Join('|', a.ExecutablePaths)).ToArray());
        shell.UseCloudflareDnsCommand.Execute(null); Assert.Equal("https://1.1.1.1/dns-query", shell.DnsServer);
    }
}
