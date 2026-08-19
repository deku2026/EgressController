using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using EgressController.Core.Contracts;
using EgressController.Core.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace EgressController.Windows.Process;

/// <summary>PID → executable path + start time, with final-path canonicalization (plan §Step 08).</summary>
public sealed unsafe class WindowsProcessIdentityResolver : IProcessIdentityResolver
{
    private readonly IExecutablePathCanonicalizer _canon;

    public WindowsProcessIdentityResolver(IExecutablePathCanonicalizer canon)
        => _canon = canon;

    public ProcessIdentity? Resolve(uint pid)
    {
        HANDLE h = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, bInheritHandle: false, pid);
        if (h.IsNull)
            return null; // gone or not inspectable (another privilege domain) — not treated as managed

        try
        {
            char[] buffer = new char[32768];
            uint size = (uint)buffer.Length;
            bool ok;
            fixed (char* p = buffer)
            {
                ok = PInvoke.QueryFullProcessImageName(h, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(p), &size);
            }
            if (!ok)
                return null;

            string observed = new string(buffer, 0, (int)size);

            FILETIME creation, exit, kernel, user;
            if (!PInvoke.GetProcessTimes(h, &creation, &exit, &kernel, &user))
                return null;

            long ft = ((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
            DateTime startUtc = DateTime.FromFileTimeUtc(ft);

            return new ProcessIdentity(pid, startUtc, observed, _canon.Canonicalize(observed));
        }
        finally
        {
            PInvoke.CloseHandle(h);
        }
    }
}