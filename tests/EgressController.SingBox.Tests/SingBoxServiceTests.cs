using EgressController.SingBox.Runtime;
using EgressController.State.SingBox;

namespace EgressController.SingBox.Tests;

public sealed class SingBoxServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EgressController.SingBoxServiceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Successful_apply_persists_last_good_and_clears_pending()
    {
        var host = new FakeHost();
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root));

        SingBoxApplyResult result = await service.ApplyCandidateAsync(Candidate("one"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(result.RestoredLastGood);
        Assert.Null(new SingBoxStateStore(_root).LoadPendingApply());
        Assert.Equal("one", new SingBoxStateStore(_root).LoadLastGoodRuntime()!.ConfigSha256);
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
    }

    [Fact]
    public async Task Failed_apply_restores_last_good_without_losing_current_runtime()
    {
        int healthCalls = 0;
        var host = new FakeHost();
        await using var service = new SingBoxService(
            host,
            new SingBoxStateStore(_root),
            (_, _) => Task.FromResult(Interlocked.Increment(ref healthCalls) != 2));
        Assert.True((await service.ApplyCandidateAsync(Candidate("good"), TestContext.Current.CancellationToken)).Succeeded);

        SingBoxApplyResult failed = await service.ApplyCandidateAsync(Candidate("bad"), TestContext.Current.CancellationToken);

        Assert.False(failed.Succeeded);
        Assert.True(failed.RestoredLastGood);
        Assert.Equal(3, host.Started.Count);
        Assert.Equal("bad", host.Started[1].Candidate.ConfigSha256);
        Assert.Equal("good", host.Started[2].Candidate.ConfigSha256);
        Assert.Equal("good", new SingBoxStateStore(_root).LoadCurrentRuntime()!.ConfigSha256);
        Assert.Null(new SingBoxStateStore(_root).LoadPendingApply());
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
    }

    [Fact]
    public async Task Stop_cancels_preparation_before_waiting_for_lifecycle_lock()
    {
        var host = new FakeHost();
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<SingBoxApplyResult> apply = service.ApplyAsync(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Candidate("never");
        }, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await service.StopAsync(TestContext.Current.CancellationToken);
        SingBoxApplyResult result = await apply;

        Assert.False(result.Succeeded);
        Assert.Equal("operation.cancelled", result.ErrorCode);
        Assert.Equal(1, host.StopCount);
        Assert.Equal(SingBoxServiceState.Stopped, service.Status.State);
    }

    [Fact]
    public async Task Unexpected_core_exit_changes_running_service_to_failed()
    {
        var host = new FakeHost();
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root));
        Assert.True((await service.ApplyCandidateAsync(Candidate("running"), TestContext.Current.CancellationToken)).Succeeded);

        host.Emit(new SingBoxOutputEvent("lifecycle", "sing-box exited with code 1.", 0));

        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
        Assert.Equal("process.exited", service.Status.ErrorCode);
        Assert.Contains("exited", service.Status.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preparation_keeps_the_old_tun_and_guard_runs_before_each_core_restart()
    {
        var order = new List<string>();
        var host = new FakeHost { OnStart = () => order.Add("core restart") };
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root),
            (_, _) => { order.Add("takeover confirmed"); return Task.FromResult(true); },
            () => order.Add("terminate selected apps"));
        Assert.True((await service.ApplyCandidateAsync(Candidate("old"), TestContext.Current.CancellationToken)).Succeeded);
        order.Clear();
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validated = new TaskCompletionSource<SingBoxRuntimeCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var change = service.ApplyAsync(_ => { preparing.SetResult(); return validated.Task; }, TestContext.Current.CancellationToken);
        await preparing.Task;
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
        Assert.Empty(order);
        validated.SetResult(Candidate("new"));
        Assert.True((await change).Succeeded);
        Assert.Equal(new[] { "terminate selected apps", "core restart", "takeover confirmed" }, order);
    }

    [Fact]
    public async Task Invalid_candidate_does_not_kill_or_restart_a_running_tun()
    {
        var host = new FakeHost();
        int guards = 0;
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root), beforeTunChange: () => guards++);
        await service.ApplyCandidateAsync(Candidate("good"), TestContext.Current.CancellationToken);
        var result = await service.ApplyAsync(_ => throw new IOException("bad config"), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
        Assert.Equal(1, guards);
        Assert.Single(host.Started);
    }

    [Fact]
    public async Task Guard_failure_cancels_restart_without_stopping_or_rolling_back_the_old_core()
    {
        var host = new FakeHost();
        bool fail = false;
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root),
            beforeTunChange: () => { if (fail) throw new IOException("termination denied"); });
        await service.ApplyCandidateAsync(Candidate("good"), TestContext.Current.CancellationToken);
        fail = true;
        var result = await service.ApplyCandidateAsync(Candidate("next"), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal("protection.failed", result.ErrorCode);
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
        Assert.Single(host.Started);
        Assert.Equal(0, host.StopCount);
        fail = false;
    }

    [Fact]
    public async Task Failed_rollback_takeover_is_not_reported_as_running()
    {
        var host = new FakeHost();
        bool healthy = true;
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root), (_, _) => Task.FromResult(healthy));
        await service.ApplyCandidateAsync(Candidate("good"), TestContext.Current.CancellationToken);
        healthy = false;
        var result = await service.ApplyCandidateAsync(Candidate("bad"), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.False(result.RestoredLastGood);
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
    }

    [Fact]
    public async Task Missing_takeover_routes_changes_running_service_to_failed()
    {
        var host = new FakeHost();
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root));
        await service.ApplyCandidateAsync(Candidate("good"), TestContext.Current.CancellationToken);
        service.ReportTakeoverFailure("TUN IPv6 接管路由缺失");
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
        Assert.Equal("tun.takeover", service.Status.ErrorCode);
    }

    [Fact]
    public async Task Observation_from_before_restart_cannot_fail_the_new_tun()
    {
        var host = new FakeHost();
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root));
        await service.ApplyCandidateAsync(Candidate("old"), TestContext.Current.CancellationToken);
        var observed = service.Status;
        await service.ApplyCandidateAsync(Candidate("new"), TestContext.Current.CancellationToken);
        service.ReportTakeoverFailure("route missing during restart", observed);
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
        service.ReportTakeoverFailure("route missing now", service.Status);
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
    }

    [Fact]
    public async Task Identical_checked_configuration_does_not_guard_or_restart_again()
    {
        var host = new FakeHost();
        int guards = 0;
        await using var service = new SingBoxService(host, new SingBoxStateStore(_root), beforeTunChange: () => guards++);
        var candidate = Candidate("same");
        await service.ApplyCandidateAsync(candidate, TestContext.Current.CancellationToken);
        Assert.True((await service.ApplyAsync(_ => Task.FromResult(candidate), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Single(host.Started);
        Assert.Equal(1, guards);
    }

    private static SingBoxRuntimeCandidate Candidate(string suffix)
        => new()
        {
            CoreVersion = "1.13.19",
            CorePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EgressController", "core", "sing-box.exe")),
            CoreSha256 = new('a', 64),
            ConfigPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EgressController", "config-" + suffix + ".json")),
            ConfigSha256 = suffix,
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeHost : ISingBoxProcessClient
    {
        public List<(SingBoxRuntimeCandidate Candidate, bool Restart)> Started { get; } = new();
        public int StopCount { get; private set; }
        public Action? OnStart { get; init; }
        public event Action<SingBoxOutputEvent>? Output;

        public void Emit(SingBoxOutputEvent output) => Output?.Invoke(output);

        public Task<SingBoxProcessStatus> StartAsync(
            SingBoxRuntimeCandidate candidate,
            bool restart,
            CancellationToken cancellationToken = default)
        {
            OnStart?.Invoke();
            Started.Add((candidate, restart));
            return Task.FromResult(new SingBoxProcessStatus(true, "running", 1234, 0, null, null));
        }

        public Task<SingBoxProcessStatus> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(new SingBoxProcessStatus(true, "stopped", null, 0, null, null));
        }

        public Task<SingBoxProcessStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SingBoxProcessStatus(true, "running", 1234, 0, null, null));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
