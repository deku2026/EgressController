# Protocol Compatibility — EgressController routing proxy

根据 Windows 真机与 loopback 测试结果整理（2026-08-19）。每项记录：验证方式 → 状态。
状态释义：`Supported`（有真机/loopback 证据）· `Unsupported`（V1 明确不当成支持，明确报错/不做）
· `Unverified`（未测，V1 limitation，不得当作支持）。

## 数据面：本地路由代理（LocalProxyServer :18080）

| 协议/特性 | 验证 | 状态 |
|---|---|---|
| HTTP CONNECT（HTTPS 隧道） | `curl -x 127.0.0.1:18080 https://icanhazip.com`（ESIM 与 upstream 两条路） | **Supported** |
| TLS 1.2/1.3 隧道内 | 经 CONNECT 通道 HTTPS 请求成功（TLS 端到端不被触碰） | **Supported** |
| HTTP/2 over CONNECT | CONNECT 是字节隧道，应用层交由浏览器/服务端协商 EP；未抓包验证 | Unverified |
| `wss://` WebSocket over CONNECT | CONNECT 隧道可承载任意字节；未专门验证 | Unverified（预期通则） |
| CONNECT 任意允许端口 | 解析器/连接器接受任意 host:port（loopback 测试覆盖） | Supported |
| CONNECT IPv6 目标 | 解析器支持 `[::1]:port`（单测）；真机 IPv6 目标未测 | parser Supported / live Unverified |
| plain HTTP GET/HEAD | `curl -x ... http://icanhazip.com` 真机；parser 单测 | **Supported** |
| plain HTTP POST + Content-Length | ForwardBody 内容长度转发（Proxy.Tests 覆盖） | Supported |
| chunked body | parser + 转发 path（Proxy.Tests 覆盖） | Supported |
| V1 单请求/close | 每请求决策 + `Connection: close` 后关闭（设计 + 测试） | Supported |
| `ws://` Upgrade（plain） | V1 未实现 upgrade 半交给上游 → **明确列为 V1 limitation** | Unsupported |

## 出口

| 出口 | 机制 | 状态 |
|---|---|---|
| ESIM DIRECT | interface-bound socket（DNS 接口钉 + bind-to-address）+ 实际接口绑定出口 | **Supported** |
| Upstream（默认） | HTTP-compatible proxy（Clash 7890）hostname 原样转发，不解析 | **Supported** |
| SYSTEM PROXY 接管 | 接管→OWNED(18080)→TestGuard 恢复 | **Supported** |
| fail-closed | upstream/ESIM 不可用 → 502，无 DIRECT fallback | **Supported** |

## 明确不做（V1 boundary）
- QUIC/UDP/raw-TCP 透明接管：只对进入 System Proxy 的 HTTP(S) 有 per-request 决策；
  完全绕过 proxy 的 UDP/QUIC 不在代理视野内（不引入 TUN/WFP driver）。
- TLS MITM / 证书注入。
- PAC/WPAD takeover（V1 只接管静态 WinINet，遇 PAC 判为 conflict）。
- `ws://` plain 升级半连接。

## 长稳/并发（待补，真机 >30min / 500 并发 / 故障注入）
本会话未跑长稳基准；协议 framing 的正确性用 loopback fake upstream/origin 覆盖。
剩余项入 Step 14 长稳 harness（连接数/失败/bytes/GC/working-set/socket count/drop counter）。
