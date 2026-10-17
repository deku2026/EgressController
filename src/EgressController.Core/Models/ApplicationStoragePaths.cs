namespace EgressController.Core.Models;

public sealed record ApplicationStoragePaths(string Data, string Rulesets)
{
    // Pure path policy: MSIX install files are immutable; portable ZIP keeps its existing layout.
    public static ApplicationStoragePaths Resolve(string applicationDirectory, string localAppData, bool packaged)
    {
        string root = packaged ? Path.Combine(localAppData, "EgressController") : applicationDirectory;
        root = Path.GetFullPath(root);
        return new(Path.Combine(root, "data"), Path.Combine(root, "ruleset"));
    }
}
