namespace EgressController.Launcher.Discovery;

/// <summary>
/// Builds the executable membership snapshot used by process-tree routing. It intentionally
/// walks each application root recursively, but never treats the root directory itself as an
/// implicit process match: only paths observed in this collection are eligible as descendants.
/// </summary>
public static class ExecutableInventory
{
    private static readonly EnumerationOptions RecursiveExeOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static IReadOnlyList<string> Collect(
        IEnumerable<string> roots,
        string? primary = null,
        IDictionary<string, IReadOnlyList<string>>? cache = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFile(result, primary);

        foreach (string root in roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (cache is not null && cache.TryGetValue(root, out IReadOnlyList<string>? cached))
            {
                foreach (string file in cached)
                    AddFile(result, file);
                continue;
            }

            var discovered = new List<string>();
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*.exe", RecursiveExeOptions))
                    {
                        string full = Path.GetFullPath(file);
                        discovered.Add(full);
                        AddFile(result, full);
                    }
                }
            }
            catch
            {
                // A protected child directory must not prevent the rest of the app catalog from
                // being returned. The process matcher remains fail-closed for paths not seen.
            }
            cache?.TryAdd(root, discovered);
        }

        return result.ToArray();
    }

    private static void AddFile(HashSet<string> result, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)
            && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            result.Add(Path.GetFullPath(path));
    }
}
