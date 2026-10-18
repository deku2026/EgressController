using EgressController.State.Traffic;
using Microsoft.Data.Sqlite;

namespace EgressController.State.Tests;

public sealed class TrafficUsageStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EgressController.TrafficTests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private TrafficUsageStore Store() => new(_directory, TimeZoneInfo.Utc);
    private static TrafficCounter Counter(long up, long down, TrafficExit exit = TrafficExit.Home, string id = "one", DateTimeOffset? start = null)
        => new(id, start ?? Now.AddMinutes(-1), @"C:\Apps\Claude.exe", exit, up, down);

    [Fact]
    public void Restart_and_repeated_snapshots_do_not_double_count_and_closed_apps_keep_their_usage()
    {
        Store().Record([Counter(100, 200)], Now);
        var reopened = Store();
        reopened.Record([Counter(100, 200)], Now.AddSeconds(1));
        reopened.Record([], Now.AddSeconds(2));
        Assert.Equal(300, Assert.Single(reopened.Read(Now.AddSeconds(2)).Entries).Total);
        reopened.Record([Counter(150, 250)], Now.AddSeconds(3));
        Assert.Equal(400, Assert.Single(Store().Read(Now.AddSeconds(4)).Entries).Total);
    }

    [Fact]
    public void Exit_changes_classify_only_the_new_bytes_and_do_not_relabel_history()
    {
        var store = Store();
        store.Record([Counter(100, 200)], Now);
        store.Record([Counter(150, 250)], Now.AddSeconds(1));
        store.Record([Counter(190, 310, TrafficExit.Other)], Now.AddSeconds(2));
        store.Record([Counter(200, 330, TrafficExit.Unknown)], Now.AddSeconds(3));
        var rows = store.Read(Now.AddSeconds(4)).Entries;
        Assert.Equal(400, rows.Single(row => row.Exit == TrafficExit.Home).Total);
        Assert.Equal(100, rows.Single(row => row.Exit == TrafficExit.Other).Total);
        Assert.Equal(30, rows.Single(row => row.Exit == TrafficExit.Unknown).Total);
    }

    [Fact]
    public void Counter_resets_and_reused_ids_with_new_start_time_are_accounted_separately()
    {
        var store = Store(); store.Record([Counter(100, 200)], Now);
        store.Record([Counter(5, 10)], Now.AddSeconds(1));
        store.Record([Counter(50, 70, start: Now)], Now.AddSeconds(2));
        Assert.Equal(435, Assert.Single(store.Read(Now).Entries).Total);
    }

    [Fact]
    public void Out_of_order_duplicate_and_invalid_counters_do_not_change_the_saved_total()
    {
        var store = Store(); store.Record([Counter(100, 200), Counter(100, 200)], Now);
        store.Record([Counter(999, 999)], Now.AddSeconds(-1));
        store.Record([Counter(-1, 400)], Now.AddSeconds(1));
        Assert.Equal(300, Assert.Single(store.Read(Now).Entries).Total);
    }

    [Fact]
    public void Window_contains_thirty_calendar_days_and_midnight_adds_only_the_delta()
    {
        var store = Store();
        store.Record([Counter(10, 20, id: "expired")], Now.AddDays(-30));
        store.Record([Counter(30, 40, id: "edge")], Now.AddDays(-29));
        store.Record([Counter(50, 60)], Now);
        var snapshot = store.Read(Now);
        Assert.Equal(DateOnly.FromDateTime(Now.Date).AddDays(-29), snapshot.From);
        Assert.Equal(180, snapshot.Entries.Sum(row => row.Total));
        store.Record([Counter(70, 80)], Now.AddDays(1));
        var tomorrow = store.Read(Now.AddDays(1));
        Assert.Equal(150, tomorrow.Entries.Sum(row => row.Total));
        Assert.Equal(40, tomorrow.Entries.Single(row => row.Day == tomorrow.Through).Total);
    }

    [Fact]
    public void Calendar_boundary_uses_the_selected_local_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test", "test");
        var store = new TrafficUsageStore(_directory, zone);
        store.Record([Counter(1, 2)], Now.AddHours(6));
        Assert.Equal(new DateOnly(2026, 10, 19), Assert.Single(store.Read(Now.AddHours(6)).Entries).Day);
    }

    [Fact]
    public void Application_paths_merge_case_insensitively_without_losing_the_display_path()
    {
        var store = Store(); store.Record([Counter(1, 2)], Now);
        store.Record([Counter(3, 4, id: "second") with { ProcessPath = @"c:\apps\CLAUDE.exe" }], Now.AddSeconds(1));
        var row = Assert.Single(store.Read(Now).Entries);
        Assert.Equal(10, row.Total); Assert.Equal(@"C:\Apps\Claude.exe", row.ProcessPath);
    }

    [Fact]
    public void Failed_transaction_rolls_back_checkpoint_so_retry_records_the_missing_bytes_once()
    {
        var store = Store(); store.Record([Counter(10, 20)], Now);
        using var database = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"); database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_update BEFORE UPDATE ON application_traffic BEGIN SELECT RAISE(ABORT,'mock storage failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.Record([Counter(20, 40)], Now.AddSeconds(1)));
        command.CommandText = "DROP TRIGGER fail_update"; command.ExecuteNonQuery();
        store.Record([Counter(20, 40)], Now.AddSeconds(2));
        Assert.Equal(60, Assert.Single(store.Read(Now).Entries).Total);
    }

    [Fact]
    public void Large_counters_saturate_instead_of_wrapping_to_negative_values()
    {
        var store = Store(); store.Record([Counter(long.MaxValue, long.MaxValue)], Now);
        store.Record([Counter(100, 100, id: "second")], Now.AddSeconds(1));
        var row = Assert.Single(store.Read(Now).Entries);
        Assert.Equal(long.MaxValue, row.Upload); Assert.Equal(long.MaxValue, row.Download); Assert.Equal(long.MaxValue, row.Total);
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(_directory);
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EgressController.TrafficTests"));
        if (Path.GetDirectoryName(full) != parent) throw new InvalidOperationException("Unexpected test path");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
