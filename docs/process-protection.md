# Process protection and two adapter roles

## Configuration

Schema 4 has exactly two configurable roles: `defaultAdapterId` (`ESIM-家宽`) and
`proxyAdapterId` (`Proxy-代理`). `adapters` contains their stable GUIDs and fixed role names.
They must be different and both must be configured before readiness can pass. A role can be
replaced or the pair swapped atomically; explicit application/domain/SRS and DNS references
follow that role. Interface enumeration never chooses a substitute. The default picker filters
offline, unaddressed and common internal virtual interfaces, with an expanded view available.
Saved unavailable choices remain visible. No name or enumeration order is persisted as identity.

`dnsAdapterId` is a migration-only field; DNS always uses the direct role. Website probes use
the residential direct exit and the configured default SOCKS5 exit independently.
SOCKS5 `upstreamPorts` and `upstreamPort` remain separate. Generated SOCKS tags are
`upstream-<port>`; the prefix identifies an internal exit, not a proxy software brand. Routes use `adapter-default`,
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
   its expected IPv4/IPv6 addresses and both default routes. Also require both live physical
   adapters and completed healthy website probes for the residential exit and configured default
   proxy before allowing selected apps. Without configured proxy ports, only the residential
   website probe is required; the UI explicitly reports that proxy checking is not configured.
   The guard checks takeover and live adapters every second.
5. DNS is fixed to Cloudflare DoH through the direct role. There are no DoH health queries,
   generated probe hostnames, DNS health rules, provider selection or DNS status protection gates.
   The bootstrap resolver is retained for resolving the fixed server and preparing an offline exit.
6. Probe https://cp.cloudflare.com/generate_204 through the authenticated sing-box delay API,
   once through dns-direct (residential NIC) and once through `upstream-<upstreamPort>`
   (the current default loopback SOCKS5 endpoint). Its proxy core's external connections remain
   bound to the non-residential NIC through the higher-priority `proxy-direct` process route.
   The test must traverse the default proxy instead of requiring direct access to the website.
   Both run in parallel on a shared 30-second cadence measured from the start of each round;
   each failed attempt waits five seconds before retrying, up to three attempts per exit. Any
   success ends that exit's retries. Only three consecutive failures publish an outage. The gate
   spans retries so automatic/manual rounds cannot overlap; slower rounds delay the next check.
   Progress and failure reasons are displayed without replacing the previous health verdict;
   startup checks immediately, and a manual check uses the same probes. Each probe is bounded
   to eight seconds, and each completed result takes effect independently. An online NIC with
   an unavailable default proxy still fails. Never fall back to another SOCKS port, a direct exit,
   recovery-direct or an unbound HTTP client. This uses sing-box's HTTPS reachability verdict,
   not DNS responses. With no proxy ports configured, do not issue a proxy probe or synthesize
   a successful proxy result. Explicit non-default application/domain proxy ports are not tested
   by this default-proxy health check.
7. During rechecks retain the last completed verdict for each adapter. A previously healthy check
   keeps apps running; a failed check keeps terminating selected instances every second until
   recovery. Startup or configuration changes require fresh successful results. Observed adapter
   changes invalidate pending results and clear affected health, including same-IP reconnects;
   a reconnect waits for the next scheduled or manual check. Cancellation never becomes a
   network failure or success. Changing the default proxy invalidates its previous result;
   applying the new configuration starts fresh checks. Generation checks reject older
   configuration, adapter and default-port results, including remove/re-add of the same port.
8. A completed network failure activates process protection on the next guard tick without
   restarting TUN or changing DNS. Keep the original interface/address binding through outages
   so it cannot silently select another exit. The guard uses uncached live adapter snapshots.
   Actual address/alias changes on recovery require a checked configuration update; terminate
   selected processes before stopping the old core. Recovery routes remain available throughout.

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
- Network health tests: successful/failed rechecks, no-IP/link loss for both roles, cached versus live
  adapter state, residential/default-proxy probes using generate_204, arbitrary default ports,
  no direct fallback on proxy failure, direct-only mode, Internet failures with an online NIC,
  timeouts, malformed responses, five-second retry spacing, early success, three-failure confirmation,
  cancellation during retries, stale generations, port changes and reconnects.
  Fake process control verifies termination and recovery.
- Lifecycle tests: preparation preserving TUN, protection before restart, and checked rollback.
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
