namespace EgressController.Core.Models;

/// <summary>Keep an installed explicit binding through an outage; never choose another adapter.
/// A recovered adapter with a new alias/address produces a real configuration change.</summary>
public sealed class AdapterBindingCache
{
    private readonly Dictionary<Guid, AdapterSelection> _bindings = new();
    private readonly object _gate = new();

    public NetworkEnvironmentSnapshot Resolve(NetworkEnvironmentSnapshot live, IReadOnlyList<NetworkAdapterInfo>? observedAdapters = null)
    {
        lock (_gate)
        {
            AdapterSelection Bind(AdapterSelection adapter)
            {
                if (adapter.IsReady) _bindings[adapter.AdapterId] = adapter;
                if (adapter.IsReady || !_bindings.TryGetValue(adapter.AdapterId, out var previous)) return adapter;
                // A disconnected GUID must never bind to a different NIC that reused its alias.
                if (observedAdapters?.Any(other => other.Identity.Guid != adapter.AdapterId
                    && string.Equals(other.Identity.NameSnapshot, previous.Alias, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    _bindings.Remove(adapter.AdapterId);
                    return adapter;
                }
                return previous;
            }
            // Drop deselected adapters so a later user selection starts from current state.
            var selected = live.AllAdapters.Select(adapter => adapter.AdapterId).ToHashSet();
            foreach (Guid id in _bindings.Keys.Where(id => !selected.Contains(id)).ToArray()) _bindings.Remove(id);
            return live with
            {
                Adapters = live.Adapters.Select(Bind).ToArray(),
                DefaultAdapter = Bind(live.DefaultAdapter),
                ProxyAdapter = Bind(live.ProxyAdapter),
                DnsAdapter = Bind(live.DnsAdapter),
            };
        }
    }
}
