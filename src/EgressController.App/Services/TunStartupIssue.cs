using EgressController.SingBox.Runtime;

namespace EgressController.App.Services;

/// <summary>Current preparation progress is separate from retained historical failures.</summary>
public static class TunStartupIssue
{
    public static string? Current(SingBoxServiceStatus status, string downloadStatus) => status.State switch
    {
        SingBoxServiceState.Running => null,
        SingBoxServiceState.Preparing => "正在准备启动资源：" + downloadStatus,
        SingBoxServiceState.Starting or SingBoxServiceState.Applying => "正在启动内核并确认 TUN 接管",
        SingBoxServiceState.RollingBack => "正在恢复上次有效配置",
        SingBoxServiceState.Stopping => "正在停止受管理 TUN",
        SingBoxServiceState.Failed => status.ErrorMessage ?? "启动失败，等待自动重试",
        _ => "等待 TUN 自动启动",
    };
}
