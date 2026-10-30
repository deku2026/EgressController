// SPDX-License-Identifier: MIT
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Mac;

public sealed unsafe class NativeMacPrivileges(LiveOperationGate gate)
{
    private delegate int ReadBuffer(byte* output, nuint capacity, nuint* required);
    public uint GetUserId() { gate.Require("read user identity"); return PrivilegeMethods.Uid(); }
    public uint GetGroupId() { gate.Require("read group identity"); return PrivilegeMethods.Gid(); }
    public uint GetEffectiveUserId() { gate.Require("read effective identity"); return PrivilegeMethods.Euid(); }
    public MacPeerIdentity ReadPeer(int socketDescriptor)
    { gate.Require("authenticate local helper peer"); return JsonSerializer.Deserialize(Read((p, n, r) => PrivilegeMethods.Peer(socketDescriptor, p, n, r)), MacHelperWireJson.Default.MacPeerIdentity)!; }
    public MacPeerIdentity ReadProcess(int processId)
    { gate.Require("verify authenticated process instance"); return JsonSerializer.Deserialize(Read((p, n, r) => PrivilegeMethods.Process(processId, p, n, r)), MacHelperWireJson.Default.MacPeerIdentity)!; }
    public MacFileSecurityInfo ReadFileInfo(string path)
    { gate.Require("inspect local artifact ownership"); return JsonSerializer.Deserialize(Read((p, n, r) => PrivilegeMethods.File(path, p, n, r)), MacHelperWireJson.Default.MacFileSecurityInfo)!; }
    public MacFileSecurityInfo ReadDescriptorInfo(int descriptor)
    { gate.Require("inspect opened artifact ownership"); return JsonSerializer.Deserialize(Read((p, n, r) => PrivilegeMethods.Descriptor(descriptor, p, n, r)), MacHelperWireJson.Default.MacFileSecurityInfo)!; }
    public SafeFileHandle OpenOwnedRead(string path, uint expectedOwner, long maximumBytes)
    {
        gate.Require("open verified artifact without following links");
        if (!MacPath.IsAbsolute(path) || MacPath.HasTraversal(path)) throw new IOException("Invalid artifact path.");
        int descriptor = PrivilegeMethods.Open(path, 0x100); // Darwin O_RDONLY | O_NOFOLLOW.
        if (descriptor < 0) throw new IOException("Unable to open owned artifact.");
        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            var info = ReadDescriptorInfo(descriptor);
            if (!info.IsRegular || info.UserId != expectedOwner || info.Size is < 0 || info.Size > maximumBytes || (info.Mode & 0x12) != 0)
                throw new IOException("Artifact owner, permissions, type or size was not trusted.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    public void SetOwnerAndMode(string path, uint owner, uint group, uint mode)
    { gate.Require("set installed helper ownership"); Check(PrivilegeMethods.Owner(path, owner, group, mode), "secure helper ownership"); }
    public void BindSocket(Socket socket, string bsdInterface)
    { gate.Require("bind resource socket to selected interface"); Check(PrivilegeMethods.Bind(socket.SafeHandle.DangerousGetHandle().ToInt32(), bsdInterface), "bind selected interface"); }
    public MacDnsServiceSnapshot[] ReadDnsSnapshots() => new NativeMacPlatform(gate).ReadDnsSnapshots();
    public void SetDns(MacDnsServiceSnapshot[] snapshots, string serverIp) => WriteDns(snapshots, serverIp, false);
    public void RestoreDns(MacDnsServiceSnapshot[] snapshots) => WriteDns(snapshots, "", true);
    private void WriteDns(MacDnsServiceSnapshot[] snapshots, string serverIp, bool restore)
    {
        gate.Require(restore ? "restore system DNS" : "apply TUN DNS");
        new MacDnsRecoverySnapshot(1, Guid.NewGuid(), DateTimeOffset.UtcNow, snapshots).Validate();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshots, MacPlatformJson.Default.MacDnsServiceSnapshotArray);
        fixed (byte* data = json) Check(PrivilegeMethods.Dns(data, (nuint)json.Length, serverIp, restore ? 1 : 0), "commit system DNS");
    }
    public bool IsTunInterfacePresent(string name) { gate.Require("inspect live TUN interface"); return Predicate(PrivilegeMethods.Tun(name)); }
    public bool IsTunTakeoverActive(string name, string ipv4Cidr)
    { gate.Require("inspect TUN address and IPv4/IPv6 route ownership"); return Predicate(PrivilegeMethods.Takeover(name, ipv4Cidr)); }
    public void VerifyProcessCode(int pid, string expectedPath)
    { gate.Require("verify running GUI code identity"); Check(PrivilegeMethods.VerifyCode(pid, expectedPath), "verify authenticated GUI code identity"); }
    private static bool Predicate(int result) { if (result < 0) throw new IOException("Unable to inspect current TUN takeover."); return result == 1; }
    private static void Check(int status, string operation) { if (status != 0) throw new IOException($"Unable to {operation} (native status {status})."); }
    private static byte[] Read(ReadBuffer read)
    {
        nuint required = 0; int status = read(null, 0, &required);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (status is not (0 or 1) || required is 0 or > 2_097_152) throw new IOException("Native identity/ownership query failed.");
            byte[] data = new byte[(int)required];
            fixed (byte* pointer = data) status = read(pointer, (nuint)data.Length, &required);
            if (status == 1) continue;
            if (status != 0 || required > (nuint)data.Length) throw new IOException("Native identity/ownership query changed or failed.");
            return data.AsSpan(0, (int)required).ToArray();
        }
        throw new IOException("Native identity query changed repeatedly.");
    }
}

