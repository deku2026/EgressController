using System.Security.Cryptography;
using System.Text;
using EgressController.Core.Storage;

namespace EgressController.Surge;

public sealed record PreparedConfiguration(string Content, string Sha256, ConfigValidation Validation);
/// <summary>Owns private candidates only. Never writes into Surge's installed/active profile directories.</summary>
public sealed class ManagedConfiguration(string directory, IConfigValidator validator)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string CandidatePath => Path.Combine(directory, "candidate.conf");
    public string LastValidPath => Path.Combine(directory, "last-valid.conf");
    public async Task<PreparedConfiguration> PrepareAsync(string content, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            await PrivateFile.WriteAsync(CandidatePath, bytes, token).ConfigureAwait(false);
            var result = await validator.CheckAsync(CandidatePath, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Valid)
            {
                if (File.Exists(LastValidPath)) await PrivateFile.WriteAsync(Path.Combine(directory, "previous-valid.conf"), await File.ReadAllBytesAsync(LastValidPath, token).ConfigureAwait(false), token).ConfigureAwait(false);
                await PrivateFile.WriteAsync(LastValidPath, bytes, token).ConfigureAwait(false);
            }
            return new(content, Convert.ToHexStringLower(SHA256.HashData(bytes)), result);
        }
        finally { _gate.Release(); }
    }
    public async Task<string> RestoreCandidateAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var bytes = await File.ReadAllBytesAsync(LastValidPath, token).ConfigureAwait(false);
            await PrivateFile.WriteAsync(CandidatePath, bytes, token).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { _gate.Release(); }
    }
}
