# Windows 全流量 TUN 边界

EgressController 的唯一业务数据面是 sing-box TUN。管理员 C# / Avalonia App 只负责 Profile、
网卡环境、AI/浏览器扫描、规则 catalog/SRS、受管理 core 下载校验、配置编译和 API 展示；
C# 不转发业务流量。

## 固定语义

- 首页维护多个本地 SOCKS5 端口和唯一默认端口，初始为 7890。
- 应用递归发现的 EXE、SRS 和手工域名可分别选择 eSIM、跟随默认或具体端口；新勾选项
  默认 eSIM。应用路由使用 sing-box `process_name`，不使用 PID 表或启动按钮决定出口。
- 业务规则按应用、自定义域名（子域名优先）、SRS（名称顺序）依次匹配，未命中项进入
  `route.final` 对应的默认 SOCKS5 端口；勾选并选择“默认”可显式覆盖后续规则。
- eSIM 缺失或离线时命中项直接 `reject`；指定端口不可用时连接失败，均不回退其他出口。
- 所有配置端口的监听 owner 由 Windows 表动态解析并优先走主网卡，防止回流到上游自身。
  暂未启动的可选端口仍保留独立出口，运行时监视器在发现监听进程后补充防回流规则。
- 主网卡和 eSIM 网卡分别绑定到对应 direct 出口；TUN 使用 IPv4/IPv6、auto route、strict route。
- sing-box 管理 DNS hijack、经 eSIM 的全局 Cloudflare/腾讯 DoH、IPv4-only DNS 策略及 IPv6 reject；
  DNS 出口不改变业务规则；全局 DoH 不可用时仍拒绝所有 TUN 外部流量。
- 控制面下载显式通过默认 SOCKS5 端口，不读取 Windows 全局代理，也不向普通应用注入代理参数。
- 规则来自 MetaCubeX `sing` 分支的 commit-pinned SRS；完整配置先执行目标 core 的 `check`。
- App manifest 要求管理员权限，直接启动受管理的 sing-box；不再存在 System core、ElevatedHost
  或 Named Pipe 第二套控制面。

## 配置顺序

1. 首次启动在 EXE 同级创建 `data` 和 `ruleset`，扫描受支持的 AI 应用与浏览器。
2. 在首页添加 SOCKS5 端口并设定默认项；启动时探测默认端口的真实 SOCKS5 greeting。
3. 选择主网卡和 eSIM 网卡，校验当前 IPv4/IPv6 环境；eSIM 可暂时离线，命中流量仍 reject。
4. 在应用页勾选应用，在域名页选择 SRS 或手工域名，并通过下拉框指定出口。
5. 生成按摘要命名的运行配置，执行 `sing-box check`，通过后启动或应用 TUN。
6. 通过 sing-box 1.13 controller API 读取连接、traffic、DNS 诊断，并将核心输出写入有界日志。

## 不在范围内

不实现 C# HTTP/CONNECT 转发、全局代理接管、PID 决定网络出口、CLI/快捷方式发现、手工 EXE
添加、订阅/节点/selector/provider 管理或 YAML 配置。应用 PID 和 LaunchSession 只用于连接
详情或普通启动状态展示；应用启动不是路由前置条件。
