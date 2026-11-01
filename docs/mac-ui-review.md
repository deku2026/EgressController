# macOS UI review — 0.1.9

The review was triggered by the failed workspace shown in `6.png`. That screenshot contained real user data and is not committed. All images below and under `artifacts/mac-ui/` are generated from synthetic, isolated fixtures.

## Findings and fixes

| Priority | Finding | Correction / regression coverage |
| --- | --- | --- |
| P0 | Native interface enumeration boxed `active || addresses.count > 0` as an NSNumber integer. Managed JSON expected a Boolean and rejected the entire workspace. | The live serializer explicitly emits `@YES`/`@NO`. Four active/address combinations exercise the same serializer through the C ABI and source-generated managed deserializer, including both packaged NativeAOT mock-smoke entrypoints. |
| P1 | Workspace discovery failure left an editable-looking empty setup and misleading synchronized/connection-failure status. | Saved draft loading is separate from interface/application discovery. Loading/failure/retry have an explicit view; incomplete workspaces cannot be edited. A readable draft can still be backed up and reset. Unknown/unreadable drafts are never replaced with empty defaults. Retry cancellation preserves choices. |
| P1 | Group member buttons retained the disabled state published during a switch; dependency changes did not notify member bindings. | Publish member availability and all-probes availability after busy/load transitions. A real second pointer selection and successful keyboard focus are required by the preview test. |
| P1 | DNS label and input occupied the same Grid cell; the input was clipped outside the form. | Correct column placement, plus a rendered-geometry assertion for non-overlap and containment. XAML compilation alone cannot detect this class of error. |
| P1 | Covered controls and page shortcuts remained interactive under dialogs/catalog panels. | Setup/reset block background navigation. Page overlays disable covered controls, own Tab traversal, and move focus to search/name/finish. The shared focus helper handles delayed enablement after loading. |
| P2 | Wizard headings and errors moved with scroll offsets; log actions could be clipped. | Fixed heading/error/actions and footer; explicit step/open transitions reset only the wizard body. Group/application steps each use one bounded list viewport. Modal dimensions remain stable between steps. |
| P2 | Empty ComboBoxes opened blank popups; long names stretched rows; rule details were silently clipped. | Empty selectors are disabled, names truncate with full-name tooltips, bounded grids replace unconstrained horizontal stacks, and detail/form content scrolls vertically. |
| P2 | Light blue-gray inline text and large port steppers were inconsistent with the shared presentation. | Shared readable secondary/semantic text tokens; a compact numeric port field; consistent rounded controls and existing Inter/CJK fonts/artwork. Reset copy now correctly states generated cache cleanup. |

## Render and interaction coverage

`tools/EgressController.MacPreview/UiAuditChecks.cs` renders the full shell at **1260 × 860** and the actual minimum **960 × 660**, with ordinary and deliberately long names:

- Overview, connection activity, applications and fixed-inventory details, rules and matching details, nodes/groups.
- All five settings tabs: network/TUN, DNS, DSL, resources, diagnostics.
- All five setup steps, application/rule catalogs, custom rule creation, group retention.
- Pending load, discovery failure, storage failure, retry and recovery; normal, hover, pressed and keyboard-focused retry controls.

The existing preview suites additionally cover reset confirm/cancel/error/recovery, six connection phases, real pointer input to log/retry controls, hover/pressed/focused/disabled group/reset controls, group expansion anchoring, connection list virtualization, refresh/selection/viewport preservation, empty searches and redacted error presentation. The audit checks unexpected horizontal overflow, DNS geometry, modal shortcuts and repeated Tab traversal. Managed tests cover preserved saved inventories, reset during discovery failure, unknown drafts, delayed loading and canceled retry.

![Recoverable workspace loading failure](images/mac-workspace-load-error.png)

![Minimum-size setup group selection](images/mac-ui-review-groups.png)

![DNS form after geometry correction](images/mac-ui-review-dns.png)

Run the full acceptance path:

```sh
python3 build/Verify-Mac.py --version 0.1.9
```

For focused iteration:

```sh
dotnet run --project tools/EgressController.MacPreview -- --ui-audit-only --screenshots artifacts/mac-ui
```

Local acceptance passed 565 managed tests (Policy 116, Runtime 170, Mac 196, Client 83), two pure native ABI checks, the synthetic Xray RPC regression, all UI suites, and the 0.1.9 NativeAOT package mock-smokes. The 120-node / 13-group UI fixture rendered in 61 ms in the final review run; this measures synthetic UI publication, not live network latency.

The earlier suite passed without testing the native interface serializer or every settings tab. This was a coverage gap, not evidence that the reported UI was healthy. These regressions now exercise actual native serialization, compiled bindings, rendered bounds, and keyboard/pointer input.

## Boundaries

Validation uses mocks, synthetic files, pure ABI fixtures and explicit isolated previews. It does not start real TUN, change system DNS/routes, install a helper, stop target processes or inspect the user's saved configuration. Headless UI and package verification do not establish that the user's iPhone uplink or real proxy nodes can reach the Internet. Runtime routing, guardian/protection decisions and saved executable membership are unchanged by this presentation review.
