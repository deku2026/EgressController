// SPDX-License-Identifier: MIT
using System.Collections.Immutable;

namespace EgressController.Mac;

public sealed record MacCatalogDiagnostic(string Path, string Code, string Message);

/// <summary>Discovery metadata is never executable membership. A bundle's helpers are not separate apps.</summary>
public sealed record MacApplication(
    string Id,
    string BundleIdentifier,
    string DisplayName,
    string BundlePath,
    string? PrimaryExecutablePath,
    ImmutableArray<string> ExecutablePaths,
    bool InventoryReady,
    ImmutableArray<MacCatalogDiagnostic> Diagnostics);

public sealed record MacApplicationDiscovery(ImmutableArray<MacApplication> Applications,
    bool IsComplete, ImmutableArray<MacCatalogDiagnostic> Diagnostics);

public sealed record MacExecutableInventory(string ApplicationId, string BundlePath,
    ImmutableArray<string> ExecutablePaths, bool IsComplete, ImmutableArray<MacCatalogDiagnostic> Diagnostics);

public sealed record MacBundleMetadata(string BundleIdentifier, string DisplayName,
    string BundlePath, string? PrimaryExecutablePath);

/// <summary>Saved membership can only change through accepting an explicit complete inventory.</summary>
public sealed record MacManagedApplication(MacApplication Application, string RouteId,
    ImmutableArray<string> SavedExecutablePaths)
{
    public MacManagedApplication WithRoute(string routeId) => this with { RouteId = routeId };

    public MacManagedApplication RefreshMetadata(MacApplication discovered)
    {
        if (Application.Id != discovered.Id) throw new ArgumentException("Application identity changed.", nameof(discovered));
        return this with { Application = discovered with
        {
            ExecutablePaths = SavedExecutablePaths,
            InventoryReady = Application.InventoryReady && (discovered.PrimaryExecutablePath is null
                || SavedExecutablePaths.Contains(discovered.PrimaryExecutablePath, StringComparer.Ordinal)),
        } };
    }

    public MacManagedApplication AcceptExplicitInventory(MacExecutableInventory inventory)
    {
        if (inventory.ApplicationId != Application.Id || inventory.BundlePath != Application.BundlePath)
            throw new ArgumentException("Inventory belongs to a different application.", nameof(inventory));
        if (!inventory.IsComplete) throw new InvalidOperationException("Incomplete application scans cannot replace saved membership.");
        if (inventory.ExecutablePaths.Any(path => !SavedExecutablePaths.Contains(path, StringComparer.Ordinal)
            && (!MacPath.IsAbsolute(path) || MacPath.HasTraversal(path)
                || !MacApplicationCatalog.IsInside(path, Application.BundlePath))))
            throw new ArgumentException("New executable paths must remain inside the verified application bundle.", nameof(inventory));
        var paths = SavedExecutablePaths.Concat(inventory.ExecutablePaths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        return this with { SavedExecutablePaths = paths, Application = Application with
        { ExecutablePaths = paths, InventoryReady = true, Diagnostics = inventory.Diagnostics } };
    }

    public bool ContainsExecutable(string canonicalPath) => SavedExecutablePaths.Contains(canonicalPath, StringComparer.Ordinal);
}
