// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json;
using EgressController.Policy;

[assembly: InternalsVisibleTo("EgressController.Runtime.Tests")]

namespace EgressController.Runtime;

public sealed record TrafficObservation(string ConnectionId, string ApplicationId, ExitKind Outbound,
    long UploadedBytes, long DownloadedBytes, DateTimeOffset ObservedAt);
public sealed record UsagePeriod(DateOnly Start, DateOnly EndExclusive);
public sealed record TrafficTotal(DateOnly PeriodStart, string ApplicationId, ExitKind Outbound, long UploadedBytes, long DownloadedBytes);
public sealed record TrafficCheckpoint(string ConnectionId, long UploadedBytes, long DownloadedBytes, DateTimeOffset ObservedAt);
public sealed class TrafficLedgerSnapshot
{
    public DateTimeOffset FirstOpenedAt { get; set; }
    public string CalendarTimeZone { get; set; } = "UTC";
    public List<TrafficTotal> Totals { get; set; } = [];
    public List<TrafficCheckpoint> Checkpoints { get; set; } = [];
}
public interface ITrafficLedgerStorage { void Commit(TrafficLedgerSnapshot snapshot); }

/// <summary>Observation-only accounting. Statistics failures never invoke protection or core operations.</summary>
public sealed class TrafficLedger
{
    public const int MaximumCheckpointCount = 50_000;
    public const int MaximumTotalCount = 50_000;
    private readonly object _gate = new();
    private readonly ITrafficLedgerStorage _storage;
    private readonly TimeZoneInfo _calendar;
    private readonly int _checkpointCapacity;
    private readonly bool _invalidInitialState;
    private bool _resumeBaseline = true;
    private TrafficLedgerSnapshot _snapshot;
    private string? _lastStorageError;
    public TrafficLedgerSnapshot Snapshot { get { lock (_gate) return Copy(_snapshot); } }
    public string? LastStorageError { get { lock (_gate) return _lastStorageError; } }

    public TrafficLedger(TrafficLedgerSnapshot? snapshot, ITrafficLedgerStorage storage, int checkpointCapacity = MaximumCheckpointCount)
    {
        _storage = storage;
        _checkpointCapacity = Math.Clamp(checkpointCapacity, 1, MaximumCheckpointCount);
        if (ValidateSnapshot(snapshot) is null)
        {
            _calendar = TimeZoneInfo.FindSystemTimeZoneById(snapshot!.CalendarTimeZone);
            _snapshot = Copy(snapshot);
        }
        else
        {
            // Keep a trustworthy calendar anchor; never overwrite a corrupt source with an empty ledger.
            string zone = TryCalendar(snapshot?.CalendarTimeZone, out var calendar) ? snapshot!.CalendarTimeZone : "UTC";
            _calendar = calendar ?? TimeZoneInfo.Utc;
            _snapshot = new()
            {
                FirstOpenedAt = snapshot is not null && ValidTimestamp(snapshot.FirstOpenedAt)
                ? snapshot.FirstOpenedAt : DateTimeOffset.UtcNow,
                CalendarTimeZone = zone
            };
            _invalidInitialState = true;
            _lastStorageError = "统计存储无效，统计已暂停；原始文件与已有备份未被修改。";
        }
    }

