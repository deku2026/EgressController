// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: DisableRuntimeMarshalling]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace EgressController.Mac;

/// <summary>Read-only native platform adapter. No helper installation or network mutation exports exist.</summary>
public sealed unsafe class NativeMacPlatform(LiveOperationGate gate) : IMacBundleMetadataReader, IMacIconSource
{
    private const int MaximumBufferBytes = 16 * 1024 * 1024;
    private delegate int ReadBuffer(byte* buffer, nuint capacity, nuint* required);

    public MacBundleMetadata ReadBundle(string bundlePath)
    {
        RequirePath(bundlePath); gate.Require("read application bundle metadata"); EnsureAbi();
        return JsonSerializer.Deserialize(Read((buffer, capacity, required) => NativeMethods.Bundle(bundlePath, buffer, capacity, required), "bundle metadata"),
            MacPlatformJson.Default.MacBundleMetadata) ?? throw new IOException("Native bundle metadata was empty.");
    }
    public string Canonicalize(string path)
    {
        RequirePath(path); gate.Require("resolve canonical application path"); EnsureAbi();
        byte[] data = Read((buffer, capacity, required) => NativeMethods.CanonicalPath(path, buffer, capacity, required), "canonical path");
        return Encoding.UTF8.GetString(data);
    }
    public byte[] ReadPng(string canonicalBundlePath, int pixelSize)
    {
        RequirePath(canonicalBundlePath); gate.Require("read native application icon"); EnsureAbi();
        if (pixelSize is < 16 or > 512) throw new ArgumentOutOfRangeException(nameof(pixelSize));
        return Read((buffer, capacity, required) => NativeMethods.Icon(canonicalBundlePath, (uint)pixelSize, buffer, capacity, required), "application icon");
    }
    public MacNetworkInterface[] ReadInterfaces()
    {
        gate.Require("read network service identities"); EnsureAbi();
        return JsonSerializer.Deserialize(Read(NativeMethods.Interfaces, "network interfaces"), MacPlatformJson.Default.MacNetworkInterfaceArray) ?? [];
    }
    public MacDnsServiceSnapshot[] ReadDnsSnapshots()
    {
        gate.Require("read DNS recovery state"); EnsureAbi();
        return JsonSerializer.Deserialize(Read(NativeMethods.Dns, "DNS recovery state"), MacPlatformJson.Default.MacDnsServiceSnapshotArray) ?? [];
    }
    private static void RequirePath(string path)
    {
        if (!MacPath.IsAbsolute(path)) throw new ArgumentException("Expected an absolute UTF-8 path without embedded NUL.", nameof(path));
    }
    private static void EnsureAbi()
    {
        if (NativeMethods.Version() != 1) throw new IOException("Unsupported EgressMacNative ABI version.");
    }
    private static byte[] Read(ReadBuffer call, string operation)
    {
        nuint required = 0;
        int status = call(null, 0, &required);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (status is not (0 or 1) || required == 0 || required > MaximumBufferBytes)
                throw new IOException($"macOS {operation} failed (native status {status}, required bytes {required}).");
            byte[] buffer = new byte[(int)required];
            fixed (byte* pointer = buffer)
            {
                status = call(pointer, (nuint)buffer.Length, &required);
            }
            if (status == 1) continue; // The read-only platform snapshot changed between size query and copy.
            if (status != 0 || required > (nuint)buffer.Length)
                throw new IOException($"macOS {operation} failed (native status {status}).");
            return buffer.AsSpan(0, (int)required).ToArray();
        }
        throw new IOException($"macOS {operation} changed repeatedly while copying its snapshot.");
    }
}

internal static unsafe partial class NativeMethods
{
    private const string Library = "EgressMacNative";
    [LibraryImport(Library, EntryPoint = "egress_mac_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Version();
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_bundle_json", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Bundle(string path, byte* buffer, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_canonical_path", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CanonicalPath(string path, byte* buffer, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_icon_png", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Icon(string path, uint size, byte* buffer, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_interfaces_json")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Interfaces(byte* buffer, nuint capacity, nuint* required);
    [LibraryImport(Library, EntryPoint = "egress_mac_copy_dns_json")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Dns(byte* buffer, nuint capacity, nuint* required);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MacBundleMetadata))]
[JsonSerializable(typeof(MacNetworkInterface[]))]
[JsonSerializable(typeof(MacDnsServiceSnapshot[]))]
internal partial class MacPlatformJson : JsonSerializerContext;
