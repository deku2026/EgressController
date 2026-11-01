using System.Net;

namespace EgressController.Policy;

public static class DnsEndpoint
{
    public static bool IsValid(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && uri.Port == 443 && uri.AbsolutePath == "/dns-query"
        && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsPublic(ip);
    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) return false;
        var bytes = ip.GetAddressBytes();
        return bytes.Length == 16 ? (bytes[0] & 0xfe) != 0xfc
            : bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254)
              && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 192 && bytes[1] == 168);
    }
}
