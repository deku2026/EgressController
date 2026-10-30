# Egress

Mac 优先的原生 Avalonia/.NET 客户端，以 Xray-core 为唯一网络内核。

应用、域名集合和 IP 集合共用三个出口：**家宽直连 / 非家宽直连 / 指定节点或分组**。中国大陆默认非家宽，其他流量必须使用全局代理；代理服务器也严格经非家宽连接，绑定失败不换网卡。

![macOS 首次设置](docs/images/mac-production-onboarding.png)

## 运行

**双击打包后的 `Egress.app`，默认进入生产模式。** 打开界面会读取实际 App、网卡和保存的配置；不会仅因打开窗口而启动 TUN。

首次使用：选择两张不同网卡 → 输入 Clash HTTPS 订阅 → 明确选择全局节点/分组 → 搜索并勾选应用 → 点击“应用并连接”。需要时出现 macOS 管理员授权框，安装受限助手并启动 Xray。停止或退出客户端会恢复网络设置。真实接管请使用 NativeAOT `.app`，不要以 root 身份运行 GUI。

`--preview` 才进入隔离 UI；示例 URL 和示例测速只出现在预览中：

```sh
dotnet run --project src/EgressController.Desktop -- --preview
```

生产模式会经所选非家宽网卡获取订阅/provider、规则及固定版本核心，优先验证本地缓存。DNS 解析和 HTTPS socket 均显式绑定该接口，无系统代理或默认接口回退。不支持的节点插件会保留并明确标识，不能静默改变语义。

## 开发验证与打包

需要 Apple Silicon macOS、.NET 10 SDK、Xcode Command Line Tools 和 CMake：

```sh
python3 build/Verify-Mac.py
```

**开发测试只用 mock。** 该命令运行注入资源/进程/权限适配器的测试、纯 C ABI 探针、普通/紧凑 UI 截图与交互检查，再打包 GUI 和助手。只执行包内显式 `--mock-smoke`，从不启动真实 TUN、安装服务、修改系统 DNS/路由或终止真实应用。

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
