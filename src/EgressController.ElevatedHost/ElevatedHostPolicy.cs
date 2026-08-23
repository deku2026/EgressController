using System.Security.Cryptography;

namespace EgressController.ElevatedHost;

public sealed record ElevatedHostPathPolicy
{
    public required string DataRoot { get; init; }
    public string? AllowedSystemCorePath { get; init; }

    public string NormalizedDataRoot => Path.GetFullPath(DataRoot);

    public bool IsAllowedCorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string full = Path.GetFullPath(path.Trim());
        if (!string.Equals(Path.GetFileName(full), "sing-box.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        string managedRoot = Path.Combine(NormalizedDataRoot, "core") + Path.DirectorySeparatorChar;
        if (full.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        return AllowedSystemCorePath is not null
            && string.Equals(full, Path.GetFullPath(AllowedSystemCorePath), StringComparison.OrdinalIgnoreCase);
    }

    public bool IsAllowedConfigPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string full = Path.GetFullPath(path.Trim());
        string root = NormalizedDataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        await using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsSha256(string? value)
        => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed class ElevatedHostValidationException(string message, string code)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
