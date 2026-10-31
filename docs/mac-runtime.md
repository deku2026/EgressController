# macOS runtime, isolation and durability

## Startup and production composition

No arguments selects production. Only explicit `--preview` or `--mock-smoke` enters `PreviewEnvironment` before framework/services/storage/transport initialization. Unknown/conflicting modes and production scenarios are rejected. A mock-only environment prevents production platform operations rather than silently changing the selected mode.

Production startup reads the private saved draft and actual app/network catalogs. It does not start TUN or request administrator authorization. `ProductionClientBackend` composes real native catalogs/icons, bound HTTPS/DNS resources, private caches/drafts/generations, authenticated helper IPC and `MacXrayCoreRuntime`. Views only call the injected backend. The preview uses a separate fixture implementation and temporary directory; production never receives synthetic nodes, paths, latency or traffic charts.

Clicking Connect validates a policy snapshot and DAT resources, prepares the pinned core, installs/updates the helper if required, waits for a fresh authenticated readiness reply, and applies through `ConfigurationCoordinator`. NativeAOT GUI identity is required for privileged control; the shared `dotnet` host is rejected. Closing the GUI disposes its backend and requests restoration; the helper independently restores on authenticated-client loss.

## Configuration transaction

The coordinator snapshots policy/assets and validates every external DAT reference against actual protobuf kind/tag before changing the core. Runtime fingerprint is JSON plus sorted asset hashes; generation identity also includes saved DSL. Metadata-only publication does not restart. A disk generation is not assumed to be an actually running core.

Apply failure/cancellation independently attempts core recovery and saved-pointer recovery, even if one fails. Recovery is not canceled by the caller. Failed recovery reports unavailable. Fixed executable inventories remain in the desired policy through retry. Every lifecycle change has a concrete cause; logging failure cannot change its verdict.

`AtomicGenerationStore` publishes immutable verified generations with file/directory fsync and atomic pointer replacement. Drafts preserve incomplete onboarding choices in a checksummed, bounded current/previous store. Sensitive files are created 0600, private directories 0700. Neither store assumes an attacker-controlled shared directory.

## Resource bootstrap and bound runtime probes

Resource bootstrap now defaults to a captured macOS static system-proxy/default-network decision, independently of TUN and policy interface selection. It captures applicable environment overrides and system proxy settings lazily once per operation, then resolves destination-specific bypasses against that same snapshot, including redirects/providers/catalog/raw data and core checksum/archive. Configured proxy failure never retries direct; PAC, malformed settings and read errors are actionable failures. Cached resources do not require any system proxy read. HTTPS certificates, redirect schemes, response sizes, cancellation, cache validation and atomic publication remain enforced. Explicitly bound transports remain available as a distinct typed resource exit and cannot be confused with system bootstrap.

Startup validates caches before network requests; explicit subscription update forces refresh. Providers use the same snapshot. Official Meta indexes are tied to a commit and raw YAML blob hashes before DAT compilation. The core archive, checksum file and extracted ARM64 executable have independent pinned digests. Download/extraction never executes a core.

Startup probes only missing fresh URL-specific observations in reachable automatic groups and dialer dependencies, following the explicit member of manual groups. It does not probe unrelated subscription nodes; the explicit measurement action may still test the whole catalog. Automatic groups are probed using a short-lived unprivileged Xray observatory process with no inbound, TUN or API listener. Only exact pinned observer messages count as results; absent/malformed output is unavailable. Probe files remain private and are removed. All inherited `XRAY_*` and `xray.*` settings are cleared before running any owned core/probe, preventing injected config directories or inherited TUN descriptors.

Preparation cancellation sources are operation-owned, obsolete work cannot replace current progress, and historical failures are separate from current health. Refresh failures preserve the previous validated cache/runtime. Resource diagnostics identify stage/interface/port without exposing URL credentials.

## Privileged helper and restoration

The helper communicates only over a permission-restricted Unix domain socket. It authenticates UID, PID/start identity, executable path/hash and dynamic code identity against root-owned installation metadata. Frames are bounded, sessions/sequences echoed and checked; status observations must be fresh. The helper admits one native TUN and explicit physical interfaces, with no HTTP/SOCKS or network API listeners.

Only the independently pinned official core may be installed/executed as root. User generation files are opened without following links, verified, copied into root-owned immutable generations, rechecked and tested before mutation. No user-writable executable is run as root. Installation stages verified helper/native bytes privately, replaces exact payloads and preserves rollback.

Original DNS dictionaries (including enabled/absent protocol state and unknown plist fields) are durably journaled before mutation. The helper confirms actual TUN address and pinned Xray IPv4/IPv6 route ownership before switching DNS. Stop, core/TUN failure, GUI loss and launchd restart attempt owned-core shutdown and DNS restoration independently. A failed process operation cannot skip DNS restoration; unresolved recovery retains the journal and unavailable status. No Internet/latency/telemetry result is used as a guardian verdict.

## Independent observations and usage

Guardian input contains authenticated main-instance liveness and actual TUN takeover only. Stale/read failures never produce healthy status. Production UI polls actual helper state; it does not create successful Internet/latency or traffic samples. The independent main-network state machine retains 30-second rounds, 5-second retries and three consecutive failures; no failed display request can restart TUN.

The usage ledger supports fixed 30-natural-day periods from first opening and a saved time zone, resume baselines, observed-outbound history and atomic checkpoints/totals. Production currently shows no traffic curve until a real observation source supplies samples; it never substitutes preview observations or estimates. No quota UI is present.

## Verification boundary

`python3 build/Verify-Mac.py` builds production GUI/helper code, runs only injected mock tests and pure native ABI/route-parser probes, renders isolated UI states and publishes both NativeAOT binaries. Packaged execution is restricted to explicit mock-smoke entrypoints. Computer Use UI checks must launch with `--preview`.

No automatic check installs launchd jobs, starts a real core/TUN, changes DNS/routes, terminates protected apps or reads the user's real configuration. Live TCP/UDP, DNS, IPv4/IPv6, helper authorization and interface-disconnect behavior remain user-run acceptance; they are not inferred from compilation or mocks.

## Connection activity

`XrayConnectionObserver` parses only the pinned core stdout/stderr grammar into bounded in-memory records. Optional snapshots piggyback on authenticated helper replies; malformed, stale, oversized or failing observation data cannot invalidate a valid lifecycle status or stop/restart the core. No API/control listener or per-connection disk log is introduced. Actual destination, transport, outbound and matched-rule tags may be displayed; unknown PID/path, bytes and active/closed states remain unknown. Access-history rows are never silently treated as active connections or fed into the usage ledger.

Desktop labels match the applied generation, never the editable draft. The running generation remains pinned through metadata-only publications; observed route classification and names stay stable across later edits. The virtualized UI supports search/app filters/sorting and a selected detail pane while preserving the inspected row and focus as new records arrive. Development screenshots and populated fixtures use explicit isolated previews only.
