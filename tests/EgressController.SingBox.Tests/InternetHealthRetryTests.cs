using EgressController.Core.Protection;
using EgressController.SingBox.Api.Models;
using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class InternetHealthRetryTests
{
    [Theory]
    [InlineData(NetworkHealthCheck.DirectInternet, 1)]
    [InlineData(NetworkHealthCheck.DirectInternet, 2)]
    [InlineData(NetworkHealthCheck.DirectInternet, 3)]
    [InlineData(NetworkHealthCheck.DirectInternet, 0)]
    [InlineData(NetworkHealthCheck.ProxyInternet, 1)]
    [InlineData(NetworkHealthCheck.ProxyInternet, 2)]
    [InlineData(NetworkHealthCheck.ProxyInternet, 3)]
    [InlineData(NetworkHealthCheck.ProxyInternet, 0)]
    public async Task Success_stops_retries_and_failure_requires_three_attempts_on_the_same_exit(NetworkHealthCheck check, int successAttempt)
    {
        var paths = new List<string>();
        var waits = new List<TimeSpan>();
        var progress = new List<string>();
        var result = await InternetHealthProbe.RunWithRetriesAsync(check, 1080, (outbound, _) =>
        {
            paths.Add(outbound);
            return Task.FromResult(new SingBoxDelayResponse { Delay = paths.Count == successAttempt ? 12 : -1 });
        }, TestContext.Current.CancellationToken, progress.Add, (interval, _) =>
        {
            waits.Add(interval);
            return Task.CompletedTask;
        });
        int attempts = successAttempt == 0 ? 3 : successAttempt;
        Assert.Equal(attempts, paths.Count);
        Assert.All(paths, path => Assert.Equal(check == NetworkHealthCheck.DirectInternet ? "dns-direct" : "upstream-1080", path));
        Assert.Equal(attempts - 1, waits.Count);
        Assert.All(waits, interval => Assert.Equal(TimeSpan.FromSeconds(5), interval));
        Assert.Equal(successAttempt > 0, result.IsHealthy);
        Assert.Contains(successAttempt > 0 ? "12 ms" : "连续 3 次", result.Detail);
        Assert.Contains($"第 {attempts}/3", progress[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exceptions_and_ignored_timeout_cancellation_also_require_three_failures(bool hang)
    {
        int attempts = 0, waits = 0;
        var never = new TaskCompletionSource<SingBoxDelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await InternetHealthProbe.RunWithRetriesAsync(NetworkHealthCheck.ProxyInternet, 7891, (_, _) =>
        {
            attempts++;
            return hang ? never.Task : Task.FromException<SingBoxDelayResponse>(new IOException("mock transport failure"));
        }, TestContext.Current.CancellationToken, delay: (_, _) => { waits++; return Task.CompletedTask; },
            timeout: TimeSpan.FromMilliseconds(20));
        Assert.False(result.IsHealthy);
        Assert.Equal(3, attempts);
        Assert.Equal(2, waits);
        Assert.Contains("连续 3 次", result.Detail);
        Assert.Contains(hang ? "超时" : "mock transport failure", result.Detail);
    }

    [Fact]
    public async Task Cancellation_during_the_five_second_wait_does_not_emit_failure_or_probe_again()
    {
        using var canceled = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var pending = InternetHealthProbe.RunWithRetriesAsync(NetworkHealthCheck.DirectInternet, 1080, (_, _) =>
        {
            attempts++;
            return Task.FromResult(new SingBoxDelayResponse { Delay = -1 });
        }, canceled.Token, delay: (_, token) =>
        {
            waiting.SetResult();
            return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        });
        await waiting.Task.WaitAsync(TestContext.Current.CancellationToken);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Success_resets_the_failure_sequence_for_the_next_round()
    {
        int count = 0;
        foreach (int successfulAttempt in new[] { 2, 3 })
        {
            int roundAttempts = 0;
            var result = await InternetHealthProbe.RunWithRetriesAsync(NetworkHealthCheck.DirectInternet, null, (_, _) =>
            {
                count++;
                return Task.FromResult(new SingBoxDelayResponse { Delay = ++roundAttempts == successfulAttempt ? 1 : -1 });
            }, TestContext.Current.CancellationToken, delay: (_, _) => Task.CompletedTask);
            Assert.True(result.IsHealthy);
            Assert.Equal(successfulAttempt, roundAttempts);
        }
        Assert.Equal(5, count);
    }
}
