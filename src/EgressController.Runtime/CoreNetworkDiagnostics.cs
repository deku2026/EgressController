namespace EgressController.Runtime;

/// <summary>Separate bounded diagnostic counters; no raw connection data and no protection/lifecycle decisions.</summary>
public sealed class CoreNetworkDiagnostics(ILifecycleLog log, Func<DateTimeOffset>? clock = null)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (DateTimeOffset Last, int Count)> _counts = new(StringComparer.Ordinal);
    private readonly OperationDiagnostics _log = new(log);
    public void Observe(string line)
    {
        if (line.Length > 8192 || !(line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("[Warning]", StringComparison.Ordinal) || line.Contains("[Error]", StringComparison.Ordinal))) return;
        string code = NetworkFailure.Classify(line);
        lock (_sync)
        {
            var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
            var previous = _counts.GetValueOrDefault(code);
            int count = previous.Count + 1;
            if (now - previous.Last >= TimeSpan.FromSeconds(30))
            { _log.Record("core.network", $"code={code}; occurrences={count}; details=redacted; status=diagnostic-only"); _counts[code] = (now, 0); }
            else _counts[code] = (previous.Last, count);
        }
    }
}
