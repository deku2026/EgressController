using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using EgressController.Core.Ipc;

namespace EgressController.ElevatedHost;

public sealed record SingBoxHostStatus(
    string State,
    int? ProcessId,
    int DroppedOutputCount,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record SingBoxOutputLine(string Source, string Line, int DroppedOutputCount);

public interface ISingBoxProcessHost : IAsyncDisposable
{
    event Action<SingBoxOutputLine>? Output;
    SingBoxHostStatus Status { get; }
    Task<SingBoxHostStatus> StartAsync(ElevatedIpcMessage request, CancellationToken cancellationToken = default);
    Task<SingBoxHostStatus> StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts only the fixed sing-box command and owns it through a kill-on-close Windows Job.
/// Stdout/stderr are first placed in bounded channels; a slow IPC client can only increase the
/// visible dropped counter and can never back up the child process pipes.
/// </summary>
public sealed partial class SingBoxProcessHost : ISingBoxProcessHost
{
    private const int OutputCapacity = 256;
    private readonly ElevatedHostPathPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private Process? _process;
    private JobHandle? _job;
    private CancellationTokenSource? _outputLifetime;
    private Task? _outputPump;
    private Channel<SingBoxOutputLine>? _outputChannel;
    private int _dropped;
    private SingBoxHostStatus _status = new("stopped", null, 0, null, null);

    public SingBoxProcessHost(ElevatedHostPathPolicy policy)
        => _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public event Action<SingBoxOutputLine>? Output;

    public SingBoxHostStatus Status
    {
        get
        {
            lock (_stateGate)
                return _status;
        }
    }

    public async Task<SingBoxHostStatus> StartAsync(
        ElevatedIpcMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status.State is "running" or "starting")
            {
                if (request.Kind != ElevatedIpcKind.Restart)
                    return Status;
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            }

            ValidateRequest(request);
            SetStatus(new SingBoxHostStatus("starting", null, _dropped, null, null));
            string corePath = Path.GetFullPath(request.CorePath!);
            string configPath = Path.GetFullPath(request.ConfigPath!);
            string actualCoreHash = await ElevatedHostPathPolicy.ComputeSha256Async(corePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualCoreHash, request.CoreSha256, StringComparison.OrdinalIgnoreCase))
                throw new ElevatedHostValidationException("sing-box core SHA-256 与 check 时不一致。", "core.hash");
            string actualConfigHash = await ElevatedHostPathPolicy.ComputeSha256Async(configPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualConfigHash, request.ConfigSha256, StringComparison.OrdinalIgnoreCase))
                throw new ElevatedHostValidationException("config SHA-256 与 check 时不一致。", "config.hash");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = corePath,
                    WorkingDirectory = Path.GetDirectoryName(corePath) ?? _policy.NormalizedDataRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            process.StartInfo.ArgumentList.Add("run");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(configPath);
            if (!process.Start())
                throw new InvalidOperationException("无法启动 sing-box。");

            JobHandle job = JobHandle.CreateKillOnClose();
            try
            {
                job.Assign(process.Handle);
            }
            catch
            {
                job.Dispose();
                process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }

            _process = process;
            _job = job;
            _dropped = 0;
            _outputChannel = Channel.CreateBounded<SingBoxOutputLine>(new BoundedChannelOptions(OutputCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
            _outputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _outputPump = PumpOutputAsync(_outputLifetime.Token);
            _ = ReadOutputAsync(process.StandardOutput, "stdout", _outputLifetime.Token);
            _ = ReadOutputAsync(process.StandardError, "stderr", _outputLifetime.Token);
            _ = ObserveExitAsync(process, _outputLifetime.Token);
            SetStatus(new SingBoxHostStatus("running", process.Id, _dropped, null, null));
            return Status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ElevatedHostValidationException ex)
        {
            SetStatus(new SingBoxHostStatus("failed", null, _dropped, ex.Code, ex.Message));
            throw;
        }
        catch (Exception ex)
        {
            SetStatus(new SingBoxHostStatus("failed", null, _dropped, "process.start", ex.Message));
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SingBoxHostStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(new SingBoxHostStatus("stopped", null, _dropped, null, null));
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            await DisposeProcessAsync().ConfigureAwait(false);
        }
        _gate.Dispose();
    }

    public static void ValidateStartRequest(ElevatedHostPathPolicy policy, ElevatedIpcMessage request)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not ElevatedIpcKind.Start and not ElevatedIpcKind.Restart)
            throw new ElevatedHostValidationException("不是 Start/Restart IPC 命令。", "command.kind");
        if (!policy.IsAllowedCorePath(request.CorePath!))
            throw new ElevatedHostValidationException("core path 不在允许范围内。", "core.path");
        if (!policy.IsAllowedConfigPath(request.ConfigPath!))
            throw new ElevatedHostValidationException("config path 不在应用数据目录内。", "config.path");
        if (!ElevatedHostPathPolicy.IsSha256(request.CoreSha256)
            || !ElevatedHostPathPolicy.IsSha256(request.ConfigSha256))
            throw new ElevatedHostValidationException("core/config SHA-256 无效。", "hash.format");
    }

    private void ValidateRequest(ElevatedIpcMessage request)
        => ValidateStartRequest(_policy, request);

    private async Task ReadOutputAsync(StreamReader reader, string source, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var item = new SingBoxOutputLine(source, line, _dropped);
                if (!(_outputChannel?.Writer.TryWrite(item) ?? false))
                    Interlocked.Increment(ref _dropped);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Process? process = _process;
        if (process is null)
        {
            SetStatus(new SingBoxHostStatus("stopped", null, _dropped, null, null));
            return;
        }

        SetStatus(new SingBoxHostStatus("stopping", process.Id, _dropped, null, null));
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The child may have exited between HasExited and Kill.
        }
        finally
        {
            await DisposeProcessAsync().ConfigureAwait(false);
        }
    }

    private async Task PumpOutputAsync(CancellationToken cancellationToken)
    {
        ChannelReader<SingBoxOutputLine>? reader = _outputChannel?.Reader;
        if (reader is null)
            return;
        try
        {
            await foreach (SingBoxOutputLine item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                Output?.Invoke(item with { DroppedOutputCount = _dropped });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ObserveExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(_process, process))
                SetStatus(new SingBoxHostStatus("stopped", null, _dropped, null, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task DisposeProcessAsync()
    {
        CancellationTokenSource? outputLifetime = _outputLifetime;
        _outputLifetime = null;
        outputLifetime?.Cancel();
        _outputChannel?.Writer.TryComplete();
        if (_outputPump is not null)
        {
            try { await _outputPump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _outputPump = null;
        outputLifetime?.Dispose();
        _job?.Dispose();
        _job = null;
        _process?.Dispose();
        _process = null;
        _outputChannel = null;
    }

    private void SetStatus(SingBoxHostStatus status)
    {
        lock (_stateGate)
            _status = status;
    }

    private sealed class JobHandle : IDisposable
    {
        private nint _handle;

        private JobHandle(nint handle) => _handle = handle;

        public static JobHandle CreateKillOnClose()
        {
            nint handle = NativeMethods.CreateJobObject(0, null);
            if (handle == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 },
            };
            if (!NativeMethods.SetInformationJobObject(
                    handle,
                    9,
                    ref limits,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                NativeMethods.CloseHandle(handle);
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");
            }
            return new JobHandle(handle);
        }

        public void Assign(nint processHandle)
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
        }

        public void Dispose()
        {
            nint handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0)
                NativeMethods.CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint CreateJobObject(nint attributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AssignProcessToJobObject(nint job, nint process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint handle);
    }
}
