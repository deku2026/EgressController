using EgressController.State.Quota;

namespace EgressController.State.Tests;

public sealed class EgressQuotaStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "EgressController.QuotaTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Configure_persists_baseline_and_resets_local_usage()
    {
        var store = new EgressQuotaStore(_directory);
        EgressQuotaSnapshot configured = store.Configure(1000, 750);
        Assert.Equal(1000, configured.TotalBytes);
        Assert.Equal(750, configured.RemainingBytes);
        Assert.Equal(0, configured.UsedBytes);

        Assert.Equal(configured, new EgressQuotaStore(_directory).Load());
    }

    [Fact]
    public void Usage_is_durable_and_remaining_never_goes_negative()
    {
        var store = new EgressQuotaStore(_directory);
        store.Configure(1000, 750);

        Assert.Equal(200, store.AddUsage(200).UsedBytes);
        EgressQuotaSnapshot exhausted = store.AddUsage(900);
        Assert.Equal(1000, exhausted.UsedBytes);
        Assert.Equal(0, exhausted.RemainingBytes);
        Assert.Equal(0, exhausted.RemainingPercent);
    }

    [Fact]
    public void Clear_usage_keeps_the_entered_package_baseline()
    {
        var store = new EgressQuotaStore(_directory);
        store.Configure(1024, 512);
        store.AddUsage(100);

        EgressQuotaSnapshot cleared = store.ClearUsage();
        Assert.Equal(1024, cleared.TotalBytes);
        Assert.Equal(512, cleared.StartingRemainingBytes);
        Assert.Equal(0, cleared.UsedBytes);
        Assert.Equal(512, cleared.RemainingBytes);
    }

    [Fact]
    public void Invalid_baselines_are_rejected()
    {
        var store = new EgressQuotaStore(_directory);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Configure(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Configure(1, 2));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
