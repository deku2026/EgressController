# Process protection and multiple adapters

## Configuration

Schema 3 has `adapters` (stable Windows GUID plus display name), `defaultAdapterId`, and
`dnsAdapterId` (null follows the default adapter). SOCKS5 `upstreamPorts` and `upstreamPort`
remain separate. Routes use `adapter-default`, `adapter` plus `adapterId`, `default` (port),
or `port` plus `port`. A missing selected interface remains selected and unavailable.
No interface name or enumeration order is used as persistent identity.

Schema 1/2 adapter IDs and eSIM routes are migrated. Selected applications retain a cached
executable inventory for protection before discovery finishes. Cached entries missing from
discovery remain visible and can be deselected; they are not silently discarded.

## Runtime sequence

1. Load selections and start the independent process guard in the protected state.
2. Discover applications and prepare cached/downloaded core and rules. Recovery downloads
   do not require a SOCKS listener. Their explicit self route precedes TUN DNS interception.
3. The supervisor attempts startup once at a time and retries on subsequent one-second ticks.
4. TUN API startup health must pass. Each configured DoH gets a unique DNS probe; both must
   respond successfully. Cloudflare remains the default resolver.
5. Probe each distinct outbound used by application/domain/SRS rules and the DNS outbound.
   Only fresh, successful results for the current configuration permit selected applications
   to remain running. Configuration changes invalidate prior results before application.
6. A failed/expired probe, missing interface or exited TUN returns to protection. Guard sweeps
   continue independently of downloads, configuration application and network timeouts.

The guard expands exact executable membership, identifies instances by PID and creation time,
and tracks descendants while healthy as well as protected. Windows verifies the creation time
on the same handle used to terminate, then waits briefly for exit. Access failures are recorded.
Control-plane executables and SOCKS listener owners are excluded and selection conflicts are
reported. Identical failures are retried without repeated UI records; history is capped at 200.

Configured recovery communication remains permitted. This is not an operating-system kill
switch: process termination has a detection interval and does not guarantee zero packets.
The UI never claims system-wide disconnection. Closing the window hides to tray; explicit exit
ends protection and stops the owned core. There is no persistent firewall or background service.

## Verification without network interruption

- `ProcessProtectionTests`: exact paths, all instances, recursive inventory, descendants,
  detached descendants, PID reuse, dependency exclusions, access failures and deduplication.
- Readiness/supervisor tests: failed TUN, changes, network loss, DoH failure, stale health,
  recovery, one pending startup, retry, and no startup while already running or busy.
- Profile/resolver/compiler tests: independent defaults, legacy migration, multiple bindings,
  no fallback when absent, direct-only configuration and recovery route precedence.
- Fake HTTP tests: authenticated outbound probes, explicit failure and bounded recovery downloads.
- View-model tests: default-first category choices and transactional selection changes.

Default builds omit live tests. `build/Invoke-Tests.ps1` and CI set `EGRESS_MOCK_ONLY=1`, which
also prevents the real process and core adapters from running if called accidentally. Compile,
mock tests and packaging are allowed; do not start the product or alter real network state.
