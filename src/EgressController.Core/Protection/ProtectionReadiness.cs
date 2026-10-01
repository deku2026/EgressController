namespace EgressController.Core.Protection;

/// <summary>DNS and physical exit reachability never decide whether an application may run.</summary>
public sealed record ProtectionReadiness(bool Ready, string Reason)
{
    public static ProtectionReadiness Evaluate(bool tunRunning, string? tunError,
        bool configurationApplied, string? configurationError, string? takeoverError)
    {
        if (!string.IsNullOrWhiteSpace(configurationError)) return new(false, configurationError);
        if (!tunRunning) return new(false, "TUN 未就绪：" + (tunError ?? "正在自动启动"));
        if (!configurationApplied) return new(false, "配置尚未成功应用，正在自动恢复");
        if (!string.IsNullOrWhiteSpace(takeoverError)) return new(false, takeoverError);
        return new(true, "TUN 已接管，可以打开所选应用");
    }
}
