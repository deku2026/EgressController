# sing-box 数据面兼容性

应用使用 sing-box TUN 接管 Windows 全流量，路由与 DNS 由生成的 JSON 配置决定。C# 不监听
业务代理端口，因此不提供独立的 HTTP、HTTPS 或 CONNECT 代理协议。

| 能力 | 当前实现 |
| --- | --- |
| IPv4/IPv6 TUN | sing-box `tun` inbound，按已解析网卡环境生成 |
| 应用分流 | 全部使用锚定完整 EXE 路径的大小写不敏感 `process_path_regex`；显式进程规则先于域名/规则集，商店应用升级按同包身份迁移已保存的相对路径 |
| eSIM 不可用 | eSIM 命中项由 sing-box `reject`，不回退其他出口 |
| 未命中流量 | `route.final` 指向首页默认 SOCKS5 端口，初始为 7890 |
| 多端口 | 每个端口一个 `upstream-<port>` 出口，全部监听 owner 优先走第二张网卡的 `proxy-direct`；离线端口不回退 |
| DNS | sing-box 劫持普通 DNS，统一经 eSIM 使用 Cloudflare DoH；未匹配业务流量走默认端口；DoH 不做健康检测，不作为关闭程序的判断条件 |
| 网络保护 | 每秒检查 TUN 与网卡，两出口每 30 秒检测联网；失败隔 5 秒重试，共连续三次失败才确认。独立守护仅检查主程序与 TUN |
| 规则 | MetaCubeX `sing` 分支的 SRS，commit/blob 校验后原子缓存 |
| 诊断 | sing-box 本地 REST API：connections 快照、连接操作与联网探测；统计采样不控制 TUN |
| 普通启动 | `WindowsLaunchService.Launch`，Win32 使用已确定的主 EXE，MSIX 激活注册 AUMID；不影响路由，不跟踪父子 PID，不注入代理参数 |

UDP、QUIC、原始 TCP 和不受 TUN 影响的系统流量不由 C# 单独判断；它们是否可达由
Windows 网络栈与 sing-box TUN 运行状态共同决定。
