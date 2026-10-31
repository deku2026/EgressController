# macOS policy compiler

`EgressController.Policy` targets portable `net10.0` / AnyCPU and has no dependency
on Windows, GUI lifetimes, process termination, networking, privileged services or
core execution. All APIs operate on supplied data. YAML uses an explicit node
tree mapper rather than a reflection serializer; JSON and protobuf emission are
compatible with NativeAOT.

## Configuration boundary

`MacPolicyDocument` is the configuration model. `PolicyYaml.Read` parses and
validates a complete version-1 DSL; `PolicyYaml.Write` preserves nodes, group
members, explicit selections, application paths, ordered rule sets and secrets.
The caller must store the DSL with private permissions: it contains subscription
URLs and node credentials. Parser diagnostics never echo source excerpts.

DNS and TUN preferences are typed document state, never compiler-option overrides:
`dns.upstream_ip` (`DnsPolicy.UpstreamIp`) stores the explicit non-loopback bootstrap
IP; `tun.name`, `tun.ipv4_cidr` and `tun.mtu` (`TunPolicy.Name/Ipv4Cidr/Mtu`) store
the TUN plan. Defaults are `1.1.1.1`, `utun10`, `198.18.0.1/30` and 1500.
MTU must be 1280–9000 for the dual-stack plan. Older version-1 DSL without these
sections receives the typed defaults, then writes them explicitly on save.
Automatic allocation and persistence of a genuinely available macOS utun number
is not implemented in this isolated delivery; a real helper must reconcile that
plan with the OS before takeover rather than assume `utun10` is available.

`providers` preserves each HTTP provider's stable ID, display name, subscription
`source_id`, URL and diagnostics. Provider payloads remain caller-supplied;
serializing the policy never downloads a provider or removes pending definitions.

`interfaces.home` and `interfaces.nonhome` refer to stable OS identities, resolved
by the macOS layer to current BSD names immediately before compilation. They must
resolve to two different interfaces. The compiler never chooses another adapter.
The default China exit is fixed to `nonhome`; the global exit must resolve to a
real proxy group. Global, enabled application and enabled domain/IP proxy routes
must reference a group, never a raw node. Manual groups may explicitly select
node members or nested groups in their declared order. A legacy saved node exit
is retained with `proxy.group-required` for explicit repair; it is never silently
wrapped or reassigned. Every actual proxy socket and DNS bootstrap query uses `nonhome`.

Application membership is an explicit saved `executables` array. `path` must be a
canonical absolute `.app` bundle root; every saved executable must be lexically
inside that exact root plus `/`. Shared interpreter/bin directories, sibling
bundles and prefix lookalikes cannot become members. Discovery separately verifies
physical paths and symlink containment. The root is never emitted as an Xray
directory wildcard. Missing old paths inside the bundle are retained. Outside
paths remain visible for explicit repair and block compilation instead of being
silently removed. A route edit changes only `via`.
Conflicting routes for the same exact executable are rejected instead of being
silently unified. No parent PID or descendant relationship is represented.

## Subscription import

`ClashSubscriptionImporter.Import(yaml, sourceId, providerPayloads?)` reads only
`proxies`, `proxy-groups`, and HTTP `proxy-providers`. It never fetches URLs.
The caller supplies fetched provider bodies keyed by provider name or ID.
Unfetched providers remain pending, and referencing groups cannot compile.
Clash DNS, TUN, rules, rule providers, controller and listening-port settings are
ignored with explicit diagnostics. YAML anchors, aliases and merge mappings are
supported; cyclic aliases, overly deep trees and input above 8 MiB are rejected.

Supported protocol mappings are VLESS (including the complete VLESS Encryption
string and REALITY), current VMess AEAD, Xray-supported Shadowsocks ciphers,
Trojan, SOCKS5 and HTTP. RAW/TCP, WebSocket, gRPC and HTTPUpgrade have explicit
mappings where their protocol/security combination is compatible. An enabled
Clash `smux`, an unsupported Shadowsocks plugin/mode, unknown protocol, unknown option, unsupported
transport detail, or incompatible security choice produces an item diagnostic.
Disabling `smux` maps to the default non-multiplexed outbound. REALITY's
`support-x25519mlkem768: true` maps to the pinned core's built-in support; forcing
that capability off cannot be represented and is rejected.

