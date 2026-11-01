# Subscription groups, latency and live routing

The typed policy/DSL is the shared source for onboarding, application/rule pickers, group cards, compilation and runtime updates. Applications never acquire a node identity or a new executable inventory from a group edit.

## Import and retained groups

Clash import preserves declared membership/order, provider membership, nested groups, URL, interval, tolerance and icon metadata. New `select` groups initialize from a valid `default-selected`, otherwise their first declared member. A node-only subscription receives a stable default `url-test` group. Existing unresolved saved choices are not guessed or reset.

`retained: false` removes a group from normal workspace/picker choices, not from the graph. Default groups and groups referenced by saved application/rule routes cannot be unchecked in the UI. Nested dependencies remain usable. Refresh preserves retention, manual choices and explicit mode overrides; an explicitly added DIRECT option also survives refresh. Removed subscription members stay visible as unresolved metadata for repair.

Each group can explicitly select DIRECT, mapped to the configured non-home interface. REJECT remains a blocked choice when declared. There is no automatic direct substitute for failed proxy probes. Neither builtin can be a node's dialer chain. Unsupported protocols and dynamic group filters/options retain actionable diagnostics rather than being silently approximated.

The native card list uses shared Inter/CJK typography, semantic tokens and adaptive product artwork. Known product names/icon filenames map to bundled icons; unknown groups use a consistent fallback. Importing an arbitrary icon URL does not perform a hidden UI network fetch. Onboarding chooses retained/default groups without a separate download-node page.

## Observations and automatic selection

Explicit group tests traverse that group's leaves with its configured URL. “全部测速” tests retained groups. The default endpoint is `https://www.gstatic.com/generate_204`. Observations carry actual timestamps and URL identity; missing, stale and failed results never become 0 ms. Tests run bounded unprivileged Xray observatory processes with no TUN or listener, bound to non-home. This is latency measurement, not a throughput/traffic estimate.

A separate policy timer checks the already-applied automatic groups at their configured intervals. `url-test` uses fresh successful observations and tolerance; `fallback` uses declared order. Explicit measurements immediately reevaluate applied automatic selections too; subscription import/preview hydration never auto-applies a configuration. Manual member selection saves an explicit `select` override. Background updates operate on the applied snapshot, cannot overwrite pending edits, and yield to user operations through operation-owned cancellation. Probe failure retains current routes and does not produce a guardian/network-protection failure. Status/telemetry failures are not selection inputs.

## Automatic rule-download exit

Connection establishes the authorized TUN before group probes or remote rule preparation. Rules first attempt a cache-only read with integrity/format validation. If resources are missing, probe non-DIRECT leaves of the default group with its test URL, prefer its resolved node when successful, otherwise choose the fastest successful leaf. No available proxy produces `resources.no-proxy`; there is no direct/system-proxy fallback. This works even when normal default-group traffic explicitly selects DIRECT.

The selected download node is transient compile input, not a second saved group choice. Downloads retain the scoped loopback SOCKS port and DefaultProxyDownloadTransport. Subscription/provider acquisition remains the separate direct HTTPS/non-home path. Cached resources need neither a download probe nor an available download node.

## No-restart routing boundary

The compiler loads all valid, resolvable subscription node outbounds so normal group switches only change routing. The helper independently validates the whole candidate generation and compares all non-routing runtime fields, physical service identities and routing domain strategy. Only an unchanged listener/TUN/DNS/outbound configuration with confirmed existing takeover is eligible for a live route update.

The trusted helper alone injects RoutingService into `core.runtime.json`. Its socket is inside the root-owned private directory; user-supplied `api` and runtime-file overrides remain rejected. The pinned Xray CLI runs `api adrules --server=unix:///…/routing.sock --append=false` with the validated candidate JSON and candidate DAT assets. This replaces the complete table atomically while keeping core, TUN, DNS and existing connections. Bootstrap completion uses this same path when its outbounds are unchanged.

If the RPC fails or cancellation makes its outcome uncertain, restore the complete previous table with independent bounded cancellation. Report `routing.update` only after restoration succeeds; failed restoration is `recovery.failed` and triggers existing recovery. Concrete runtime changes (node transports/dialer chains, interfaces, DNS or listeners) still require a logged reconfiguration. This feature does not promise that arbitrary configuration changes are restart-free.

Per-record telemetry generation preserves existing observed labels through a hot update; new records use the new generation. Telemetry remains observational and never controls the core. Guardian continues to use authenticated GUI identity/liveness and actual TUN takeover only.

## Startup diagnosis and verification

The reported 2026-11-01 logs showed successful TUN establishment and rule preparation followed by a second core start at full routing application. Route-only bootstrap completion now avoids that second start. Separate immediate cold core exits were also present; their exact cause was not captured by the older log and has not been established by a live test.

Startup diagnostics now classify bounded fatal output into fixed port, permission, TUN or configuration codes, and record the exit code before recovery. They never forward raw per-connection errors or credentials. Main/helper/native payload identity is checked so a new UI cannot silently keep an old helper implementation. Diagnostic failures remain outside protection decisions.

Validation uses injected resources/runtime, synthetic subscriptions, pure native ABI checks, actual headless pointer/keyboard input, normal/compact rendered states, and NativeAOT GUI/helper mock smoke. No real TUN, helper installation, system DNS/routes or protected targets are used. Cases cover DIRECT/download separation, refresh retention, atomic route replacement and rollback/cancellation, applied-versus-pending automatic edits, observation generation, and inventory preservation.

Implementation references: [Xray Unix API listener](https://github.com/XTLS/Xray-core/blob/v26.10.29/app/commander/commander.go), [full routing-table update](https://github.com/XTLS/Xray-core/blob/v26.10.29/main/commands/all/api/rules_add.go), [Mihomo group configuration](https://wiki.metacubex.one/en/config/proxy-groups/).
