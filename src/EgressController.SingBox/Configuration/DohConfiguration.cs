namespace EgressController.SingBox.Configuration;

/// <summary>Fixed DNS configuration only. Internet health never queries or selects a DoH provider.</summary>
public static class EgressDohConfiguration
{
    public const string CloudflareTag = "dns-global";
    public const string Server = "cloudflare-dns.com";
    public const int ServerPort = 443;
    public const string Path = "/dns-query";
    public const string ServerName = "cloudflare-dns.com";
}
