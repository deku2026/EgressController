using System.Collections.Concurrent;
using EgressController.Core.Models;

namespace EgressController.Launcher.Discovery;

/// <summary>
/// Merges discovery results from the MSIX / Program Files / App-Paths / registry providers
/// into a deduplicated launch-target set (plan §1.5 / §Step 10). Dedup key is
/// <see cref="LaunchTarget.DiscoveryKey"/>; when two providers produce the same key we keep the
/// higher-quality entry (resolved canonical executable wins over command-only; packaged keys stay
/// separate from exe keys; a shortcut with real args keeps its own key).
/// </summary>
public sealed class LaunchTargetRegistry
{
    private readonly ConcurrentDictionary<string, LaunchTarget> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idByKey = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Add a discovered target; returns true if it was newly accepted, false if a strictly
    /// better same-key target already exists and this one was dropped.</summary>
    public bool Add(LaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            if (_idByKey.TryGetValue(target.DiscoveryKey, out string? existingId)
                && _byId.TryGetValue(existingId, out LaunchTarget? existing)
                && Quality(existing) >= Quality(target))
            {
                return false; // existing is at least as good; drop the duplicate
            }

            if (existingId is not null)
                _byId.TryRemove(existingId, out _); // replace the weaker entry

            string id = target.Id;
            _byId[id] = target;
            _idByKey[target.DiscoveryKey] = id;
            return true;
        }
    }

    public IReadOnlyList<LaunchTarget> All()
        => _byId.Values.OrderBy(t => t.Name).ToList();

    public void Clear()
    {
        lock (_gate)
        {
            _byId.Clear();
            _idByKey.Clear();
        }
    }

    public bool SetManaged(string id, bool managed)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out LaunchTarget? target))
                return false;
            target.Managed = managed;
            return true;
        }
    }

    public LaunchTarget? Get(string id)
        => _byId.TryGetValue(id, out var t) ? t : null;

    public int Count => _byId.Count;

    private static int Quality(LaunchTarget t)
    {
        int q = 0;
        if (!string.IsNullOrWhiteSpace(t.CanonicalExecutable)) q += 4;      // resolved final path
        else if (!string.IsNullOrWhiteSpace(t.Command)) q += 2;              // command-only
        if (!string.IsNullOrWhiteSpace(t.IconPath)) q += 2;                  // visible catalog icon
        if (t.OwnedExecutables.Count > 0) q += 1;                            // recursive executable inventory
        if (!string.IsNullOrWhiteSpace(t.Source)) q += 1;
        if (t.Managed) q += 1;
        if (t.Kind == LaunchKind.PackagedAumid && !string.IsNullOrWhiteSpace(t.Aumid)) q += 1;
        if (t.ResolutionUnsupported) q -= 8;                                 // wrapper we can't yet resolve
        return q;
    }
}
