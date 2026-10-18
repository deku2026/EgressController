namespace EgressController.Launcher.Discovery;

/// <summary>
/// Builds the executable membership snapshot used by exact-path routing. It intentionally
/// walks each application root recursively, but never treats the root directory itself as an
/// implicit process match: only paths observed in this collection are members of the saved application inventory.
/// </summary>
public static class ExecutableInventory
{
    public static IReadOnlyList<string> CollectChecked(IEnumerable<string> roots, string? primary = null, CancellationToken token = default, IReadOnlyList<string>? excludedRoots = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = (excludedRoots ?? []).Select(Path.GetFullPath).Select(path => path.TrimEnd(Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddFile(result, primary);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (excluded.Contains(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))) continue;
            ApplicationDirectoryBoundary.RequireScoped(root);
            if (!Directory.Exists(root)) throw new IOException("应用目录不存在：" + root);
            if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("安装目录是链接，请选择实际安装目录：" + root);
            var pending = new Stack<string>(); pending.Push(Path.GetFullPath(root));
            while (pending.TryPop(out string? directory))
            {
                token.ThrowIfCancellationRequested();
                // Enumerate names/attributes only. Never read game assets or follow a junction
                // into another product/drive. Incomplete inventories must be reported, not saved.
                var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, MatchCasing = MatchCasing.CaseInsensitive };
                foreach (string file in Directory.EnumerateFiles(directory, "*.exe", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("EXE 是链接，请单独指定实际目标：" + file);
                    result.Add(Path.GetFullPath(file));
                }
                foreach (string child in Directory.EnumerateDirectories(directory, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (excluded.Contains(Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar))) continue;
                    if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("目录包含链接，请为实际目标单独指定应用目录：" + child);
                    pending.Push(child);
                }
            }
        }
        if (result.Count == 0) throw new IOException("没有找到可用于分流的 EXE，请指定应用目录。");
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
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
