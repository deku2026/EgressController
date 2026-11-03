# macOS management and subscription delivery

## Contract and order

1. Preserve the Windows schema-4 EXE inventory, guardian/network separation and existing mock-only regression lane. Build portable presentation assets without pulling Windows services into macOS.
2. Add a native Avalonia 12 macOS composition root using the existing Inter/CJK typography, tokens, compact controls and sidebar. macOS owns configuration, never TUN or process termination. Provide isolated interactive preview and screenshots before production initialization.
3. Add typed, platform-specific routing inputs, stable adapter identity, explicit Bundle/native CLI selection, SOCKS5 probes/loop prevention, Surge rule catalog, deterministic complete CONF compilation, private atomic storage, validation, preview and last-valid recovery.
4. Add a shared subscription workflow: configured site provider, dedicated real Chrome profile, system credential storage, explicit manual challenge state, format-aware validation, authenticated protobuf/gRPC upload and receipt verification. Raw subscriptions are separate from private runtime profiles.
5. Add independent 15-minute node observation/regex/condition/cooldown scheduling. Unknown results and backend/network outages never count as node failures. Agent packaging is separate from GUI startup.
6. Extend the existing SubVault service with authenticated bounded ingress, SHA-256 verification, durable latest-per-provider/client/format storage and idempotent receipts. Preserve existing server functions.
7. Validate mock regressions, real macOS AOT, offline Surge --check, isolated Chrome + OrbStack gRPC end-to-end and ordinary/compact UI screenshots. Document local commands; never add live end-to-end to CI. Open one PR per repository and inspect CI; do not merge.

## Local safety and ownership

- Worktrees: EgressController/.worktree/mac-surge-manager and SubVault/.worktree/subscription-ingress.
- No Surge reload/switch, active profile writes, TUN start, real target termination, guardian operations or launchd installation during verification.
- Generated configs and subscriptions stay in private application directories; previews and checked-in screenshots use synthetic data.
- Real provider onboarding depends on the target site's documented controls; unknown sites are not guessed. A local fixture exercises the complete browser/validation/upload path.
- User acceptance must cover actual Surge activation; a syntax check never means applied.

## Documentation checked

- https://manual.nssurge.com/tools/cli.html
- https://manual.nssurge.com/tools/http-api.html
- https://www.puppeteersharp.com/docs/AOT/
- https://learn.microsoft.com/en-us/aspnet/core/grpc/native-aot?view=aspnetcore-10.0

## Evidence (updated during implementation)

- Baseline: EgressController 8d05852, SubVault fd880df; clean original checkouts.
- Host: .NET SDK 10.0.403, macOS arm64, OrbStack Docker 29.8.2, installed Surge CLI and Google Chrome.

- Real TAG manual login persisted in a dedicated Chrome profile. JIT and NativeAOT refresh reused it without submitting credentials. Both raw formats passed native validators and exact gRPC hash/version acknowledgement. Downloads use an ephemeral context with a retained CDP download session; no Save As dialog or client-import action.
- Portable mock suite: 21 cases. Windows solution build, all mock test projects and ordinary/compact UI rendering passed on the remote Windows host.
- macOS ARM64 GUI and Agent published with NativeAOT; ad-hoc bundle signature and early mock smoke verified. The native GUI was opened in isolated preview mode and its subscription workflow exercised.
- OrbStack ARM64 server ran as UID 1654, accepted synthetic uploads and preserved idempotent receipts. SubVault: 418 server and 54 GUI tests; PR CI passed.
- Rendered seven macOS scenarios at 1280×840, ready pages at 1024×680, plus hover/pressed/focused/disabled session actions. Selected synthetic screenshots are in `docs/images/macos`.

## Explicit acceptance boundaries

- Active Surge was never loaded, reloaded or switched. Import/application stays manual. Its current profile has no HTTP API configured, so real API health observation remains for user acceptance; failures remain unknown and cannot trigger a refresh.
- Windows keeps manual subscription acquisition. Its node-health adapter is not connected in this implementation; automatic controls are hidden there. The macOS scheduler/Agent and shared condition evaluator are implemented.
- The first UI edits a single regex/percentage rule; advanced AND/OR/age conditions exist in the evaluator, not as a full rule editor.
- launchd installation and distribution signing/notarization remain owner-controlled. No live end-to-end job was added to CI.
- PuppeteerSharp aggregate trim/AOT warnings remain visible and are documented; product-code AOT warnings remain errors.
