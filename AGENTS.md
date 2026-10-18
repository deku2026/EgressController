# EgressController development contract

## Process scope

- User terminology “子进程 / 递归进程” means EXE files found by recursively scanning the selected application's directories. It does not mean Windows parent/child processes, fork/CreateProcess relationships or descendant PIDs.
- Scan and save an explicit set of canonical EXE paths. Runtime protection matches all instances of those saved paths; PID and creation time are used only to identify an instance safely.
- Never expand membership from ParentPid, dynamically inherit an application's route for shared tools, or restart TUN because an application/process instance starts.
- Changing an application’s exit must preserve its saved EXE inventory, including temporarily missing/older paths. The UI, routing compiler and guardian must use the same membership.
- Directory roots are discovery inputs, not a runtime wildcard that silently enrolls new executable paths. Explicit rescanning/selection can update the saved inventory.
- Existing descendant tracking and its tests are not requirements. Remove that behavior instead of preserving it under a different name.

## Windows application catalog

- Classify the primary EXE using an exact case-insensitive product catalog. Do not classify Visual Studio/system tools as AI or browser apps because a nested helper or a substring matches.
- Display one main application row for its recursively scanned fixed EXEs. Related discovery keys only reconcile saved selections; they must never broaden a conflicting helper route to the whole app.
- Native AI CLI commands get distinct names and exact EXE paths from command metadata. Never execute the AI command during discovery, recursively enroll a shared bin directory, or bind a generic node/python/powershell host.
- Keep saved paths through rescans and route changes. Preserve conflicting old routes until the user explicitly unifies them. UI icons/names are presentation metadata, never route identity.

## Independent responsibilities

- The companion guardian monitors the authenticated main-process instance and actual TUN takeover. It never performs or consumes Internet/DoH reachability checks.
- Main-process network protection owns the existing Internet rule: a round every 30 seconds, retry failures after 5 seconds, confirm only three consecutive failures. Link/IP availability remains independently checked.
- Connection/traffic display is observational. A failed telemetry request cannot restart TUN, reset network readiness or terminate a protected application.
- TUN lifecycle changes require an explicit configuration change affecting the generated runtime or a confirmed core/TUN failure. Record the concrete cause before changing the core.
- Diagnostic/file logging failures must not change the protection verdict.

## Startup resources and companion updates

- Catalog, SRS, release metadata and core ZIP downloads must use DefaultProxyDownloadTransport and the selected default SOCKS5 port. Never add a system/direct HTTP download client or silently fall back to an adapter.
- Pending profile preparation uses that profile's port through a scoped snapshot; concurrent catalog requests keep the saved profile's port. Do not dispose another request's client or cancellation source.
- Use validated local core/rule caches before remote requests. Recover missing/corrupt catalog through the same proxy path. Distinguish caller cancellation from transport/CLI timeout and retain resource/port/stage in errors.
- A new preparation shows current progress; prior failures belong in history. Only the operation that owns its cancellation source disposes it; obsolete work cannot overwrite a replacement's state.
- The installed tray is a separate copied payload. On main startup, compare bytes, stage/verify, replace only exact installed tray instances, and preserve rollback. Do not stop the protection service or reset user stop/startup preferences to update its UI.
- Keep mock combination matrices for startup states, caches, selected ports, cancellation/recovery, and separate guardian/network verdicts. Use in-memory state/resource adapters for large Cartesian matrices; validate real storage integrity, atomic publication and rollback in focused boundary tests. Never weaken production durability to speed up tests.

## Diagnostics

- The default file log is a bounded protection/lifecycle event log: start, stop, restart and their causes; protection transitions; process termination results; inability to enforce protection.
- Do not forward routine sing-box connection errors, per-connection events or display-monitor reconnects to this file or the global protection status.
- User-facing current status and historical events are separate. History cannot leave a healthy application under a permanent red banner.
- Keep the log-directory action prominent and accessible.

## UI and architecture

- Native Avalonia, compiled bindings and NativeAOT remain required. No WebView dependency for the main interface.
- Use the supplied Surge references for hierarchy, restrained surfaces, sidebar navigation, typography and dense readable tables. Preserve Windows window controls.
- Shared design tokens, icons and reusable controls belong in a presentation layer. Views must not create production controllers or invoke Windows service/process/network operations.
- Provide a fully isolated, interactive UI preview mode with mock data and screenshot rendering. Enter mock safety mode before any production initialization. Preview mode must not enumerate/terminate real targets, start TUN, install services, change startup entries or write the user's real configuration.
- Telemetry charts must use actual observation times, a zero baseline and explicit shared numeric scale. UI refresh cannot manufacture samples; missing/stale data is not zero. Byte units and app percentages must match their real denominators.
- Guardian icons and text must distinguish healthy, protecting, unavailable and manually stopped states; never show a green check based on a stale reply after a read error.
- Resolve displayed adapter names/descriptions from live GUID identity; keep raw runtime tags out of primary connection/route labels.
- Both tray menus use the same rounded presentation. Position them from the actual Windows notification icon rectangle and its monitor DPI/work area, including negative coordinates. Native shell APIs must be fenced out of mock previews.
- Read Windows executable icons off the UI thread with bounded shared caching; helper and CLI artwork should reuse their product icon.
- Validate normal, hover, pressed, focused and disabled states. Use meaningful mock screenshots at ordinary and compact Windows window sizes, not only XAML compilation.

## Workflow and validation

- Work in a new Git worktree/branch for implementation. Do not edit the user's original checkout.
- Only mock tests and isolated UI previews. Never start a real TUN, terminate real target processes, install/stop/restart a real guard service or change real startup entries for testing.
- NativeAOT build/package and mock smoke are allowed. Keep their early exit before any live initialization.
- Review the full change, verify process-scope and guardian/network separation, inspect rendered UI, update concise user-facing README and put technical details in docs/.
- Submit a PR, attach it to this chat and check its CI. Do not merge unless asked.