public static class MacPeerSecurity
{
    public static async Task VerifyAsync(NativeMacPrivileges native, MacPeerIdentity peer, MacHelperInstallation installation, CancellationToken cancellationToken = default)
    {
        if (peer.UserId != installation.AuthorizedUserId || peer.ProcessId <= 0 || peer.StartedAtMicroseconds <= 0
            || peer.ExecutablePath != installation.AuthorizedGuiPath || Path.GetFileName(peer.ExecutablePath) != "EgressController.Desktop")
            throw new UnauthorizedAccessException("The helper peer is not the installed GUI instance.");
        var current = native.ReadProcess(peer.ProcessId);
        if (current != peer) throw new UnauthorizedAccessException("The authenticated GUI process instance changed.");
        var file = native.ReadFileInfo(installation.AuthorizedGuiPath);
        if (file.UserId != 0 && file.UserId != installation.AuthorizedUserId) throw new UnauthorizedAccessException("The GUI artifact is not owned by its authorized user.");
        using var handle = native.OpenOwnedRead(installation.AuthorizedGuiPath, file.UserId, 512L * 1024 * 1024);
        await using var stream = new FileStream(handle, FileAccess.Read);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!hash.Equals(installation.AuthorizedGuiSha256, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("The installed GUI digest changed.");
        native.VerifyProcessCode(peer.ProcessId, installation.AuthorizedGuiPath);
    }
}

public sealed class MacPhysicalSocketBinder(NativeMacPrivileges native) : IPhysicalSocketBinder
{ public void Bind(Socket socket, InterfaceBinding binding) => native.BindSocket(socket, binding.BsdName); }

internal static unsafe partial class PrivilegeMethods
{
    private const string Library = "EgressMacNative";
    [LibraryImport(Library, EntryPoint = "egress_mac_uid")][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial uint Uid();
    [LibraryImport(Library, EntryPoint = "egress_mac_gid")][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial uint Gid();
    [LibraryImport(Library, EntryPoint = "egress_mac_euid")][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial uint Euid();
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_peer_json")][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Peer(int fd, byte* data, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_process_json")][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Process(int pid, byte* data, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_file_info", StringMarshalling = StringMarshalling.Utf8)][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int File(string path, byte* data, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_fd_info")][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Descriptor(int fd, byte* data, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_verify_process_code", StringMarshalling = StringMarshalling.Utf8)][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int VerifyCode(int pid, string path);
    [LibraryImport(Library, EntryPoint = "egress_mac_set_owner_mode", StringMarshalling = StringMarshalling.Utf8)][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Owner(string path, uint uid, uint gid, uint mode);
    [LibraryImport(Library, EntryPoint = "egress_mac_bind_socket", StringMarshalling = StringMarshalling.Utf8)][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Bind(int fd, string name);
    [LibraryImport(Library, EntryPoint = "egress_mac_write_dns", StringMarshalling = StringMarshalling.Utf8)][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Dns(byte* json, nuint length, string address, int restore);
    [LibraryImport(Library, EntryPoint = "egress_mac_tun_present", StringMarshalling = StringMarshalling.Utf8)][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Tun(string name);
    [LibraryImport(Library, EntryPoint = "egress_mac_tun_takeover", StringMarshalling = StringMarshalling.Utf8)][UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])] internal static partial int Takeover(string name, string cidr);
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] internal static partial int Open(string path, int flags);
}
