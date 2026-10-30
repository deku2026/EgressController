using EgressController.Policy;

namespace EgressController.Runtime;

public static class SubscriptionReconciler
{
    /// <summary>Refresh content without resetting global, application, rule or manual group selections.</summary>
    public static MacPolicyDocument Merge(MacPolicyDocument current, string sourceId, ClashImportResult imported)
    {
        if (!imported.IsSuccess)
            throw new InvalidOperationException("订阅结构验证失败，保留原有节点、分组与选择。");
        if (imported.Nodes.Any(node => node.SourceId != sourceId) || imported.Groups.Any(group => group.SourceId != sourceId)
            || imported.Providers.Any(provider => provider.SourceId != sourceId))
            throw new ArgumentException("导入内容不属于当前订阅来源。", nameof(imported));
        var read = PolicyYaml.Read(PolicyYaml.Write(current));
        var result = read.Document ?? throw new InvalidOperationException("The current policy cannot be copied.");
        // Neither the existing draft nor a reusable import result is mutated during reconciliation.
        var incoming = PolicyYaml.Read(PolicyYaml.Write(new MacPolicyDocument
        {
            Interfaces = current.Interfaces,
            Defaults = current.Defaults,
            Dns = current.Dns,
            Tun = current.Tun,
            Nodes = imported.Nodes.ToList(),
            Groups = imported.Groups.ToList(),
            Providers = imported.Providers.ToList()
        })).Document ?? throw new InvalidOperationException("The imported policy cannot be copied.");
        var incomingNodes = incoming.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var incomingGroups = incoming.Groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var incomingProviders = incoming.Providers.ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        foreach (var previous in result.Nodes.Where(node => node.SourceId == sourceId))
        {
            if (incomingNodes.ContainsKey(previous.Id)) continue;
            previous.Diagnostics.RemoveAll(diagnostic => diagnostic.Code == "subscription.removed");
            previous.Diagnostics.Add(new("subscription.removed", "该节点已从订阅移除，请重新选择。", ObjectId: previous.Id));
            incomingNodes.Add(previous.Id, previous);
        }
        foreach (var previous in result.Groups.Where(group => group.SourceId == sourceId))
        {
            if (incomingGroups.TryGetValue(previous.Id, out var replacement))
            {
                // A missing selected member stays selected and becomes a validation error.
                replacement.SelectedMemberId = previous.SelectedMemberId;
            }
            else
            {
                previous.Diagnostics.RemoveAll(diagnostic => diagnostic.Code == "subscription.removed");
                previous.Diagnostics.Add(new("subscription.removed", "该分组已从订阅移除，请重新选择。", ObjectId: previous.Id));
                incomingGroups.Add(previous.Id, previous);
            }
        }
        result.Nodes.RemoveAll(node => node.SourceId == sourceId);
        result.Groups.RemoveAll(group => group.SourceId == sourceId);
        foreach (var previous in result.Providers.Where(provider => provider.SourceId == sourceId))
        {
            if (incomingProviders.ContainsKey(previous.Id)) continue;
            previous.Diagnostics.RemoveAll(diagnostic => diagnostic.Code == "subscription.removed");
            previous.Diagnostics.Add(new("subscription.removed", "该节点来源已从订阅移除，请重新选择。", ObjectId: previous.Id));
            incomingProviders.Add(previous.Id, previous);
        }
        result.Providers.RemoveAll(provider => provider.SourceId == sourceId);
        result.Nodes.AddRange(incomingNodes.Values);
        result.Groups.AddRange(incomingGroups.Values);
        result.Providers.AddRange(incomingProviders.Values);
        return result;
    }
}
