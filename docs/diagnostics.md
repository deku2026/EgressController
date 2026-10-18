# 日志与存储

侧栏的“打开日志目录”始终可用；“运行事件”显示保护日志完整路径、关键事件及详情，可复制诊断信息。
当前状态与历史记录独立，恢复后不会因为旧错误保持红色警告。

| 文件 | 内容 |
| --- | --- |
| 便携目录 `data/logs/protection.log` | TUN 启停/重启原因、保护状态变化、进程终止结果、守护与保护故障 |
| MSIX `%LOCALAPPDATA%/EgressController/data/logs/protection.log` | 同上；安装目录只读，数据写入用户目录 |
| `%LOCALAPPDATA%/EgressController/logs/startup.log` | 主程序初始化与未处理异常，启动失败窗口提供目录入口 |
| `%ProgramData%/EgressController/Guard/<SID-hash>/logs/guard-service.log` | 独立守护状态、接管检查、终止与服务生命周期 |
| `%LOCALAPPDATA%/EgressController/Guard/<服务名>/logs/guard-tray.log` | 小托盘服务管理事件 |

默认文件日志不收集逐连接输出或统计采样错误。sing-box 输出仅保留在有界内存窗口，位于“运行事件”的高级诊断中；不会创建新的 `sing-box.log`。旧版本留下的日志不自动删除。

每个常规日志最大约 5 MB，保留一份 `.1` 轮转文件；重复失败合并计数并限制写入频率，API 凭据脱敏。无法写文件时保留界面中的错误和复制功能，日志故障不改变保护结论。

连接/流量是本地 HTTP `/connections` 的每秒快照。单次失败显示统计不可用并等待下一次采样，保留已有记录；不触发网络重测、TUN 重启或终止。主程序与守护仍使用经过身份验证的异步命名管道，不依赖统计 API。

`data/profile.json` 保存网卡、代理端口、分流规则和固定 EXE 清单。`data/usage.db` 保存本地流量估算；`data/runtime` 保存生成配置。内核与规则位于 `ruleset`。
守护的 SQLite 名单/记录、登记信息和待同步名单位于 `%ProgramData%/EgressController/Guard/<SID-hash>`。
主程序与守护终止记录各保留最近 24 小时、最多 200 条，清空记录不会关闭保护。
