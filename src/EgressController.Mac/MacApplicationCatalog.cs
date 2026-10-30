// SPDX-License-Identifier: MIT
using System.Collections.Immutable;

namespace EgressController.Mac;

public sealed record MacFileEntry(string Path, bool IsDirectory, bool IsSymbolicLink);

public interface IMacCatalogFileSystem
{
    bool DirectoryExists(string path);
    string Canonicalize(string path);
    IReadOnlyList<MacFileEntry> Enumerate(string directory);
    bool IsExecutableMachO(string path);
}

public interface IMacBundleMetadataReader
{
    MacBundleMetadata ReadBundle(string bundlePath);
}

/// <summary>Uses registered application roots, stops at each .app, and scans executables only on request.</summary>
public sealed class MacApplicationCatalog(IMacCatalogFileSystem files, IMacBundleMetadataReader metadata,
    IReadOnlyList<string> discoveryRoots, int maximumEntries = 100_000)
{
    public static IReadOnlyList<string> StandardRoots(string userHome) =>
        ["/Applications", Path.Combine(userHome, "Applications"), "/System/Applications"];

    public Task<MacApplicationDiscovery> DiscoverAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => Discover(cancellationToken), cancellationToken);

    public Task<MacExecutableInventory> BuildInventoryAsync(MacApplication application,
        IEnumerable<string>? previouslySavedPaths = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(application, previouslySavedPaths ?? [], cancellationToken), cancellationToken);

    private MacApplicationDiscovery Discover(CancellationToken cancellationToken)
    {
        var applications = new Dictionary<string, MacApplication>(StringComparer.Ordinal);
        var diagnostics = new List<MacCatalogDiagnostic>();
        bool complete = true;
        int visited = 0;
        var pending = new Stack<(string Path, int Depth)>();
        foreach (string root in discoveryRoots.Reverse())
        {
            if (!MacPath.IsAbsolute(root) || !root.EndsWith("/Applications", StringComparison.Ordinal) || MacPath.HasTraversal(root))
                throw new ArgumentException("Application discovery is restricted to Applications directories; select a .app explicitly for manual inventory.", nameof(discoveryRoots));
            try { if (files.DirectoryExists(root)) pending.Push((root, 0)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { complete = false; diagnostics.Add(new(root, "discovery-incomplete", error.Message)); }
        }
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (++visited > maximumEntries || item.Depth > 24)
                {
                    complete = false; diagnostics.Add(new(item.Path, "discovery-limit", "Application discovery exceeded its bounded directory budget."));
                    continue;
                }
                if (item.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    var info = metadata.ReadBundle(item.Path);
                    string root = files.Canonicalize(info.BundlePath);
                    if (string.IsNullOrWhiteSpace(info.BundleIdentifier) || !IsApplicationRoot(root))
                        throw new IOException("The bundle has no exact application identity or valid application root.");
                    string? executable = info.PrimaryExecutablePath is { } path ? files.Canonicalize(path) : null;
                    if (executable is not null && !IsInside(executable, root)) throw new IOException("The bundle's primary executable escapes its application root.");
                    string id = "bundle:" + info.BundleIdentifier + ":" + root;
                    applications.TryAdd(id, new(id, info.BundleIdentifier, info.DisplayName, root, executable, [], false, []));
                    continue; // Never infer another application from a nested helper bundle.
                }
                foreach (var entry in files.Enumerate(item.Path).Reverse())
                    if (entry.IsDirectory && !entry.IsSymbolicLink) pending.Push((entry.Path, item.Depth + 1));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                complete = false; diagnostics.Add(new(item.Path, "discovery-incomplete", error.Message));
            }
        }
        return new(applications.Values.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(), complete, diagnostics.ToImmutableArray());
    }

    private MacExecutableInventory Scan(MacApplication application, IEnumerable<string> previous, CancellationToken cancellationToken)
    {
        string root = files.Canonicalize(application.BundlePath);
        if (!IsApplicationRoot(root) || root != application.BundlePath)
            throw new ArgumentException("Choose a canonical .app bundle; shared Applications/bin roots cannot be enrolled.", nameof(application));
        var paths = new HashSet<string>(previous, StringComparer.Ordinal);
        var diagnostics = new List<MacCatalogDiagnostic>();
        bool complete = true;
        int visited = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in files.Enumerate(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visited > maximumEntries)
                    {
                        complete = false; diagnostics.Add(new(directory, "inventory-limit", "Executable inventory exceeded its bounded entry budget."));
                        pending.Clear(); break;
                    }
                    string canonical = files.Canonicalize(entry.Path);
                    if (!IsInside(canonical, root))
                    {
                        complete = false; diagnostics.Add(new(entry.Path, "outside-bundle", "The application entry resolves outside its bundle and was not enrolled.")); continue;
                    }
                    if (entry.IsSymbolicLink) continue; // Real version paths are scanned; aliases never expand membership.
                    if (entry.IsDirectory) pending.Push(canonical);
                    else if (files.IsExecutableMachO(canonical)) paths.Add(canonical);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                complete = false; diagnostics.Add(new(directory, "inventory-incomplete", error.Message));
            }
        }
        if (application.PrimaryExecutablePath is { } primary && !paths.Contains(primary))
        {
            complete = false; diagnostics.Add(new(primary, "primary-not-executable", "The saved primary executable is missing or is not an executable Mach-O file."));
        }
        return new(application.Id, root, paths.Order(StringComparer.Ordinal).ToImmutableArray(), complete, diagnostics.ToImmutableArray());
    }

    internal static bool IsApplicationRoot(string path) => MacPath.IsAbsolute(path) && !MacPath.HasTraversal(path)
        && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && path != "/.app";
    internal static bool IsInside(string path, string root) => path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);
}
