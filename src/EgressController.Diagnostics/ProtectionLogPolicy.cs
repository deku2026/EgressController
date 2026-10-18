namespace EgressController.Diagnostics;

/// <summary>File logs describe protection/lifecycle decisions, never ordinary connection output.</summary>
public static class ProtectionLogPolicy
{
    public static bool IsRelevant(CoreLogEntry entry)
    {
        string[] stages = ["TUN", "保护", "进程保护", "进程终止", "守护状态", "守护终止", "守护保护", "守护接管检查",
            "守护名单同步", "守护管理", "独立守护通信", "出口变化检测", "内核生命周期", "出口配置更新", "退出保护", "服务", "启动", "退出", "主程序启动"];
        return stages.Any(stage => entry.Source.StartsWith(stage, StringComparison.Ordinal))
            || entry.Source.StartsWith("联网检测", StringComparison.Ordinal) && entry.Message.Contains("[network.failed]", StringComparison.Ordinal);
    }
}
