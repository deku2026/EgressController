using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using EgressController.Core.Contracts;
using EgressController.Core.Protection;

namespace EgressController.Subscriptions;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecretStore : ISecretStore
{
    public string? Read(string account)
    {
        RuntimeSafety.RequireLiveOperations();
        if (!CredRead("EgressController/" + account, 1, 0, out nint pointer))
        { if (Marshal.GetLastPInvokeError() == 1168) return null; throw new IOException("Windows 凭据读取失败。"); }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer); byte[] data = new byte[credential.BlobSize];
            Marshal.Copy(credential.Blob, data, 0, data.Length);
            try { return Encoding.UTF8.GetString(data); } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(data); }
        }
        finally { CredFree(pointer); }
    }
    public void Write(string account, string secret)
    {
        RuntimeSafety.RequireLiveOperations(); byte[] bytes = Encoding.UTF8.GetBytes(secret);
        nint target = Marshal.StringToCoTaskMemUni("EgressController/" + account), blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = target, BlobSize = (uint)bytes.Length, Blob = blob, Persist = 2 };
            if (!CredWrite(ref credential, 0)) throw new IOException("Windows 凭据保存失败。");
        }
        finally { Array.Clear(bytes); Marshal.Copy(bytes, 0, blob, bytes.Length); Marshal.FreeCoTaskMem(blob); Marshal.FreeCoTaskMem(target); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Credential
    { public uint Flags, Type; public nint TargetName, Comment; public long LastWritten; public uint BlobSize; public nint Blob; public uint Persist, AttributeCount; public nint Attributes, TargetAlias, UserName; }
    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredRead(string target, uint type, uint flags, out nint credential);
    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CredWrite(ref Credential credential, uint flags);
    [LibraryImport("advapi32.dll")] private static partial void CredFree(nint credential);
}
