namespace EgressController.Core.Protection;

public sealed record ProtectionReadiness(bool Ready, string Reason)
{
    public static ProtectionReadiness Evaluate(bool tunRunning, string? tunError, bool changing,
        string? configurationError, string? networkError, DohHealthSnapshot health)
    {
        if (!string.IsNullOrWhiteSpace(configurationError)) return new(false, configurationError);
        if (!tunRunning) return new(false, "TUN 未就绪：" + (tunError ?? "正在自动启动"));
        if (changing) return new(false, "配置正在应用，等待重新检测");
        if (!string.IsNullOrWhiteSpace(networkError)) return new(false, networkError);
        return new(health.Ready, health.Reason);
    }
}
