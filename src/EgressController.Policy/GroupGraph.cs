namespace EgressController.Policy;

/// <summary>Subscription graph identity is independent of retained presentation rows and route assignments.</summary>
public static class GroupGraph
{
    public const string Direct = "builtin:direct";
    public const string Reject = "builtin:reject";
    public const string DefaultTestUrl = "https://www.gstatic.com/generate_204";

    public static IReadOnlyList<string> Leaves(string target, MacPolicyDocument document)
    {
        var result = new List<string>(); var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id, int depth)
        {
            if (depth > 128 || !visited.Add(id)) return;
            if (id is Direct or Reject || document.Nodes.Any(node => node.Id == id)) { result.Add(id); return; }
            if (document.Groups.FirstOrDefault(group => group.Id == id) is { } group)
                foreach (string member in group.MemberIds) Visit(member, depth + 1);
        }
        Visit(target, 0); return result;
    }

    public static string Name(string id, MacPolicyDocument document) => id switch
    {
        Direct => "DIRECT · 非家宽直连", Reject => "REJECT · 拒绝连接",
        _ => document.Nodes.FirstOrDefault(node => node.Id == id)?.Name
            ?? document.Groups.FirstOrDefault(group => group.Id == id)?.Name ?? "缺失的成员"
    };

    public static string? ResourceNode(MacPolicyDocument document, IReadOnlyList<GroupProbe> probes, DateTimeOffset now)
    {
        string target = document.Defaults.Global.TargetId ?? "";
        var group = document.Groups.FirstOrDefault(group => group.Id == target);
        string url = group?.TestUrl ?? DefaultTestUrl;
        var candidates = Leaves(target, document).Where(id => document.Nodes.Any(node => node.Id == id && node.IsSupported))
            .Select(id => (Id: id, Probe: probes.Where(probe => probe.NodeId == id && probe.TestUrl == url
                && probe.ObservedAt <= now && now - probe.ObservedAt <= TimeSpan.FromMinutes(10)).MaxBy(probe => probe.ObservedAt)))
            .Where(item => item.Probe is { Available: true } && double.IsFinite(item.Probe.LatencyMilliseconds) && item.Probe.LatencyMilliseconds >= 0).ToArray();
        string? selected = GroupSelector.Resolve(target, document, probes, now).NodeId;
        return candidates.Length == 0 ? null : candidates.FirstOrDefault(item => item.Id == selected).Id ?? candidates.MinBy(item => item.Probe!.LatencyMilliseconds).Id;
    }
}
