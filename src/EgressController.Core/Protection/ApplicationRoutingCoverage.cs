namespace EgressController.Core.Protection;

public sealed class ApplicationRoutingCoverage(IEnumerable<string> executables, IEnumerable<string> directories)
{
    private readonly HashSet<string> _executables = executables.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _directories = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static ApplicationRoutingCoverage Empty { get; } = new([], []);
    public bool Contains(GuardProcess process) => process.Paths.Any(Contains);
    public bool Contains(string path) => _executables.Contains(path)
        || Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && _directories.Any(root => ProcessProtection.IsWithinDirectory(path, root));
}
public sealed record InheritedProtectionExecutable(string Path, ProtectedExecutable Source);
