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
- 首页可手动添加多个本地 SOCKS5 端口，用“设为默认”选择唯一默认端口；初始为 `7890`。
  未匹配分流的流量走默认端口，控制面下载也使用该端口。
- 应用和 SRS 勾选后默认走 eSIM，可在每行下拉框选择“eSIM”“默认”或首页添加的具体端口。
  自定义域名在添加时选择出口，也可随后修改。全选保留每行已选出口，取消勾选移除该条规则。
- 业务规则优先级为：应用进程、自定义域名（子域名优先）、SRS（按名称排序）、默认端口。
  未勾选的应用仍会匹配域名规则；若希望应用始终走默认端口，可勾选并选择“默认”。
  “默认”随首页设置变化，具体端口保持固定。默认端口和被规则引用的端口不能直接删除。
- eSIM 命中且网卡可用时走 eSIM 直连；不可用时直接 `reject`。指定端口离线时连接失败，
  两者均不回退到其他出口。所有已配置端口的监听进程由 Windows owner table 动态识别，
  并优先绑定主网卡，避免 sing-box 回流到上游自身。进程名相同但出口冲突的应用会报错。
- sing-box 管理 DoH、DNS 劫持、IPv4-only DNS 策略、IPv6 防漏规则以及 Windows 全流量 TUN。
  主网卡和 eSIM 网卡分别绑定到对应 direct 出口。
- “网络与内核”页展示实际生成的全局 DoH server、TLS SNI、detour 和连接状态。所有普通 DNS
  统一经 eSIM 使用 Cloudflare，失败时切换腾讯 DNSPod，恢复后自动切回；解析后的未命中业务
  流量仍由 `route.final` 送往默认端口。程序每 60 秒检测一次，两项都失败时保持 TUN 并拒绝外部流量。
- TUN 运行时会定期重新检查网卡和所有上游端口的 owner；环境发生变化时重新生成、校验并应用配置。
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
  profile.json                 # 用户意图：网卡、端口列表、默认端口、各规则的出口
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

Profile schema 2 会读取 schema 1 的单端口与 eSIM 选择，保留旧默认端口和已有规则；下次保存
写入新格式，并由现有原子保存逻辑备份旧文件。旧版本程序不能编辑 schema 2 配置。

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
也不向浏览器/WebView 注入代理参数。上游代理负责提供配置的本地 SOCKS5 端口；本项目将
流量交给所选出口，并用 sing-box API 展示实际连接状态。

配置与实施验收记录保存在本机
`C:\MyFile\ArcForges\Plan\windows-egress-controller-full-traffic-design.md`，边界说明见
[docs/traffic-migration-boundary.md](docs/traffic-migration-boundary.md)。sing-box 配置字段以
[官方文档](https://sing-box.sagernet.org/) 为准。

## License

[MIT](LICENSE)
