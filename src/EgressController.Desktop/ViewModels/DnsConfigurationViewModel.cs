using EgressController.Policy;

namespace EgressController.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private bool _subscriptionDnsIssue;
    private bool _showSubscriptionDnsRetry;
    public bool SubscriptionDnsIssue { get => _subscriptionDnsIssue; private set => Set(ref _subscriptionDnsIssue, value); }
    public bool ShowSubscriptionDnsRetry { get => _showSubscriptionDnsRetry; private set => Set(ref _showSubscriptionDnsRetry, value); }
    public UiCommand UseAliDnsCommand { get; private set; } = null!;
    public UiCommand UseCloudflareDnsCommand { get; private set; } = null!;
    private void InitializeDnsCommands()
    {
        UseAliDnsCommand = new(() => DnsServer = DnsEndpoint.DefaultHttpsUrl, () => CanEdit);
        UseCloudflareDnsCommand = new(() => DnsServer = "https://1.1.1.1/dns-query", () => CanEdit);
    }
}
