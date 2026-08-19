# EgressController

[![CI](https://github.com/deku2026/EgressController/actions/workflows/ci.yml/badge.svg)](https://github.com/deku2026/EgressController/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

EgressController 是一个纯 Windows 的 HTTP/HTTPS 分流控制器。它把当前用户的 Windows
System Proxy 接到本地路由代理，再按应用和域名把请求送往指定的 eSIM 网卡或普通上游代理。
它不安装 TUN/WFP 驱动，也不做 TLS MITM。

## 功能

- 自动扫描经典 Win32 应用、注册表安装项、快捷方式和 MSIX/AppX 安装包，并显示应用图标。
- 手动添加一个 EXE 后递归扫描其目录中的可执行文件，作为同一应用的可识别组件。
- 启动选中的应用并跟踪实际出现的已扫描进程；匹配到的连接进入 Managed 路由。
- 域名可按 MetaCubeX `meta-rules-dat` 规则集或手工域名选择 eSIM 出口。
- 规则从官方仓库实时获取，固定到已解析的 commit 并缓存在本机；不需要克隆规则仓库。
- 默认上游为 `127.0.0.1:7890`，本地路由代理监听 `127.0.0.1:18080`。
- 连接日志支持搜索、关闭全部连接和查看单条连接详情。
- eSIM 掉线时关闭现有连接并拒绝新连接；不会静默回落到普通上游。
- System Proxy 使用所有权记录和 compare-before-restore，避免覆盖用户或其他软件的新状态。

## 系统要求

- Windows 10 2004（build 19041）或更新版本；推荐 Windows 11 x64。
- 从源码构建需要 `global.json` 指定的 .NET SDK。
- NativeAOT 需要 Visual Studio Build Tools 的 **Desktop development with C++** 工作负载。
- 构建 MSIX 需要带 `MakeAppx.exe` 的 Windows 10/11 SDK。

## 构建与测试

```powershell
dotnet restore EgressController.slnx
dotnet build EgressController.slnx -c Release --no-restore
./build/Invoke-Tests.ps1 -Configuration Release -NoBuild
```

启动开发构建：

```powershell
dotnet run --project ./src/EgressController.App/EgressController.App.csproj -c Release
```

测试使用 xUnit v3 与 Microsoft.Testing.Platform。需要真实规则 corpus 的可选测试通过
`EGRESS_RULES_ROOT` 指向 `meta-rules-dat` 根目录或 `geo/geosite`；需要联网和显式上游的测试
只有在 `EGRESS_LIVE_RULES_TEST=1` 时才运行。

## NativeAOT 与 MSIX

```powershell
./build/Package.ps1 -Version 0.1.0
```

输出位于 `artifacts/package`：

- `EgressController-win-x64.zip`：自包含 NativeAOT 便携包。
- `EgressController-x64.unsigned.msix`：未签名的 MSIX 结构验证包，不可直接用于生产安装。
- `SHA256SUMS.txt`：发布文件校验值。

若传入可信 PFX，脚本会生成可安装的 `EgressController-x64.msix` 和具有自动更新设置的
`EgressController.appinstaller`：

```powershell
$env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD = '<password>'
./build/Package.ps1 -Version 1.0.0 -CertificatePath C:\secure\egress-controller.pfx
```

证书 Subject 必须与包 Publisher 完全一致，默认是 `CN=ArcForges`。证书和密码不得提交。
若将来改用 Microsoft Store 分发，应同时替换包 Identity/Publisher 为 Store 保留值。

## CI 与发布

- 每次 push 和 pull request 都会在 `windows-latest` 上还原、编译、运行测试，并生成
  NativeAOT ZIP 与未签名 MSIX 工件。
- 推送 `v*` 标签会自动创建 GitHub Release。
- 仓库配置 `WINDOWS_SIGNING_CERTIFICATE_BASE64` 和
  `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` 后，标签发布会签名 MSIX 并附带 App Installer；
  未配置签名时仍发布便携 ZIP 和明确标记的 unsigned MSIX。
- 已签名版本可直接下载
  [`EgressController.appinstaller`](https://github.com/deku2026/EgressController/releases/latest/download/EgressController.appinstaller)
  安装；文件使用稳定的 latest URL 检查后续更新。

## 数据与安全边界

运行状态和规则缓存保存在 `%LOCALAPPDATA%\EgressController`。仓库不包含连接日志、应用清单、
本机路径、代理凭据或签名材料。EgressController 只控制进入 Windows HTTP/HTTPS System Proxy
的流量；绕过系统代理的 UDP、QUIC 和原始 TCP 不在其控制范围内。

协议支持范围见 [docs/protocol-compatibility.md](docs/protocol-compatibility.md)，WFP 用户态探针结论见
[docs/probes/connection-policy-CONCLUSION.md](docs/probes/connection-policy-CONCLUSION.md)。

## License

[MIT](LICENSE)
