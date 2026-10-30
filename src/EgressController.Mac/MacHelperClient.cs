// SPDX-License-Identifier: MIT
using System.Net.Sockets;

namespace EgressController.Mac;

public interface IMacHelperConnection : IAsyncDisposable
{
    Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken cancellationToken);
}

public sealed class MacHelperClient : IAsyncDisposable
{
    private readonly IMacHelperConnection _connection;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Guid _session = Guid.NewGuid();
    private readonly TimeSpan _heartbeatInterval;
    private long _sequence;
    private Task? _heartbeat;
    private int _disposed;
    private MacHelperStatus _status = new(false, false, false, false, 0, null, "尚未连接特权服务。", DateTimeOffset.MinValue);
    public MacHelperStatus Status => Volatile.Read(ref _status);
    public MacHelperClient(LiveOperationGate gate, MacHelperEndpoint? endpoint = null)
        : this(new UnixMacHelperConnection(gate, endpoint ?? MacHelperEndpoint.Installed)) { }
    /// <summary>Injected connections are reserved for isolated tests; no native adapters are created.</summary>
    public MacHelperClient(IMacHelperConnection connection, TimeSpan? heartbeatInterval = null)
    {
        _connection = connection; _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(2);
        if (_heartbeatInterval < TimeSpan.FromMilliseconds(10) || _heartbeatInterval > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
    }
    public async Task<MacHelperStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        try { return (await SendAsync(MacHelperCommand.Inspect, null, cancellationToken).ConfigureAwait(false)).Status; }
        catch (Exception error) when (error is IOException or SocketException) { return Status; }
    }
    public Task<MacHelperWireReply> ValidateAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken = default) => SendAsync(MacHelperCommand.Validate, plan, cancellationToken);
    public async Task<MacHelperWireReply> ApplyAsync(MacHelperApplyPlan plan, CancellationToken cancellationToken = default)
    {
        var reply = await SendAsync(MacHelperCommand.Apply, plan, cancellationToken).ConfigureAwait(false);
        if (reply.Succeeded && (_heartbeat is null || _heartbeat.IsCompleted)) _heartbeat = HeartbeatLoopAsync();
        return reply;
    }
    public Task<MacHelperWireReply> StopAndRestoreAsync(CancellationToken cancellationToken = default) => SendAsync(MacHelperCommand.StopAndRestore, null, cancellationToken);
    private async Task<MacHelperWireReply> SendAsync(MacHelperCommand command, MacHelperApplyPlan? plan, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        bool locked = false;
        try
        {
            await _requests.WaitAsync(timeout.Token).ConfigureAwait(false); locked = true;
            var request = new MacHelperWireRequest(2, _session, ++_sequence, command, plan);
            var reply = await _connection.ExchangeAsync(request, timeout.Token).ConfigureAwait(false);
            if (reply is null || reply.Status is null || reply.SessionId != request.SessionId || reply.Sequence != request.Sequence
                || reply.Status.ObservedAt < DateTimeOffset.UtcNow.AddSeconds(-15) || reply.Status.ObservedAt > DateTimeOffset.UtcNow.AddSeconds(5))
                throw new IOException("Helper reply identity or freshness was not valid.");
            Volatile.Write(ref _status, reply.Status);
            return reply;
        }
        catch
        {
            Volatile.Write(ref _status, new MacHelperStatus(false, false, false, false, 0, null, "特权服务状态读取失败，当前接管未获确认。", DateTimeOffset.UtcNow));
            if (locked) { try { await _connection.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
            throw;
        }
        finally { if (locked) _requests.Release(); }
    }
    private async Task HeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval);
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                try { await SendAsync(MacHelperCommand.Heartbeat, null, _lifetime.Token).ConfigureAwait(false); }
                catch (Exception) when (!_lifetime.IsCancellationRequested) { }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); await _connection.DisposeAsync().ConfigureAwait(false);
        if (_heartbeat is not null) { try { await _heartbeat.ConfigureAwait(false); } catch (Exception) { } }
        _lifetime.Dispose();
    }
}

internal sealed class UnixMacHelperConnection(LiveOperationGate gate, MacHelperEndpoint endpoint) : IMacHelperConnection
{
    private Socket? _socket;
    private NetworkStream? _stream;
    public async Task<MacHelperWireReply> ExchangeAsync(MacHelperWireRequest request, CancellationToken cancellationToken)
    {
        gate.Require("communicate with installed privileged helper");
        try
        {
            if (_stream is null)
            {
                var native = new NativeMacPrivileges(gate);
                var parent = native.ReadFileInfo(Path.GetDirectoryName(endpoint.SocketPath)!);
                var socketFile = native.ReadFileInfo(endpoint.SocketPath);
                var helper = native.ReadFileInfo(endpoint.ExpectedHelperPath);
                if (!parent.IsDirectory || parent.IsSymbolicLink || parent.UserId != 0 || (parent.Mode & 0x12) != 0
                    || !socketFile.IsSocket || socketFile.IsSymbolicLink || socketFile.UserId != native.GetUserId() || (socketFile.Mode & 0x3f) != 0
                    || !helper.IsRegular || helper.IsSymbolicLink || helper.UserId != 0 || (helper.Mode & 0x12) != 0)
                    throw new UnauthorizedAccessException("The helper endpoint ownership was not trusted.");
                _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await _socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.SocketPath), cancellationToken).ConfigureAwait(false);
                var peer = native.ReadPeer(_socket.SafeHandle.DangerousGetHandle().ToInt32());
                if (peer.UserId != 0 || peer.ExecutablePath != endpoint.ExpectedHelperPath) throw new UnauthorizedAccessException("The endpoint is not the installed root helper.");
                native.VerifyProcessCode(peer.ProcessId, endpoint.ExpectedHelperPath);
                _stream = new NetworkStream(_socket, ownsSocket: false);
            }
            await MacHelperWireCodec.WriteRequestAsync(_stream, request, cancellationToken).ConfigureAwait(false);
            return await MacHelperWireCodec.ReadReplyAsync(_stream, cancellationToken).ConfigureAwait(false);
        }
        catch { await DisposeAsync().ConfigureAwait(false); throw; }
    }
    public ValueTask DisposeAsync() { _stream?.Dispose(); _socket?.Dispose(); _stream = null; _socket = null; return ValueTask.CompletedTask; }
}
