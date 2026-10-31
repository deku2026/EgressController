using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using EgressController.Policy;

namespace EgressController.Runtime;

public sealed record PreparedCoreRelease(string ExecutablePath, string Sha256, string Version);
public sealed record XrayCoreReleaseSpecification(string Version, Uri ArchiveUrl, Uri ChecksumUrl, string ArchiveSha256, string ChecksumSha256)
{
    // Derived offline from the official archive after independently checking the release API and .dgst ZIP digest.
    // This pin is also enforced by the privileged installer, which must not trust a caller's self-reported core hash.
    public const string PinnedMacArm64ExecutableSha256 = "2d4bfa91aae5f90dee691cb5eb794316510c7f6006973eb46e60d7978bb789aa";
    public static XrayCoreReleaseSpecification PinnedMacArm64 { get; } = new("26.10.29",
        new("https://github.com/XTLS/Xray-core/releases/download/v26.10.29/Xray-macos-arm64-v8a.zip"),
        new("https://github.com/XTLS/Xray-core/releases/download/v26.10.29/Xray-macos-arm64-v8a.zip.dgst"),
        "57755127d38c0c4357e667761c78b257cf617f05015bad5060bf2dae75fcd00b",
        "cb53e5a892f10794499861a26465178b42ed324c3a783e10eaf6e635af9a63ae");
}

