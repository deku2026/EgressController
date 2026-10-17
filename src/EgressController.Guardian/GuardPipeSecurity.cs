using System.IO.Pipes;
using System.Runtime.InteropServices;
using EgressController.Core.Protection;
using EgressController.Windows.Process;
using Microsoft.Win32.SafeHandles;

namespace EgressController.Guardian;

internal static partial class GuardPipeSecurity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public uint Length; public nint Descriptor; public int Inherit; }
    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ConvertDescriptor(string sddl, uint revision, out nint descriptor, out uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreatePipe(string name, uint mode, uint pipeMode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenPipe(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint pointer);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    public static NamedPipeServerStream CreateServer(GuardPaths paths)
    {
        RuntimeSafety.RequireLiveOperations();
        string descriptor = GuardPipeAccess.Descriptor(paths.OwnerSid);
        GuardNative.Check(ConvertDescriptor(descriptor, 1, out nint security, out _));
        try
        {
            var attributes = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = security };
            // Duplex + overlapped; PIPE_REJECT_REMOTE_CLIENTS. Explicit ACL, non-inheritable.
            nint raw = CreatePipe(@"\\.\pipe\" + paths.PipeName, 3 | 0x40000000, 8, 16, 65536, 65536, 0, ref attributes);
            if (raw == -1) throw GuardNative.Error();
            var handle = new SafePipeHandle(raw, true);
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(security); }
    }
    public static async Task<NamedPipeClientStream> ConnectAsync(GuardPaths paths, CancellationToken token)
    {
        RuntimeSafety.RequireLiveOperations();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // Match the server's individual rights; prevent a spoofed server from
            // impersonating the elevated main before VerifyServer has completed.
            nint raw = OpenPipe(@"\\.\pipe\" + paths.PipeName, GuardPipeAccess.ClientRights, 0, 0, 3, GuardPipeAccess.ClientFlags, 0);
            if (raw != -1)
            {
                var handle = new SafePipeHandle(raw, true);
                try { return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle); }
                catch { handle.Dispose(); throw; }
            }
            int error = Marshal.GetLastPInvokeError();
            if (error is not (2 or 231)) throw new System.ComponentModel.Win32Exception(error);
            await Task.Delay(TimeSpan.FromMilliseconds(25), token).ConfigureAwait(false);
        }
    }
    public static GuardProcess Client(NamedPipeServerStream pipe, GuardPaths paths)
    {
        GuardNative.Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid));
        if (GuardWindowsIdentity.ProcessSid(pid) != paths.OwnerSid)
            throw new UnauthorizedAccessException("通信进程不属于守护账号。");
        var identity = new WindowsProcessIdentityResolver(new ExecutablePathCanonicalizer()).Resolve(pid)
            ?? throw new UnauthorizedAccessException("无法确认通信进程身份。");
        return new(new(pid, identity.StartTimeUtc), 0, identity.ExePathFinal
            ?? throw new UnauthorizedAccessException("无法确认通信进程路径。"));
    }
    public static void VerifyServer(NamedPipeClientStream pipe, GuardPaths paths)
    {
        GuardNative.Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid));
        GuardPipeAccess.ValidateServer(paths, pid, new WindowsGuardServiceControl(paths).Query());
    }
}
