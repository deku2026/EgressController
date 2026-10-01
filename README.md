# EgressController

EgressController 是 Windows x64 上专门面向 AI 应用和浏览器的全流量 TUN 控制器。
sing-box 是唯一的网络数据面；C# / Avalonia 只负责扫描、生成配置、下载校验、启动控制和
连接展示，不实现 HTTP、SOCKS 或其他代理转发。

## 当前行为

- 启动后立即进入进程保护，再扫描应用、准备内核和规则、自动启动 TUN。每秒检查进程与启动状态，
  同一次启动未完成时不重复启动。界面和托盘不再提供停止 TUN 的入口。
- 应用需要管理员权限。sing-box 负责全流量 TUN 与分流，C# 负责配置、发现、健康检测和进程保护。
- 网卡固定为两个角色，实际网卡由用户从本机接口中任意选择两张不同的网卡：
  `ESIM-家宽` 是应用的默认直连出口，`Proxy-代理` 是所有本地代理核心的出口。
  两个角色都可更换或交换网卡，没有添加第三个角色的入口。支持有线、Wi-Fi、USB 共享、蜂窝；
  默认只列出在线且有 IP 的常见接口，“显示更多”可查看离线和虚拟接口。回环与自身 TUN 不可选。
  按 GUID 保存绑定；所选网卡掉线或消失仍保留选择，绝不自动换网卡。
- 多个 SOCKS5 端口及默认端口独立保留。每个端口自动识别监听 PID 和完整 EXE 路径，
  多个端口可以属于同一个核心进程；所有已识别核心统一通过绑定 `Proxy-代理` 的 direct 出口。
  该规则优先于应用、域名及默认端口，防止再次进入自己的 SOCKS 端口。
  代理网卡不可用时拒绝这些核心的流量，不使用控制器恢复通信的备用路径。
  界面显示端口对应的 PID、完整路径、网卡和识别失败原因；代理重启后重新识别并检测。
  端口身份未确认时拒绝送往该端口的流量，避免未知核心递归进入默认 SOCKS 端口。
- 勾选应用默认使用“默认直连 · ESIM-家宽”。应用、域名和 SRS 可选择任意一个角色直连，
  或选择默认/具体代理端口。更换角色网卡时，相应显式规则跟随角色一起更新，DNS 固定跟随直连角色，
  端口配置保持独立。没有 SOCKS5 端口的纯网卡配置也可以准备 TUN。
- 进程保护只依据 TUN 接管与当前配置是否生效。启动、停止、重启、进程退出或接管路由丢失时，
  每秒持续终止所选应用；TUN 接管确认后即可运行，不等待 DoH 或互联网连通性检测。
  启动确认包括本地 API、核心进程、TUN 接口地址及 IPv4/IPv6 默认接管路由；运行期间检查进程、接口和路由。
- DNS 和 DoH 健康检测固定通过 `ESIM-家宽`。默认 Cloudflare，失败时选择正常的 DNSPod，恢复后优先切回。
  两个查询并行、各最多 8 秒；完成一轮后间隔 1 分钟，保留手动检测入口。
  检测中、超时或两个都失败时保留当前 DNS 配置，本身不终止应用或重启 TUN。
- 只有 DNS 选择真正变化时生成并校验新配置：准备期间保留旧 TUN，校验通过后先终止所选应用，
  再重启核心，接管确认后解除保护。终止失败则取消变更并显示原因；恢复后不会自动启动应用。
  过期或取消的检测结果不能修改新配置；切换失败保留原选择，只有通过接管检查的回滚才算恢复。
- 出口网卡掉线时保留同一网卡的接口/地址绑定，连接会失败，不因掉线单独重启或终止应用，
  也不自动换到另一张网卡。恢复后的实际地址/名称变化、代理路径/规则变化会重新应用配置。
- 保护按勾选目录递归扫描得到的完整 EXE 路径匹配所有运行实例，并追踪子孙进程；不区分网卡或端口。
  终止前验证 PID 与创建时间，避免 PID 复用误判。TUN 接管后停止终止，应用由用户重新打开。
  这是进程保护，不是系统防火墙：终止前可能已有请求，未勾选应用不属于终止范围。
- “进程保护”页显示原因，以及最近 24 小时内最多 200 条终止结果、应用名、路径、PID 和时间。
  记录自动清理，提供“清空记录”按钮；清空只影响显示，保留进程追踪和失败重试。
  权限不足、身份无法确认、终止尚未完成都明确报告；同一失败会重试但不每秒刷屏。
- 控制器自己的下载使用独立的直接 HTTP 客户端，不依赖本地 SOCKS5 启动成功；TUN 配置优先放行
  控制器和核心的恢复通信。默认端口身份未知时，未匹配流量也会拒绝。
  勾选范围包含控制器、核心或上游代理时报告冲突，不终止这些恢复依赖。
- 应用发现仍使用 Windows Store/MSIX、卸载注册表、App Paths 和 Program Files。已选择但暂未发现
  的项目保留在列表，仍可取消选择；缓存已扫描的 EXE 路径，供下次启动扫描完成前进行保护。
- 业务规则优先级仍为应用、自定义域名、SRS、默认端口；没有端口时，未匹配流量使用默认网卡出口。
  指定网卡启动时不可用则对应规则拒绝，运行中离线则绑定出口连接失败，指定端口离线时连接失败，不回退到其他业务出口。
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

Profile schema 4 读取 schema 1/2/3，保留端口与规则。旧 PRIMARY 迁移为代理角色，旧 eSIM
迁移为直连角色；schema 3 保留默认直连网卡，只有可确定另一角色时才迁移代理网卡。
曾配置超过两张网卡而无法确定角色时要求手动选择；指向第三张网卡的旧规则保留并显示待重新选择，
完成调整前保持进程保护。后续保存清除旧字段；旧版程序不能编辑 schema 4。

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
假进程快照、假端口监听表、假终止结果和假 HTTP 返回；已有 SOCKS 测试只使用本地模拟服务器，不修改系统网络。
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
也不向浏览器/WebView 注入代理参数。上游代理应运行在普通本地代理模式，关闭其自身 TUN；
本项目只为经过所管理 TUN 的连接选择出口，无法改写主动绕过该 TUN 的外部套接字。
上游代理负责提供配置的本地 SOCKS5 端口；本项目将
流量交给所选出口，并用 sing-box API 展示实际连接状态。

配置与实施验收记录保存在本机
`C:\MyFile\ArcForges\Plan\windows-egress-controller-full-traffic-design.md`，边界说明见
[docs/traffic-migration-boundary.md](docs/traffic-migration-boundary.md)。sing-box 配置字段以
[官方文档](https://sing-box.sagernet.org/) 为准。

## License

[MIT](LICENSE)
