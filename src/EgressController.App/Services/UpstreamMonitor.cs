using EgressController.Windows.Network;
using EgressController.Windows.Process;

namespace EgressController.App.Services;

public sealed record UpstreamStatusSnapshot
{
    public required int Port { get; init; }
    public required Socks5ProbeResult Probe { get; init; }
    public required IReadOnlyList<TcpListenerOwner> Owners { get; init; }
    public required IReadOnlyList<string> OwnerPaths { get; init; }
    public required bool OwnerResolutionComplete { get; init; }
    public string? Error { get; init; }
    public bool IsReady => Probe.IsReady && OwnerResolutionComplete && OwnerPaths.Count > 0 && Error is null;
}

public sealed class UpstreamStatusChangedEventArgs(UpstreamStatusSnapshot snapshot) : EventArgs
{
    public UpstreamStatusSnapshot Snapshot { get; } = snapshot;
}

/// <summary>
/// Maintains the one-second LISTEN/owner view. SOCKS5 greeting is intentionally throttled to
/// start, port changes and owner transitions so the monitor never creates continuous probe noise.
/// </summary>
public sealed class UpstreamMonitor : IAsyncDisposable
{
    private readonly UpstreamSocksProbe _probe;
    private readonly TcpListenerOwnerResolver _ownerResolver;
    private readonly HashSet<string> _forbiddenPaths;
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private int _port;
    private HashSet<string> _lastOwnerPaths = new(StringComparer.OrdinalIgnoreCase);
    private Socks5ProbeResult? _lastProbe;
    private UpstreamStatusSnapshot? _lastSnapshot;

    public UpstreamMonitor(
        int port,
        UpstreamSocksProbe? probe = null,
        TcpListenerOwnerResolver? ownerResolver = null,
        IEnumerable<string>? forbiddenPaths = null)
    {
        ValidatePort(port);
        _port = port;
        _probe = probe ?? new UpstreamSocksProbe();
        _ownerResolver = ownerResolver ?? new TcpListenerOwnerResolver();
        _forbiddenPaths = new HashSet<string>(
            (forbiddenPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
    }

    public event EventHandler<UpstreamStatusChangedEventArgs>? StatusChanged;
    public UpstreamStatusSnapshot? Current => _lastSnapshot;

    public void UpdatePort(int port)
    {
        ValidatePort(port);
        lock (_gate)
        {
            if (_port == port)
                return;
            _port = port;
            _lastOwnerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lastProbe = null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false })
                return _loop;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = MonitorLoopAsync(_lifetime.Token);
            return _loop;
        }
    }

    public async Task<UpstreamStatusSnapshot> CheckAsync(
        bool forceProbe = false,
        CancellationToken cancellationToken = default)
    {
        int port;
        HashSet<string> previousOwners;
        Socks5ProbeResult? previousProbe;
        lock (_gate)
        {
            port = _port;
            previousOwners = new HashSet<string>(_lastOwnerPaths, StringComparer.OrdinalIgnoreCase);
            previousProbe = _lastProbe;
        }

        IReadOnlyList<TcpListenerOwner> owners = _ownerResolver.Resolve(port, cancellationToken);
        string[] paths = owners
            .Where(owner => owner.CanonicalExecutablePath is not null)
            .Select(owner => owner.CanonicalExecutablePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        bool ownerChanged = !previousOwners.SetEquals(paths);
        bool shouldProbe = forceProbe || previousProbe is null || (previousOwners.Count == 0 && paths.Length > 0)
            || ownerChanged;
        Socks5ProbeResult probe = shouldProbe
            ? await _probe.ProbeAsync(port, cancellationToken).ConfigureAwait(false)
            : previousProbe!;

        string? error = null;
        bool complete = owners.All(owner => owner.IsResolved);
        if (!complete)
            error = "SOCKS5 监听 owner 进程路径无法全部解析，已拒绝启动/应用。";
        else if (paths.Any(path => _forbiddenPaths.Contains(path)))
            error = "SOCKS5 owner 是 EgressController/sing-box 自身，已拒绝递归。";

        var snapshot = new UpstreamStatusSnapshot
        {
            Port = port,
            Probe = probe,
            Owners = owners,
            OwnerPaths = paths,
            OwnerResolutionComplete = complete,
            Error = error,
        };

        bool changed;
        lock (_gate)
        {
            _lastOwnerPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            _lastProbe = probe;
            changed = !AreEquivalent(_lastSnapshot, snapshot);
            _lastSnapshot = snapshot;
        }
        if (changed)
            StatusChanged?.Invoke(this, new UpstreamStatusChangedEventArgs(snapshot));
        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? lifetime;
        Task? loop;
        lock (_gate)
        {
            lifetime = _lifetime;
            loop = _loop;
            _lifetime = null;
            _loop = null;
        }
        if (lifetime is null)
            return;
        lifetime.Cancel();
        try
        {
            if (loop is not null)
                await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        await CheckAsync(forceProbe: true, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await CheckAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool AreEquivalent(UpstreamStatusSnapshot? previous, UpstreamStatusSnapshot current)
        => previous is not null
            && previous.Port == current.Port
            && previous.Probe.Status == current.Probe.Status
            && string.Equals(previous.Error, current.Error, StringComparison.Ordinal)
            && previous.OwnerPaths.SequenceEqual(current.OwnerPaths, StringComparer.OrdinalIgnoreCase);

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));
    }
}
