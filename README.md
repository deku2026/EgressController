# EgressController

EgressController 是 Windows x64 上专门面向 AI 应用和浏览器的全流量 TUN 控制器。
sing-box 是唯一的网络数据面；C# / Avalonia 只负责扫描、生成配置、下载校验、启动控制和
连接展示，不实现 HTTP、SOCKS 或其他代理转发。

## 当前行为

- 应用启动后自动扫描受支持的 AI 客户端和浏览器，并自动尝试启动 TUN。
- 应用自身要求管理员权限；它直接启动受管理的 sing-box 子进程，不再有 System core、
  ElevatedHost、Named Pipe 或其他第二套提权控制面。
- 应用发现只使用 Windows Store/MSIX、卸载注册表、App Paths 和 Program Files 目录；
  PATH、CLI、快捷方式和手工选择 EXE 不参与发现。已发现应用的目录会递归收集全部 EXE。
- 勾选应用的递归 EXE 会转换为 sing-box `process_name` 规则，同时包含带 `.exe`/不带扩展名和
  Windows 常见大小写形式（例如 `claude.exe`、`Claude.exe`）。sing-box 在每条新连接上实时解析
  进程，不依赖启动按钮、PID 表或 LaunchSession。
- 勾选的应用、SRS 和手工域名组成一个 eSIM 集合。命中且 eSIM 网卡可用时走 eSIM 直连；
  eSIM 不存在或离线时直接 `reject`，绝不回退到 7890。
- 未命中规则的流量固定走用户已有的 `127.0.0.1:7890` SOCKS5。7890 的监听进程由 Windows
  owner table 动态识别，并优先绑定主网卡，避免 sing-box 回流到自身。
- sing-box 管理 DoH、DNS 劫持、IPv4-only DNS 策略、IPv6 防漏规则以及 Windows 全流量 TUN。
  主网卡和 eSIM 网卡分别绑定到对应 direct 出口。
- “网络与内核”页展示实际生成的 DoH server、TLS SNI、detour 和连接状态。eSIM 与 7890 是两个
  不同出口，各自配置 Cloudflare 与 Google 候选；程序每 60 秒通过 sing-box DNS query 检测，
  当前候选不可用时只在同一出口内切换，两个候选都失败时保持 TUN 并拒绝 TUN 外部流量。
- TUN 运行时会定期重新检查网卡和 7890 owner；环境发生变化时重新生成、校验并应用配置。
- “连接”页展示真实活动/历史连接、进程、目标、协议、出口、规则和流量，支持双击详情、关闭
  单条/全部连接和清空历史；不提供独立的核心日志页面。sing-box 输出只保留有界的本地诊断日志。
- 流量页使用 SQLite 保存 eSIM 套餐总量、配置时的剩余量和本地统计的已用量，可清空统计并
  重新显示圆形占比。它不是运营商计费接口。

## 本地文件布局

首次启动会在发布 EXE 同级创建目录；不会迁移、删除或覆盖旧的
`%LOCALAPPDATA%\EgressController` 数据。

```text
EgressController.App.exe
data\
  profile.json                 # 用户意图：网卡、7890、应用、SRS、域名
  ui-state.json                # 页面状态
  usage.db                     # eSIM 本地流量统计
  current-runtime.json         # 当前运行指针
  last-good-runtime.json       # 可回滚运行指针
  apply.pending.json           # 应用中的崩溃恢复标记
  core\                       # 仅保存 core 指针，不保存 sing-box 二进制
  runtime\config-<sha256>.json
  logs\sing-box.log
  logs\sing-box.log.1
ruleset\
  core\<version>\sing-box.exe
  core\<version>\...
  rules\catalog.json
  rules\<catalog-commit>\<name>.srs
```

`data` 和 `ruleset` 都由程序自动创建，release ZIP 不携带本机配置、连接记录或规则缓存。

## 构建与测试

需要 `global.json` 指定的 .NET SDK、Windows 10/11 x64，以及 NativeAOT 所需的 Visual Studio
Build Tools Desktop development with C++ 工作负载。

```powershell
dotnet restore EgressController.slnx
dotnet build EgressController.slnx --configuration Release --no-restore
dotnet test EgressController.slnx --configuration Release --no-restore -- --minimum-expected-tests 1 --progress off
```

默认测试不依赖公网。需要验证真实本机 sing-box 1.13.x、7890、catalog/SRS 和 REST/WebSocket
API 时显式打开实时测试；下载失败时可只对当前 PowerShell 会话设置临时代理：

```powershell
$env:HTTP_PROXY = 'http://127.0.0.1:7890'
$env:HTTPS_PROXY = 'http://127.0.0.1:7890'
$env:EGRESS_LIVE_RULES_TEST = '1'
dotnet test ./tests/EgressController.Rules.Tests/EgressController.Rules.Tests.csproj --configuration Release --no-restore --no-build -- --minimum-expected-tests 1 --progress off
dotnet test ./tests/EgressController.SingBox.Tests/EgressController.SingBox.Tests.csproj --configuration Release --no-restore --no-build -- --minimum-expected-tests 1 --progress off
$env:HTTP_PROXY = $null
$env:HTTPS_PROXY = $null
$env:EGRESS_LIVE_RULES_TEST = $null
```

## NativeAOT 与发布

发布脚本只打包管理员 App；不再构建或发布 ElevatedHost：

```powershell
./build/Package.ps1 -Version 0.1.5 -SkipMsix
```

产物写入仓库内的 `artifacts\package`：

- `EgressController-win-x64.zip`：App 的 NativeAOT 自包含 ZIP；
- `SHA256SUMS.txt`：所有发布文件的 SHA-256；
- 不带 `-SkipMsix` 时，若安装 Windows SDK，还会生成未签名 MSIX。

发布前应检查 ZIP 中存在 `EgressController.App.exe` 及其运行时文件、不包含 `.pdb`，并在无
.NET Runtime 的 Windows 环境启动验证。CI 的 PR 检查会执行 Release build/test 和同一打包脚本；
合并到 `main` 后由 release workflow 生成版本发布包。

## 设计边界

本项目不管理节点、订阅、selector、provider、YAML、Windows 全局代理或应用代理环境变量，
也不向浏览器/WebView 注入代理参数。上游代理负责提供 7890 SOCKS5；本项目只把未命中流量
交给它，并用 sing-box API 展示实际连接状态。

配置与实施验收记录保存在本机
`C:\MyFile\ArcForges\Plan\windows-egress-controller-full-traffic-design.md`，边界说明见
[docs/traffic-migration-boundary.md](docs/traffic-migration-boundary.md)。sing-box 配置字段以
[官方文档](https://sing-box.sagernet.org/) 为准。

## License

[MIT](LICENSE)
