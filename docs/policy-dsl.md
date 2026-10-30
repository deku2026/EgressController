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
real proxy. Every actual proxy socket and DNS bootstrap query uses `nonhome`.

Application membership is an explicit saved `executables` array. `path` identifies
the app for discovery and display; it is never emitted as an Xray directory
wildcard. Missing old paths are retained. A route edit changes only `via`.
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
Clash `smux`, Shadowsocks plugin, unknown protocol, unknown option, unsupported
transport detail, or incompatible security choice produces an item diagnostic.
Disabling `smux` maps to the default non-multiplexed outbound. REALITY's
`support-x25519mlkem768: true` maps to the pinned core's built-in support; forcing
that capability off cannot be represented and is rejected.

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
