using System.Runtime.InteropServices;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Core.Protection;

namespace EgressController.Mac;

public sealed partial class KeychainSecretStore : ISecretStore
{
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("org.egresscontroller.manager");
    public string? Read(string account)
    {
        RuntimeSafety.RequireLiveOperations();
        byte[] name = Encoding.UTF8.GetBytes(account);
        int status = Find(0, (uint)Service.Length, Service, (uint)name.Length, name, out uint length, out nint data, out nint item);
        if (status == -25300) return null;
        if (status != 0) throw new IOException($"Keychain 读取失败（{status}）。");
        try { return Marshal.PtrToStringUTF8(data, checked((int)length)); }
        finally { FreeContent(0, data); if (item != 0) Release(item); }
    }
    public void Write(string account, string secret)
    {
        RuntimeSafety.RequireLiveOperations();
        byte[] name = Encoding.UTF8.GetBytes(account), bytes = Encoding.UTF8.GetBytes(secret);
        int found = Find(0, (uint)Service.Length, Service, (uint)name.Length, name, out _, out nint data, out nint item);
        if (found == 0) FreeContent(0, data);
        try
        {
            int status = found == 0 ? Modify(item, 0, (uint)bytes.Length, bytes)
                : found == -25300 ? Add(0, (uint)Service.Length, Service, (uint)name.Length, name, (uint)bytes.Length, bytes, out item) : found;
            if (status != 0) throw new IOException($"Keychain 保存失败（{status}）。");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); if (item != 0) Release(item); }
    }
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")] private static partial int Find(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out nint password, out nint item);
    [LibraryImport(Security, EntryPoint = "SecKeychainAddGenericPassword")] private static partial int Add(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] password, out nint item);
    [LibraryImport(Security, EntryPoint = "SecKeychainItemModifyAttributesAndData")] private static partial int Modify(nint item, nint attributes, uint length, byte[] bytes);
    [LibraryImport(Security, EntryPoint = "SecKeychainItemFreeContent")] private static partial int FreeContent(nint attributes, nint data);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")] private static partial void Release(nint value);
}
