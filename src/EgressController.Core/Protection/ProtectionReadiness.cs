namespace EgressController.Core.Protection;

/// <summary>Applications require confirmed takeover, current configuration and healthy exits.</summary>
public sealed record ProtectionReadiness(bool Ready, string Reason)
{
    public static ProtectionReadiness Evaluate(bool tunRunning, string? tunError,
        bool configurationApplied, string? configurationError, string? takeoverError, ProtectionReadiness network)
    {
        if (!string.IsNullOrWhiteSpace(configurationError)) return new(false, configurationError);
        if (!tunRunning) return new(false, "TUN 未就绪：" + (tunError ?? "正在自动启动"));
        if (!configurationApplied) return new(false, "配置尚未成功应用，正在自动恢复");
        if (!string.IsNullOrWhiteSpace(takeoverError)) return new(false, takeoverError);
        if (!network.Ready) return network;
        return new(true, "TUN 已接管；" + network.Reason + "。可以打开所选应用");
    }
}
