using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace EgressController.Windows.Process;

/// <summary>
/// A non-terminating Job Object retained for one managed launch. It supplies an OS-maintained
/// candidate set even after Windows reparents a descendant; closing this tracker never kills the
/// application because the job has no KILL_ON_JOB_CLOSE limit.
/// </summary>
public sealed unsafe class WindowsProcessJob : IDisposable
{
    private const int ErrorMoreData = 234;
    private readonly object _gate = new();
    private HANDLE _handle;

    internal WindowsProcessJob(HANDLE handle)
    {
        if (handle.IsNull)
            throw new ArgumentException("Job handle must not be null.", nameof(handle));
        _handle = handle;
    }

    public IReadOnlySet<uint> SnapshotProcessIds()
    {
        lock (_gate)
        {
            if (_handle.IsNull)
                return new HashSet<uint>();

            int capacity = 16;
            while (capacity <= 4096)
            {
                int size = JOBOBJECT_BASIC_PROCESS_ID_LIST.SizeOf(capacity);
                byte* buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
                try
                {
                    uint returnedLength = 0;
                    bool success = PInvoke.QueryInformationJobObject(
                        _handle,
                        JOBOBJECTINFOCLASS.JobObjectBasicProcessIdList,
                        buffer,
                        checked((uint)size),
                        &returnedLength);
                    var info = (JOBOBJECT_BASIC_PROCESS_ID_LIST*)buffer;
                    if (!success)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorMoreData && info->NumberOfAssignedProcesses > capacity)
                        {
                            capacity = checked((int)info->NumberOfAssignedProcesses + 8);
                            continue;
                        }
                        throw new Win32Exception(error, "QueryInformationJobObject failed");
                    }

                    if (info->NumberOfAssignedProcesses > capacity)
                    {
                        capacity = checked((int)info->NumberOfAssignedProcesses + 8);
                        continue;
                    }

                    var result = new HashSet<uint>();
                    nuint* processIds = (nuint*)(buffer + sizeof(uint) * 2);
                    for (uint index = 0; index < info->NumberOfProcessIdsInList; index++)
                    {
                        nuint processId = processIds[index];
                        if (processId <= uint.MaxValue)
                            result.Add((uint)processId);
                    }
                    return result;
                }
                finally
                {
                    NativeMemory.Free(buffer);
                }
            }

            throw new InvalidOperationException("Managed Job process list exceeded the safety limit.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle.IsNull)
                return;
            _ = PInvoke.CloseHandle(_handle);
            _handle = default;
        }
        GC.SuppressFinalize(this);
    }
}
