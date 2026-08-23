using EgressController.App.ViewModels;
using EgressController.Diagnostics;

namespace EgressController.Windows.IntegrationTests;

public sealed class DiagnosticsPresentationTests
{
    [Fact]
    public void Counter_addition_saturates_and_ignores_negative_api_values()
    {
        Assert.Equal(long.MaxValue, TrafficFormat.AddCounters(long.MaxValue, 1));
        Assert.Equal(12, TrafficFormat.AddCounters(-5, 12));
        Assert.Equal(0, TrafficFormat.AddCounters(-5, -12));
    }

    [Fact]
    public void Connection_row_does_not_wrap_large_traffic_or_rate_counters()
    {
        var row = new ConnectionRowViewModel(new ConnectionObservation
        {
            Id = "connection-1",
            Upload = long.MaxValue,
            Download = long.MaxValue,
            UploadRate = long.MaxValue,
            DownloadRate = long.MaxValue,
            StartedAtUtc = DateTimeOffset.UtcNow,
        }, active: true);

        Assert.Equal(TrafficFormat.Bytes(long.MaxValue), row.Traffic);
        Assert.Equal(TrafficFormat.Rate(long.MaxValue), row.Speed);
    }

    [Fact]
    public void Last_updated_text_reports_api_observation_time_instead_of_ui_timer_time()
    {
        Assert.Equal("等待 sing-box API", TrafficFormat.UpdatedAt(null));
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        Assert.Equal(
            observedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
            TrafficFormat.UpdatedAt(observedAtUtc));
    }
}
