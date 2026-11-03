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
