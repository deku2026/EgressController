# macOS implementation and acceptance plan

The user-approved baseline is an Xray-only macOS client. This change implements and verifies the policy/data/UI paths in an isolated mock environment. It does not install a privileged service or take over the host network.

1. **Policy:** typed, round-trippable YAML DSL; stable identities; fixed application/helper paths; deterministic priorities; explicit physical exits; mandatory global proxy; fail-closed reference/group validation.
2. **Sources:** full Clash YAML node/group/provider parsing with aliases and compatibility diagnostics; raw Meta YAML to GeoSite/GeoIP DAT; stable refresh; validated atomic generations.
3. **Native seam:** independently written C ABI/CMake bridge inspired by the supplied DesktopPlatform reference, LibraryImport wrappers, injectable bundle catalog, bounded icon cache, stable service identities, and restricted helper/DNS recovery contracts.
4. **UI:** shared design system, native Mac window controls, overview/applications/rule sets/nodes; search → explicit add → shared exit picker; first-run flow; settings/DSL validation; mock lifecycle and diagnostics.
5. **Verification:** policy/runtime/platform unit tests, pure ABI probes, screenshot/interaction matrices, native Computer Use smoke, NativeAOT .app packaging, PR and CI.

All automatic tests/previews enter mock-only mode before native services, transports or controllers. Resources are injected. Native tests invoke pure ABI/error functions only. Helper installation and live takeover remain unavailable and clearly labeled.

## Supplied private Clash file

The file is read only for local in-memory compatibility checks. It is never copied into the repository, logs, screenshots or preview data. Committed fixtures use documentation hosts and synthetic credentials.

All supplied nodes and groups are checked individually. Natively translatable nodes remain usable; entries requiring unsupported Shadowsocks plugin semantics stay visible with explicit diagnostics. No plugin option is silently discarded, and unrelated supported selections remain usable.

Clash ports, DNS/TUN/controller settings and rules remain outside node-import scope. Their omission is reported instead of silently becoming new policy.

## Review gates

- Route edits preserve executable inventory without rescanning or broadening membership.
- Global groups must resolve to proxies; DIRECT/REJECT cannot masquerade as proxies.
- Preparation/apply failures retain the last usable generation; metadata edits never restart.
- Guardian and Internet verdicts remain independent; telemetry/storage/logging cannot alter them.
- UI controls edit one model, with generated DSL/config, validation and mock rollback exercised.
- Screenshots show preview state and do not imply real traffic/latency measurement.
- PR documents the remaining live-service/network acceptance boundary explicitly.

## Local verification receipt — 2026-10-30

`python3 build/Verify-Mac.py` passed on Apple Silicon macOS with .NET SDK 10.0.403:

- Release build: no warnings or errors.
- Managed mocks: 86 Policy + 50 Runtime + 81 Mac tests, all passing, none skipped.
- CMake/CTest: one pure ABI test passed; live platform functions were not called.
- Avalonia: 13 scenarios at normal/compact sizes, five onboarding steps, keyboard/pointer/control states, stable refresh viewport, DSL/import/apply recovery, and binding diagnostics passed.
- NativeAOT: arm64 Mach-O `.app`, ad-hoc signature verification and packaged `--mock-smoke` passed.
- Computer Use: actual packaged window screenshot, search/manual Steam selection, explicit route editing, mock Stop/Resume, and the native Settings/Quit menu verified.
- Private attachment credential/endpoint scan: no findings in changed repository files.

Rendered UI: [overview](images/mac-overview.png), [applications](images/mac-applications.png), [DSL](images/mac-dsl.png), [explicit onboarding selection](images/mac-onboarding.png).

These results establish the isolated policy/UI pipeline. They do not establish real TUN, privileged helper installation or live traffic routing.
