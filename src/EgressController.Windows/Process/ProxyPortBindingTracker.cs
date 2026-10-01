namespace EgressController.Windows.Process;

public sealed record ProxyPortBinding(int Port, IReadOnlyList<TcpListenerOwner> Owners, string? Error)
{
    public bool IsReady => Error is null && Owners.Count == 1 && Owners[0].IsResolved;
    public string Fingerprint => $"{Port}:{Error}:" + string.Join('|', Owners.Select(owner =>
        $"{owner.ProcessId}:{owner.StartedAtUtc:O}:{owner.CanonicalExecutablePath}:{owner.ObservedExecutablePath}"));
}

public sealed record ProxyPortSnapshot(IReadOnlyList<ProxyPortBinding> Ports, IReadOnlyList<string> OwnerPaths)
{
    public static ProxyPortSnapshot Empty { get; } = new([], []);
    public string Fingerprint => string.Join(';', Ports.Select(port => port.Fingerprint));
}

/// <summary>Port ownership is discovered, never a user-supplied PID. The last known path stays
/// exempt while its listener restarts, so it cannot recursively enter its own SOCKS endpoint.</summary>
public sealed class ProxyPortBindingTracker(ITcpListenerOwnerResolver resolver)
{
    private readonly Dictionary<int, string[]> _knownPaths = new();
    private readonly object _gate = new();

    public ProxyPortSnapshot Capture(IReadOnlyList<int> ports, CancellationToken token = default)
    {
        lock (_gate)
        {
            foreach (int removed in _knownPaths.Keys.Where(port => !ports.Contains(port)).ToArray())
                _knownPaths.Remove(removed);
            var bindings = new List<ProxyPortBinding>();
            foreach (int port in ports.Order())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    TcpListenerOwner[] owners = resolver.Resolve(port, token).DistinctBy(owner => owner.ProcessId).OrderBy(owner => owner.ProcessId).ToArray();
                    string? error = owners.Length switch
                    {
                        0 => "端口未监听，等待代理程序启动",
                        > 1 => "端口对应多个监听进程，无法确定代理核心",
                        _ when !owners[0].IsResolved => "无法确认代理进程完整路径，请检查权限",
                        _ => null,
                    };
                    if (error is null) _knownPaths[port] = owners[0].RoutePaths.ToArray();
                    else if (owners.Any(owner => owner.IsResolved))
                        _knownPaths[port] = _knownPaths.GetValueOrDefault(port, []).Concat(owners.Where(owner => owner.IsResolved)
                            .SelectMany(owner => owner.RoutePaths)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    bindings.Add(new(port, owners, error));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    bindings.Add(new(port, [], "端口进程检查失败：" + exception.Message));
                }
            }
            return new(bindings, _knownPaths.Values.SelectMany(paths => paths)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
}
