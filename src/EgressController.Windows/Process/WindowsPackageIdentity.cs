using System.ComponentModel;
using Windows.Win32;

namespace EgressController.Windows.Process;

public static unsafe class WindowsPackageIdentity
{
    public static bool IsPackaged()
    {
        uint length = 0;
        int result = (int)PInvoke.GetCurrentPackageFullName(&length, null);
        return result switch
        {
            15700 => false, // APPMODEL_ERROR_NO_PACKAGE
            122 or 0 => true, // ERROR_INSUFFICIENT_BUFFER / ERROR_SUCCESS
            _ => throw new Win32Exception(result),
        };
    }
}
