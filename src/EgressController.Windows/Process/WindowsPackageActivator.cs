using System.Runtime.InteropServices;

namespace EgressController.Windows.Process;

/// <summary>
/// NativeAOT-safe IApplicationActivationManager bridge. Unlike launching
/// <c>explorer.exe shell:AppsFolder\...</c> and guessing from a process scan,
/// ActivateApplication returns the package application's actual root PID.
/// </summary>
internal static unsafe partial class WindowsPackageActivator
{
    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitMultithreaded = 0x0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly Guid ActivationManagerClass =
        new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid ActivationManagerInterface =
        new("2E941141-7F97-4756-BA1D-9DECDE894A3D");

    public static uint ActivateApplication(string aumid, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aumid);

        int initializeResult = CoInitializeEx(0, CoinitMultithreaded);
        bool uninitialize = initializeResult is 0 or 1;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
            throw new COMException("初始化 package activation COM apartment 失败。", initializeResult);

        nint manager = 0;
        try
        {
            Guid classId = ActivationManagerClass;
            Guid interfaceId = ActivationManagerInterface;
            int createResult = CoCreateInstance(
                in classId,
                0,
                ClsctxInprocServer,
                in interfaceId,
                out manager);
            if (createResult < 0 || manager == 0)
                throw new COMException("创建 IApplicationActivationManager 失败。", createResult);

            // Let the activation broker transfer foreground permission to the app when Windows
            // permits it. Activation itself remains valid if the current process cannot grant it.
            _ = CoAllowSetForegroundWindow(manager, 0);

            string activationArguments = arguments ?? string.Empty;
            uint processId = 0;
            nint* vtable = *(nint**)manager;
            var activate = (delegate* unmanaged[Stdcall]<nint, char*, char*, uint, uint*, int>)vtable[3];
            fixed (char* aumidPointer = aumid)
            fixed (char* argumentsPointer = activationArguments)
            {
                int activateResult = activate(
                    manager,
                    aumidPointer,
                    argumentsPointer,
                    0,
                    &processId);
                if (activateResult < 0)
                    throw new COMException($"激活 packaged app '{aumid}' 失败。", activateResult);
            }

            if (processId == 0)
                throw new InvalidOperationException("Windows package activation 没有返回根 PID。");
            return processId;
        }
        finally
        {
            if (manager != 0)
            {
                nint* vtable = *(nint**)manager;
                var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
                _ = release(manager);
            }
            if (uninitialize)
                CoUninitialize();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoAllowSetForegroundWindow(nint unknown, nint reserved);
}
