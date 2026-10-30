// SPDX-License-Identifier: MIT
using System.Text.Json;
using EgressController.Policy;
using EgressController.Runtime;

namespace EgressController.Runtime.Tests;

public sealed class TrafficStorageTests
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("null-totals")]
    [InlineData("null-checkpoints")]
    [InlineData("duplicate-checkpoint")]
    [InlineData("duplicate-total")]
    [InlineData("wrong-period")]
    [InlineData("negative")]
    [InlineData("invalid-exit")]
    [InlineData("invalid-zone")]
    [InlineData("empty-identity")]
    [InlineData("too-many")]
    public void InvalidSchemaIsContainedAndDoesNotWriteAReplacement(string invalid)
    {
        var snapshot = State(10);
        switch (invalid)
        {
            case "null-totals": snapshot.Totals = null!; break;
            case "null-checkpoints": snapshot.Checkpoints = null!; break;
            case "duplicate-checkpoint": snapshot.Checkpoints.Add(snapshot.Checkpoints[0]); break;
            case "duplicate-total": snapshot.Totals.Add(snapshot.Totals[0]); break;
            case "wrong-period": snapshot.Totals[0] = snapshot.Totals[0] with { PeriodStart = DateOnly.FromDateTime(Opened.Date).AddDays(1) }; break;
            case "negative": snapshot.Checkpoints[0] = snapshot.Checkpoints[0] with { UploadedBytes = -1 }; break;
            case "invalid-exit": snapshot.Totals[0] = snapshot.Totals[0] with { Outbound = (ExitKind)99 }; break;
            case "invalid-zone": snapshot.CalendarTimeZone = "invalid/no-such-calendar"; break;
            case "empty-identity": snapshot.Totals[0] = snapshot.Totals[0] with { ApplicationId = " " }; break;
            case "too-many": snapshot.Checkpoints = Enumerable.Repeat(snapshot.Checkpoints[0], TrafficLedger.MaximumCheckpointCount + 1).ToList(); break;
        }
        Assert.NotNull(TrafficLedger.ValidateSnapshot(snapshot));
        var storage = new InMemoryTrafficStorage();
        var ledger = new TrafficLedger(snapshot, storage);
        Assert.NotNull(ledger.LastStorageError);
        Assert.Equal(Opened, ledger.Snapshot.FirstOpenedAt);
        Assert.False(ledger.TryObserve([new("new", "example", ExitKind.Home, 20, 20, Opened.AddDays(1))]));
        Assert.Null(storage.LastCommitted);
    }

    [Fact]
    public void CorruptCurrentRecoversOnlyVerifiedBackupAndKeepsTheCalendarAndHistoricalTotals()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "private", "traffic.json");
        var storage = new AtomicTrafficStorage(path);
        storage.Commit(State(10)); storage.Commit(State(20));
        File.WriteAllText(path, "{\"FirstOpenedAt\":\"2026-10-30T12:00:00Z\",\"Totals\":null}");
        byte[] corrupt = File.ReadAllBytes(path);
        var recovered = storage.Read();
        Assert.NotNull(recovered); Assert.Equal(10, Assert.Single(recovered.Totals).UploadedBytes);
        Assert.Equal(Opened, recovered.FirstOpenedAt); Assert.NotNull(storage.LastReadError);
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        storage.Commit(State(30));
        Assert.Equal(30, Assert.Single(storage.Read()!.Totals).UploadedBytes);
        Assert.Null(storage.LastReadError);
        Assert.Equal(10, Assert.Single(JsonSerializer.Deserialize(File.ReadAllBytes(path + ".previous"), RuntimeJsonContext.Default.TrafficLedgerSnapshot)!.Totals).UploadedBytes);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
    }

    [Fact]
    public void OversizedOrInvalidFilesAreBoundedAndNeverOverwrittenAsEmptyStatistics()
    {
        using var directory = new TemporaryDirectory(); string path = Path.Combine(directory.Path, "traffic.json");
        using (var oversized = File.Create(path)) oversized.SetLength(AtomicTrafficStorage.MaximumFileBytes + 1L);
        var storage = new AtomicTrafficStorage(path);
        Assert.Null(storage.Read()); Assert.NotNull(storage.LastReadError);
        Assert.Throws<InvalidDataException>(() => storage.Commit(State(10)));
        Assert.Equal(AtomicTrafficStorage.MaximumFileBytes + 1L, new FileInfo(path).Length);
    }

    [Fact]
    public void FailureAfterActualAtomicRenameRollsBackBothTotalsAndCheckpoints()
    {
        using var directory = new TemporaryDirectory(); string path = Path.Combine(directory.Path, "private", "traffic.json");
        var publisher = new FailAfterRealRename(path); var storage = new AtomicTrafficStorage(path, publisher);
        storage.Commit(State(10));
        byte[] previous = File.ReadAllBytes(path);
        publisher.FailNextCurrent = true;
        Assert.Throws<IOException>(() => storage.Commit(State(20)));
        Assert.Equal(previous, File.ReadAllBytes(path));
        var restored = storage.Read()!;
        Assert.Equal(10, Assert.Single(restored.Totals).UploadedBytes);
        Assert.Equal(10, Assert.Single(restored.Checkpoints).UploadedBytes);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        storage.Commit(State(20));
        Assert.Equal(20, Assert.Single(storage.Read()!.Totals).UploadedBytes);
    }

    [Fact]
    public void FirstPublicationFailureRestoresAbsenceRatherThanLeavingAnUncommittedLedger()
    {
        using var directory = new TemporaryDirectory(); string path = Path.Combine(directory.Path, "private", "traffic.json");
        var publisher = new FailAfterRealRename(path) { FailNextCurrent = true };
        var storage = new AtomicTrafficStorage(path, publisher);
        Assert.Throws<IOException>(() => storage.Commit(State(10)));
        Assert.False(File.Exists(path)); Assert.Null(storage.Read());
    }

    [Fact]
    public void NewPublicationCannotResetPriorTotalsOrFirstOpeningDate()
    {
        using var directory = new TemporaryDirectory(); string path = Path.Combine(directory.Path, "private", "traffic.json");
        var storage = new AtomicTrafficStorage(path); storage.Commit(State(20));
        Assert.Throws<InvalidDataException>(() => storage.Commit(State(10)));
        var changedAnchor = State(30); changedAnchor.FirstOpenedAt = Opened.AddDays(-30);
        Assert.Throws<InvalidDataException>(() => storage.Commit(changedAnchor));
        Assert.Equal(20, Assert.Single(storage.Read()!.Totals).UploadedBytes);
    }

    private static TrafficLedgerSnapshot State(long total) => new()
    {
        FirstOpenedAt = Opened,
        CalendarTimeZone = "UTC",
        Totals = [new(DateOnly.FromDateTime(Opened.Date), "example", ExitKind.Home, total, total)],
        Checkpoints = [new("connection", total, total, Opened.AddSeconds(total))]
    };
    private sealed class FailAfterRealRename(string currentPath) : ITrafficFilePublication
    {
        private readonly DurableTrafficFilePublication _durable = new();
        public bool FailNextCurrent { get; set; }
        public void Replace(string path, byte[] bytes)
        {
            _durable.Replace(path, bytes);
            if (path == currentPath && FailNextCurrent)
            { FailNextCurrent = false; throw new IOException("Injected failure after actual durable rename."); }
        }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "egress-traffic-boundary-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
