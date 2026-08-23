namespace EgressController.Diagnostics;

/// <summary>A normalized connection row owned by the sing-box diagnostics surface.</summary>
public sealed record ConnectionObservation
{
    public required string Id { get; init; }
    public string Network { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string SourceIp { get; init; } = string.Empty;
    public string DestinationIp { get; init; } = string.Empty;
    public string SourcePort { get; init; } = string.Empty;
    public string DestinationPort { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string DnsMode { get; init; } = string.Empty;
    public string? ProcessPath { get; init; }
    public uint? ProcessId { get; init; }
    public long Upload { get; init; }
    public long Download { get; init; }
    public long UploadRate { get; init; }
    public long DownloadRate { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset LastSeenAtUtc { get; init; }
    public IReadOnlyList<string> Chains { get; init; } = [];
    public string? Rule { get; init; }
    public string? RulePayload { get; init; }
    public string? Outbound { get; init; }
    public DateTimeOffset? ClosedAtUtc { get; init; }
}

/// <summary>
/// Active rows are replaced by each full sing-box snapshot; rows that disappear are retained in
/// a bounded closed history. A repeated connection id updates one row instead of creating a copy.
/// </summary>
public sealed class ConnectionHistoryStore
{
    private const int DefaultClosedCapacity = 2048;
    private readonly object _gate = new();
    private readonly int _closedCapacity;
    private readonly Dictionary<string, ConnectionObservation> _active = new(StringComparer.Ordinal);
    private readonly Queue<ConnectionObservation> _closed = new();
    private long _droppedClosed;

    public ConnectionHistoryStore(int closedCapacity = DefaultClosedCapacity)
    {
        if (closedCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(closedCapacity));
        _closedCapacity = closedCapacity;
    }

    public long DroppedClosed => Interlocked.Read(ref _droppedClosed);

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _active.Count;
        }
    }

    public int ClosedCount
    {
        get
        {
            lock (_gate)
                return _closed.Count;
        }
    }

    public void ApplySnapshot(IEnumerable<ConnectionObservation> snapshot, DateTimeOffset? observedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DateTimeOffset closedAt = observedAtUtc ?? DateTimeOffset.UtcNow;
        var next = new Dictionary<string, ConnectionObservation>(StringComparer.Ordinal);
        foreach (ConnectionObservation item in snapshot)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                continue;
            next[item.Id] = item with { ClosedAtUtc = null };
        }

        lock (_gate)
        {
            foreach ((string id, ConnectionObservation previous) in _active)
            {
                if (!next.ContainsKey(id))
                    AddClosed(previous with { ClosedAtUtc = closedAt });
            }

            _active.Clear();
            foreach ((string id, ConnectionObservation current) in next)
                _active[id] = current;
        }
    }

    public void UpsertActive(ConnectionObservation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id))
            throw new ArgumentException("The connection id is required.", nameof(item));
        lock (_gate)
            _active[item.Id] = item with { ClosedAtUtc = null };
    }

    public bool MarkClosed(string connectionId, DateTimeOffset? closedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return false;
        lock (_gate)
        {
            if (!_active.Remove(connectionId, out ConnectionObservation? item))
                return false;
            AddClosed(item with { ClosedAtUtc = closedAtUtc ?? DateTimeOffset.UtcNow });
            return true;
        }
    }

    public IReadOnlyList<ConnectionObservation> ActiveSnapshot()
    {
        lock (_gate)
            return _active.Values.ToArray();
    }

    public IReadOnlyList<ConnectionObservation> ClosedSnapshot()
    {
        lock (_gate)
            return _closed.ToArray();
    }

    public void ClearClosed()
    {
        lock (_gate)
        {
            _closed.Clear();
            Interlocked.Exchange(ref _droppedClosed, 0);
        }
    }

    private void AddClosed(ConnectionObservation item)
    {
        if (_closed.Count >= _closedCapacity)
        {
            _closed.Dequeue();
            Interlocked.Increment(ref _droppedClosed);
        }
        _closed.Enqueue(item);
    }
}

public sealed record CoreLogEntry(
    DateTimeOffset TimestampUtc,
    string Source,
    string Level,
    string Message);

/// <summary>Bounded core/stdout/stderr log storage shared by the diagnostics view.</summary>
public sealed class BoundedLogStore
{
    private const int DefaultCapacity = 4096;
    private const int DefaultMaxMessageLength = 32 * 1024;
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly int _maxMessageLength;
    private readonly Queue<CoreLogEntry> _entries = new();
    private long _dropped;

    public BoundedLogStore(int capacity = DefaultCapacity, int maxMessageLength = DefaultMaxMessageLength)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxMessageLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageLength));
        _capacity = capacity;
        _maxMessageLength = maxMessageLength;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public void Append(
        string source,
        string level,
        string message,
        DateTimeOffset? timestampUtc = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("The log source is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(level))
            throw new ArgumentException("The log level is required.", nameof(level));
        ArgumentNullException.ThrowIfNull(message);

        string boundedMessage = message.Length <= _maxMessageLength
            ? message
            : message[.._maxMessageLength] + "… [truncated]";
        var entry = new CoreLogEntry(timestampUtc ?? DateTimeOffset.UtcNow, source, level, boundedMessage);
        lock (_gate)
        {
            if (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
                Interlocked.Increment(ref _dropped);
            }
            _entries.Enqueue(entry);
        }
    }

    public IReadOnlyList<CoreLogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Interlocked.Exchange(ref _dropped, 0);
        }
    }
}
