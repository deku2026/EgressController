using EgressController.Runtime;

namespace EgressController.Mac;

/// <summary>Resolves configured egress identities, independently of link, IP and Internet health.</summary>
public sealed record MacRuntimeInterfaces(string Home, string NonHome)
{
    public static MacRuntimeInterfaces Resolve(IReadOnlyList<MacNetworkInterface> interfaces, string homeId, string nonHomeId)
    {
        string ResolveOne(string id)
        {
            var matches = interfaces.Where(item => item.ServiceId == id).ToArray();
            if (matches.Length != 1 || !Physical(matches[0].BsdName)) throw new RuntimeFailureException("interface.identity");
            // An unplugged or disconnected service still has a configured BSD name. Do not inspect
            // IsActive or Addresses: Xray's per-socket bind enforces this exit when traffic is sent.
            return matches[0].BsdName;
        }
        string home = ResolveOne(homeId), nonHome = ResolveOne(nonHomeId);
        if (home == nonHome) throw new RuntimeFailureException("interface.identity");
        return new(home, nonHome);
    }

    private static bool Physical(string name) => name.Length is > 0 and <= 32
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        && name != "lo0" && !name.StartsWith("utun", StringComparison.Ordinal);
}
