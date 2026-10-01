using EgressController.SingBox.Configuration;

namespace EgressController.SingBox.Tests;

public sealed class DohSelectionControllerTests
{
    [Fact]
    public async Task Unchanged_or_failed_checks_never_apply_configuration_and_switches_apply_once()
    {
        var controller = new DohSelectionController();
        int applied = 0;
        Task<string?> Apply(DohRoutingDecision _, CancellationToken __) { applied++; return Task.FromResult<string?>(null); }
        async Task Check(bool cf, bool pod)
        {
            var selected = EgressDohConfiguration.Decide([new(EgressDohConfiguration.CloudflareTag, cf),
                new(EgressDohConfiguration.DnsPodTag, pod)], true, controller.Current);
            Assert.Null(await controller.ApplyAsync(controller.Generation, selected, Apply, TestContext.Current.CancellationToken));
        }
        await Check(false, false); // No previous health result is required to keep the initial TUN.
        await Check(true, true);
        Assert.Equal(0, applied);
        await Check(false, true);
        Assert.Equal(EgressDohConfiguration.DnsPodTag, controller.Current.DnsTag);
        Assert.Equal(1, applied);
        await Check(false, false);
        await Check(false, true);
        Assert.Equal(EgressDohConfiguration.DnsPodTag, controller.Current.DnsTag);
        Assert.Equal(1, applied);
        await Check(true, true);
        Assert.Equal(2, applied);
        Assert.Equal(EgressDohConfiguration.CloudflareTag, controller.Current.DnsTag);
        Assert.Equal(TimeSpan.FromMinutes(1), EgressDohConfiguration.CheckInterval);
    }

    [Fact]
    public async Task Failed_apply_keeps_the_previous_selection_and_can_retry_next_round()
    {
        var controller = new DohSelectionController();
        var backup = new DohRoutingDecision { DnsTag = EgressDohConfiguration.DnsPodTag };
        Assert.Equal("validation failed", await controller.ApplyAsync(controller.Generation, backup,
            (_, _) => Task.FromResult<string?>("validation failed"), TestContext.Current.CancellationToken));
        Assert.Equal(DohRoutingDecision.Default, controller.Current);
        Assert.Null(await controller.ApplyAsync(controller.Generation, backup,
            (_, _) => Task.FromResult<string?>(null), TestContext.Current.CancellationToken));
        Assert.Equal(backup, controller.Current);
    }

    [Fact]
    public async Task Results_from_before_a_configuration_change_do_not_restart_or_replace_dns()
    {
        var controller = new DohSelectionController();
        int stale = controller.Generation;
        controller.Invalidate();
        int applied = 0;
        var backup = new DohRoutingDecision { DnsTag = EgressDohConfiguration.DnsPodTag };
        Assert.NotNull(await controller.ApplyAsync(stale, backup, (_, _) =>
        { applied++; return Task.FromResult<string?>(null); }, TestContext.Current.CancellationToken));
        Assert.Equal(0, applied);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var change = controller.ApplyAsync(controller.Generation, backup, (_, _) => pending.Task, TestContext.Current.CancellationToken);
        controller.Invalidate();
        pending.SetResult(null);
        Assert.NotNull(await change);
        Assert.Equal(DohRoutingDecision.Default, controller.Current);
    }

    [Fact]
    public async Task Canceled_check_does_not_touch_the_runtime()
    {
        var controller = new DohSelectionController();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.ApplyAsync(controller.Generation,
            new() { DnsTag = EgressDohConfiguration.DnsPodTag }, (_, _) => throw new InvalidOperationException("must not apply"), cts.Token));
        Assert.Equal(DohRoutingDecision.Default, controller.Current);
    }
}
