namespace EgressController.Policy;

/// <summary>Selection is client-owned. Never substitutes a first member for missing user/probe state.</summary>
public static class GroupSelector
{
    public static GroupSelectionResult Resolve(string targetId, MacPolicyDocument doc, IReadOnlyList<GroupProbe>? probes = null, DateTimeOffset? now = null, TimeSpan? maxAge = null)
    {
        var errors = new List<PolicyDiagnostic>(); var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        var clock = now ?? DateTimeOffset.UtcNow; var age = maxAge ?? TimeSpan.FromMinutes(10);
        string? ResolveInner(string id)
        {
            if (resolved.TryGetValue(id, out var cached)) return cached;
            var result = ResolveUncached(id); resolved[id] = result; return result;
        }
        string? ResolveUncached(string id)
        {
            if (ancestors.Count >= 128) { errors.Add(new("proxy.depth", "代理嵌套深度超过安全限制。", ObjectId: id)); return null; }
            if (!ancestors.Add(id)) { errors.Add(new("proxy.cycle", "代理引用存在循环。", ObjectId: id)); return null; }
            try
            {
                if (doc.Nodes.FirstOrDefault(x => x.Id == id) is { } node)
                {
                    errors.AddRange(node.Diagnostics); errors.AddRange(PolicyValidator.ValidateNode(node));
                    if (!node.IsSupported || PolicyValidator.ValidateNode(node).Any()) return null;
                    if (node.DialerProxyId is { } dialer && ResolveInner(dialer) is null) return null;
                    return node.Id;
                }
                if (doc.Groups.FirstOrDefault(x => x.Id == id) is not { } group) { errors.Add(new("proxy.missing", "代理对象不存在或不是代理出口。", ObjectId: id)); return null; }
                errors.AddRange(group.Diagnostics);
                if (!group.IsSupported) return null;
                if (group.Kind == GroupKind.Select)
                {
                    if (group.SelectedMemberId is not { } selected || !group.MemberIds.Contains(selected, StringComparer.Ordinal)) { errors.Add(new("group.selection", "请先明确选择分组成员。", ObjectId: id)); return null; }
                    return ResolveInner(selected);
                }
                var candidates = new List<(string Id, double Latency)>();
                foreach (var member in group.MemberIds)
                {
                    var nodeId = ResolveInner(member);
                    if (nodeId is null) continue;
                    var probe = probes?.Where(x => x.NodeId == nodeId && string.Equals(x.TestUrl, group.TestUrl, StringComparison.Ordinal) && x.ObservedAt <= clock && clock - x.ObservedAt <= age).OrderByDescending(x => x.ObservedAt).FirstOrDefault();
                    if (probe is { Available: true } && double.IsFinite(probe.LatencyMilliseconds) && probe.LatencyMilliseconds >= 0) candidates.Add((nodeId, probe.LatencyMilliseconds));
                }
                if (candidates.Count == 0) { errors.Add(new("group.probes", "自动分组没有新鲜且成功的测速结果，未选择替代出口。", ObjectId: id)); return null; }
                if (group.Kind == GroupKind.Fallback) return candidates[0].Id;
                var fastest = candidates.MinBy(x => x.Latency);
                if (group.SelectedMemberId is { } previous)
                {
                    var previousNode = group.MemberIds.Contains(previous, StringComparer.Ordinal) ? ResolveInner(previous) : previous;
                    var current = candidates.FirstOrDefault(x => x.Id == previousNode);
                    if (current.Id is not null && current.Latency <= fastest.Latency + group.ToleranceMilliseconds) return current.Id;
                }
                return fastest.Id;
            }
            finally { ancestors.Remove(id); }
        }
        var selectedNode = ResolveInner(targetId);
        return new(errors.Any(x => x.Severity == DiagnosticSeverity.Error) ? null : selectedNode, errors.Distinct().ToList());
    }
}
