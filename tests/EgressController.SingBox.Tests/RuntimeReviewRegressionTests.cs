using EgressController.SingBox.Runtime;
using EgressController.State.SingBox;

namespace EgressController.SingBox.Tests;

public sealed class RuntimeReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "egress-runtime-review-" + Guid.NewGuid().ToString("N"));
    private SingBoxStateStore Store => new(_root);

    [Fact]
    public async Task First_start_with_failed_takeover_stops_candidate_even_without_last_good()
    {
        var host = new Host();
        await using var service = new SingBoxService(host, Store, (_, _) => Task.FromResult(false));
        var result = await service.ApplyCandidateAsync(Candidate("bad"), TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.True(result.CoreChanged);
        Assert.False(host.Running);
        Assert.Equal(1, host.Stops);
        Assert.Null(Store.LoadLastGoodRuntime());
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
    }

    [Fact]
    public async Task Failed_candidate_and_failed_rollback_leave_no_live_core()
    {
        var host = new Host();
        bool healthy = true;
        await using var service = new SingBoxService(host, Store, (_, _) => Task.FromResult(healthy));
        await service.ApplyCandidateAsync(Candidate("good"), TestContext.Current.CancellationToken);
        healthy = false;
        var result = await service.ApplyCandidateAsync(Candidate("bad"), TestContext.Current.CancellationToken);
        Assert.False(result.RestoredLastGood);
        Assert.Equal(new[] { "good", "bad", "good" }, host.Starts);
        Assert.False(host.Running);
        Assert.Equal(1, host.Stops);
        Assert.NotNull(Store.LoadPendingApply());
    }

    [Fact]
    public async Task Cleanup_failure_remains_visible_and_never_reports_stopped()
    {
        var host = new Host { StopFails = true };
        await using var service = new SingBoxService(host, Store, (_, _) => Task.FromResult(false));
        var result = await service.ApplyCandidateAsync(Candidate("bad"), TestContext.Current.CancellationToken);
        Assert.Contains("清理未完成", result.ErrorMessage);
        Assert.Contains("mock stop denied", result.ErrorMessage);
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
        await Assert.ThrowsAsync<SingBoxServiceException>(() => service.StopAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
    }

    [Fact]
    public async Task Recover_pending_cleans_failed_restored_core_too()
    {
        var host = new Host();
        bool healthy = true;
        await using var service = new SingBoxService(host, Store, (_, _) => Task.FromResult(healthy));
        await service.ApplyCandidateAsync(Candidate("good"), TestContext.Current.CancellationToken);
        Store.SavePendingApply(new() { Candidate = Store.LoadLastGoodRuntime()!, CreatedAtUtc = DateTimeOffset.UtcNow });
        healthy = false;
        Assert.False(await service.RecoverPendingAsync(TestContext.Current.CancellationToken));
        Assert.False(host.Running);
        Assert.Equal(1, host.Stops);
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
    }

    [Fact]
    public async Task Preparing_and_rejecting_candidate_keeps_active_settings_health_and_core()
    {
        var host = new Host();
        int transitions = 0;
        bool health = true;
        string profile = "old";
        await using var service = new SingBoxService(host, Store, beforeTunChange: () => { transitions++; health = false; });
        await service.ApplyCandidateAsync(Candidate("old"), TestContext.Current.CancellationToken);
        health = true;
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validation = new TaskCompletionSource<SingBoxRuntimeCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SingBoxApplyResult> pending = PreparedRuntimeUpdate.ApplyAsync(service, _ =>
        {
            preparing.SetResult();
            return validation.Task;
        }, () => profile = "new", () => profile = "old", TestContext.Current.CancellationToken);
        await preparing.Task;
        Assert.True(health);
        Assert.Equal("old", profile);
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
        health = false; // a real probe failure during validation must not be overwritten
        validation.SetException(new IOException("mock invalid config"));
        var result = await pending;
        Assert.False(result.CoreChanged);
        Assert.Equal("old", profile);
        Assert.False(health);
        Assert.Equal(1, transitions);
        Assert.Single(host.Starts);
        Assert.Equal(0, host.Stops);
    }

    [Fact]
    public async Task Identical_prepared_config_commits_without_changing_tun_or_readiness()
    {
        var host = new Host();
        int transitions = 0;
        await using var service = new SingBoxService(host, Store, beforeTunChange: () => transitions++);
        await service.ApplyCandidateAsync(Candidate("same"), TestContext.Current.CancellationToken);
        bool committed = false;
        var result = await PreparedRuntimeUpdate.ApplyAsync(service, _ => Task.FromResult(Candidate("same")),
            () => committed = true, () => committed = false, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.False(result.CoreChanged);
        Assert.True(committed);
        Assert.Equal(1, transitions);
        Assert.Single(host.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_or_handoff_failure_rolls_back_profile_without_restarting(bool handoffFails)
    {
        var host = new Host();
        bool fail = false;
        string profile = "old";
        await using var service = new SingBoxService(host, Store, beforeTunChange: () =>
        {
            if (fail && handoffFails) throw new IOException("mock termination denied");
        });
        await service.ApplyCandidateAsync(Candidate("old"), TestContext.Current.CancellationToken);
        fail = true;
        var result = await PreparedRuntimeUpdate.ApplyAsync(service, _ => Task.FromResult(Candidate("new")),
            () => { profile = "new"; if (!handoffFails) throw new IOException("mock disk full"); },
            () => profile = "old", TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.False(result.CoreChanged);
        Assert.Equal("old", profile);
        Assert.Single(host.Starts);
        Assert.Equal(0, host.Stops);
        Assert.Equal(SingBoxServiceState.Running, service.Status.State);
        fail = false;
    }

    [Fact]
    public async Task Cancelled_preparation_never_commits_or_changes_current_core()
    {
        var host = new Host();
        await using var service = new SingBoxService(host, Store);
        await service.ApplyCandidateAsync(Candidate("old"), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        bool committed = false;
        var result = await PreparedRuntimeUpdate.ApplyAsync(service, _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(Candidate("new"));
        }, () => committed = true, () => committed = false, cancellation.Token);
        Assert.Equal("operation.cancelled", result.ErrorCode);
        Assert.False(committed);
        Assert.False(result.CoreChanged);
        Assert.Single(host.Starts);
    }


    [Fact]
    public async Task Transition_barrier_stays_set_through_health_check_and_releases_after_failure_cleanup()
    {
        var host = new Host();
        bool transitioning = false;
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var health = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new SingBoxService(host, Store, (_, _) =>
        {
            checking.SetResult();
            return health.Task;
        }, () => transitioning = true, () => transitioning = false);
        var apply = service.ApplyCandidateAsync(Candidate("new"), TestContext.Current.CancellationToken);
        await checking.Task;
        Assert.True(transitioning);
        Assert.True(host.Running);
        health.SetResult(false);
        Assert.False((await apply).Succeeded);
        Assert.False(transitioning);
        Assert.False(host.Running);
    }

    [Fact]
    public async Task Initial_protection_failure_sets_failed_state_and_survives_retry_until_success()
    {
        var host = new Host();
        bool blocked = true;
        await using var service = new SingBoxService(host, Store, beforeTunChange: () =>
        {
            if (blocked) throw new IOException("mock guard handoff denied");
        });
        var first = await service.StartAsync(_ => Task.FromResult(Candidate("first")), TestContext.Current.CancellationToken);
        Assert.False(first.Succeeded);
        Assert.Equal(SingBoxServiceState.Failed, service.Status.State);
        Assert.Equal("protection.failed", service.Status.ErrorCode);
        Assert.Empty(host.Starts);
        var ready = new TaskCompletionSource<SingBoxRuntimeCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = service.StartAsync(_ => ready.Task, TestContext.Current.CancellationToken);
        Assert.Equal(SingBoxServiceState.Preparing, service.Status.State);
        Assert.Contains("mock guard handoff denied", service.LastFailure!.ErrorMessage);
        blocked = false;
        ready.SetResult(Candidate("second"));
        Assert.True((await retry).Succeeded);
        Assert.Null(service.LastFailure);
    }

    [Fact]
    public async Task Detailed_failure_code_and_failed_rollback_are_both_preserved()
    {
        var host = new Host();
        bool fail = false;
        List<string> stages = [];
        await using var service = new SingBoxService(host, Store, (candidate, _) => !fail
            ? Task.FromResult(true) : Task.FromException<bool>(new SingBoxServiceException("tun.takeover",
                candidate.ConfigSha256 == "old" ? "mock rollback interface missing" : "mock IPv6 route conflict")));
        service.FailureReported += (stage, _) => stages.Add(stage);
        await service.ApplyCandidateAsync(Candidate("old"), TestContext.Current.CancellationToken);
        fail = true;
        var result = await service.ApplyCandidateAsync(Candidate("new"), TestContext.Current.CancellationToken);
        Assert.Contains("mock IPv6 route conflict", result.ErrorMessage);
        Assert.Contains("mock rollback interface missing", result.ErrorMessage);
        Assert.Equal("tun.takeover", service.Status.ErrorCode);
        Assert.Contains("apply", stages);
        Assert.Contains("rollback", stages);
        Assert.False(host.Running);
    }

    [Fact]
    public async Task Preparation_reports_full_exception_and_keeps_specific_error_code()
    {
        var host = new Host();
        var failure = new SingBoxServiceException("tun.conflict", "mock other tunnel enabled");
        await using var service = new SingBoxService(host, Store);
        Exception? observed = null;
        service.FailureReported += (_, error) => observed = error;
        var result = await service.StartAsync(_ => Task.FromException<SingBoxRuntimeCandidate>(failure), TestContext.Current.CancellationToken);
        Assert.Same(failure, observed);
        Assert.Equal("tun.conflict", result.ErrorCode);
        Assert.Equal("tun.conflict", service.Status.ErrorCode);
        Assert.Empty(host.Starts);
    }

    private static SingBoxRuntimeCandidate Candidate(string config) => new()
    {
        CoreVersion = "1.13.19", CorePath = @"C:\mock\sing-box.exe", CoreSha256 = new('a', 64),
        ConfigPath = @"C:\mock\" + config + ".json", ConfigSha256 = config
    };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class Host : ISingBoxProcessClient
    {
        public bool Running;
        public bool StopFails;
        public int Stops;
        public List<string> Starts = [];
        public event Action<SingBoxOutputEvent>? Output { add { } remove { } }
        public Task<SingBoxProcessStatus> StartAsync(SingBoxRuntimeCandidate candidate, bool restart, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts.Add(candidate.ConfigSha256); Running = true;
            return GetStatusAsync(cancellationToken);
        }
        public Task<SingBoxProcessStatus> StopAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            if (StopFails) return Task.FromResult(new SingBoxProcessStatus(false, "running", 123, 0, "denied", "mock stop denied"));
            Running = false; return GetStatusAsync(cancellationToken);
        }
        public Task<SingBoxProcessStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SingBoxProcessStatus(true, Running ? "running" : "stopped", Running ? 123 : null, 0, null, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
