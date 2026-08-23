# 全流量 TUN 迁移边界

这份清单与 `Plan/windows-egress-controller-full-traffic-design.md` 的 Step 00 对齐，作为后续提交的范围门槛。

## 保留

- Avalonia 四页 UI、窗口/托盘/单实例和普通权限启动。
- Windows 应用发现、递归 EXE inventory、图标、搜索、手工添加、启动会话和 Job Object。
- 网卡稳定标识、当前地址族解析、连接详情/日志的有界 UI 缓冲。
- 原子 JSON 文件基础设施。

## 替换

- C# 业务数据面替换为 sing-box TUN；C# 只生成配置、准备 core/SRS、管理生命周期并读取 API。
- 应用勾选和 SRS/手工域名勾选合并为同一个 eSIM 集合；未命中流量固定进入 `127.0.0.1:7890`。
- RouterHost 逐步收敛为 AppController + SingBoxService。
- 域名 list 解析逐步替换为 MetaCubeX `sing` 分支的本地 SRS。
- 首次 UAC 只启动最小 ElevatedHost；UI 与普通 Launch 保持普通权限。

## 最终删除

- `EgressController.Proxy`、`LocalProxyServer` 和 C# HTTP/CONNECT 转发。
- Windows System Proxy manager/watcher、恢复状态和代理注入环境变量/浏览器参数。
- C# list parser/matcher/routing engine，以及按 PID/LaunchSession 决定网络出口的逻辑。
- 与节点、订阅、selector、proxy provider 无关的 sing-box API 端点。

## 固定路由契约

sing-box route rule 顺序不可由 UI 调整：

1. `sniff`
2. `hijack-dns`
3. 上游 SOCKS5 owner 的完整 `process_path` → `primary-direct`
4. 应用递归 EXE 的 `process_path` → `esim-direct`
5. 已选 SRS 和手工域名 → `esim-direct`
6. `final` → `upstream-socks`

稳定 tag 只有 `tun-in`、`primary-direct`、`esim-direct`、`upstream-socks` 和 `dns-doh`。

每个后续步骤必须保持：Profile 只保存用户意图；owner、ifIndex、源地址、API secret 和 PID 都是运行时数据。
