# macOS runtime, isolation and durability

## Current execution boundary

The default desktop executable is an isolated interactive preview. Its first operation enables `PreviewEnvironment`, before Avalonia, view models, storage, native adapters or transports. `--live` is rejected; `--mock-smoke` runs the real policy/compiler/mock-controller checks and returns before GUI/platform initialization. No Xray process is launched, no privileged helper is installed and no system network settings are changed.

The native bridge implements read-only macOS mechanisms behind an explicit, default-deny `LiveOperationGate`. Preview catalogs, interfaces, icons and downloads are fixtures. The helper wire/request/configuration validators are implemented, but production socket authentication, launchd installation and real DNS/TUN restoration remain separate live-integration work. An unavailable helper is not represented as healthy.

## Configuration transaction

`ConfigurationCoordinator` snapshots the typed draft and assets, compiles policy, validates every external DAT reference against the actual protobuf tag/kind, and takes a serialized application lock. Validation precedes core changes.

- A runtime fingerprint contains Xray JSON plus the sorted resource names and hashes.
- A generation identity also includes the saved DSL. Metadata-only saves publish a new generation without restarting the mock core.
- Persisted configuration is distinguished from an actually running core. Starting after a cold launch, or explicitly resuming after Stop, applies even if disk configuration is unchanged.
- Apply failure/cancellation restores both the prior running generation and prior saved pointer. Recovery is not canceled by the original request. Recovery failure reports protection unavailable, including cancellation followed by failed recovery.
- Desired DSL retains fixed executable inventories through failed apply/retry. Process starts and telemetry observations are not inputs to the coordinator.
- Every lifecycle change has a concrete cause. Diagnostic-log failures cannot change the decision.

`AtomicGenerationStore` writes immutable whole generations, checks their content hashes, flushes files, synchronizes publication directories on macOS/Linux and atomically switches a small pointer. Last verified generations remain available for recovery from corrupt files, malformed/null manifests and corrupt pointers. Sensitive files are created with mode 0600, staging/generation directories with mode 0700; permissions are set at creation, not after writing credentials. The store assumes an application-owned directory, not a shared or attacker-controlled directory.

## Resources and cancellation

`ResourcePreparation` receives an injected transport and an immutable physical-interface binding. No system/direct HTTP client or fallback transport is constructed by the preview. HTTPS URLs and explicit interface identity/BSD names are required. Cache keys include a hash of the source URL, avoiding accidental reuse after a source URL changes without exposing query credentials in file names.

Startup preparation validates a local cache before requesting a remote resource. Explicit subscription updates use `Refresh`, validating the new response before replacing cache content. Failure keeps the prior cache/runtime. Caller cancellation, replacement cancellation and transport timeout remain distinct; errors identify resource, interface, remote port and stage without dumping endpoint credentials.

Each preparation owns and disposes only its own linked cancellation source. Replacement cancels the old operation but cannot dispose its source or let obsolete work overwrite the current progress. Prior failures are bounded history; a successful new operation has a current Ready status.

`SubscriptionReconciler` keeps global/app/rule routes and explicit manual group choices. Missing nodes remain explicit unavailable identities. Fatal subscription structure errors cannot remove previous nodes or selections. Per-node unsupported protocols stay visible and cannot be selected by the compiler.

## Independent verdicts

Guardian input contains only authenticated main-instance status, TUN takeover, manual-stop status and observation/read freshness. It has no Internet, DNS or latency field. Failed/stale reads produce Unavailable rather than a healthy icon.

Main network readiness tracks link/IP independently. Internet probes use 30-second rounds, retry after 5 seconds and confirm only three consecutive failures. UI refresh cannot manufacture observations or accelerate the failure counter. Telemetry and accounting have no coordinator/core reference.

## Usage accounting

The ledger anchors calendar periods to first opening and a saved time zone; successive periods are exactly 30 natural days even across DST. It preserves old totals and classifies increments by the outbound observed at that time.

After opening/resume, the first valid observation establishes a baseline and charges no offline bytes. Unavailable, empty partial and invalid observations cannot clear that baseline. Callers may mark an authoritative complete active-connection snapshot to prune closed checkpoints; partial/missing telemetry must never use that flag.

Observation, checkpoint advancement and totals commit under one private lock and one atomic persistence transaction. Schema/counter/key/time-zone/capacity failures stay local to statistics. Valid backups, private file modes, size bounds and rollback preserve the prior totals and first-opening anchor. Snapshot getters return copies; later route changes cannot relabel prior usage.

## Verification

`python3 build/Verify-Mac.py` builds the active Mac solution, runs managed mock tests, compiles the C bridge and invokes pure ABI probes, renders UI/control/interaction states, and creates an ad-hoc signed Apple Silicon NativeAOT `.app`. The packaged binary's `--mock-smoke` is executed before distribution. No verification step enables live operations.

For manual UI checks use `artifacts/package/Egress.app`, which also defaults to preview. Real network acceptance is deliberately unclaimed: it still requires authorized helper installation and separate TCP/UDP, DNS, IPv4/IPv6, Helper, interface-disconnect and recovery tests on the user's two interfaces.
