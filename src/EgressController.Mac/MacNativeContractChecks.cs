using System.Runtime.InteropServices;
using System.Text.Json;

namespace EgressController.Mac;

/// <summary>Pure native serialization + AOT managed deserialization; no filesystem, UID, process, network or privilege access.</summary>
public static partial class MacNativeContractChecks
{
    public static void VerifyFileInfo()
    {
        uint[] modes = [0x8180, 0x41c0, 0xc180, 0xa1ff]; // regular, directory, socket, symlink
        for (int index = 0; index < modes.Length; index++)
        {
            byte[] bytes = new byte[1024];
            int status = FileInfoFixture(modes[index], bytes, (nuint)bytes.Length, out nuint size);
            if (status != 0 || size > (nuint)bytes.Length) throw new InvalidDataException("Native file-info fixture failed.");
            var value = JsonSerializer.Deserialize(bytes.AsSpan(0, (int)size), MacHelperWireJson.Default.MacFileSecurityInfo)!;
            bool[] kinds = [value.IsRegular, value.IsDirectory, value.IsSocket, value.IsSymbolicLink];
            if (kinds.Count(flag => flag) != 1 || !kinds[index] || value.Mode != (modes[index] & 0xfff))
                throw new InvalidDataException("Native file-info wire contract changed.");
        }
    }
    [LibraryImport("EgressMacNative", EntryPoint = "egress_mac_file_info_fixture")]
    private static partial int FileInfoFixture(uint mode, [Out] byte[] output, nuint capacity, out nuint required);
}