Shadowsocks `plugin: obfs` with `plugin-opts: {mode: http, host: ...}` uses
Xray's native RAW HTTP header authenticator; the typed DSL preserves `plugin`,
`plugin_mode`, `plugin_host` and optional boolean `tcp_fast_open`. Explicit
Clash `tfo: true`/`false` maps to `sockopt.tcpFastOpen`; absence remains absence.
The compiler sends HTTP/1.1 `GET /` with the exact camouflage Host (and server
port unless 80), `Upgrade: websocket` and `Connection: Upgrade`, then the unchanged
encrypted SS stream. This is HTTP obfuscation, not WebSocket framing. The initial
response header is removed and SS ciphertext remains unchanged. UDP remains
native SS UDP on the same explicitly bound non-home socket path. No plugin process,
local relay, listening port or additional core is introduced. Simple-obfs TLS mode,
unknown plugin fields, incompatible transport/security, header injection and
unrecognized plugins remain explicit errors; plugins are never stripped to plain SS.

This mapping follows the server's actual admission/decoding rules in
[simple-obfs](https://github.com/shadowsocks/simple-obfs/blob/master/src/obfs_http.c),
the [Mihomo HTTP obfuscation client](https://github.com/MetaCubeX/mihomo/blob/Meta/transport/simple-obfs/http.go),
and pinned Xray [HTTP header authentication](https://github.com/XTLS/Xray-core/blob/v26.10.29/transport/internet/headers/http/http.go),
[RAW configuration](https://github.com/XTLS/Xray-core/blob/v26.10.29/infra/conf/transport_method.go)
and [UDP dialer](https://github.com/XTLS/Xray-core/blob/v26.10.29/transport/internet/udp/dialer.go).
It preserves protocol admission and encrypted stream semantics; it does not
reproduce the plugin's randomized curl fingerprint, dynamic Content-Length or
Sec-WebSocket-Key, which its server does not validate. Live endpoint reachability
and the pinned native header reader's fragmented-header robustness still require
user acceptance; parsing/compilation tests do not claim a successful connection.

Shadowsocks 2022 accepts canonical standard Base64 keys: each AES-128 segment
decodes to 16 bytes; AES-256 and ChaCha segments decode to 32 bytes. AES preserves
colon-separated identity/user key chains; ChaCha rejects multiple keys. The
client deliberately rejects the pinned core's raw-key fallback and unpadded or
whitespace-bearing input. Validation follows the pinned
[key parser](https://github.com/XTLS/Xray-core/blob/v26.10.29/proxy/shadowsocks_2022/kdf.go),
[cipher lengths](https://github.com/XTLS/Xray-core/blob/v26.10.29/proxy/shadowsocks_2022/cipher.go)
and [outbound multi-key restriction](https://github.com/XTLS/Xray-core/blob/v26.10.29/proxy/shadowsocks_2022/outbound.go).

Unselected incompatible items are retained for display and do not prevent a
valid policy from compiling. Selecting an incompatible node or a chain that
reaches one fails compilation. Import success therefore means a structurally
valid subscription was retained, rather than that every contained node can run.
Stable node IDs hash source/endpoint/protocol identity rather than list position
or display name. Credentials are never included in diagnostic messages or
object `ToString()` output.

`GroupSelector.Resolve` owns group semantics outside Xray:

- `select` requires an explicit saved member; it never chooses the first item.
- `url-test` chooses the fastest fresh successful probe and retains the selected
  member while within its tolerance.
- `fallback` chooses the first available member in the saved declaration order.
- Stale, future-dated, missing, failed or invalid probes do not imply availability.
- Probes must identify the exact group's test URL; observations from another
  endpoint cannot select a node for this group.
- Nested groups and `dialer-proxy` chains are checked for cycles. DIRECT/REJECT
  cannot resolve a proxy exit. Automatic groups containing such members cannot
  silently change those members into proxies.

The runtime must obtain probes through the specified nonhome-bound proxy chain,
then safely recompile/apply when an automatic selection actually changes. The
policy library does not perform reachability checks or change protection state.

## Xray generation

`XrayConfigCompiler.Compile(document, options)` returns JSON only after validation
and group resolution succeed. The baseline is Xray `v26.10.29`, containing
macOS process identification and the interface-binding fixes. It emits one native
TUN inbound and no HTTP/SOCKS listener or network control listener. The initial
outbound is a blackhole, so accidental unmatched traffic cannot default to direct.

Routing order is system DNS and local-network handling, saved executable rules,
ordered user domain/IP sets, China domains, China IPs, then all TCP/UDP to the
mandatory global proxy. Domain and IP conditions for a single collection become
separate rules with the same exit, preserving OR semantics. `IPOnDemand` is
intentional: `IPIfNonMatch` would never reach its second pass after an explicit
catch-all. Sniffing uses `routeOnly` and never replaces the actual destination.
Fake-IP is not configured. `autoOutboundsInterface` is explicitly the empty string:
[the pinned core parser](https://github.com/XTLS/Xray-core/blob/v26.10.29/infra/conf/tun.go)
only enables its automatic controller when this property is omitted. Explicit
per-outbound interface bindings protect every socket; disabling the global
controller also prevents a missing nonhome interface from blocking otherwise
valid home-bound connections. Never omit this field or replace it with `auto`.
macOS DNS takeover/restoration is the native helper's
responsibility; Xray's TUN `dns` field is Windows-only.

External YAML sources require `XrayCompileOptions.PreparedRuleSources` entries
with verified DAT filenames/tags. The compiler rejects unprepared sources rather
than silently omitting them. Default China resources are `geosite.dat:cn` and
`geoip.dat:cn`; preparation must validate both before runtime starts.

## Meta YAML and DAT

`GeoRuleCompiler.Parse` accepts raw Meta `payload` YAML with explicit Domain, Ip
or Classical data kind. Exact names, `+.` suffixes, subdomain-only dot patterns,
whole-label star patterns, classical domain keywords/regex and IPv4/IPv6 CIDRs
preserve their distinct semantics. Unsupported conditions and per-rule
`no-resolve` are rejected. There is no MRS/SRS decoder.

`GeoRuleCompiler.Compile` emits deterministic wire-compatible GeoSiteList and
GeoIPList data using the official protobuf field numbers. Domain enums are
keyword=0, regex=1, suffix=2 and full=3; CIDRs contain network-masked 4/16-byte
addresses. Duplicate values are removed.

Codes are written **uppercase as the first field of each entry**, with at most
127 ASCII bytes. This follows the pinned core's actual loading contract:
[rule parsing uppercases the requested code](https://github.com/XTLS/Xray-core/blob/v26.10.29/common/geodata/rule_parser.go),
and [the streaming loader compares an exact first-field byte prefix](https://github.com/XTLS/Xray-core/blob/v26.10.29/common/geodata/geodat_loader.go).
A valid protobuf with a lowercase code, reordered code field or multi-byte code
length can still be unreadable by that loader. An independent seek-prefix test
covers this distinction. Local network routing uses explicit CIDRs, avoiding an
implicit `geoip:private` dependency on an unprepared `PRIVATE` DAT tag.

Exact/suffix values must be hostnames, not URLs, paths, IP literals or wildcard
expressions. The same check applies to DSL, parsed payloads and manually built
rule objects. Internationalized names compile to lowercase ASCII IDNA names;
full, suffix, keyword and regex matching keep their separate protobuf semantics.

`GeoDatInspector.HasTag(bytes, tag, kind)` independently validates a complete
in-memory DAT before a runtime generation trusts an `ext:` reference. It rejects
input above 64 MiB, truncated/unknown protobuf fields, duplicate tags or scalar
fields, malformed UTF-8, invalid domain enums/values, empty entries, invalid CIDR
byte lengths/prefixes or unmasked host bits. A matching tag needs at least one
valid item of the requested kind. Inverse IP inventories are rejected because
the current policy compiler models positive collections only. No filesystem or
core execution is involved.

`PublishAsync` stages and flushes both DAT files, verifies their contents,
synchronizes the staging directory, renames the complete generation and atomically
replaces a single `CURRENT` pointer. On macOS/Linux, containing directories are
synchronized after the generation rename and pointer replacement. A cancellation,
digest mismatch or corrupt existing generation leaves
the previous pointer intact. Consumers must resolve one generation and read
both files from it, never mix independently replaced files. Old generations are
retained for rollback. File and directory synchronization preserves the OS's
durability boundary; this does not claim immunity from hardware or filesystem
failure.

## Production resource preparation

`ProductionResources` fetches Clash subscriptions and HTTP providers as node
sources only. Production URLs must use HTTPS. Each operation captures one
non-home `InterfaceBinding`; redirects, DNS queries and target sockets keep that
same binding. `BoundDnsResolver` uses the profile's literal DNS upstream through
bound UDP, retrying a truncated response through bound TCP. It validates the
transaction, question and bounded alias chain and never uses the system resolver.
`BoundHttpsResourceTransport` binds before connecting, disables system proxies,
cookies and automatic redirects, rejects HTTPS downgrades, limits redirects and
response bytes, and retains normal certificate validation. Binding failure aborts
the connection. Network exceptions are sanitized without embedding resource URLs
or credentials. Optional exchange/HTTP factories are injection seams for tests.

The public Meta catalog is read from the official repository's
[`meta` commit endpoint](https://api.github.com/repos/MetaCubeX/meta-rules-dat/commits/meta)
and two complete Git subtree indexes under `geo/geosite` and `geo/geoip`.
Truncated indexes are rejected. A single verified commit pins every raw URL;
the Git blob identity is checked before parsing. Domain collections use
`geo/geosite/classical/<name>.yaml` to retain full, suffix, keyword and regex
conditions; IP collections use `geo/geoip/<name>.yaml`. No MRS, SRS or precompiled
DAT is downloaded. DSL references remain `geosite/<name>.yaml` and
`geoip/<name>.yaml`; names may include the catalog's safe `@`, `!` and `+`
characters. Unknown sources require repair. Preparation always includes both
China defaults plus requested sources and returns parsed collections, exact DSL
bindings, and a single generated `geosite.dat`/`geoip.dat` pair for validation and
atomic runtime generation publication. Explicit refresh replaces validated
cache entries; normal preparation uses valid cached content first.

`PrivateDiskResourceCache` hashes resource identities into filenames and stores
each payload together with its SHA-256 in one atomically replaced record. New
directories are `0700`, files are `0600`, and symlink/public storage is rejected.
Files and containing directories are synchronized before publication. Corrupt
cache content is unavailable and may be fetched again through the chosen binding.

`XrayCoreReleaseManager` defaults to the official
[v26.10.29 Apple Silicon release](https://github.com/XTLS/Xray-core/releases/tag/v26.10.29).
The archive and accompanying `.dgst` each have independently pinned SHA-256
identities; the checksum's `SHA2-256` must also match the archive. ZIP paths,
duplicates, symbolic links and expanded-size limits are checked before extracting
only the ARM64 Mach-O `xray`. The private executable is published with `0700`
permissions, rechecked against the cached verified archive and never run by this
manager. A prepared core is distinct from an installed authorized helper or a
successful TUN takeover.
The extracted official ARM64 executable is independently pinned to SHA-256
`2d4bfa91aae5f90dee691cb5eb794316510c7f6006973eb46e60d7978bb789aa`;
the privileged installer must enforce that shared pin before any core execution,
rather than accepting a digest supplied by an unprivileged installation plan.

Node measurements require the actual selected protocol and dialer chain. The
pinned core's [observatory implementation](https://github.com/XTLS/Xray-core/blob/v26.10.29/app/observatory/observer.go)
can issue a GET through an outbound without an inbound proxy listener or TUN;
it reports measured duration in its pinned info log format. A worker must use
an isolated, explicitly bound configuration, accept only its known outbound tags,
bind each result to the exact test URL, and report timeout/unavailable when no
verified observation arrives. Endpoint TCP-connect timing is not a substitute
for protocol reachability, and fixture observations are never production results.

Startup uses `RequiredGroupProbes.Missing(document, options)` and required-only
observatory mode. Only automatic groups reachable from the global exit and
enabled application/rule exits contribute requests. Manual groups follow their
explicit selected member; unrelated automatic groups and unselected branches do
not trigger subscription-wide probing. Existing fresh observations match both
node and exact test URL; completed failed observations remain unavailable until
expiry or explicit retest. Nested automatic groups and automatic dialer
dependencies are measured in phases, with `GroupSelector` choosing the resolved
parent candidate between phases. Required-only results include the caller's
cached observations plus new measurements, without mutating the input. Explicit
user-triggered full probing remains a separate mode. Internal node probes use a
temporary explicit selector group, preserving the public group-only exit rule.

## Validation

All Policy tests use synthetic data and temporary storage. They never execute
Xray, start TUN, change DNS, enumerate live processes or fetch subscriptions.
`POLICY_PRIVATE_FIXTURE` optionally points to a private local subscription for an
in-memory compatibility check; its contents are neither copied nor printed.
Run `dotnet run --project tests/EgressController.Policy.Tests -- --progress off`.

Official contracts:

- [Xray TUN](https://xtls.github.io/config/inbounds/tun.html)
- [Xray process and routing rules](https://xtls.github.io/config/routing.html)
- [Xray VLESS](https://xtls.github.io/config/outbounds/vless.html)
- [Xray REALITY](https://xtls.github.io/config/transports/reality.html)
- [Pinned protobuf schema](https://github.com/XTLS/Xray-core/blob/v26.10.29/common/geodata/geodat.proto)
- [Meta domain wildcard implementation](https://github.com/MetaCubeX/mihomo/blob/Meta/component/trie/domain.go)
