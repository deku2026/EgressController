using EgressController.Policy;

namespace EgressController.Runtime;

public sealed record RequiredGroupProbe(string NodeId, string TestUrl);

/// <summary>Only discovers required observations. GroupSelector remains the sole group-selection authority.</summary>
public static class RequiredGroupProbes
{
    public static bool NeedsProbes(MacPolicyDocument document, XrayCompileOptions options) => Missing(document, options).Count > 0;

    public static IReadOnlyList<RequiredGroupProbe> Missing(MacPolicyDocument document, XrayCompileOptions options)
    {
        // Invalid routes need explicit repair, not network work against a widened substitute policy.
        if (PolicyValidator.Validate(document).Any(item => item.Severity == DiagnosticSeverity.Error)) return [];
        var groups = document.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var nodes = document.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal); var pending = new HashSet<(string NodeId, string Url)>();
        var result = new List<RequiredGroupProbe>();
        void Visit(string id, int depth)
        {
            if (depth >= 128 || !visited.Add(id)) return;
            if (nodes.TryGetValue(id, out var node))
            {
                if (node.DialerProxyId is { } dialer) Visit(dialer, depth + 1);
                return;
            }
            if (!groups.TryGetValue(id, out var group) || !group.IsSupported) return;
            if (group.Kind == GroupKind.Select)
            {
                if (group.SelectedMemberId is { } selected && group.MemberIds.Contains(selected, StringComparer.Ordinal)) Visit(selected, depth + 1);
                return;
            }
            if (group.Kind is not (GroupKind.UrlTest or GroupKind.Fallback)) return;
            foreach (string member in group.MemberIds) Visit(member, depth + 1);
            foreach (string member in group.MemberIds)
            {
                // Nested automatic selections and automatic dialer groups must resolve from their own
                // observations first. Replan after measuring that dependency instead of probing all branches.
                var resolved = GroupSelector.Resolve(member, document, options.Probes, options.Now, options.ProbeMaxAge);
                if (resolved.NodeId is not { } nodeId || !resolved.IsSuccess) continue;
                var latest = options.Probes.Where(probe => probe.NodeId == nodeId && string.Equals(probe.TestUrl, group.TestUrl, StringComparison.Ordinal)
                    && probe.ObservedAt <= options.Now && options.Now - probe.ObservedAt <= options.ProbeMaxAge)
                    .OrderByDescending(probe => probe.ObservedAt).FirstOrDefault();
                if (latest is not null && double.IsFinite(latest.LatencyMilliseconds) && latest.LatencyMilliseconds >= 0) continue;
                if (pending.Add((nodeId, group.TestUrl))) result.Add(new(nodeId, group.TestUrl));
            }
        }
        void Exit(ExitChoice exit) { if (exit.Kind == ExitKind.Proxy && exit.TargetId is { } id) Visit(id, 0); }
        Exit(document.Defaults.Global);
        foreach (var application in document.Applications.Where(application => application.Enabled)) Exit(application.Via);
        foreach (var rule in document.RuleSets.Where(rule => rule.Enabled)) Exit(rule.Via);
        return result;
    }
}
