using System.Threading.Channels;
using System.Collections.Concurrent;
using EgressController.Core.Diagnostics;

namespace EgressController.Diagnostics;

/// <summary>
/// Bounded ring buffer (drop-oldest) feeding the UI's Connections page. Writers never block:
/// when full, the newest event drops and a DROPPED counter increments so the UI can show
/// saturation (plan §Step 11). UI reads via <see cref="Reader"/> or <see cref="Latest"/>.
/// </summary>
public sealed class ConnectionLog : IConnectionLog
{
    private const int DefaultCapacity = 8192;
    private readonly int _capacity;
    private readonly Channel<ConnectionEvent> _channel;
    private long _dropped;
    private readonly ConcurrentQueue<ConnectionEvent> _snapshot = new();

    public ConnectionLog(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
        _channel = Channel.CreateBounded<ConnectionEvent>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    }

    public ChannelReader<ConnectionEvent> Reader => _channel.Reader;

    /// <summary>Monotonic count of events dropped due to saturation.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Write(ConnectionEvent e)
    {
        _snapshot.Enqueue(e);
        while (_snapshot.Count > _capacity)
            _snapshot.TryDequeue(out _);

        if (!_channel.Writer.TryWrite(e))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// Clears both the UI snapshot and unread channel backlog. Concurrent writers may append new
    /// events after this call; callers that need a clean connection boundary should first stop or
    /// reject the connections which produced the old events.
    /// </summary>
    public void Clear()
    {
        _snapshot.Clear();
        while (_channel.Reader.TryRead(out _)) { }
        Interlocked.Exchange(ref _dropped, 0);
    }

    /// <summary>Latest events, newest last (for a one-shot UI draw / diagnostics).</summary>
    public IReadOnlyList<ConnectionEvent> Latest() => _snapshot.ToArray();
}