    public UsagePeriod PeriodAt(DateTimeOffset time)
    {
        lock (_gate)
        {
            if (!ValidTimestamp(time)) { _lastStorageError = "统计观测时间无效。"; time = _snapshot.FirstOpenedAt; }
            return PeriodAtCore(time);
        }
    }
    private UsagePeriod PeriodAtCore(DateTimeOffset time)
    {
        var anchor = Anchor(_snapshot.FirstOpenedAt, _calendar);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, _calendar).DateTime);
        int period = Math.Max(0, day.DayNumber - anchor.DayNumber) / 30;
        var start = anchor.AddDays(period * 30);
        return new(start, start.AddDays(30));
    }
    public void Resume() { lock (_gate) _resumeBaseline = true; }

    /// <param name="completeSnapshot">True only for an authoritative complete active-connection list.
    /// Failed/partial telemetry must not claim completeness. Only complete lists prune closed checkpoints.</param>
    public bool TryObserve(IReadOnlyList<TrafficObservation>? observations, bool completeSnapshot = false)
    {
        lock (_gate)
        {
            if (_invalidInitialState) return false;
            try
            {
                if (observations is null) { _lastStorageError = "暂未取得有效流量观测。"; return false; }
                if (observations.Count > MaximumCheckpointCount) { _lastStorageError = "流量观测超过统计容量，已暂停本轮统计。"; return false; }
                var next = Copy(_snapshot);
                var checkpoints = next.Checkpoints.ToDictionary(item => item.ConnectionId, StringComparer.Ordinal);
                var totals = next.Totals.ToDictionary(item => (item.PeriodStart, item.ApplicationId, item.Outbound));
                var active = new HashSet<string>(StringComparer.Ordinal);
                bool invalidRows = false;
                int freshSamples = 0;
                foreach (var observation in observations)
                {
                    if (observation is null || !ValidIdentifier(observation.ConnectionId) || !ValidIdentifier(observation.ApplicationId)
                        || !Enum.IsDefined(observation.Outbound) || observation.UploadedBytes < 0 || observation.DownloadedBytes < 0
                        || !ValidTimestamp(observation.ObservedAt) || observation.ObservedAt < _snapshot.FirstOpenedAt)
                    { invalidRows = true; continue; }
                    if (!active.Add(observation.ConnectionId)) { _lastStorageError = "本轮流量观测包含重复连接，已保留原统计。"; return false; }
                    checkpoints.TryGetValue(observation.ConnectionId, out var previous);
                    if (previous is not null && observation.ObservedAt <= previous.ObservedAt) continue;
                    freshSamples++;
                    long uploaded = Math.Max(0, observation.UploadedBytes - (previous?.UploadedBytes ?? 0));
                    long downloaded = Math.Max(0, observation.DownloadedBytes - (previous?.DownloadedBytes ?? 0));
                    bool reset = previous is not null && (observation.UploadedBytes < previous.UploadedBytes || observation.DownloadedBytes < previous.DownloadedBytes);
                    if (!_resumeBaseline && !reset)
                    {
                        var key = (PeriodAtCore(observation.ObservedAt).Start, observation.ApplicationId, observation.Outbound);
                        totals.TryGetValue(key, out var current);
                        totals[key] = new(key.Start, observation.ApplicationId, observation.Outbound,
                            SaturatingAdd(current?.UploadedBytes ?? 0, uploaded), SaturatingAdd(current?.DownloadedBytes ?? 0, downloaded));
                    }
                    checkpoints[observation.ConnectionId] = new(observation.ConnectionId, observation.UploadedBytes, observation.DownloadedBytes, observation.ObservedAt);
                }
                bool pruned = false;
                if (completeSnapshot && !invalidRows)
                    foreach (string closed in checkpoints.Keys.Where(id => !active.Contains(id)).ToArray()) pruned |= checkpoints.Remove(closed);
                if (freshSamples == 0 && !pruned)
                {
                    if (invalidRows) _lastStorageError = "本轮流量观测无效，已保留恢复基线。";
                    return !invalidRows;
                }
                if (_resumeBaseline && invalidRows)
                { _lastStorageError = "恢复时的流量观测不完整，已保留原基线并等待重试。"; return false; }
                if (checkpoints.Count > _checkpointCapacity || totals.Count > MaximumTotalCount)
                { _lastStorageError = "统计容量已满；等待完整连接快照或统计维护，已有数据未丢弃。"; return false; }
                next.Checkpoints = checkpoints.Values.OrderBy(item => item.ConnectionId, StringComparer.Ordinal).ToList();
                next.Totals = totals.Values.OrderBy(item => item.PeriodStart).ThenBy(item => item.ApplicationId, StringComparer.Ordinal).ThenBy(item => item.Outbound).ToList();
                if (ValidateSnapshot(next) is not null) { _lastStorageError = "统计快照校验失败，已有数据未修改。"; return false; }
                // Totals and their checkpoints are one transaction; give adapters their own copy.
                _storage.Commit(Copy(next));
                _snapshot = next;
                if (freshSamples > 0) _resumeBaseline = false;
                _lastStorageError = null;
                return true;
            }
            catch (Exception)
            {
                _lastStorageError = "用量暂未保存，将在下一次有效观测时重试。";
                return false;
            }
        }
    }

    public static string? ValidateSnapshot(TrafficLedgerSnapshot? value)
    {
        try
        {
            if (value is null || !ValidTimestamp(value.FirstOpenedAt) || !TryCalendar(value.CalendarTimeZone, out var calendar)
                || value.Totals is null || value.Checkpoints is null || value.Totals.Count > MaximumTotalCount
                || value.Checkpoints.Count > MaximumCheckpointCount) return "snapshot-shape";
            DateOnly anchor = Anchor(value.FirstOpenedAt, calendar!);
            var totalKeys = new HashSet<(DateOnly, string, ExitKind)>();
            foreach (var total in value.Totals)
                if (total is null || !ValidIdentifier(total.ApplicationId) || !Enum.IsDefined(total.Outbound)
                    || total.UploadedBytes < 0 || total.DownloadedBytes < 0 || total.PeriodStart < anchor
                    || total.PeriodStart.Year > 9998 || (total.PeriodStart.DayNumber - anchor.DayNumber) % 30 != 0
                    || !totalKeys.Add((total.PeriodStart, total.ApplicationId, total.Outbound))) return "invalid-total";
            var connectionKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var checkpoint in value.Checkpoints)
                if (checkpoint is null || !ValidIdentifier(checkpoint.ConnectionId) || checkpoint.UploadedBytes < 0
                    || checkpoint.DownloadedBytes < 0 || !ValidTimestamp(checkpoint.ObservedAt)
                    || checkpoint.ObservedAt < value.FirstOpenedAt || !connectionKeys.Add(checkpoint.ConnectionId)) return "invalid-checkpoint";
            return null;
        }
        catch (Exception) { return "invalid-statistics"; }
    }
    private static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Contains('\0');
    private static bool ValidTimestamp(DateTimeOffset time) => time >= DateTimeOffset.UnixEpoch && time.Year <= 9998;
    private static bool TryCalendar(string? id, out TimeZoneInfo? calendar)
    {
        calendar = null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
        try { calendar = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { return false; }
    }
    private static DateOnly Anchor(DateTimeOffset firstOpened, TimeZoneInfo calendar)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(firstOpened, calendar).DateTime);
    private static long SaturatingAdd(long first, long second) => first > long.MaxValue - second ? long.MaxValue : first + second;
    internal static TrafficLedgerSnapshot Copy(TrafficLedgerSnapshot value) => new()
    { FirstOpenedAt = value.FirstOpenedAt, CalendarTimeZone = value.CalendarTimeZone, Totals = value.Totals.ToList(), Checkpoints = value.Checkpoints.ToList() };
}

