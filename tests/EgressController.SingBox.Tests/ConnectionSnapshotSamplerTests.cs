using EgressController.SingBox.Api;
using EgressController.SingBox.Api.Models;

namespace EgressController.SingBox.Tests;

public sealed class ConnectionSnapshotSamplerTests
{
    [Fact]
    public async Task Failed_display_sample_preserves_history_and_next_request_recovers_without_a_stream()
    {
        var clock = new Clock();
        int calls = 0;
        var sampler = new ConnectionSnapshotSampler(_ => ++calls == 2
            ? Task.FromException<SingBoxConnectionsResponse>(new IOException("mock transport closed"))
            : Task.FromResult(new SingBoxConnectionsResponse { UploadTotal = calls * 100, DownloadTotal = calls * 1000 }), clock);
        Assert.NotNull((await sampler.SampleAsync(TestContext.Current.CancellationToken)).Snapshot);
        clock.Advance();
        var failure = await sampler.SampleAsync(TestContext.Current.CancellationToken);
        Assert.Null(failure.Snapshot); Assert.IsType<IOException>(failure.Error);
        Assert.Equal(0, failure.UploadRate);
        clock.Advance();
        var recovery = await sampler.SampleAsync(TestContext.Current.CancellationToken);
        Assert.Null(recovery.Error); Assert.Equal(300, recovery.Snapshot!.UploadTotal);
        Assert.Equal(0, recovery.UploadRate); // Never manufacture a spike over missing samples.
        clock.Advance();
        var next = await sampler.SampleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100, next.UploadRate); Assert.Equal(1000, next.DownloadRate);
    }

    [Fact]
    public async Task Replaced_core_counters_reset_without_negative_rates()
    {
        var clock = new Clock(); long counter = 1000;
        var sampler = new ConnectionSnapshotSampler(_ => Task.FromResult(new SingBoxConnectionsResponse { UploadTotal = counter }), clock);
        await sampler.SampleAsync(TestContext.Current.CancellationToken); clock.Advance(); counter = 2;
        Assert.Equal(0, (await sampler.SampleAsync(TestContext.Current.CancellationToken)).UploadRate);
        clock.Advance(); counter = 102;
        Assert.Equal(100, (await sampler.SampleAsync(TestContext.Current.CancellationToken)).UploadRate);
    }

    [Fact]
    public async Task Cancellation_stops_sampling_and_timeout_remains_a_display_error()
    {
        var sampler = new ConnectionSnapshotSampler(_ => Task.FromException<SingBoxConnectionsResponse>(new TaskCanceledException("mock HTTP timeout")));
        Assert.IsType<TaskCanceledException>((await sampler.SampleAsync(TestContext.Current.CancellationToken)).Error);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.SampleAsync(cancellation.Token));
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += 1000;
    }
}
