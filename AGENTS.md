# EgressController development contract

## Process scope

- User terminology “子进程 / 递归进程” means EXE files found by recursively scanning the selected application's directories. It does not mean Windows parent/child processes, fork/CreateProcess relationships or descendant PIDs.
- Scan and save an explicit set of canonical EXE paths. Runtime protection matches all instances of those saved paths; PID and creation time are used only to identify an instance safely.
- Never expand membership from ParentPid, dynamically inherit an application's route for shared tools, or restart TUN because an application/process instance starts.
- Changing an application’s exit must preserve its saved EXE inventory, including temporarily missing/older paths. The UI, routing compiler and guardian must use the same membership.
- Directory roots are discovery inputs, not a runtime wildcard that silently enrolls new executable paths. Explicit rescanning/selection can update the saved inventory.
- Existing descendant tracking and its tests are not requirements. Remove that behavior instead of preserving it under a different name.

## Independent responsibilities

- The companion guardian monitors the authenticated main-process instance and actual TUN takeover. It never performs or consumes Internet/DoH reachability checks.
- Main-process network protection owns the existing Internet rule: a round every 30 seconds, retry failures after 5 seconds, confirm only three consecutive failures. Link/IP availability remains independently checked.
- Connection/traffic display is observational. A failed telemetry request cannot restart TUN, reset network readiness or terminate a protected application.
- TUN lifecycle changes require an explicit configuration change affecting the generated runtime or a confirmed core/TUN failure. Record the concrete cause before changing the core.
- Diagnostic/file logging failures must not change the protection verdict.

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
- Validate normal, hover, pressed, focused and disabled states. Use meaningful mock screenshots at ordinary and compact Windows window sizes, not only XAML compilation.

## Workflow and validation

- Work in a new Git worktree/branch for implementation. Do not edit the user's original checkout.
- Only mock tests and isolated UI previews. Never start a real TUN, terminate real target processes, install/stop/restart a real guard service or change real startup entries for testing.
- NativeAOT build/package and mock smoke are allowed. Keep their early exit before any live initialization.
- Review the full change, verify process-scope and guardian/network separation, inspect rendered UI, update concise user-facing README and put technical details in docs/.
- Submit a PR, attach it to this chat and check its CI. Do not merge unless asked.
