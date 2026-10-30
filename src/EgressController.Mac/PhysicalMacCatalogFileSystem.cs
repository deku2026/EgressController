// SPDX-License-Identifier: MIT
namespace EgressController.Mac;

/// <summary>Only constructed for explicit live discovery. Tests inject a synthetic filesystem.</summary>
public sealed class PhysicalMacCatalogFileSystem(LiveOperationGate gate, NativeMacPlatform platform) : IMacCatalogFileSystem
{
    public bool DirectoryExists(string path) { gate.Require("inspect application directory"); return Directory.Exists(path); }
    public string Canonicalize(string path) { gate.Require("resolve application path"); return platform.Canonicalize(path); }
    public IReadOnlyList<MacFileEntry> Enumerate(string directory)
    {
        gate.Require("enumerate application directory");
        return new DirectoryInfo(directory).EnumerateFileSystemInfos().Select(info => new MacFileEntry(info.FullName,
            (info.Attributes & FileAttributes.Directory) != 0, (info.Attributes & FileAttributes.ReparsePoint) != 0)).ToArray();
    }
    public bool IsExecutableMachO(string path)
    {
        gate.Require("inspect application executable");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if ((File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) return false;
        using var stream = File.OpenRead(path);
        return MachOInspector.IsExecutable(stream);
    }
    // Recognition only. This must never be used as the inventory membership verdict.
    public static bool IsMachOMagic(uint value) => value is 0xfeedface or 0xcefaedfe or 0xfeedfacf or 0xcffaedfe
        or 0xcafebabe or 0xbebafeca or 0xcafebabf or 0xbfbafeca;
}
