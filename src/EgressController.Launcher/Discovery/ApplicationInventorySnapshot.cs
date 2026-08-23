using EgressController.Core.Models;

namespace EgressController.Launcher.Discovery;

public sealed record ApplicationInventoryEntry
{
    public required string DiscoveryKey { get; init; }
    public required string DisplayName { get; init; }
    public required LaunchKind Kind { get; init; }
    public required IReadOnlyList<string> ExecutablePaths { get; init; }
    public required bool CanRoute { get; init; }
    public required bool CanLaunch { get; init; }
    public string? IconPath { get; init; }
    public string? Source { get; init; }
}

/// <summary>
/// Stable, compiler-facing view of the current application scan. It contains only normalized
/// executable paths; process IDs and launch sessions never participate in routing.
/// </summary>
public sealed class ApplicationInventorySnapshot
{
    private readonly IReadOnlyDictionary<string, ApplicationInventoryEntry> _byKey;

    private ApplicationInventorySnapshot(
        IReadOnlyList<ApplicationInventoryEntry> entries,
        DateTimeOffset capturedAtUtc)
    {
        Entries = entries;
        CapturedAtUtc = capturedAtUtc;
        _byKey = entries.ToDictionary(entry => entry.DiscoveryKey, StringComparer.Ordinal);
    }

    public DateTimeOffset CapturedAtUtc { get; }
    public IReadOnlyList<ApplicationInventoryEntry> Entries { get; }

    public static ApplicationInventorySnapshot Create(
        IEnumerable<LaunchTarget> targets,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var entries = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.DiscoveryKey))
            .Select(target => new ApplicationInventoryEntry
            {
                DiscoveryKey = target.DiscoveryKey,
                DisplayName = target.Name,
                Kind = target.Kind,
                ExecutablePaths = NormalizeExecutablePaths(target),
                CanRoute = target.CanRoute,
                CanLaunch = target.CanLaunch,
                IconPath = target.IconPath,
                Source = target.Source,
            })
            .OrderBy(entry => entry.DiscoveryKey, StringComparer.Ordinal)
            .ToArray();

        return new ApplicationInventorySnapshot(entries, capturedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public bool TryGet(string discoveryKey, out ApplicationInventoryEntry? entry)
        => _byKey.TryGetValue(discoveryKey, out entry);

    public IReadOnlyList<string> ExpandSelected(IEnumerable<string> discoveryKeys)
    {
        ArgumentNullException.ThrowIfNull(discoveryKeys);
        return discoveryKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .SelectMany(key => _byKey.TryGetValue(key, out ApplicationInventoryEntry? entry)
                ? entry.ExecutablePaths
                : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizeExecutablePaths(LaunchTarget target)
        => target.OwnedExecutables
            .Append(target.CanonicalExecutable)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeExecutablePath(path!))
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? NormalizeExecutablePath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path.Trim());
            return Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
