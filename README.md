# Egress

Mac 优先的原生 Avalonia/.NET 客户端，以 Xray-core 为唯一网络内核。

应用、域名集合和 IP 集合共用三个出口：**家宽直连 / 非家宽直连 / 指定代理分组**。中国大陆默认非家宽，其他流量必须使用全局代理；代理服务器也严格经非家宽连接，绑定失败不换网卡。

![macOS 首次设置](docs/images/mac-production-onboarding.png)

## 运行

**双击打包后的 `Egress.app`，默认进入生产模式。** 打开界面会读取实际 App、网卡和保存的配置；不会仅因打开窗口而启动 TUN。

首次使用：选择两张不同网卡 → 粘贴包含实际节点的本地 Clash YAML → 明确选择全局分组及成员 → 搜索并勾选应用 → 点击“应用并连接”。安装包自带固定版本 Xray；需要时出现 macOS 管理员授权框。连接默认启用 TUN，确认本地代理后再下载缺失规则，最后应用完整分流。停止、退出或准备失败时恢复连接前的网络状态。真实接管请使用完整 NativeAOT `.app`。

`--preview` 才进入隔离 UI；示例 URL 和示例测速只出现在预览中：

```sh
dotnet run --project src/EgressController.Desktop -- --preview
```

在线订阅、provider 和规则下载统一经 **Egress 自带 Xray → 所选非家宽网卡 → 全局代理分组节点**，不使用系统代理或直连回退。优先使用已验证缓存；默认本地 SOCKS5 端口为 `17890`，可在“设置 → 网络与 TUN”修改。首次没有节点时使用本地 YAML 导入，连接后再在线更新。SS 的 `obfs/http` 与 TFO 保留为 Xray 原生传输。

## 分组与连接活动

应用、规则集合及全局代理都只选择分组；节点是组内成员。Clash 的 `ServiceProxies` 等 YAML 锚点按原成员顺序展开，手动组不会自动选第一个节点。旧配置中直接指向节点的出口保留并提示改选分组。启动只测速当前策略使用的自动组及其依赖，其他节点不会拖慢连接。

总览中的“连接活动”提供应用筛选、搜索排序、最近/确认活跃、暂停显示及下方详情。列表更新保留选择、焦点和滚动位置。生产数据来自助手解析的实际 Xray 输出；访问记录无法证明连接仍存活，也不提供逐连接字节或完整 PID/路径，缺失字段显示未知，不读取请求内容。

![连接活动（隔离合成样本）](docs/images/mac-connections.png)

连接流程显示检查策略、内置 Xray、授权助手、建立 TUN、准备规则、应用分流六个阶段。规则准备期间普通流量暂缓；端口占用、TUN 占用、网卡不可用、授权失败和接管超时都有明确提示与重试入口。

应用搜索支持名称、Bundle ID 和路径；Safari 的系统 Cryptex 应用链接会正确识别并去重。节点搜索支持名称、协议、服务器和所属分组。

![Safari 搜索（隔离样本）](docs/images/mac-safari-search.png)

## 开发验证与打包

需要 Apple Silicon macOS、.NET 10 SDK、Xcode Command Line Tools 和 CMake：

```sh
# 构建机获取固定发布归档；不会启动 Xray/TUN。
gh release download v26.10.29 --repo XTLS/Xray-core --pattern Xray-macos-arm64-v8a.zip --dir artifacts/core
python3 build/Verify-Mac.py --version 0.1.1
```

**开发测试只用 mock。** 该命令运行注入资源/进程/权限适配器的测试、纯 C ABI 探针、普通/紧凑 UI 截图与交互检查，再打包 GUI、助手及经归档/可执行文件双重 SHA-256 校验的 Xray。缺少内核时拒绝生成安装包。只执行包内显式 `--mock-smoke`，从不启动真实 TUN、安装服务、修改系统 DNS/路由或终止真实应用。

产物：`artifacts/package/Egress.app`、`EgressController-osx-arm64.zip` 和 `SHA256SUMS.txt`。使用本地 ad-hoc 签名，未做 Developer ID 签名或公证。本轮自动验收不代表已实测双网卡 TCP/UDP/IPv4/IPv6 出口。

只检查 UI：

```sh
dotnet run --project tools/EgressController.MacPreview -- --screenshots artifacts/mac-ui
```

## 配置与文档

强类型 YAML DSL 是唯一事实来源；Xray JSON/DAT 是生成产物。应用路由使用显式保存的 Bundle 内可执行路径，修改出口保留旧清单。Clash 只提供节点、分组和 provider，不导入其端口、DNS、TUN 或路由。

- [DSL 与配置编译](docs/policy-dsl.md)
- [生产运行、持久化与恢复](docs/mac-runtime.md)
- [macOS C ABI 与特权助手](docs/macos-native.md)
- [实施和验收边界](docs/mac-implementation-plan.md)

旧 Windows 代码隔离在 `EgressController.Windows.slnx`，不参与默认产品构建。

## Rider 运行与调试

打开 `EgressController.slnx`。普通 Build 会通过 CMake 自动编译当前架构的原生桥接库，并将 `libEgressMacNative.dylib` 复制到 Desktop、助手及引用项目的输出目录；无需设置 `DYLD_LIBRARY_PATH` 或手工复制。首次构建需要上面的 CMake 与 Xcode Command Line Tools；从 Finder 打开的 Rider 也能发现 Homebrew 标准目录里的 CMake。

右键 `src/EgressController.Desktop/Properties/launchSettings.json` → **Generate Configurations**，选择生成的运行配置（[Rider 官方说明](https://www.jetbrains.com/help/rider/Running_LaunchSettings.html)）：

| 配置 | 程序参数 | 环境变量 | 用途 |
| --- | --- | --- | --- |
| `Egress · Production` | 空 | 不设置 `EGRESS_MOCK_ONLY` | 默认生产界面，读取实际应用、网卡和保存的配置 |
| `Egress · Preview` | `--preview` | `EGRESS_MOCK_ONLY=1` | 隔离 UI 开发与截图 |

已有的 `.NET Project` 配置也可继续使用：Project 选 `EgressController.Desktop`，framework 选 `net10.0`，Runtime 选 Automatic，保留自动计算的 Exe path / Working directory，并保留启动前 **Build**。修改原生 C/Objective-C 文件后若 Rider 没有触发构建，使用 **Rebuild**。

Rider 普通构建适合调试界面及非特权生产流程，不附带可安装的 NativeAOT 助手。实际“应用并连接”请运行 `python3 build/Package-Mac.py` 生成的 `artifacts/package/Egress.app`；仅打开界面不会启动 TUN。自动开发验证仍只用显式预览、mock 或纯 ABI 探针。
