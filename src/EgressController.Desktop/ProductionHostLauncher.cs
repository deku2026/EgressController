using System.Runtime.InteropServices;
using EgressController.Mac;

namespace EgressController.Desktop;

/// <summary>Rider's managed entrypoint becomes the same signed NativeAOT product that is packaged for users.</summary>
public static partial class ProductionHostLauncher
{
    public static bool NeedsHandoff(StartupMode mode, bool managedRuntime, bool macOS)
        => mode == StartupMode.Production && managedRuntime && macOS;

    public static string ExecutablePath(string baseDirectory)
        => Path.GetFullPath(Path.Combine(baseDirectory, "production", "Egress.app", "Contents", "MacOS", "EgressController.Desktop"));

    public static unsafe int ReplaceWithNativeProduct(string baseDirectory)
    {
        LiveOperationGate.ForExplicitLiveUse().Require("enter built NativeAOT product from Rider");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        string executable = ExecutablePath(baseDirectory);
        if (!File.Exists(executable)) throw new IOException("Rider 的完整生产产物缺失。请重新 Build EgressController.Desktop；构建会准备内置 Xray 和原生助手。");
        nint argument = Marshal.StringToCoTaskMemUTF8(executable);
        try
        {
            nint* arguments = stackalloc nint[2] { argument, 0 };
            // exec preserves the PID/lifetime: stopping the Rider run stops this actual main instance.
            _ = Exec(executable, arguments);
            throw new IOException("无法进入已构建的原生客户端，请检查构建产物与执行权限。");
        }
        finally { Marshal.FreeCoTaskMem(argument); }
    }

    [LibraryImport("libc", EntryPoint = "execv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int Exec(string path, nint* arguments);
}
