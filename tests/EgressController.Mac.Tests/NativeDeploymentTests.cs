// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using EgressController.Mac;

namespace EgressController.Mac.Tests;

public sealed class NativeDeploymentTests
{
    [Fact]
    public void ReferencingProjectReceivesNativeLibraryAndLoadsPureAbiVersion()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "libEgressMacNative.dylib")),
            "The native bridge must propagate through ProjectReference without a DYLD_LIBRARY_PATH override.");
        nint library = NativeLibrary.Load("EgressMacNative", typeof(NativeMacPlatform).Assembly,
            DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories);
        try
        {
            var version = Marshal.GetDelegateForFunctionPointer<AbiVersion>(NativeLibrary.GetExport(library, "egress_mac_abi_version"));
            Assert.Equal(1u, version()); // Constant-only export: no platform state or live gate is accessed.
        }
        finally { NativeLibrary.Free(library); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersion();
}
