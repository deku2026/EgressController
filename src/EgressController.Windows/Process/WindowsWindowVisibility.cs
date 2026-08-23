using System.Runtime.InteropServices;

namespace EgressController.Windows.Process;

public static partial class WindowsWindowVisibility
{
    public static bool IsVisible(nint window)
        => window != 0 && IsWindowVisible(window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);
}
