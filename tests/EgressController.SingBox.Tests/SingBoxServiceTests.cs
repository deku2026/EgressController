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
            (_, _) => Task.FromResult(Interlocked.Increment(ref healthCalls) == 1));
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

    private sealed class FakeHost : IElevatedHostClient
    {
        public List<(SingBoxRuntimeCandidate Candidate, bool Restart)> Started { get; } = new();
        public int StopCount { get; private set; }
        event Action<SingBoxOutputEvent>? IElevatedHostClient.Output
        {
            add { }
            remove { }
        }

        public Task<ElevatedHostClientStatus> StartAsync(
            SingBoxRuntimeCandidate candidate,
            bool restart,
            CancellationToken cancellationToken = default)
        {
            Started.Add((candidate, restart));
            return Task.FromResult(new ElevatedHostClientStatus(true, "running", 1234, 0, null, null));
        }

        public Task<ElevatedHostClientStatus> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(new ElevatedHostClientStatus(true, "stopped", null, 0, null, null));
        }

        public Task<ElevatedHostClientStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ElevatedHostClientStatus(true, "stopped", null, 0, null, null));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
