using System.Text.Json.Nodes;

namespace EgressController.Runtime;

/// <summary>Only validated route changes may use the private runtime API. No TUN/DNS/listener/outbound mutation.</summary>
public static class RuntimeRoutingUpdate
{
    public static async Task ReplaceAsync(string previous, string next, Func<string, CancellationToken, Task> replace, CancellationToken cancellationToken)
    {
        try { await replace(next, cancellationToken).ConfigureAwait(false); }
        catch (Exception)
        {
            // Cancellation/transport loss may follow a successful RPC. Restore the complete old table.
            try { await replace(previous, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { throw new RuntimeFailureException("recovery.failed"); }
            throw new RuntimeFailureException("routing.update");
        }
    }
    public static bool CanReplace(string previous, string next)
    {
        var before = JsonNode.Parse(previous)!.AsObject(); var after = JsonNode.Parse(next)!.AsObject();
        if (before["routing"]?["domainStrategy"]?.ToJsonString() != after["routing"]?["domainStrategy"]?.ToJsonString()) return false;
        before.Remove("routing"); after.Remove("routing");
        return JsonNode.DeepEquals(before, after);
    }
}
