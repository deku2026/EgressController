# macOS implementation and acceptance scope

The active product is the default-production macOS Xray-only client. Mock is a development/test restriction, not the product execution mode. Only explicit `--preview`/`--mock-smoke` selects isolated data and controllers.

Implemented paths:

1. Typed round-trippable YAML DSL, fixed Bundle-contained executable inventory, stable selections and deterministic three-exit compilation.
2. Real installed-App/interface/icon adapters, explicit selection/refresh, and empty/saved production first-run state.
3. Interface-bound DNS/HTTPS, durable validated caches, real Clash/provider and Meta catalog/raw YAML flows, pinned core download/verification/extraction.
4. Authenticated restricted helper IPC, explicit administrator installer, root-owned generation staging, real Xray validation/start/stop, TUN route confirmation and durable DNS recovery.
5. Unified native Avalonia presentation, independent production/fixture backend composition, settings/DSL/import/group and application workflows.
6. Mock-only unit/integration matrices, pure native ABI/route-parser probes, normal/compact renders, actual native preview Computer Use, GUI/helper NativeAOT package and PR CI.

The supplied private Clash file is used only for local in-memory compatibility checks. Credentials/endpoints are never committed, logged or rendered in screenshots. Unsupported plugin semantics remain explicit; unrelated supported selections remain usable.

Review gates include validation before mutation; preservation of old executable membership and selections; independent core/DNS/pointer recovery; fresh authenticated helper status; no shared interpreter enrollment; no inherited Xray configuration environment; no synthetic observations in production; and no local proxy/API listener.

The automated receipt is the passing CI for this PR and `build/Verify-Mac.py`. It establishes compiled production paths and mock behavior. It does not establish live routing reliability: real TUN and physical-interface acceptance are intentionally not performed by development automation.

Local verification (2026-10-30): Release build has zero warnings/errors; 281 managed mock tests passed (Policy 94, Runtime 74, Mac 87, Client/helper 26), pure native ABI/synthetic-route CTest passed, and isolated normal/compact/production-composition UI matrices passed. Both GUI and helper were published as arm64 NativeAOT, independently signature-checked and executed only with `--mock-smoke`. Native Computer Use launches use explicit `--preview`.
