using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EgressController.Runtime;

/// <summary>Publishes complete immutable generations, then atomically switches a small manifest.</summary>
public sealed class AtomicGenerationStore(string directory) : IRuntimeGenerationStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _publication = new(1, 1);

    public Task<RuntimeGeneration?> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string pointerName in new[] { "current.json", "current.previous.json" })
        {
            var pointer = ReadPointer(Path.Combine(_directory, pointerName));
            foreach (string? id in new[] { pointer?.Current, pointer?.Previous })
                if (id is not null && LoadGeneration(id) is { } generation) return Task.FromResult<RuntimeGeneration?>(generation);
        }
        return Task.FromResult<RuntimeGeneration?>(null);
    }

    public async Task PublishAsync(RuntimeGeneration generation, CancellationToken cancellationToken = default)
    {
        if (!ValidId(generation.Id) || generation.Id != ConfigurationCoordinator.GenerationIdentity(generation.PolicyYaml, generation.XrayJson, generation.Assets))
            throw new InvalidOperationException("Invalid generation identity.");
        if (generation.Assets.Keys.Any(name => !ConfigurationCoordinator.SafeArtifactName(name) || name is "policy.yaml" or "xray.json" or "manifest.json"))
            throw new InvalidOperationException("Unsafe generation resource name.");
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            DurableFiles.CreatePrivateDirectory(_directory);
            var previous = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (previous?.Id == generation.Id) return;
            string generations = Path.Combine(_directory, "generations");
            DurableFiles.CreatePrivateDirectory(generations);
            staging = Path.Combine(generations, ".preparing-" + Guid.NewGuid().ToString("N"));
            DurableFiles.CreatePrivateDirectory(staging);
            var files = generation.Assets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            files["policy.yaml"] = Encoding.UTF8.GetBytes(generation.PolicyYaml);
            files["xray.json"] = Encoding.UTF8.GetBytes(generation.XrayJson);
            var manifest = new GenerationManifest { Id = generation.Id };
            foreach (var (name, bytes) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DurableFiles.Write(Path.Combine(staging, name), bytes);
                manifest.Files.Add(name, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            DurableFiles.Write(Path.Combine(staging, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, RuntimeJsonContext.Default.GenerationManifest));
            DurableFiles.SyncDirectory(staging);
            string target = Path.Combine(generations, generation.Id);
            if (!Directory.Exists(target)) Directory.Move(staging, target);
            else if (LoadGeneration(generation.Id) is null) throw new IOException("An existing generation failed its integrity check.");
            DurableFiles.SyncDirectory(generations);
            var pointer = new GenerationPointer { Current = generation.Id, Previous = previous?.Id };
            string current = Path.Combine(_directory, "current.json");
            // A corrupt pointer must not replace the last known-good backup.
            if (File.Exists(current) && ReadPointer(current) is not null)
                DurableFiles.Replace(Path.Combine(_directory, "current.previous.json"), File.ReadAllBytes(current));
            cancellationToken.ThrowIfCancellationRequested();
            DurableFiles.Replace(current, JsonSerializer.SerializeToUtf8Bytes(pointer, RuntimeJsonContext.Default.GenerationPointer));
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            _publication.Release();
        }
    }

    private RuntimeGeneration? LoadGeneration(string id)
    {
        if (!ValidId(id)) return null;
        try
        {
            string directory = Path.Combine(_directory, "generations", id);
            string manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1024 * 1024) return null;
            var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), RuntimeJsonContext.Default.GenerationManifest);
            if (manifest?.Id != id || manifest.Files is null || manifest.Files.Count > 1024 || !manifest.Files.ContainsKey("policy.yaml") || !manifest.Files.ContainsKey("xray.json")) return null;
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (name, expectedHash) in manifest.Files)
            {
                if (!ConfigurationCoordinator.SafeArtifactName(name)) return null;
                string path = Path.Combine(directory, name);
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 * 1024) return null;
                var bytes = File.ReadAllBytes(path);
                if (!string.Equals(expectedHash, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal)) return null;
                files.Add(name, bytes);
            }
            string policy = Encoding.UTF8.GetString(files["policy.yaml"]);
            string config = Encoding.UTF8.GetString(files["xray.json"]);
            files.Remove("policy.yaml"); files.Remove("xray.json");
            if (ConfigurationCoordinator.GenerationIdentity(policy, config, files) != id) return null;
            return new(id, policy, config, files);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    private static GenerationPointer? ReadPointer(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var result = JsonSerializer.Deserialize(File.ReadAllBytes(path), RuntimeJsonContext.Default.GenerationPointer);
            return result is not null && ValidId(result.Current) && (result.Previous is null || ValidId(result.Previous)) ? result : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private static bool ValidId(string? id) => id is not null && id.Length == 64 && id.All(character => char.IsAsciiHexDigitLower(character));

    public async Task RestoreAsync(RuntimeGeneration? generation, CancellationToken cancellationToken = default)
    {
        if (generation is not null) { await PublishAsync(generation, cancellationToken).ConfigureAwait(false); return; }
        await _publication.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (string name in new[] { "current.json", "current.previous.json" })
            {
                string path = Path.Combine(_directory, name);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(_directory)) DurableFiles.SyncDirectory(_directory);
        }
        finally { _publication.Release(); }
    }
}

internal sealed class GenerationPointer
{
    public string Current { get; set; } = "";
    public string? Previous { get; set; }
}
internal sealed class GenerationManifest
{
    public string Id { get; set; } = "";
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}

[JsonSerializable(typeof(GenerationPointer))]
[JsonSerializable(typeof(GenerationManifest))]
[JsonSerializable(typeof(TrafficLedgerSnapshot))]
internal partial class RuntimeJsonContext : JsonSerializerContext;

internal static partial class DurableFiles
{
    public static void Write(string path, byte[] bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 64 * 1024, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(bytes); file.Flush(flushToDisk: true);
    }
    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public static void Replace(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Write(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            SyncDirectory(Path.GetDirectoryName(path)!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        int descriptor = Open(path, 0);
        if (descriptor < 0) throw new IOException("Unable to open the publication directory for synchronization.");
        try { if (Fsync(descriptor) != 0) throw new IOException("Unable to synchronize the publication directory."); }
        finally { Close(descriptor); }
    }
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)] private static partial int Fsync(int descriptor);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int descriptor);
}
