using System.IO.Pipes;

namespace EgressController.Guardian;

public sealed class GuardPipeClient(GuardPaths paths, bool main = false) : IMainGuardClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private Guid _session;
    private long _sequence;
    private string? _revision;
    public Task<GuardReply> StatusAsync(CancellationToken token = default) => ExchangeAsync(new() { Kind = "status" }, null, token);
    public Task<GuardReply> ClearAsync(CancellationToken token = default) => ExchangeAsync(new() { Kind = "clear" }, null, token);
    public Task<GuardReply> SyncAsync(GuardPolicy policy, CancellationToken token = default)
        => ExchangeAsync(new() { Kind = "policy", Policy = policy }, policy, token);
    public Task<GuardReply> HeartbeatAsync(GuardPolicy policy, GuardHeartbeat heartbeat, CancellationToken token = default)
        => ExchangeAsync(new() { Kind = "heartbeat", Heartbeat = heartbeat }, policy, token);
    public Task<GuardReply> PrepareAsync(GuardPolicy policy, CancellationToken token = default)
        => ExchangeAsync(new() { Kind = "prepare" }, policy, token);

    private async Task<GuardReply> ExchangeAsync(GuardRequest request, GuardPolicy? policy, CancellationToken token)
    {
        EgressController.Core.Protection.RuntimeSafety.RequireLiveOperations();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (_pipe is null)
            {
                _pipe = await GuardPipeSecurity.ConnectAsync(paths, deadline.Token).ConfigureAwait(false);
                GuardPipeSecurity.VerifyServer(_pipe, paths);
                _session = Guid.NewGuid(); _sequence = 0; _revision = null;
                if (main)
                {
                    if (policy is null) throw new InvalidOperationException("主程序连接需要保护名单。");
                    await SendAsync(new() { Kind = "hello", Policy = policy, Session = _session }, deadline.Token).ConfigureAwait(false);
                    _revision = policy.Revision;
                }
            }
            if (main && policy is not null && policy.Revision != _revision)
            {
                await SendAsync(new() { Kind = "policy", Policy = policy, Session = _session, Sequence = ++_sequence }, deadline.Token).ConfigureAwait(false);
                _revision = policy.Revision;
            }
            return await SendAsync(request with { Session = _session, Sequence = ++_sequence }, deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            _pipe?.Dispose(); _pipe = null;
            throw;
        }
        finally { _gate.Release(); }
    }
    private async Task<GuardReply> SendAsync(GuardRequest request, CancellationToken token)
    {
        await GuardWire.WriteAsync(_pipe!, request, GuardJson.Default.GuardRequest, token).ConfigureAwait(false);
        var response = await GuardWire.ReadAsync(_pipe!, GuardJson.Default.GuardReply, token).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Error == "不支持的守护通信版本。"
                ? "已安装旧版守护组件。请退出旧托盘，卸载后重新安装守护服务，再重新打开主程序。保护名单会保留。"
                : response.Error);
        return response;
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _pipe?.Dispose(); _pipe = null; }
        finally { _gate.Release(); }
    }
}
