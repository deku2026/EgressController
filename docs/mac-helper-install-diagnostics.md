# macOS helper authorization and installation diagnostics

The November 1 installation failure occurred after administrator authorization. The matching system crash report recorded the staged installer running with UID 0 and aborting on an unhandled managed exception. A synthetic temporary-file metadata probe reproduced the native wire mismatch: `S_ISREG` and the other C file-type predicates were boxed as integer `NSNumber` values, so JSON contained `1/0` where `MacFileSecurityInfo` requires `true/false`. The first `ReadFileInfo(planPath)` therefore failed before installation created any persistent helper files.

File-kind flags now use explicit Foundation booleans. DNS snapshot presence/enabled flags had the same expression-boxing defect and now use explicit booleans too. Managed JSON remains strict; numeric booleans are not accepted to hide broken native contracts.

## Evidence and validation

- `egress_mac_file_info_fixture` calls the production metadata serializer with a synthetic mode. It performs no stat, UID, process, socket or network queries and remains usable with `EGRESS_MOCK_ONLY=1`.
- The native ABI suite checks JSON boolean tokens. Managed tests and the packaged NativeAOT helper's `--mock-smoke` deserialize regular-file, directory, socket and symlink fixtures using the production source-generated context.
- Installer error classification distinguishes canceled authorization, invalid installation data, abnormal termination and other install errors. Output capture drains both pipes while retaining bounded tails.
- Injected UI checks fail the authorization stage, verify the pinned error at 1260×860 and 980×700, exercise log-directory pointer input and diagnostic navigation, and check that retries clear old errors. No production backend is constructed.
- CMake cache source/build paths are checked after a worktree move. `--fresh` regenerates relocated build metadata, so Rider rebuilds in the current `.worktree/mac-xray-client/EgressController` checkout.

These checks do not install launchd services or validate live TUN takeover. Only explicit mock entrypoints run during packaging.

## Follow-up: installed helper cannot be authenticated

At 04:28 and 04:29 on November 1, `osascript-exit=0` confirmed installer success, but the GUI repeatedly failed in `NativeMacPrivileges.ReadPeer` and reported readiness timeout. Read-only inspection found launchd's root helper running continuously with its socket present. The original `proc_pidinfo(PROC_PIDTBSDINFO)` call returned `EPERM` when the ordinary user queried this root process. This occurred before any TUN request, so stopping Clash could not resolve it.

Process identity now uses `sysctl(KERN_PROC_PID)` and `proc_pidpath`. Both were verified read-only against the existing root helper as the ordinary user. The actual updated native library returned the correct root UID, PID, start time and installed executable path. Metadata is read before and after the path query to reject PID reuse or credential changes. Socket credentials, root-owned endpoint checks, code signatures, authorized GUI hash and process-instance checks remain enforced. The GUI also rechecks the root process after code verification.

The native regression suite injects synthetic metadata/path callbacks into the production lookup: root identity, denied access, exited/truncated processes, invalid paths and changed UID/GID/start time. It never queries a live PID. A separate unprivileged AppleScript test runs only synthetic `printf`/`exit` commands to verify both successful and failed installer stderr capture. Readiness tests retain the identity diagnosis across deadline cancellation, clear it on recovery and never infer TUN health from an error code.

Clash Verge follows the same service-before-core pattern: its [service module](https://github.com/clash-verge-rev/clash-verge-rev/blob/main/src-tauri/src/core/service.rs) installs the privileged service and starts the core over authenticated IPC; its [TUN reconciliation](https://github.com/clash-verge-rev/clash-verge-rev/blob/main/src-tauri/src/feat/tun.rs) disables TUN when service availability cannot support it. This comparison supports fixing Egress's service authentication; it does not require replacing Xray or elevating Rider.

## Log coverage

The GUI creates `~/Library/Logs/EgressController/lifecycle.log` from startup, before coordinator/runtime preparation. It retains the existing 256 KiB bound and private file permissions.

Events include startup/version, explicit connection request and resource port, stage transitions, helper artifact identity, authorization request/exit, bounded installer failure output, helper readiness/timeout, authenticated transport failure/recovery, helper state transitions, connection completion/recovery, and operation/subscription failures. Installer traces identify parent verification, payload copies, manifest rewriting, metadata/plan reading, payload verification, launchctl operations, publication and rollback. `launchctl` failures preserve command, exit code and bounded output. The helper returns a nonzero exit with a structured diagnosis instead of aborting on an unhandled exception.

Generic exceptions record type, HResult, structured JSON field/position and native/socket error codes. Resource failures preserve resource, route, port, stage and timeout. Arbitrary exception messages and configuration contents are omitted. Installer command output is bounded and redacts URL/credential assignments. No password is collected by the application. Repeated identical helper transport/state failures are suppressed; telemetry samples and per-connection streams are not forwarded to the lifecycle log.

Native identity/file failures now preserve operation, native status and errno; GUI hash/identity rejection records a fixed reason. Readiness reports distinguish identity verification, endpoint queries and generic timeouts. AppleScript merges installer stderr into stdout before running the script, preserving success-stage traces as well as nonzero failure status.

The service writes sanitized startup, recovery, socket readiness, peer authentication/rejection, command result and shutdown events to `/Library/Logs/EgressController/helper.log` (root-owned, bounded to 256 KiB, readable by the GUI). Log writes verify root-owned ancestors and atomically replace the file; failures remain best effort. “打开日志目录” copies a bounded root-owned snapshot to `~/Library/Logs/EgressController/helper-service.log` beside `lifecycle.log`. This service log becomes available after the updated helper is installed.

Logging is best effort and cannot change authorization, rollback, core or protection decisions. Current UI errors are separate from historical events. The wizard error panel stays outside the scrolling/disabled form, has a bounded selectable error area, and provides log/diagnostic actions. Diagnostic navigation dismisses the wizard without discarding selections; retry clears the old banner.

Rider and the GUI intentionally remain ordinary user processes. The system password prompt authorizes the installer only. Running Rider as root is neither necessary nor a remedy for an installer defect.
