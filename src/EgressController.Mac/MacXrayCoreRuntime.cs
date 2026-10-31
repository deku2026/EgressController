// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Mac;

public interface IMacGenerationStager
{
    Task<MacHelperApplyPlan> StageAsync(RuntimeGeneration generation, MacRuntimeLaunch launch, CancellationToken cancellationToken);
}

/// <summary>Production core adapter: all privileged operations remain in the authenticated root helper.</summary>
public sealed class MacXrayCoreRuntime : ICoreRuntime
{
    private readonly MacHelperClient _client;
    private readonly IMacGenerationStager _stager;
    private readonly Func<CancellationToken, Task<MacRuntimeLaunch>> _resolve;
    private (string Generation, MacHelperApplyPlan Plan)? _prepared;
    public MacXrayCoreRuntime(MacHelperClient client, string stagingRoot, Func<CancellationToken, Task<MacRuntimeLaunch>> launchResolver)
        : this(client, stagingRoot, launchResolver, LiveOperationGate.ForExplicitLiveUse()) { }
    public MacXrayCoreRuntime(MacHelperClient client, string stagingRoot, Func<CancellationToken, Task<MacRuntimeLaunch>> launchResolver, LiveOperationGate gate)
        : this(client, new MacUserGenerationStager(gate, stagingRoot), launchResolver) { }
    public MacXrayCoreRuntime(MacHelperClient client, IMacGenerationStager stager, Func<CancellationToken, Task<MacRuntimeLaunch>> launchResolver)
    { _client = client; _stager = stager; _resolve = launchResolver; }
    public async Task ValidateAsync(RuntimeGeneration generation, CancellationToken cancellationToken)
    {
        var launch = await _resolve(cancellationToken).ConfigureAwait(false);
        var parsed = PolicyYaml.Read(generation.PolicyYaml);
        if (!parsed.IsSuccess) throw new InvalidDataException("The runtime policy is not valid.");
        // Rollback must use the old generation's interface identities, not an edited UI draft.
        launch = launch with { HomeServiceId = parsed.Document!.Interfaces.HomeId, NonHomeServiceId = parsed.Document.Interfaces.NonHomeId };
        var plan = await _stager.StageAsync(generation, launch, cancellationToken).ConfigureAwait(false);
        var reply = await _client.ValidateAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!reply.Succeeded) throw new RuntimeFailureException(reply.Code);
        _prepared = (generation.Id, plan);
    }
    public async Task ApplyAsync(RuntimeGeneration generation, CancellationToken cancellationToken)
    {
        if (_prepared is not { } prepared || prepared.Generation != generation.Id)
            await ValidateAsync(generation, cancellationToken).ConfigureAwait(false);
        var reply = await _client.ApplyAsync(_prepared!.Value.Plan, cancellationToken).ConfigureAwait(false);
        if (!reply.Succeeded || !reply.Status.Running || !reply.Status.TunTakenOver)
            throw new RuntimeFailureException(reply.Code);
    }
    public async Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken)
    {
        if (generation is not null)
        { await ValidateAsync(generation, cancellationToken).ConfigureAwait(false); await ApplyAsync(generation, cancellationToken).ConfigureAwait(false); return; }
        var reply = await _client.StopAndRestoreAsync(cancellationToken).ConfigureAwait(false);
        _prepared = null;
        if (!reply.Succeeded || !reply.Status.DnsRestored || reply.Status.Running) throw new IOException("Network restoration was not confirmed: " + reply.Code);
    }
}

public sealed class MacUserGenerationStager(LiveOperationGate gate, string stagingRoot) : IMacGenerationStager
{
    public async Task<MacHelperApplyPlan> StageAsync(RuntimeGeneration generation, MacRuntimeLaunch launch, CancellationToken cancellationToken)
    {
        gate.Require("prepare private runtime generation for privileged validation");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (generation.Id.Length != 64 || generation.Id != ConfigurationCoordinator.GenerationIdentity(generation.PolicyYaml, generation.XrayJson, generation.Assets))
            throw new InvalidDataException("Runtime generation identity did not match its bytes.");
        if (generation.Assets.Count > 254 || generation.Assets.Any(item => !ConfigurationCoordinator.SafeArtifactName(item.Key)
            || item.Key is "xray.json" or "policy.yaml" or "runtime-manifest.json" || item.Value.Length > 64 * 1024 * 1024))
            throw new InvalidDataException("Runtime generation resources are not bounded owned filenames.");
        string root = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string stage = Path.Combine(root, ".preparing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var files = generation.Assets.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal);
            files.Add("xray.json", Encoding.UTF8.GetBytes(generation.XrayJson)); files.Add("policy.yaml", Encoding.UTF8.GetBytes(generation.PolicyYaml));
            if (files.Values.Sum(bytes => (long)bytes.Length) > 256 * 1024 * 1024) throw new InvalidDataException("Runtime generation exceeds the privileged staging limit.");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, bytes) in files)
            {
                cancellationToken.ThrowIfCancellationRequested(); await WriteAsync(Path.Combine(stage, name), bytes, cancellationToken).ConfigureAwait(false);
                hashes.Add(name, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new MacRuntimeManifest(generation.Id, hashes), MacHelperWireJson.Default.MacRuntimeManifest);
            await WriteAsync(Path.Combine(stage, "runtime-manifest.json"), manifest, cancellationToken).ConfigureAwait(false);
            string destination = Path.Combine(root, generation.Id);
            if (Directory.Exists(destination) && !await ExistingMatchesAsync(destination, files, manifest, cancellationToken).ConfigureAwait(false))
                destination += ".recovered-" + Guid.NewGuid().ToString("N");
            if (!Directory.Exists(destination)) Directory.Move(stage, destination);
            return new(generation.Id, destination, Convert.ToHexStringLower(SHA256.HashData(manifest)), launch.XrayPath, launch.XraySha256,
                launch.HomeServiceId, launch.NonHomeServiceId);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        await using var stream = new FileStream(path, options); await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); stream.Flush(true);
    }
    private static async Task<bool> ExistingMatchesAsync(string directory, IReadOnlyDictionary<string, byte[]> files, byte[] manifest, CancellationToken cancellationToken)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var (name, expected) in files.Append(new KeyValuePair<string, byte[]>("runtime-manifest.json", manifest)))
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length != expected.Length) return false;
                await using var stream = File.OpenRead(path);
                if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false), SHA256.HashData(expected))) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
