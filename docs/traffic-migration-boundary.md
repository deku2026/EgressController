# Windows 全流量 TUN 边界

EgressController 的唯一业务数据面是 sing-box TUN。C# UI 只负责 Profile、网卡环境、规则
catalog/SRS、core 下载校验、ElevatedHost 生命周期和 Clash API 诊断。

## 固定语义

- eSIM 是应用选择、SRS 和手工域名的并集，命中后使用 eSIM 直连。
- 未命中项固定进入 Profile 中的 upstream SOCKS5，默认 `127.0.0.1:7890`。
- 主网卡用于控制面和上游连接；eSIM 网卡只由 sing-box direct 出口使用。
- 软件控制面使用显式 SOCKS5 传输，不读取 Windows 全局代理，也不向普通应用注入代理参数。
- 规则来自 MetaCubeX `sing` 分支的 commit-pinned SRS；配置写入后先执行 core check。
- 只有 ElevatedHost/sing-box 需要管理员权限；App 和普通 Launch 保持普通权限。

## 配置顺序

1. 选择 Managed 或 System sing-box core。
2. 确认 upstream SOCKS5 端口并探测真实 SOCKS5 greeting。
3. 选择主网卡和 eSIM 网卡，校验 IPv4/IPv6 环境。
4. 扫描应用、选择 eSIM 应用；选择 SRS 或手工域名。
5. 生成 config.next.json，执行 `sing-box check`，通过后启动或应用 TUN。
6. 通过真实 Clash API 读取连接、traffic、logs 和 DNS 诊断。

## 不在范围内

不实现 C# HTTP/CONNECT 转发、全局代理接管、PID 决定网络出口、订阅/节点/selector/provider
管理或 YAML 配置。应用 PID 和 LaunchSession 只用于普通启动状态与诊断展示。
