# sing-box 数据面兼容性

应用使用 sing-box TUN 接管 Windows 全流量，路由与 DNS 由生成的 JSON 配置决定。C# 不监听
业务代理端口，因此不提供独立的 HTTP、HTTPS 或 CONNECT 代理协议。

| 能力 | 当前实现 |
| --- | --- |
| IPv4/IPv6 TUN | sing-box `tun` inbound，按已解析网卡环境生成 |
| 应用路径分流 | `process_path` 规则，应用选择与 SRS/手工域名统一进入 eSIM 集合 |
| 未命中流量 | 固定 `upstream-socks`，默认连接本机 7890 SOCKS5 |
| DNS | sing-box DoH 经 upstream-socks；API 提供查询和缓存清理 |
| 规则 | MetaCubeX `sing` 分支的 SRS，commit/blob 校验后原子缓存 |
| 诊断 | Clash API REST/WebSocket：connections、traffic、logs、DNS |
| 普通启动 | `WindowsLaunchService.StartPlain`，无环境变量或浏览器代理参数注入 |

UDP、QUIC、原始 TCP 和不受 TUN 影响的系统流量不由 C# 单独判断；它们是否可达由
Windows 网络栈与 sing-box TUN 运行状态共同决定。