public sealed class InMemoryTrafficStorage : ITrafficLedgerStorage
{
    private readonly object _gate = new();
    private TrafficLedgerSnapshot? _lastCommitted;
    public TrafficLedgerSnapshot? LastCommitted { get { lock (_gate) return _lastCommitted is null ? null : TrafficLedger.Copy(_lastCommitted); } }
    public bool FailCommit { get; set; }
    public void Commit(TrafficLedgerSnapshot snapshot)
    {
        lock (_gate)
        {
            if (FailCommit) throw new IOException("Injected statistics failure.");
            _lastCommitted = TrafficLedger.Copy(snapshot);
        }
    }
}

/// <summary>Bounded, schema-checked snapshots with a verified previous copy and durable rollback.</summary>
public sealed class AtomicTrafficStorage : ITrafficLedgerStorage
{
    public const int MaximumFileBytes = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ITrafficFilePublication _publication;
    private string? _lastReadError;
    public string? LastReadError { get { lock (_gate) return _lastReadError; } }
    public AtomicTrafficStorage(string path) : this(path, new DurableTrafficFilePublication()) { }
    internal AtomicTrafficStorage(string path, ITrafficFilePublication publication)
    { _path = Path.GetFullPath(path); _publication = publication; }

    public void Commit(TrafficLedgerSnapshot snapshot)
    {
        lock (_gate)
        {
            if (TrafficLedger.ValidateSnapshot(snapshot) is not null) throw new InvalidDataException("Invalid traffic ledger snapshot.");
            var frozen = TrafficLedger.Copy(snapshot);
            if (TrafficLedger.ValidateSnapshot(frozen) is not null) throw new InvalidDataException("Invalid traffic ledger snapshot.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frozen, RuntimeJsonContext.Default.TrafficLedgerSnapshot);
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Traffic snapshot exceeds the file limit.");
            var current = ReadCandidate(_path);
            var backup = ReadCandidate(_path + ".previous");
            var previous = current ?? backup;
            if (previous is null && (File.Exists(_path) || File.Exists(_path + ".previous")))
                throw new InvalidDataException("Corrupt statistics must be recovered explicitly before publication.");
            if (previous is not null && !IsContinuation(previous.Value.Snapshot, frozen))
                throw new InvalidDataException("Traffic publication cannot reset its calendar or historical totals.");
            string directory = Path.GetDirectoryName(_path)!;
            DurableFiles.CreatePrivateDirectory(directory);
            if (current is { } good) _publication.Replace(_path + ".previous", good.Bytes);
            try { _publication.Replace(_path, bytes); }
            catch (Exception failure)
            {
                try
                {
                    if (previous is { } old) _publication.Replace(_path, old.Bytes);
                    else { if (File.Exists(_path)) File.Delete(_path); DurableFiles.SyncDirectory(directory); }
                }
                catch (Exception recovery) { throw new IOException("Traffic publication and rollback failed.", new AggregateException(failure, recovery)); }
                throw;
            }
            _lastReadError = null;
        }
    }
    public TrafficLedgerSnapshot? Read()
    {
        lock (_gate)
        {
            if (ReadCandidate(_path) is { } current) { _lastReadError = null; return current.Snapshot; }
            if (ReadCandidate(_path + ".previous") is { } backup)
            { _lastReadError = "已从上一份完整统计快照恢复；损坏文件未被覆盖。"; return backup.Snapshot; }
            _lastReadError = File.Exists(_path) || File.Exists(_path + ".previous") ? "统计文件无效，已停止载入并保留原始数据。" : null;
            return null;
        }
    }
    private static (TrafficLedgerSnapshot Snapshot, byte[] Bytes)? ReadCandidate(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumFileBytes) return null;
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            var snapshot = JsonSerializer.Deserialize(bytes, RuntimeJsonContext.Default.TrafficLedgerSnapshot);
            return TrafficLedger.ValidateSnapshot(snapshot) is null ? (snapshot!, bytes) : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private static bool IsContinuation(TrafficLedgerSnapshot previous, TrafficLedgerSnapshot next)
    {
        if (previous.FirstOpenedAt != next.FirstOpenedAt || previous.CalendarTimeZone != next.CalendarTimeZone) return false;
        var totals = next.Totals.ToDictionary(item => (item.PeriodStart, item.ApplicationId, item.Outbound));
        return previous.Totals.All(old => totals.TryGetValue((old.PeriodStart, old.ApplicationId, old.Outbound), out var current)
            && current.UploadedBytes >= old.UploadedBytes && current.DownloadedBytes >= old.DownloadedBytes);
    }
}

// Boundary tests wrap the real durable publisher and can fail after rename. Production always syncs.
internal interface ITrafficFilePublication { void Replace(string path, byte[] bytes); }
internal sealed class DurableTrafficFilePublication : ITrafficFilePublication
{ public void Replace(string path, byte[] bytes) => DurableFiles.Replace(path, bytes); }
