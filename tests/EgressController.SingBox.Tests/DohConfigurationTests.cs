using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohConfigurationTests
{
    [Fact]
    public void Dns_configuration_is_fixed_to_cloudflare()
    {
        Assert.Equal("dns-global", EgressDohConfiguration.CloudflareTag);
        Assert.Equal("cloudflare-dns.com", EgressDohConfiguration.Server);
        Assert.Equal(443, EgressDohConfiguration.ServerPort);
        Assert.Equal("/dns-query", EgressDohConfiguration.Path);
        Assert.Equal("cloudflare-dns.com", EgressDohConfiguration.ServerName);
    }
}
