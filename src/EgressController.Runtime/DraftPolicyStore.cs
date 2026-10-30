using System.Security.Cryptography;
using System.Text;
using EgressController.Policy;

namespace EgressController.Runtime;

/// <summary>Private, checksummed drafts may contain incomplete onboarding choices; applying remains strictly validated.</summary>
public sealed class DraftPolicyStore(string directory)
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private const int MaximumBytes = 16 * 1024 * 1024;
    public async Task SaveAsync(MacPolicyDocument document, CancellationToken cancellationToken = default)
    {
        byte[] yaml = Encoding.UTF8.GetBytes(PolicyYaml.Write(document));
        if (yaml.Length > MaximumBytes || document.Version != 1) throw new InvalidDataException("Policy draft exceeds supported limits.");
        byte[] bytes = Encoding.UTF8.GetBytes("EGRESS-DRAFT-1\n" + Convert.ToHexStringLower(SHA256.HashData(yaml)) + "\n" + Encoding.UTF8.GetString(yaml));
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DurableFiles.CreatePrivateDirectory(directory);
            string current = Path.Combine(directory, "policy.draft");
            if (Read(current) is not null) DurableFiles.Replace(Path.Combine(directory, "policy.previous.draft"), File.ReadAllBytes(current));
            cancellationToken.ThrowIfCancellationRequested();
            DurableFiles.Replace(current, bytes);
        }
        finally { _write.Release(); }
    }
    public Task<MacPolicyDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(Path.Combine(directory, "policy.draft")) ?? Read(Path.Combine(directory, "policy.previous.draft")));
    }
    private static MacPolicyDocument? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes + 128) return null;
            string text = File.ReadAllText(path);
            const string prefix = "EGRESS-DRAFT-1\n";
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length < prefix.Length + 65) return null;
            string hash = text.Substring(prefix.Length, 64);
            if (text[prefix.Length + 64] != '\n' || !hash.All(char.IsAsciiHexDigitLower)) return null;
            string yaml = text[(prefix.Length + 65)..];
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))) != hash) return null;
            var result = PolicyYaml.Read(yaml);
            return result.Document is { Version: 1 } document ? document : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}

/// <summary>Only bounded lifecycle causes are written, never core connection streams or subscription contents.</summary>
public sealed class PrivateLifecycleFileLog(string directory, int maximumBytes = 256 * 1024) : ILifecycleLog
{
    private readonly object _sync = new();
    public void Append(LifecycleEvent entry)
    {
        lock (_sync)
        {
            DurableFiles.CreatePrivateDirectory(directory);
            string path = Path.Combine(directory, "lifecycle.log");
            string previous = File.Exists(path) && new FileInfo(path).Length <= maximumBytes * 2L ? File.ReadAllText(path) : "";
            string line = $"{entry.Time:O} [{Clean(entry.Kind)}] {Clean(entry.Cause)} {Clean(entry.GenerationId ?? "")}\n";
            string text = previous + line;
            while (Encoding.UTF8.GetByteCount(text) > maximumBytes)
            {
                int boundary = text.IndexOf('\n');
                if (boundary < 0 || boundary == text.Length - 1) { text = ""; break; }
                text = text[(boundary + 1)..];
            }
            DurableFiles.Replace(path, Encoding.UTF8.GetBytes(text));
        }
    }
    private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(value.Length, 1024)];
}
