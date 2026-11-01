namespace EgressController.Runtime;

/// <summary>Only these fixed, credential-free diagnoses may cross IPC or reach current status.</summary>
public sealed class RuntimeFailureException(string code, string? diagnosticDetail = null) : IOException(RuntimeFailures.Message(code))
{
    public string? DiagnosticDetail { get; } = diagnosticDetail;
    public string Code { get; } = RuntimeFailures.IsKnown(code) ? code : "runtime.failed";
}

public static class RuntimeFailures
{
    private static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        ["core.bundle"] = "安装包缺少或损坏了内置 Xray。请使用完整的最新 Egress.app；不会联网下载内核来启动自身。",
        ["subscription.interface"] = "订阅需要直连。请先选择当前已连接的非家宽物理网卡，再重试导入；无需启动 Xray 或 TUN。",
        ["subscription.dns"] = "订阅直连所用 DNS 地址无效。请在设置中填写明确的非回环 DNS IP 地址后重试。",
        ["proxy.not-ready"] = "尚未确认 Egress 自带的 Xray TUN 与本地代理。请先导入订阅、选择非家宽网卡和全局分组，然后连接。",
        ["proxy.port-in-use"] = "Xray 本地 SOCKS5 端口已被占用。请在「设置 → 网络与 TUN」更改端口，或退出占用程序后重试。",
        ["tun.in-use"] = "所选 TUN 接口已被占用。请退出冲突的 VPN，或在配置中修改 tun.name 后重试。",
        ["interface.identity"] = "保存的出口网卡身份无法解析。请在设置中重新选择对应网卡；断网本身不阻止 TUN 启动，也不会自动换卡。",
        ["dns.snapshot"] = "无法备份所选网络服务的 DNS 设置。请检查系统网络服务配置后重试；尚未修改 DNS。",
        ["authorization.denied"] = "管理员授权被取消或拒绝。请重新连接并允许安装网络辅助服务。",
        ["helper.install"] = "网络辅助服务安装未完成。请打开日志目录查看具体阶段与错误；无需以管理员身份运行 Rider。",
        ["helper.install-data"] = "管理员授权已完成，但助手无法解析安装数据。请重新编译完整应用后重试；具体字段已记录到日志。",
        ["helper.install-crashed"] = "管理员授权后安装助手异常退出。请打开日志目录查看安装阶段和系统错误；无需提升 Rider 权限。",
        ["helper.missing"] = "当前目录缺少网络辅助服务。请使用完整 Egress.app 连接；Rider 仍可编译并启动界面。",
        ["helper.identity"] = "已连接网络辅助服务，但无法验证其进程身份。请重新编译完整应用后重试，并查看日志中的身份校验错误；无需提升 Rider 权限。",
        ["helper.endpoint"] = "无法读取网络辅助服务的安装文件或通信端点。请打开日志目录查看文件检查结果。",
        ["helper.timeout"] = "授权后未能连接网络辅助服务。请打开日志目录检查安装状态后重试。",
        ["core.permission"] = "Xray 无法取得网络操作权限。请检查辅助服务授权及系统安全设置。",
        ["core.validation-timeout"] = "内置 Xray 配置检查超时。请检查节点配置及本地安全软件后重试。",
        ["core.configuration"] = "内置 Xray 拒绝了生成配置。请检查节点协议、TUN 设置和规则后重试。",
        ["core.exited"] = "Xray 在确认接管前退出。请检查节点配置、端口和 TUN 占用后重试。",
        ["tun.timeout"] = "等待 TUN 路由接管超时。请检查冲突 VPN、TUN 设置及辅助服务权限。",
        ["recovery.failed"] = "连接准备失败后的网络恢复尚未确认。请停止连接并打开日志目录检查；当前不显示健康状态。",
        ["resources.no-proxy"] = "默认分组中没有测速成功的代理节点。请在分组页测速或更换成员；下载不会回退到 DIRECT。",
        ["routing.update"] = "路由更新未完成，已恢复更新前的规则。请打开日志目录查看原因后重试。",
        ["runtime.failed"] = "网络启动未完成。请打开日志目录检查当前故障后重试。"
    };
    public static bool IsKnown(string code) => Messages.ContainsKey(code);
    public static string Message(string code) => Messages.GetValueOrDefault(code, Messages["runtime.failed"]);
}
