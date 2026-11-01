namespace EgressController.Runtime;

/// <summary>Classifies startup output into fixed codes; never retains server addresses, credentials or connection errors.</summary>
public sealed class CoreStartupDiagnosis
{
    private string _code = "core.exited";
    public string Code => Volatile.Read(ref _code);
    public void Reset() => Volatile.Write(ref _code, "core.exited");
    public void Observe(string line)
    {
        if (line.Length > 4096 || !(line.Contains("Failed to start", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to listen", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to create tun", StringComparison.OrdinalIgnoreCase))) return;
        string code = line.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ? "proxy.port-in-use"
            : line.Contains("permission denied", StringComparison.OrdinalIgnoreCase) || line.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase) ? "core.permission"
            : line.Contains("file exists", StringComparison.OrdinalIgnoreCase) || line.Contains("device busy", StringComparison.OrdinalIgnoreCase) ? "tun.in-use"
            : line.Contains("config", StringComparison.OrdinalIgnoreCase) ? "core.configuration" : "core.exited";
        Volatile.Write(ref _code, code);
    }
}
