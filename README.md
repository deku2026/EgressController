# EgressController

EgressController 是一个纯 Windows 的全流量 TUN 控制器。sing-box 负责唯一业务数据面；C#
UI 负责生成和校验 JSON Profile、准备 core/SRS、管理 ElevatedHost 生命周期，并读取真实
Clash API 诊断。

## 功能

- 扫描 Win32、注册表安装项、快捷方式和 MSIX/AppX，并显示应用图标与可路由 EXE 数量。
- 应用选择、MetaCubeX `sing` 分支 SRS 和手工域名组成同一个 eSIM 路由集合。
- 未命中项固定进入本机 SOCKS5，默认 `127.0.0.1:7890`；控制面下载也显式经此 SOCKS5。
- Managed core 自动下载、版本/摘要/feature/check 校验；System core 使用用户选择的绝对路径。
- 通过最小 ElevatedHost 启动 sing-box TUN，App 与普通 Launch 始终保持普通权限。
- 连接页消费真实 sing-box connections/traffic/logs API，并提供连接关闭、历史清理和 DNS 诊断。

软件不修改 Windows 全局代理，不监听 C# 业务代理端口，不向应用注入代理环境变量或浏览器
参数，也不实现订阅、节点、selector、provider 或 YAML 配置。

## 系统要求

- Windows 10 2004（build 19041）或更新版本；推荐 Windows 11 x64。
- 从源码构建需要 `global.json` 指定的 .NET SDK。
- NativeAOT 需要 Visual Studio Build Tools 的 Desktop development with C++ 工作负载。
- 规则/core 下载失败时，应用使用 Profile 的显式 SOCKS5 端口；默认端口为 7890。

## 构建与测试

```powershell
dotnet restore EgressController.slnx
dotnet build EgressController.slnx -c Release --no-restore
dotnet test EgressController.slnx -c Release --no-restore -- --minimum-expected-tests 1 --progress off
```

启动开发构建：

```powershell
dotnet run --project ./src/EgressController.App/EgressController.App.csproj -c Release
```

默认测试不访问网络。实时规则/SRS/已安装 sing-box 检查显式启用：

```powershell
$env:EGRESS_LIVE_RULES_TEST = '1'
dotnet test ./tests/EgressController.Rules.Tests/EgressController.Rules.Tests.csproj -c Debug --no-restore -- --minimum-expected-tests 1 --progress off
dotnet test ./tests/EgressController.SingBox.Tests/EgressController.SingBox.Tests.csproj -c Debug --no-restore -- --minimum-expected-tests 1 --progress off
```

## NativeAOT 与发布

真实发布验证使用 `PublishAot=true`，不能用 JIT 运行替代：

```powershell
dotnet publish ./src/EgressController.App/EgressController.App.csproj -c Release -r win-x64 -p:PublishAot=true --self-contained true
dotnet publish ./src/EgressController.ElevatedHost/EgressController.ElevatedHost.csproj -c Release -r win-x64 -p:PublishAot=true --self-contained true
```

完整打包仍可使用：

```powershell
./build/Package.ps1 -Version 0.1.0
```

## 数据与安全边界

Profile、core、规则 catalog、SRS 和 sing-box state 保存在 `%LOCALAPPDATA%\EgressController`。
仓库不包含连接日志、应用清单、本机路径、代理凭据或签名材料。TUN 是否接管 UDP、QUIC、
原始 TCP 和 IPv6 由 sing-box 与 Windows 网络栈共同决定，C# 不单独重实现网络转发。

配置边界见 [docs/traffic-migration-boundary.md](docs/traffic-migration-boundary.md)，
能力矩阵见 [docs/protocol-compatibility.md](docs/protocol-compatibility.md)。

## License

[MIT](LICENSE)