/// <summary>Downloads and installs a pinned, digest-verified core without executing it.</summary>
public sealed class XrayCoreReleaseManager
{
    private readonly IResourceTransport _transport; private readonly IValidatedResourceCache _cache;
    private readonly string _root; private readonly Func<CancellationToken, ValueTask<ResourceExitSnapshot>> _binding;
    private readonly IProgress<PreparationProgress>? _progress;
    private readonly XrayCoreReleaseSpecification _release;
    public XrayCoreReleaseManager(IResourceTransport transport, IValidatedResourceCache cache, string absoluteInstallRoot,
        Func<CancellationToken, ValueTask<InterfaceBinding>> bindingProvider, XrayCoreReleaseSpecification? specification = null)
        : this(transport, cache, absoluteInstallRoot, async token => ResourceExitSnapshot.Bound(await bindingProvider(token).ConfigureAwait(false)), specification) { }
    public XrayCoreReleaseManager(IResourceTransport transport, IValidatedResourceCache cache, string absoluteInstallRoot,
        Func<CancellationToken, ValueTask<ResourceExitSnapshot>> exitProvider, XrayCoreReleaseSpecification? specification = null,
        IProgress<PreparationProgress>? progress = null)
    {
        if (!Path.IsPathFullyQualified(absoluteInstallRoot)) throw new ArgumentException("Core installation requires an absolute private directory.");
        _transport = transport; _cache = cache; _root = Path.GetFullPath(absoluteInstallRoot); _binding = exitProvider; _progress = progress;
        _release = specification ?? XrayCoreReleaseSpecification.PinnedMacArm64;
        if (!ConfigurationCoordinator.SafeArtifactName(_release.Version) || !ValidHash(_release.ArchiveSha256) || !ValidHash(_release.ChecksumSha256)
            || _release.ArchiveUrl.Scheme != "https" || _release.ChecksumUrl.Scheme != "https") throw new ArgumentException("Invalid pinned core release specification.");
    }
    public async Task<PreparedCoreRelease> PrepareAsync(CancellationToken cancellationToken = default)
    {
        var binding = await _binding(cancellationToken).ConfigureAwait(false);
        var prepare = new ResourcePreparation(_transport, _cache, _progress);
        var checksum = await prepare.PrepareAsync(Descriptor("xray-checksum", _release.ChecksumUrl, 4096, _release.ChecksumSha256),
            bytes => VerifyChecksum(bytes, _release.ArchiveSha256), cancellationToken).ConfigureAwait(false);
        var archive = await prepare.PrepareAsync(Descriptor("xray-archive", _release.ArchiveUrl, 64 * 1024 * 1024, _release.ArchiveSha256),
            bytes => { try { _ = ExtractExecutable(bytes); return true; } catch (InvalidDataException) { return false; } }, cancellationToken).ConfigureAwait(false);
        if (!VerifyChecksum(checksum, Convert.ToHexStringLower(SHA256.HashData(archive)))) throw new InvalidDataException("Core archive checksum mismatch.");
        byte[] executable = ExtractExecutable(archive);
        string digest = Convert.ToHexStringLower(SHA256.HashData(executable));
        if (_release == XrayCoreReleaseSpecification.PinnedMacArm64 && digest != XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256)
            throw new InvalidDataException("The extracted official core does not match the independent executable pin.");
        return await PublishExecutableAsync(executable, cancellationToken).ConfigureAwait(false);

        ResourceDescriptor Descriptor(string id, Uri url, int maximum, string hash) => new()
        { Id = id + "-" + _release.Version, Url = url, Exit = binding, MaximumBytes = maximum, ExpectedSha256 = hash, Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task<PreparedCoreRelease> PrepareBundledAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        // A startup never needs a download to obtain the very proxy that performs downloads.
        string installed = Path.Combine(_root, _release.Version + "-" + XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256, "xray");
        if (File.Exists(installed) && new FileInfo(installed).LinkTarget is null)
        {
            await using var stream = File.OpenRead(installed);
            if (stream.Length <= 128 * 1024 * 1024 && Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                == XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256)
                return new(installed, XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256, _release.Version);
        }
        if (!File.Exists(archivePath) || new FileInfo(archivePath).Length > 64 * 1024 * 1024) throw new RuntimeFailureException("core.bundle");
        byte[] archive = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(archive)) != XrayCoreReleaseSpecification.PinnedMacArm64.ArchiveSha256)
            throw new RuntimeFailureException("core.bundle");
        byte[] executable = ExtractExecutable(archive);
        if (Convert.ToHexStringLower(SHA256.HashData(executable)) != XrayCoreReleaseSpecification.PinnedMacArm64ExecutableSha256)
            throw new RuntimeFailureException("core.bundle");
        return await PublishExecutableAsync(executable, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreparedCoreRelease> PublishExecutableAsync(byte[] executable, CancellationToken cancellationToken)
    {
        string digest = Convert.ToHexStringLower(SHA256.HashData(executable));
        PrivateResourceFiles.EnsureDirectory(_root);
        string directory = Path.Combine(_root, _release.Version + "-" + digest); PrivateResourceFiles.EnsureDirectory(directory);
        string path = Path.Combine(directory, "xray");
        if (File.Exists(path) && new FileInfo(path).LinkTarget is not null) throw new IOException("Core executable cannot be a symbolic link.");
        if (!File.Exists(path) || new FileInfo(path).Length != executable.Length || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)), SHA256.HashData(executable)))
            PrivateResourceFiles.Replace(path, executable, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Persist permission metadata as well as content before making the path available to a process owner.
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) file.Flush(true);
        DurableFiles.SyncDirectory(directory);
        cancellationToken.ThrowIfCancellationRequested();
        return new(path, digest, _release.Version);
    }
    public static bool VerifyChecksum(byte[] checksum, string expectedArchiveSha256)
    {
        if (checksum.Length > 4096 || !ValidHash(expectedArchiveSha256)) return false;
        string[] lines = Encoding.ASCII.GetString(checksum).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var hashes = lines.Where(line => line.StartsWith("SHA2-256= ", StringComparison.Ordinal)).ToArray();
        return hashes.Length == 1 && string.Equals(hashes[0][10..], expectedArchiveSha256, StringComparison.OrdinalIgnoreCase);
    }
    public static byte[] ExtractExecutable(byte[] archive)
    {
        if (archive.Length is < 4 or > 64 * 1024 * 1024) throw new InvalidDataException("Core archive size is invalid.");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive, false), ZipArchiveMode.Read);
            if (zip.Entries.Count is < 1 or > 64) throw new InvalidDataException("Core archive entry limit.");
            long total = 0; ZipArchiveEntry? executable = null; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName;
                if (name.Length == 0 || name.Length > 240 || name[0] == '/' || name.Contains('\\') || name.Contains(':') || name.Contains('\0')
                    || name.Split('/').Any(part => part is "." or "..") || !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException("Core archive contains an unsafe path or symbolic link.");
                if (entry.Length < 0 || entry.Length > 128 * 1024 * 1024 || (total += entry.Length) > 256 * 1024 * 1024) throw new InvalidDataException("Core archive extraction size limit.");
                if (name == "xray") executable = entry;
            }
            if (executable is null || executable.Length < 32) throw new InvalidDataException("Core archive has no executable.");
            using var input = executable.Open(); using var output = new MemoryStream(); byte[] buffer = new byte[65536];
            while (true) { int count = input.Read(buffer); if (count == 0) break; if (output.Length + count > 128 * 1024 * 1024 || output.Length + count > executable.Length) throw new InvalidDataException("Core executable size limit."); output.Write(buffer, 0, count); }
            byte[] result = output.ToArray();
            if (result.Length != executable.Length || BinaryPrimitives.ReadUInt32LittleEndian(result) != 0xfeedfacf || BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)) != 0x0100000c)
                throw new InvalidDataException("Core executable must be an ARM64 Mach-O image.");
            return result;
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { throw new InvalidDataException("Core ZIP validation failed."); }
    }
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(char.IsAsciiHexDigit);
}
