# sing-box 数据面兼容性

应用使用 sing-box TUN 接管 Windows 全流量，路由与 DNS 由生成的 JSON 配置决定。C# 不监听
业务代理端口，因此不提供独立的 HTTP、HTTPS 或 CONNECT 代理协议。

| 能力 | 当前实现 |
| --- | --- |
| IPv4/IPv6 TUN | sing-box `tun` inbound，按已解析网卡环境生成 |
| 应用分流 | `process_name` 规则，递归发现的 EXE 名称与 SRS/手工域名统一进入 eSIM 集合 |
| eSIM 不可用 | eSIM 命中项由 sing-box `reject`，不回退到 7890 |
| 未命中流量 | 固定 `upstream-socks`，默认连接本机 7890 SOCKS5 |
| DNS | sing-box 劫持普通 DNS，统一经 eSIM 使用 Cloudflare/腾讯 DoH；解析后的未命中业务流量仍走 7890 |
| 规则 | MetaCubeX `sing` 分支的 SRS，commit/blob 校验后原子缓存 |
| 诊断 | sing-box controller API REST/WebSocket：connections、traffic、logs、DNS |
| 普通启动 | `WindowsLaunchService.StartPlain`，无环境变量或浏览器代理参数注入 |

UDP、QUIC、原始 TCP 和不受 TUN 影响的系统流量不由 C# 单独判断；它们是否可达由
Windows 网络栈与 sing-box TUN 运行状态共同决定。
