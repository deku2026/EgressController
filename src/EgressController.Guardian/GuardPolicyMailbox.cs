using System.Security.Cryptography;
using System.Text.Json;
using EgressController.Core.Profile;
using EgressController.Core.Protection;

namespace EgressController.Guardian;

/// <summary>Main's desired state also survives explicit service stops. Service owns guard.db.</summary>
public sealed class GuardPolicyMailbox(string directory)
{
    private readonly string _file = Path.Combine(directory, "desired-policy.json");
    public GuardPolicy? Load()
    {
        return GuardStateFile.Read(_file, GuardJson.Default.GuardPolicy)?.Validate();
    }
    public void Save(GuardPolicy policy)
    {
        GuardStateFile.Write(_file, policy.Validate(), GuardJson.Default.GuardPolicy);
    }
    public static GuardPolicy FromProfile(EgressProfileDocument profile, string main, IEnumerable<string> excluded)
    {
        var policy = new GuardPolicy
        {
            Revision = Guid.Empty.ToString("N"), MainExecutable = main,
            Executables = profile.Applications.SelectMany(item => item.ExecutablePaths
                .Concat(item.DiscoveryKey.StartsWith("exe:", StringComparison.OrdinalIgnoreCase) ? [item.DiscoveryKey[4..]] : [])
                .Select(path => new ProtectedExecutable(path, item.DisplayName ?? item.DiscoveryKey))).OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            Directories = [], // Roots are discovery inputs; only saved executable paths enforce protection.
            Exclusions = excluded.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        }.Validate();
        byte[] hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(policy, GuardJson.Default.GuardPolicy));
        return policy with { Revision = new Guid(hash.AsSpan(0, 16)).ToString("N") };
    }
}
