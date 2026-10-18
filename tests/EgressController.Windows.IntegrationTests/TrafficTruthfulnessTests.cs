using Avalonia;
using EgressController.App.Preview;
using EgressController.App.ViewModels;
using EgressController.Diagnostics;
using EgressController.Presentation;

namespace EgressController.Windows.IntegrationTests;

public sealed class TrafficTruthfulnessTests
{
    [Fact]
    public void First_valid_snapshot_is_current_while_rate_still_needs_a_second_sample()
    {
        var timeline = new TrafficTimeline(); timeline.Observe(null, null);
        Assert.True(timeline.Snapshot().ObservationFresh);
        Assert.False(timeline.Snapshot().HasCurrentRate);
        timeline.Unavailable(); Assert.False(timeline.Snapshot().ObservationFresh);
    }

    [Fact]
    public void Refreshes_never_invent_samples_and_elapsed_time_controls_position()
    {
        var clock = new Clock(); var timeline = new TrafficTimeline(clock);
        timeline.Observe(1024, 2048); clock.Advance(7); timeline.Observe(2048, 4096);
        for (int i = 0; i < 100; i++) Assert.Equal(2, timeline.Snapshot().Upload.Count);
        var plot = new Rect(0, 0, 600, 100); var snapshot = timeline.Snapshot();
        Assert.Equal(530, TrendChart.Project(snapshot.Upload[0], plot, snapshot.ScaleMaximum).X);
        Assert.Equal(600, TrendChart.Project(snapshot.Upload[1], plot, snapshot.ScaleMaximum).X);
    }

    [Fact]
    public void Unknown_stale_and_zero_are_distinct_and_failure_does_not_erase_history()
    {
        var clock = new Clock(); var timeline = new TrafficTimeline(clock);
        Assert.False(timeline.Snapshot().HasCurrentRate);
        timeline.Observe(100, 200); var observed = timeline.Snapshot().ObservedAtUtc;
        clock.Advance(1); timeline.Unavailable();
        var failed = timeline.Snapshot();
        Assert.Null(failed.UploadRate); Assert.Null(failed.Upload[1].Value);
        Assert.Equal(observed, failed.ObservedAtUtc); Assert.Equal(100, failed.Upload[0].Value);
        clock.Advance(1); timeline.Observe(0, 0);
        Assert.True(timeline.Snapshot().HasCurrentRate); Assert.Equal(0, timeline.Snapshot().UploadRate);
        clock.Advance(4); Assert.False(timeline.Snapshot().HasCurrentRate);
        Assert.Equal(3, timeline.Snapshot().Upload.Count);
        timeline.Reset(); Assert.Empty(timeline.Snapshot().Upload); Assert.Null(timeline.Snapshot().ObservedAtUtc);
    }

    [Fact]
    public void Window_uses_monotonic_time_even_if_wall_clock_changes()
    {
        var clock = new Clock(); var timeline = new TrafficTimeline(clock);
        timeline.Observe(10, 20); clock.WallOffset = TimeSpan.FromHours(-2); clock.Advance(61);
        Assert.Empty(timeline.Snapshot().Upload); Assert.False(timeline.Snapshot().HasCurrentRate);
    }

    [Fact]
    public void Small_upload_and_large_download_share_one_absolute_zero_based_scale()
    {
        var timeline = new TrafficTimeline(); timeline.Observe(1024, 1024 * 1024);
        var snapshot = timeline.Snapshot(); var plot = new Rect(0, 0, 100, 100);
        Assert.Equal(1024 * 1024, snapshot.ScaleMaximum);
        double smallHeight = 100 - TrendChart.Project(snapshot.Upload[0], plot, snapshot.ScaleMaximum).Y;
        double largeHeight = 100 - TrendChart.Project(snapshot.Download[0], plot, snapshot.ScaleMaximum).Y;
        Assert.Equal(1d / 1024, smallHeight / largeHeight, 8);
        Assert.Equal(100, TrendChart.Project(new(0, 0), plot, snapshot.ScaleMaximum).Y);
        Assert.Equal("1 MiB/s", TrafficUnits.Rate(1024 * 1024)); Assert.Equal("—", TrafficUnits.Rate(null));
    }

    [Fact]
    public void App_bars_use_all_observed_bytes_and_cannot_overflow_when_multiplied_by_100()
    {
        var backend = new PreviewController();
        backend.ConnectionHistory.ApplySnapshot(Enumerable.Range(1, 6).Select(i => new ConnectionObservation
        { Id = i.ToString(), ProcessPath = $@"C:\mock\app{i}.exe", Upload = long.MaxValue, Download = long.MaxValue }));
        var vm = new DashboardViewModel(backend); vm.Refresh();
        Assert.Equal(5, vm.Applications.Count);
        Assert.All(vm.Applications, item => Assert.Equal(100d / 6, item.Share, 8));
        Assert.True(vm.Applications.Sum(item => item.Share) < 100);
    }

    [Fact]
    public void Missing_speed_is_not_reported_as_zero_and_form_refresh_keeps_user_edits()
    {
        var backend = new PreviewController("telemetry");
        for (int i = 0; i < 60; i++) backend.AdvanceSample();
        var dashboard = new DashboardViewModel(backend); dashboard.Refresh();
        Assert.Equal("—", dashboard.UploadRate); Assert.True(backend.Protection.Ready);
        var quota = new TrafficViewModel(backend) { TotalInput = "", RemainingInput = "12.5" };
        quota.Refresh(); Assert.Equal("", quota.TotalInput); Assert.Equal("12.5", quota.RemainingInput);
        backend.ConfigureQuota(0, 0); quota.Refresh(); Assert.Equal("未设置", quota.UsedPercentText);
        var row = new ConnectionRowViewModel(new() { Id = "x", UploadRate = 200, HasRate = false }, true);
        Assert.Equal("—", row.Speed); row.SetRateFreshness(false); Assert.Equal("上次活动", row.Status);
    }

    private sealed class Clock : TimeProvider
    {
        private long _seconds;
        public TimeSpan WallOffset;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds) + WallOffset;
        public void Advance(int seconds) => _seconds += seconds;
    }
}
