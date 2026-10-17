namespace EgressController.Guardian;

/// <summary>Dedicated TUN heartbeat loop, never scheduled behind Internet probes.</summary>
public sealed class MainGuardAgent : IAsyncDisposable
{
    private readonly Func<GuardPolicy> _policy;
    private readonly Func<GuardPolicy, GuardHeartbeat> _heartbeat;
    private readonly IMainGuardClient _client;
    private readonly IMainGuardEnvironment _environment;
    private readonly Lazy<Task> _initialization;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private string? _savedRevision;
    public string Status { get; private set; } = "正在初始化独立守护";
    public MainGuardAgent(GuardPaths paths, Func<GuardPolicy> policy, Func<GuardPolicy, GuardHeartbeat> heartbeat)
        : this(new WindowsMainGuardEnvironment(paths), new GuardPipeClient(paths, main: true), policy, heartbeat) { }
    public MainGuardAgent(IMainGuardEnvironment environment, IMainGuardClient client,
        Func<GuardPolicy> policy, Func<GuardPolicy, GuardHeartbeat> heartbeat)
    {
        _environment = environment; _client = client; _policy = policy; _heartbeat = heartbeat;
        _initialization = new(() => Task.Run(() =>
        {
            try { _environment.Initialize(_policy()); }
            catch (Exception error) { Status = "独立守护初始化失败：" + error.Message; }
        }));
    }
    public void Start() => _loop ??= Task.Run(RunAsync);
    public async Task PollAsync(CancellationToken token = default)
    {
        await _initialization.Value.WaitAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var policy = SaveDesired();
            if (_environment.UserDisabled)
            {
                Status = "独立守护已手动停止/卸载";
                await _client.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await _client.HeartbeatAsync(policy, _heartbeat(policy), token).ConfigureAwait(false);
                Status = "独立守护已连接";
            }
        }
        finally { _gate.Release(); }
    }
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await PollAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception exception) { Status = "独立守护连接失败：" + exception.Message; }
            try { await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
        }
    }
    private GuardPolicy SaveDesired()
    {
        var policy = _policy();
        if (_savedRevision != policy.Revision)
        {
            _environment.SaveDesired(policy);
            _savedRevision = policy.Revision;
        }
        return policy;
    }
    public async Task SyncAsync(CancellationToken token = default)
    {
        await _initialization.Value.WaitAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var policy = SaveDesired();
            if (_environment.UserDisabled) return;
            await _client.SyncAsync(policy, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task PrepareAsync(CancellationToken token = default)
    {
        await _initialization.Value.WaitAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_environment.UserDisabled) return;
            var policy = SaveDesired();
            var reply = await _client.PrepareAsync(policy, token).ConfigureAwait(false);
            if (!reply.Success || reply.Status is null || !reply.Status.Protecting || reply.Status.FailedCount > 0
                || reply.Status.PolicyRevision != policy.Revision)
                throw new InvalidOperationException(reply.Status?.Reason ?? "守护未确认接管，取消停止/重启 TUN。");
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
