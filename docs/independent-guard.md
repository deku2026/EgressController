# Independent NativeAOT guardian

The independent guardian is a main-process/TUN watchdog. It does not run website, DoH or
latency probes, and does not receive their results. Main retains its existing Internet protection:
30-second rounds, five seconds between retries, three failures per exit to confirm an outage.
The main can terminate applications for a network outage while the guardian tray has no red dot.

## Components and lifetime

- `EgressController.App`: administrator UI, configuration, sing-box ownership and network protection.
  A separate async agent publishes selections and a TUN-only heartbeat, independently of network probes.
- `EgressController.GuardService`: NativeAOT executable hosted by SCM, LocalSystem, one instance per
  registered user SID. No Avalonia dependency, HTTP probes, TUN creation or main-process launcher.
- `EgressController.GuardTray`: NativeAOT Avalonia tray, no main-process lifetime dependency.
  Login startup is registered for the owner; closing its window hides it. Exiting the tray does not stop protection.

On first successful main startup, a complete release supplies `guard-service/` and `guard-tray/`.
The elevated installer validates both payloads, secures the destination directories, copies them
into `%ProgramFiles%/EgressController/Guard/<SID-hash>`, saves the initial policy, installs an
own-process service and starts it. Service startup and the owner's tray Run entry default to enabled.
Service names, program path, account, PID and state are shown in both management interfaces.

Installation is an explicit persisted state: missing binaries, UAC refusal or a missing previously
registered service are errors, not permission to create empty protection state. Main does not
automatically overwrite installed executables or reinstall after an explicit uninstall. Moving main
or updating these companion executables requires explicit uninstall/reinstall from the complete new
package. The MSIX merely contains the components; it does not own the separately registered SCM service.

## Two independent decisions

| Condition | Guardian service | Main network protection |
| --- | --- | --- |
| Main absent, disconnected or stale heartbeat | Terminate saved selection | Main is absent/unresponsive |
| Main online, TUN not applied / core exited / takeover route absent | Terminate selection | Startup/transition protection also remains |
| Main online, TUN ready, website round in progress | No guardian termination | Retain last completed network verdict |
| Main online, TUN ready, confirmed website outage | No guardian termination | Terminate selection |
| User stops guardian | No guardian termination | Continues independently if main is running |

Main sends a fresh heartbeat each second. Service wakes on disconnect and otherwise evaluates
each second; a monotonic three-second lease detects a hung main even when its pipe remains open.
It checks the exact main PID/creation time/path and independently inspects the reported owned
sing-box PID/creation time/path, TUN interface/address and IPv4/IPv6 default takeover routes.
No process-name search establishes main liveness. Service does still enumerate process snapshots
to find protected applications and track descendants; IPC does not eliminate that required work.

The tray uses the service verdict: red means protecting or unable to confirm state, normal means
main/TUN ready, gray means an explicitly stopped/disabled/uninstalled service. A running service
with an IPC or termination error stays red. Neither probe progress nor network health controls this badge.

## Protocol and commit order

The local asynchronous named pipe has an explicit SYSTEM/administrators/owner ACL, rejects remote
clients and denies the network SID. It does not grant the ordinary owner pipe-instance creation rights.
Windows supplies peer process IDs; both ends validate SID and full executable path. Status/history
requests can come from the authorized owner's tray; only the registered main path can publish readiness.
The client checks the installed service executable and LocalSystem identity before sending state.

Messages are length-prefixed source-generated JSON, bounded to 4 MiB, with a protocol version,
random session identifier and increasing sequence. Main connects with a full validated policy;
the service commits it to SQLite before acknowledging. Each heartbeat references that policy revision.
Reconnection and policy change revoke the previous ready state. Replays, old connections and late
disconnects cannot renew or revoke a replacement session. Never persist a ready lease across service restart.

Before main stops/restarts TUN, it marks its heartbeat not ready and sends `prepare`. The service
revokes the lease, performs a protection sweep, saves tracking/history and returns the result. Main
requires the current revision, protecting state and zero failures, then also checks its local sweep.
Failure cancels planned exit or TUN transition and keeps the UI available. This handshake is skipped
only when the user explicitly stopped/disabled/uninstalled the guardian; local protection still runs.

