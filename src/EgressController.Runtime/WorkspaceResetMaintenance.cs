namespace EgressController.Runtime;

/// <summary>Only generated workspace data; configuration backups and the privileged recovery journal are outside this boundary.</summary>
public sealed class WorkspaceResetMaintenance(string root)
{
    private string Marker => Path.Combine(root, "reset-cleanup.pending");
    public bool Pending => File.Exists(Marker);
    public void Begin()
    {
        DurableFiles.CreatePrivateDirectory(root);
        DurableFiles.Replace(Marker, "reset-v1\n"u8.ToArray());
        foreach (string name in new[] { "runtime", "resource-cache", "probes", "helper-staging" })
        {
            string path = Path.Combine(root, name);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: new DirectoryInfo(path).LinkTarget is null);
        }
        DurableFiles.SyncDirectory(root);
    }
    public void Complete()
    {
        if (File.Exists(Marker)) File.Delete(Marker);
        if (Directory.Exists(root)) DurableFiles.SyncDirectory(root);
    }
}

/// <summary>One workspace writer across Rider Debug/Release and installed app paths. Kernel ownership expires with the process.</summary>
public sealed class WorkspaceLease : IDisposable
{
    private readonly FileStream _file;
    public WorkspaceLease(string directory)
    {
        DurableFiles.CreatePrivateDirectory(directory);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _file = new FileStream(Path.Combine(directory, "workspace.lock"), options);
    }
    public void Dispose() => _file.Dispose();
}
