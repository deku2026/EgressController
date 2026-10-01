# Process protection and two adapter roles

## Configuration

Schema 4 has exactly two configurable roles: `defaultAdapterId` (`ESIM-家宽`) and
`proxyAdapterId` (`Proxy-代理`). `adapters` contains their stable GUIDs and fixed role names.
They must be different and both must be configured before readiness can pass. A role can be
replaced or the pair swapped atomically; explicit application/domain/SRS and DNS references
follow that role. Interface enumeration never chooses a substitute. The default picker filters
offline, unaddressed and common internal virtual interfaces, with an expanded view available.
Saved unavailable choices remain visible. No name or enumeration order is persisted as identity.

`dnsAdapterId` null follows the direct role; an explicit value must be one of the selected pair.
SOCKS5 `upstreamPorts` and `upstreamPort` remain separate. Routes use `adapter-default`,
`adapter` plus `adapterId`, `default` (port), or `port` plus `port`.

Schema 1/2 uses the old primary for proxy and eSIM for direct. Schema 3 preserves its saved
default; the other card is inferred only from an explicit legacy primary binding or a two-card list. DNS selection never implies the proxy role.
Ambiguous roles require user selection. Old rules and DNS references to a third card stay
visible but block readiness until reassigned. Port selections are preserved.

Each local port is resolved from the TCP listener table to PID, creation time and full executable
path. IPv4 listeners must cover `127.0.0.1`; an unrelated NIC or `::1` listener is not a match.
An IPv6 wildcard listener is considered only when there is no IPv4 listener; missing, inaccessible or ambiguous owners are shown as errors. Their application/domain rules reject until
identity is confirmed; an unready default port also adds a final catch-all reject after known
owner and recovery routes, preventing recursion through an unidentified core. Multiple ports
may share one core.

The exact path rule for all known proxy cores precedes business rules and routes only through
`proxy-direct`, bound to the proxy role. An unavailable proxy role emits a reject rule instead;
there is no unbound/system/direct-role fallback. A core's last known path remains exempt while
its listener restarts; a PID/creation-time change with identical paths does not require a new configuration.
Removing its final configured port removes that exemption. Controller/core recovery has a
separate `recovery-direct` route and can still download dependencies.

Selected applications retain a cached executable inventory for protection before discovery
finishes. Missing discoveries remain visible and can be deselected, never silently discarded.

## Runtime sequence

1. Load selections and start the independent process guard in the protected state.
2. Discover applications and prepare cached/downloaded core and rules. Recovery downloads
   do not require a SOCKS listener. Their explicit self route precedes TUN DNS interception.
3. The supervisor attempts startup once at a time and retries on subsequent one-second ticks.
4. Confirm the authenticated local API and owned core are running; verify the named TUN has
   its expected IPv4/IPv6 addresses and both default routes. Only then allow selected apps.
   The one-second guard keeps checking the owned process, local interface and routes. These
   checks do not send Internet traffic or require a physical exit to be online.
5. Independently probe both DoHs in parallel, at most eight seconds each, once per minute or
   manually. Cloudflare is preferred; DNSPod is the fallback. In-progress, canceled, failed or
   timed-out checks cannot revoke TUN readiness. If both fail, keep the current resolver.
6. Only a changed resolver prepares a new immutable checked config. Keep the old core while
   preparing. Before stopping it, enter protection and terminate selected processes. A failed
   termination cancels the transition. Start with the new DNS final and confirm takeover again.
   Configuration generations reject stale results; rollback also requires takeover confirmation.
7. Keep a previously bound physical interface/address through an outage. No fallback and no
   restart just for link loss; actual address/alias changes on recovery require a config update.
   Newly selected or initially absent adapters emit rejects until bindings are available.

The guard expands exact executable membership, identifies instances by PID and creation time,
and tracks descendants while healthy as well as protected. Windows verifies the creation time
on the same handle used to terminate, then waits briefly for exit. Access failures are recorded.
Control-plane executables and SOCKS listener owners are excluded and selection conflicts are
reported. Identical failures are retried without repeated UI records. History retains the latest
24 hours, capped at 200; healthy guard ticks also prune expired records. Clearing history only
clears displayed records, not identities, descendant tracking, protection or retry state.

Configured recovery communication remains permitted. This is not an operating-system kill
switch: process termination has a detection interval and does not guarantee zero packets.
The UI never claims system-wide disconnection. Closing the window hides to tray; explicit exit
ends protection and stops the owned core. There is no persistent firewall or background service.

## Verification without network interruption

- `ProcessProtectionTests`: exact paths, all instances, recursive inventory, descendants,
  detached descendants, PID reuse, dependency exclusions, access failures and deduplication.
- Readiness/supervisor tests: failed TUN, missing takeover routes, unapplied configuration,
  recovery, one pending startup, retry, and no startup while already running or busy.
- Resolver/lifecycle tests: unchanged and failed probes never reload, stale/canceled results,
  changed DNS final, preparation preserving TUN, protection before restart, and checked rollback.
- History tests: pruning while healthy, count limits, clear preserving descendants and retries.
- Profile/resolver/compiler tests: independent defaults, legacy migration, exactly two independent bindings,
  no fallback when absent, direct-only configuration and recovery route precedence.
- Fake HTTP tests: authenticated outbound probes, explicit failure and bounded recovery downloads.
- Fake listener tests: shared cores, PID/creation-time changes, restart gaps, replaced owners,
  ambiguity, permissions, removed ports and unrelated-interface listeners.
- View-model tests: filtered choices, saved offline cards, atomic swap/save/cancel, error
  recovery, legacy third-card repair and live binding row updates without starting the product.

Default builds omit live tests. `build/Invoke-Tests.ps1` and CI set `EGRESS_MOCK_ONLY=1`, which
also prevents the real process and core adapters from running if called accidentally. Compile,
mock tests and packaging are allowed; do not start the product or alter real network state.
