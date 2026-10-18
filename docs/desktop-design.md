# Desktop design

The reference is Surge's quiet desktop workspace: a stable left navigation, a useful overview,
clear status capsules, generous metric panels and compact connection rows. This is a Windows
network utility, so the native title bar and window controls remain. Chinese labels are primary.

## Design tokens

| Role | Value |
| --- | --- |
| Workspace | `#F4F6FA` |
| Sidebar | `#EBEFF5` |
| Surface | `#FFFFFF` |
| Primary text | `#202C3D` |
| Secondary text | `#5F6D82` |
| Accent | `#5062CE` |

Use Segoe UI Variable Text / Segoe UI with Microsoft YaHei UI fallback for Chinese. Page titles are 26 px semibold,
section titles 16 px semibold, body/control text 13–14 px, metrics 32–40 px. Do not add all-caps
eyebrows or technical prose to every panel. Icons are an original consistent set of 20 px line
geometries. The sidebar is 220 px; primary workspace padding is 28 px with a 16 px grid.

```
 native Windows title bar
 ┌──────────────────┬──────────────────────────────────────────────┐
 │ brand            │ page title            TUN / guardian status │
 │                  │ short current state                         │
 │ overview         │                                              │
 │ interfaces       │ page content: metrics, forms or table        │
 │ applications     │                                              │
 │ domain rules     │                                              │
 │ connections      │                                              │
 │ usage            │                                              │
 │ protection       │                                              │
 │ guardian         │                                              │
 │ events           │                                              │
 │ logs / preview   │                                              │
 └──────────────────┴──────────────────────────────────────────────┘
```

Cards organize genuinely separate tasks; connection/application screens use flat table rows,
not a card per item. Accent is for navigation and primary actions. Green describes current
healthy state, amber waiting and red an active protection fault. Historical errors are not a
global failure banner. Long executable paths belong in selectable detail areas with tooltips.

## Review against the brief

- Keep the reference's hierarchy, whitespace and restrained depth, while removing macOS traffic
  lights, decorative gradients and unrelated Surge capabilities.
- Do not copy a generic dashboard full of invented counters. Display actual existing metrics;
  preview fixtures are visibly labeled and never appear in production.
- Every page retains working operations from the product. Destructive service actions are
  visually secondary and grouped apart from normal start/refresh actions.
- All command states use explicit colors, keyboard focus and readable contrast. Empty, waiting
  and error views explain what the user can do in a short sentence.
- Capture actual rendered pages at 1440×900 and 1100×760 with normal, offline and empty fixtures.
  Review clipping, density, baseline alignment and disabled controls before publishing.

## Presentation and verification

`EgressController.Presentation` owns shared tokens, vector icons, trend drawing, typography and
`GuardPanel`. App and standalone GuardTray reuse the same components. Each main page and view model
has its own file. Views communicate through `IAppController`; only AppController composes production
services. PreviewController implements the same boundary entirely in memory.

`tools/EgressController.UiPreview` enters irreversible process-local mock mode before initializing
Avalonia. It never constructs AppController. Native headless Skia rendering exercises compiled bindings,
keyboard navigation, button pointer/focus/disabled states, contrast and all management commands with
fake data. CI captures normal (1440×900), compact (1100×760), waiting, network-offline, empty and
telemetry-error scenarios. Ordinary tests also cover protocol, protection, snapshots and file-log filtering.
The dedicated guardian window uses the shared panel; native titlebar/tray shell remains Windows-owned.

## Follow-up: truthful telemetry and companion consistency

Keep the same Windows typography and five core colors (workspace #F4F6FA, surface #FFFFFF,
ink #202C3D, secondary #5F6D82, accent #5062CE). The small guardian is a compact companion:
brand/open-main toolbar, truthful state hero, concise service/startup controls, structured history
and an always-visible log action. Advanced destructive actions stay available in an expander.
Notification-area registration is native; its rounded Avalonia menu shares the application theme. The shield artwork is shared with the window and
renders at multiple DPI sizes. Healthy, protecting, unavailable and manually stopped states must
have different icon/label treatment; a green check must never appear on an unavailable service.

Traffic charts use actual monotonic observation timestamps over a fixed 60-second window, a
zero baseline and one labeled numeric scale shared by upload/download. Never normalize each
line to its own peak or append points on UI refresh. Missing/initial/stale readings are unknown,
not zero; gaps stay visible. New sessions reset the series. Byte values use explicit IEC units.
App bars show each application's fraction of all observed active-connection bytes, not a fraction
of the largest row. Delay tiles use structured successful probe results rather than parsing text.

```
Guardian                         Traffic
Egress Guard       Open main     Upload 12 KiB/s     Download 1.6 MiB/s
[shield] Current state           2 MiB/s ─────────   2 MiB/s ─────────
Reason + last refresh           1 MiB/s ─────────   1 MiB/s ──/──/───
Service / startup / normal ops   0       ──____───   0       ─────────
Recent protection records               -60s now            -60s now
Advanced service controls        one scale, real timestamps, visible gaps
Log directory
```

Review scope follows the latest corrections: recursive **saved EXE paths**, no ParentPid
membership, no launch-triggered configuration/TUN changes; guardian main/TUN only; main
network protection keeps 30 s / 5 s / three failures; telemetry and file-log failures have no
protection authority. Add mock assertions for these boundaries instead of reintroducing older
requirements from superseded conversation turns.

## Updated previews

![共享数值刻度与缺失样本](images/traffic.png)

![独立守护窗口（模拟数据）](images/guardian.png)

![通知区图标的状态与尺寸](images/guardian-icons.png)

## Windows polish pass

Mode: **Operate**. Preserve the sidebar and information architecture. Refine the existing Surge-inspired
world with Windows-native typography, a calmer workspace, brighter readable surfaces, restrained offset
shadows, 36 px controls, 8 px control corners and 12–15 px panel/menu corners. Use actual product artwork
and exact route names, not decorative badges or invented statistics.

The main menu and guardian menu now share a measured rounded surface with keyboard focus, disabled and
stop states. A Win32 notification host obtains the icon rectangle and owning monitor work area/DPI;
menu placement uses physical pixels exactly once. This avoids the primary-screen scaling assumption
in Avalonia 12.1.1's stock tray implementation. No private Avalonia APIs or WebView are used.

Applications, connection rows, the inspector and application traffic share a bounded visual cache.
Windows EXE resources are extracted at 64 px off the UI thread; a neutral vector is shown while loading
or if the icon is unavailable. CLI entries reuse the product artwork with a small terminal marker.
Complete paths and adapter descriptions remain accessible in details/tooltips.

Visual review used frontend-design and Impeccable's polish/Operate guidance. Its web-oriented detector
does not inspect AXAML; native headless rendering, compiled bindings and interaction/contrast checks
remain the verification path. Three screenshot passes cover all nine pages at ordinary/compact sizes,
companion states, tray menus, keyboard focus and filtered CLI rows. These are mock previews, not a live
TUN or a real notification-area integration test.

![原生 CLI 清单](images/cli.png)

![主程序通知区菜单](images/tray-main.png)

![守护通知区菜单](images/tray-guard.png)
