namespace EgressController.Core.Protection;

public sealed record ProtectionReadiness(bool Ready, string Reason)
{
    public static ProtectionReadiness Evaluate(bool tunRunning, string? tunError, bool changing,
        string? configurationError, string? networkError, bool bothDohHealthy,
        DateTimeOffset? lastHealthyAt, DateTimeOffset now, string? probeError)
    {
        if (!string.IsNullOrWhiteSpace(configurationError)) return new(false, configurationError);
        if (!tunRunning) return new(false, "TUN 未就绪：" + (tunError ?? "正在自动启动"));
        if (changing) return new(false, "配置正在应用，等待重新检测");
        if (!string.IsNullOrWhiteSpace(networkError)) return new(false, networkError);
        if (!bothDohHealthy || lastHealthyAt is null || now - lastHealthyAt > TimeSpan.FromSeconds(20))
            return new(false, probeError ?? "等待两个 DoH 和实际出口联网检测通过");
        return new(true, "网络已就绪，可以打开所选应用");
    }
}