## Durable scope and history

`%ProgramData%/EgressController/Guard/<SID-hash>` is writable only by SYSTEM/administrators and
readable by its registered owner. It contains:

- `guard.db`: service-owned SQLite WAL database, `synchronous=FULL`, transactional policy and
  tracking/history checkpoints. Bootstrap writes occur only before service installation.
- `desired-policy.json`: main's latest desired selection, also updated while the service is manually
  stopped. Service imports it before the next sweep, including on restart. Stable revisions are derived
  from normalized paths/roots/exclusions, not network verdicts or application routing choices.
- `registration.json`: main location, installation state, startup choice and manual-stop boot ID.
- `service-error.log` and `management-error.log`: host and management failure details.

JSON writes flush before atomic replacement; sidecar file locks serialize reads/writes across main,
service and tray. Runtime mailbox/registration failures revoke readiness and retain the last saved
selection rather than silently stopping enforcement. Corrupt main profiles are not overwritten with
defaults. An unreadable SQLite policy cannot supply a trustworthy scope: startup fails visibly and
SCM recovery retries; this is a reported protection failure, not a claim that applications are blocked.

The profile retains cached full EXE paths and `directoryRoots`. Every process sweep matches all
instances at those paths plus running EXEs beneath selected roots, including later updates. This is
directory recursion, independently of parent/child process tracking. Descendants are tracked while
healthy as well as unready; PID and creation time survive a service restart so already observed
orphans remain protected. A deselected source drops its tracked descendants. A child never observed
before its parent exits cannot be reliably reconstructed from process names.

LocalSystem filtering limits termination to the registered user's SID, and checks ownership again
before termination. The underlying adapter validates creation time on the termination handle. Known
control-plane executables and proxy cores are excluded. Failed identity/ownership inspection and
termination are visible; identical failures continue to retry without flooding the log.

Guardian history retains 24 hours and at most 200 events. Main has a separate network-protection
history. Clearing history preserves identities and retry state; healthy sweeps also expire old records.

## User control and recovery

Both UIs offer install, start, stop, force stop, stop-and-disable, uninstall and startup preference.
Management writes manual intent before sending SCM commands, so even a failed stop is respected by
recovery. Force stop verifies the SCM own-process service, expected executable and creation time.

- Stop: no automatic restart in the same Windows boot, including reopening main and SCM failure recovery.
  Explicit Start clears the marker. Next boot follows the startup preference.
- Disable startup: changes service startup and the owner's tray Run entry; does not stop a running service.
- Uninstall: marks uninstalled, removes the Run entry and service, retains data; main does not reinstall automatically.
- Crash without manual stop: SCM restarts after 3, 10, then 30 seconds. Readiness always starts false.

Routine UI failures are shown with their cause. UAC-required actions from a normal tray execute a
fixed companion helper command; no arbitrary shell command or executable is accepted by the service.
SCM/service shutdown and explicit stop terminate cleanly; a nonzero service-host exit requests recovery.

## Verification and limits

Tests use fake process snapshots, identities, TUN facts, time, service operations, registration stores
and IPC request dispatch. Real SQLite/atomic-file tests use unique disposable temporary directories.
Tests cover startup, loss/recovery, replay/stale sessions, policy ACK failure, failed handoff, descendant
restoration, PID reuse, user filtering, directory updates, state corruption, service controls and UI badges.
Product adapters reject live operations under `EGRESS_MOCK_ONLY=1`.

Packaging publishes App, GuardService and GuardTray separately with NativeAOT. Both companion
executables run `--mock-smoke` under the mock fence: fake lifecycle plus source-generated JSON and
private SQLite, returning before Avalonia, SCM or pipe initialization. This does not constitute a real
service installation, boot, tray rendering or live TUN test; those are intentionally excluded.

Termination is reactive. Scheduling delays, startup before the service runs, inaccessible processes,
service failure or explicit user disablement remain protection gaps; no zero-packet guarantee or kernel
firewall claim is made. Unselected applications are outside this mechanism.
