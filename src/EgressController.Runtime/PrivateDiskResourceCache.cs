using System.Security.Cryptography;
using System.Text;

namespace EgressController.Runtime;

/// <summary>One atomically replaced, checksum-protected private record per hashed resource identity.</summary>
public sealed class PrivateDiskResourceCache : IValidatedResourceCache
{
    private readonly string _root;
    private readonly int _maximumBytes;
    public PrivateDiskResourceCache(string absoluteRoot, int maximumBytes = 128 * 1024 * 1024)
    {
        if (!Path.IsPathFullyQualified(absoluteRoot) || maximumBytes < 1) throw new ArgumentException("An absolute private cache directory is required.");
        _root = Path.GetFullPath(absoluteRoot); _maximumBytes = maximumBytes;
    }
    public async Task<byte[]?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = PathFor(id);
        try
        {
            if (!Directory.Exists(_root) || !File.Exists(path)) return null;
            PrivateResourceFiles.VerifyDirectory(_root);
            var file = new FileInfo(path);
            if (file.LinkTarget is not null || file.Length < 32 || file.Length > (long)_maximumBytes + 32) return null;
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) return null;
            var record = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (record.Length < 32 || record.Length > (long)_maximumBytes + 32 || !CryptographicOperations.FixedTimeEquals(record.AsSpan(0, 32), SHA256.HashData(record.AsSpan(32)))) return null;
            return record[32..];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    public Task WriteAsync(string id, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > _maximumBytes) throw new IOException("Resource cache entry exceeds its limit.");
        cancellationToken.ThrowIfCancellationRequested();
        PrivateResourceFiles.EnsureDirectory(_root);
        byte[] record = new byte[bytes.Length + 32]; SHA256.HashData(bytes).CopyTo(record, 0); bytes.CopyTo(record, 32);
        PrivateResourceFiles.Replace(PathFor(id), record, cancellationToken);
        return Task.CompletedTask;
    }
    private string PathFor(string id) => Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".cache");
}

internal static class PrivateResourceFiles
{
    internal static void EnsureDirectory(string path)
    {
        // Reject symlink ancestors before creating or trusting a private location.
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) throw new IOException("Resource storage cannot use symbolic links.");
        bool existed = Directory.Exists(path);
        DurableFiles.CreatePrivateDirectory(path);
        VerifyDirectory(path);
        DurableFiles.SyncDirectory(path);
        if (!existed && Path.GetDirectoryName(path) is { } parent) DurableFiles.SyncDirectory(parent);
    }
    internal static void VerifyDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) throw new IOException("Resource storage cannot use symbolic links.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Resource directory must have private permissions.");
    }
    internal static void Replace(string path, byte[] bytes, CancellationToken token)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested(); DurableFiles.Write(temporary, bytes);
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); DurableFiles.SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
