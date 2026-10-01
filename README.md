# EgressController

EgressController 是 Windows x64 上专门面向 AI 应用和浏览器的全流量 TUN 控制器。
sing-box 是唯一的网络数据面；C# / Avalonia 只负责扫描、生成配置、下载校验、启动控制和
连接展示，不实现 HTTP、SOCKS 或其他代理转发。

## 当前行为

- 启动后立即进入进程保护，再扫描应用、准备内核和规则、自动启动 TUN。每秒检查进程与启动状态，
  同一次启动未完成时不重复启动。界面和托盘不再提供停止 TUN 的入口。
- 应用需要管理员权限。sing-box 负责全流量 TUN 与分流，C# 负责配置、发现、健康检测和进程保护。
- 网卡和本地 SOCKS5 端口分别管理：可添加多张实际网卡、修改显示名称并设置默认网卡；
  端口列表保留独立的默认端口。支持可作为出口的有线、Wi-Fi、USB 共享、蜂窝和虚拟接口，
  排除回环及隧道接口。保存稳定 GUID，不依赖名称、扫描顺序或临时 ifIndex。
- 勾选应用默认使用“默认网卡”。每行先选择“网卡”或“端口”，再选择该类别的默认项或具体出口。
  两类默认项分别排在各自列表第一位；改变默认值不会改变明确指定的出口。指定接口消失后保持
  原绑定并报告断开，不自动换网卡。没有 SOCKS5 端口的纯网卡配置也可以准备 TUN。
- DNS 网卡默认跟随默认网卡，也可明确指定；默认解析服务是 Cloudflare。
  Cloudflare、DNSPod 两个 DoH 都必须成功，并且规则实际使用的出口须通过 HTTPS 联网检测。
  DoH 通过 sing-box DNS query 检测，联网通过指定 outbound 的 delay API 检测；完成一轮后间隔 5 秒。
  网卡断开、TUN 未运行、配置正在变更、健康结果过期或探测失败时，都保持进程保护。
- 保护按勾选目录递归扫描得到的完整 EXE 路径匹配所有运行实例，并追踪子孙进程；不区分网卡或端口。
  终止前验证 PID 与创建时间，避免 PID 复用误判。全部就绪后停止终止，应用由用户重新打开。
  这是进程保护，不是系统防火墙：终止前可能已有请求，未勾选应用不属于终止范围。
- “进程保护”页显示原因，以及最近 200 条终止结果、应用名、路径、PID 和时间。
  权限不足、身份无法确认、终止尚未完成都明确报告；同一失败会重试但不每秒刷屏。
- 控制器自己的下载使用独立的直接 HTTP 客户端，不依赖本地 SOCKS5 启动成功；TUN 配置优先放行
  控制器和核心的恢复通信。保护期间拒绝所选应用的 TUN 路由，不再插入整台机器的无条件拒绝规则。
  勾选范围包含控制器、核心或上游代理时报告冲突，不终止这些恢复依赖。
- 应用发现仍使用 Windows Store/MSIX、卸载注册表、App Paths 和 Program Files。已选择但暂未发现
  的项目保留在列表，仍可取消选择；缓存已扫描的 EXE 路径，供下次启动扫描完成前进行保护。
- 业务规则优先级仍为应用、自定义域名、SRS、默认端口；没有端口时，未匹配流量使用默认网卡出口。
  指定网卡离线时对应规则拒绝，指定端口离线时连接失败，不回退到其他业务出口。
- 关闭窗口继续在托盘运行；“退出并结束进程保护”才停止守护并清理 TUN。
  本方案不安装系统封网服务，不承诺应用退出、崩溃后继续保护。
- 连接页继续展示活动/历史连接；流量页统计默认网卡 direct 出口的累计用量，属于本地估算。

## 本地文件布局

首次启动会在发布 EXE 同级创建目录；不会迁移、删除或覆盖旧的
`%LOCALAPPDATA%\EgressController` 数据。

```text
EgressController.App.exe
data\
  profile.json                 # 用户意图：网卡、端口列表、默认端口、各规则的出口
  ui-state.json                # 页面状态
  usage.db                     # 默认网卡本地流量统计
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

Profile schema 3 读取 schema 1/2，将旧主网卡与 eSIM 选择迁移为网卡列表，保留端口与规则。
旧 eSIM 规则绑定迁移后的原网卡，DNS 默认跟随默认网卡。后续保存清除旧字段；旧版程序不能编辑 schema 3。

## 构建与 mock 测试

需要 `global.json` 指定的 .NET SDK、Windows 10/11 x64，以及 NativeAOT 所需的 Visual Studio
Build Tools Desktop development with C++ 工作负载。

```powershell
dotnet restore EgressController.slnx
dotnet build EgressController.slnx --configuration Release --no-restore -p:EgressMockOnly=true
./build/Invoke-Tests.ps1 -Configuration Release -NoBuild
```

`EgressMockOnly` 默认开启：真实内核、Windows 进程启动/终止、机器扫描等 live 测试不编译进测试程序集。
测试脚本及 CI 还设置 `EGRESS_MOCK_ONLY=1`，使生产进程/TUN 操作边界拒绝执行。新增保护测试使用
假进程快照、假终止结果和假 HTTP 返回；已有 SOCKS 测试只使用本地模拟服务器，不修改系统网络。
不要在日常机器上运行产品 EXE 来验证本 PR。详细状态与测试边界见 [保护设计](docs/process-protection.md)。

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
