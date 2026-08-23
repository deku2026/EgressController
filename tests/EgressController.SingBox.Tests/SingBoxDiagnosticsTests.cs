using EgressController.Diagnostics;

namespace EgressController.SingBox.Tests;

public sealed class SingBoxDiagnosticsTests
{
    [Fact]
    public void Snapshot_updates_by_id_and_moves_disappeared_rows_to_bounded_closed_history()
    {
        var history = new ConnectionHistoryStore(closedCapacity: 2);
        history.ApplySnapshot([Connection("one"), Connection("two")], DateTimeOffset.Parse("2026-08-23T00:00:00Z"));
        history.ApplySnapshot(
            [Connection("one") with { Download = 99 }],
            DateTimeOffset.Parse("2026-08-23T00:00:01Z"));

        Assert.Single(history.ActiveSnapshot());
        Assert.Equal(99, history.ActiveSnapshot().Single().Download);
        Assert.Single(history.ClosedSnapshot());
        Assert.Equal("two", history.ClosedSnapshot().Single().Id);

        history.ApplySnapshot([], DateTimeOffset.Parse("2026-08-23T00:00:02Z"));
        history.ApplySnapshot([Connection("three")], DateTimeOffset.Parse("2026-08-23T00:00:03Z"));
        history.ApplySnapshot([Connection("four")], DateTimeOffset.Parse("2026-08-23T00:00:04Z"));
        history.ApplySnapshot([], DateTimeOffset.Parse("2026-08-23T00:00:05Z"));

        Assert.Equal(2, history.ClosedCount);
        Assert.Equal(2, history.DroppedClosed);
        Assert.Equal(["three", "four"], history.ClosedSnapshot().Select(item => item.Id));
    }

    [Fact]
    public void Bounded_log_store_truncates_messages_and_reports_drops()
    {
        var logs = new BoundedLogStore(capacity: 2, maxMessageLength: 12);
        logs.Append("sing-box", "info", "first");
        logs.Append("elevated-host", "stderr", new string('x', 100));
        logs.Append("sing-box", "warn", "last");

        Assert.Equal(2, logs.Count);
        Assert.Equal(1, logs.Dropped);
        Assert.DoesNotContain("first", logs.Snapshot().Select(entry => entry.Message));
        Assert.Contains("truncated", logs.Snapshot()[0].Message, StringComparison.Ordinal);
        Assert.Equal("last", logs.Snapshot()[1].Message);

        logs.Clear();
        Assert.Empty(logs.Snapshot());
        Assert.Equal(0, logs.Dropped);
    }

    [Fact]
    public void Default_log_store_keeps_a_finite_diagnostic_window()
    {
        var logs = new BoundedLogStore();
        for (int index = 0; index < 1025; index++)
            logs.Append("sing-box", "warn", $"message-{index}");

        Assert.Equal(1024, logs.Count);
        Assert.Equal(1, logs.Dropped);
        Assert.DoesNotContain("message-0", logs.Snapshot().Select(entry => entry.Message));
        Assert.Contains("message-1024", logs.Snapshot().Select(entry => entry.Message));
    }

    [Fact]
    public void MarkClosed_is_idempotent_and_does_not_duplicate_history()
    {
        var history = new ConnectionHistoryStore();
        history.UpsertActive(Connection("one"));

        Assert.True(history.MarkClosed("one"));
        Assert.False(history.MarkClosed("one"));
        Assert.Empty(history.ActiveSnapshot());
        Assert.Single(history.ClosedSnapshot());
    }

    private static ConnectionObservation Connection(string id)
        => new()
        {
            Id = id,
            Host = id + ".example.com",
            StartedAtUtc = DateTimeOffset.Parse("2026-08-23T00:00:00Z"),
        };
}
