using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using EgressController.Core.Ipc;

namespace EgressController.ElevatedHost;

/// <summary>One-session Named Pipe server. It accepts only the fixed IPC protocol and one UI PID.</summary>
public sealed partial class ElevatedHostServer
{
    private readonly string _pipeName;
    private readonly int _clientProcessId;
    private readonly ElevatedHostPathPolicy _policy;
    private readonly ISingBoxProcessHost _processHost;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ElevatedHostServer(
        string pipeName,
        int clientProcessId,
        ElevatedHostPathPolicy policy,
        ISingBoxProcessHost processHost)
    {
        if (string.IsNullOrWhiteSpace(pipeName)
            || pipeName.Length > 200
            || pipeName.Contains(Path.DirectorySeparatorChar)
            || pipeName.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Invalid pipe name.", nameof(pipeName));
        if (clientProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientProcessId));
        _pipeName = pipeName;
        _clientProcessId = clientProcessId;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using NamedPipeServerStream pipe = CreatePipe(_pipeName);
        Task parentWatcher = WatchParentAsync(lifetime);
        _processHost.Output += OnOutput;
        try
        {
            await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
            if (!TryGetClientProcessId(pipe, out uint actualPid) || actualPid != (uint)_clientProcessId)
                throw new ElevatedHostValidationException("Named Pipe client PID 校验失败。", "pipe.client-pid");

            while (!lifetime.IsCancellationRequested)
            {
                ElevatedIpcMessage? message = await ElevatedIpcProtocol.ReadAsync(pipe, lifetime.Token).ConfigureAwait(false);
                if (message is null)
                    break;
                ElevatedIpcMessage response = await HandleAsync(message, lifetime).ConfigureAwait(false);
                await WritePipeAsync(pipe, response, lifetime.Token).ConfigureAwait(false);
                if (message.Kind == ElevatedIpcKind.Shutdown)
                {
                    lifetime.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            _processHost.Output -= OnOutput;
            try { await _processHost.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await parentWatcher.ConfigureAwait(false); } catch { }
        }

        async void OnOutput(SingBoxOutputLine line)
        {
            if (!pipe.IsConnected || lifetime.IsCancellationRequested)
                return;
            var message = new ElevatedIpcMessage
            {
                Version = ElevatedIpcProtocol.CurrentVersion,
                Kind = ElevatedIpcKind.OutputEvent,
                RequestId = Guid.NewGuid().ToString("N"),
                ClientProcessId = _clientProcessId,
                ProcessId = _processHost.Status.ProcessId,
                OutputSource = line.Source,
                OutputLine = line.Line,
                DroppedOutputCount = line.DroppedOutputCount,
            };
            try { await WritePipeAsync(pipe, message, lifetime.Token).ConfigureAwait(false); } catch { }
        }
    }

    private async Task<ElevatedIpcMessage> HandleAsync(
        ElevatedIpcMessage message,
        CancellationTokenSource lifetime)
    {
        if (message.ClientProcessId != _clientProcessId)
            return message.AsResponse(false, "pipe.client-pid", "IPC client PID 不匹配。");

        try
        {
            return message.Kind switch
            {
                ElevatedIpcKind.Hello => message.AsResponse(true) with { State = _processHost.Status.State },
                ElevatedIpcKind.Start or ElevatedIpcKind.Restart
                    => await StartAsync(message).ConfigureAwait(false),
                ElevatedIpcKind.Stop
                    => (await _processHost.StopAsync(lifetime.Token).ConfigureAwait(false)).ToResponse(message),
                ElevatedIpcKind.GetStatus
                    => _processHost.Status.ToResponse(message),
                ElevatedIpcKind.Shutdown
                    => await ShutdownAsync(message, lifetime).ConfigureAwait(false),
                _ => message.AsResponse(false, "command.unknown", "未知 IPC 命令。"),
            };
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return message.AsResponse(false, "host.cancelled", "ElevatedHost 正在退出。");
        }
        catch (ElevatedHostValidationException ex)
        {
            return message.AsResponse(false, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return message.AsResponse(false, "host.command", ex.Message);
        }
    }

    private async Task<ElevatedIpcMessage> StartAsync(ElevatedIpcMessage message)
    {
        ElevatedHostPathPolicy policy = _policy;
        SingBoxProcessHost.ValidateStartRequest(policy, message);
        SingBoxHostStatus status = await _processHost.StartAsync(message).ConfigureAwait(false);
        return status.ToResponse(message);
    }

    private static Task<ElevatedIpcMessage> ShutdownAsync(
        ElevatedIpcMessage message,
        CancellationTokenSource lifetime)
    {
        return Task.FromResult(message.AsResponse(true));
    }

    private async Task WritePipeAsync(
        Stream pipe,
        ElevatedIpcMessage message,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ElevatedIpcProtocol.WriteAsync(pipe, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WatchParentAsync(CancellationTokenSource lifetime)
    {
        try
        {
            using System.Diagnostics.Process parent = System.Diagnostics.Process.GetProcessById(_clientProcessId);
            await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Cancel();
        }
        catch (ArgumentException)
        {
            lifetime.Cancel();
        }
        catch (InvalidOperationException)
        {
            lifetime.Cancel();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();
        WindowsIdentity? identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                identity.User,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint processId)
        => NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out processId);

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
    }
}

internal static class SingBoxHostStatusExtensions
{
    public static ElevatedIpcMessage ToResponse(this SingBoxHostStatus status, ElevatedIpcMessage request)
        => request.AsResponse(
            status.State is "running" or "stopped",
            status.ErrorCode,
            status.ErrorMessage) with
        {
            State = status.State,
            ProcessId = status.ProcessId,
            DroppedOutputCount = status.DroppedOutputCount,
        };
}
