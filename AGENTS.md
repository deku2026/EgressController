# EgressController — macOS-first development contract

The active product is a native Avalonia/.NET macOS client with Xray-core as its only network core. The latest user architecture overrides the former Windows/SOCKS/sing-box design. Historical Windows projects are isolated in EgressController.Windows.slnx, outside the current desktop product.

## Product and policy

- Apple Silicon macOS comes first. Keep compiled XAML bindings, NativeAOT and macOS native window controls. No WebView.
- Applications, domain sets and IP sets share three exits: home, non-home, or a specified proxy group. Route entrypoints never choose individual nodes; manual group membership may select nodes or nested groups.
- User application rules precede user domain/IP sets, then China domain/IP defaults to non-home; all other TCP/UDP requires a global proxy. Proxy server connections use non-home only. Failed binding fails the connection; never silently choose another interface.
- Clash YAML supplies nodes/groups/providers only. Never import its listeners, DNS, TUN, controller or route policy. Unsupported protocols/plugins/group semantics need explicit diagnostics; never silently drop parameters.
- Typed policy and versioned YAML DSL are the facts; Xray JSON and GeoSite/GeoIP DAT are generated artifacts. Stable IDs survive renaming and subscription reordering. Retain unresolved selections for explicit repair.
- Compile raw MetaCubeX YAML, preserving full/suffix/keyword/regex and IP/CIDR semantics. No MRS/SRS decoding or second network core.
- Subscription URLs and their providers must use direct HTTPS over the selected non-home physical interface, including DNS and redirects. No system proxy, Xray, TUN, helper or local-YAML prerequisite; a failed direct fetch reports an error and never retries via a proxy. Bundle Xray locally. Other missing rule resources are prepared only after authorized Xray TUN startup, through Xray’s loopback resource SOCKS listener and the selected non-home proxy group. No extra relay process or network control listener. Use validated caches, operation-owned cancellation, fixed port snapshots and atomic publication; interface binding failures never switch exits.

## Application membership and UI

- Discover installed .app bundles in standard Applications roots. Discovery does not enroll apps. AI products may be ordered first; never use a catalog as an admission whitelist.
- Explicit selection/refresh saves canonical executable paths inside verified bundle roots, including Mach-O helpers. Roots are discovery inputs, never runtime wildcards. Reject shared roots/symlink escapes and report incomplete scans.
- Changing routes preserves saved paths, including missing/old paths. UI/compiler/guardian share membership. Never inherit routes from parent PIDs, restart TUN for a new process, run a CLI during discovery or enroll shared interpreter/bin directories.
- Daily application view shows managed apps; search and manual selection add apps. Load real app artwork off-thread through bounded caches; shared adaptive icon backings preserve brand color and proportions.
- Preserve list scrolling, rows, selection, focus and viewport across refresh. Publish one filtered search/category result; no pagination or scrolling animation.
- Shared tokens, Inter/CJK fonts and compact controls live in presentation. Four workspaces: overview, apps, rule sets, nodes/groups. Settings contain interfaces, DNS, DSL, sources, updates and diagnostics. Views cannot create production controllers or call OS network/process operations.
- Loading, empty, unsupported, missing-reference, retry and validation states must be actionable. Separate current health from history. Keep log access prominent.

## Independent protection and durability

- Guardian verifies authenticated main-instance liveness and actual TUN takeover only. It never consumes Internet/DoH checks. Stale/read-error replies cannot produce healthy icons.
- Main network protection uses 30-second rounds, 5-second retries and three consecutive failures; link/IP availability remains independent.
- Telemetry, diagnostic/logging and usage-storage failures cannot change protection/core lifecycle.
- Only explicit generated-runtime configuration changes or confirmed core/TUN failure can restart the core. Record cause first. Metadata-only changes never restart it.
- Usage uses successive fixed 30-natural-day periods from first opening. Resume baselines existing connections; retain old totals and observed-outbound classification. Commit checkpoints/totals atomically. No quota UI or relabeling history after route edits.
- Native C ABI defines owned buffers, size limits, errors and LibraryImport wrappers. AppKit/SystemConfiguration stay out of views. GUI is unprivileged; real helper installation needs user authorization. Never claim live integration works without evidence.
- Validate before applying and publish whole verified generations atomically with rollback. Keep desired fixed inventories through retries; never weaken durability for test speed.

## Workflow and verification

- Continue in the existing `mac-xray-client/EgressController` worktree and `codex/mac-xray-client` branch, using PR #26. Do not create another worktree, branch or PR for this task. Keep the original main checkout untouched.
- Only mocks, injected resources, synthetic bundles and isolated previews for tests. Never start real TUN, terminate real targets, install/change services, modify system DNS/routes/startup or access the user's real configuration for testing.
- Default desktop startup is production. Only explicit --preview or --mock-smoke enables isolated preview before any production initialization. Tests and development screenshots must always use explicit preview/mock entrypoints; never launch the default production app during development validation. Native compilation/pure ABI probes are allowed; previews cannot query live platform state. Real TUN takeover requires the user to explicitly start it in the production UI.
- Inspect normal/compact rendered windows and hover/pressed/focus/disabled states, not just compilation. Never put private subscriptions/credentials in logs, screenshots, fixtures or commits.
- Review fixed process scope and guardian/network separation; update concise README plus technical docs. Submit and attach a PR, check CI, and do not merge unless asked.

The old Windows-specific contract is preserved in docs/windows-development-contract.md for historical work only.
