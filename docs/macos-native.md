# macOS platform boundary

The macOS client uses a small independent C ABI instead of embedding Objective-C objects in C#.
`EgressController.Mac` targets `net10.0`, AnyCPU, with source-generated `LibraryImport` and JSON
serialization. It does not reference a Windows project. The implementation is original MIT code;
ArcForges DesktopPlatform was read for its CMake / stable C ABI / C# interop architecture only.
No ArcForges source or native dependency graph was copied.

## Application ownership

`MacApplicationCatalog.DiscoverAsync` reads app metadata beneath `/Applications`,
`~/Applications` and `/System/Applications`. Organizational folders are traversed; traversal stops
at each `.app`, so an Electron helper never becomes another top-level application. Discovery
does not inspect Mach-O files, select applications, or create runtime membership. Names and icons
are presentation metadata; identity combines the exact bundle identifier and canonical bundle path.

Only `BuildInventoryAsync` recursively inspects a selected `.app`. Files must have executable
mode bits and a bounded Mach-O inspection confirming `filetype=MH_EXECUTE`, not just a magic value.
Thin 32/64-bit headers support both byte orders. FAT32/FAT64 tables are bounded to 64 architectures;
offsets, sizes, alignment, overlap, CPU identity and every thin slice are checked. All slices must
be executable. Dylibs/frameworks, bundles, objects, truncated or malformed commands, overlapping
slices and Java class data sharing `CAFEBABE` magic are refused. This is format inspection, not
code-signature or kernel-loadability verification. The layout follows Apple's
[Mach-O loader](https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/loader.h)
and [FAT header](https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/fat.h)
definitions. All accepted paths are canonical and inside
that bundle. Symlink aliases are not enrolled; a link escaping the bundle produces an incomplete
scan diagnostic. Shared Applications/bin roots are refused. Directory and entry budgets and caller
cancellation bound discovery and inventory. Unreadable entries produce explicit diagnostics.

`MacManagedApplication` saves exact paths. Route changes and metadata refreshes preserve all saved
paths, including missing/older ones. A changed primary executable becomes pending until explicit
refresh. Only accepting a complete explicit inventory can add paths; it retains old membership.
Matching is exact and ordinal. There is no process ancestry, helper inheritance, wildcard directory
membership, command execution during discovery, or TUN restart triggered by a process starting.

## Native reads and icon cache

The bridge uses `NSBundle` for exact bundle metadata, `realpath` for canonical paths and
[`NSWorkspace.icon(forFile:)`](https://developer.apple.com/documentation/appkit/nsworkspace/icon(forfile:))
for real app artwork. C# schedules icon reads off the UI thread, shares in-flight reads, limits native
concurrency to four and keeps a bounded 256-entry LRU. Each caller receives its own PNG buffer.
Cancelling one waiter does not cancel/dispose another waiter's source operation. Faulted cache entries
can retry. Preview icons come from fixtures and never call this adapter.

Network reads use SystemConfiguration service IDs
([`SCNetworkServiceGetServiceID`](https://developer.apple.com/documentation/systemconfiguration/scnetworkservicegetserviceid(_:))).
BSD names, link state and addresses are current observations, not durable route identity. DNS snapshots
preserve whether the DNS protocol existed, whether it was enabled and the exact configured dictionary
serialized as a property list. An absent configuration differs from an explicitly empty configuration.
The recovery model preserves unknown DNS keys rather than reconstructing a partial server list.

Every physical/native read requires `LiveOperationGate.ForExplicitLiveUse()`. A new gate denies
operations. `EGRESS_MOCK_ONLY=1` also denies explicit live gates. The explicit `--preview` and
`--mock-smoke` entries enter mock mode before composition and inject in-memory adapters. The default
desktop entry uses production adapters without elevating the GUI. DNS mutation is exposed only to
the root helper, with a second native check rejecting both non-root callers and mock mode.
Resource sockets use native `IP_BOUND_IF` / `IPV6_BOUND_IF` before connect; binding errors fail the
request. The bound DNS resolver never invokes the system/default resolver.

## ABI and ownership

`native/macos/include/egress_mac.h` defines ABI 1. Imports use cdecl, fixed-width status values,
UTF-8 input strings and caller-owned byte buffers. A null buffer with zero capacity queries the
required byte count. `BUFFER_SMALL` never writes the output buffer. Counts exclude a trailing NUL.
The caller allocates and frees its own output; native allocations never cross the ABI. `realpath`
memory is freed by the same native function. Objective-C exceptions are converted to stable status
codes. C# bounds output at 16 MiB and allows at most three attempts if a snapshot changes while copying.

## Privileged helper lifecycle

`tools/EgressController.MacHelper` supplies the root launchd service; `MacHelperClient` and
`MacXrayCoreRuntime` implement the production connection and `ICoreRuntime` adapter. Nothing installs
on startup. The explicit install/apply action invokes `MacHelperInstaller`, whose administrator
authorization first copies the packaged helper, native library and plan into a root-owned random
0700 directory. Each copied payload is SHA-256 checked before executing the root-owned installer.
The existing shared system directory retains its normal 0755 protection; symlinks and writable or
foreign-owned parents are rejected. Installation pins the authorized GUI executable, UID/GID and
official Xray executable digest in root-owned metadata. A changed GUI/core identity requires another
explicit authorized installation. The server only executes its verified private core copy.

Protocol v2 uses a four-byte network-order length and at most 1 MiB UTF-8 JSON, over a local Unix
socket. The parent is root owned; the socket is 0600 and owned by the authorized user. The client
checks ownership, root peer identity and installed helper code identity. The helper obtains actual
peer UID/GID/PID and process start identity from the kernel, checks the pinned GUI digest and verifies
the running code signature/CDHash against that pinned executable. Generic interpreter hosts cannot
stand in for the GUI. Each connection has a session nonce and increasing sequence; replies echo
both and must be fresh. A failed read clears healthy status. Inspect, validate, apply, heartbeat and
stop/restore are the complete operations; no shell command, arbitrary arguments or subscription URL
is accepted through runtime IPC.

The GUI stages whole generations with a digest manifest. The helper opens source descriptors with
`O_NOFOLLOW`, verifies ownership/type/mode/size, copies into its root-private generation directory,
and verifies copied content. It validates configuration and DAT references before running the
pinned Xray with `run -test`; a successful check precedes stopping/replacing the previous runtime.
The persistent authenticated connection sends heartbeats. EOF, client instance loss, core exit,
confirmed TUN failure, helper termination or explicit stop drive independent recovery. The helper's
verdict contains main-instance and TUN facts, never Internet/DoH reachability.

Before DNS changes the helper durably writes its exact recovery snapshot. SystemConfiguration changes
are committed under a preferences lock; restore reinstates each original plist dictionary, protocol
presence and enabled state. Recovery runs after crashes/reboots as well as normal stops. TUN status
requires a running core, an up interface with the pinned Xray Darwin local address (gateway + 1),
and global IPv4/IPv6 route ownership on that interface. The pinned core's protected default uses eight
prefixes per family (`1/8`, `2/7`, `4/6`, `8/5`, `16/4`, `32/3`, `64/2`, `128/1`). Host-only,
interface-scoped, rejected, wrong-interface or malformed routes cannot claim takeover.

`MacHelperConfigurationValidator` independently checks the compiler's generated JSON. It admits one
native TUN inbound and no local HTTP/SOCKS/control listeners, API, arbitrary file logs or unreviewed
fields. The TUN global interface controller is explicitly disabled with `autoOutboundsInterface: ""`
(omission would mean `auto` in the pinned Xray version). Each Freedom/proxy/DNS outbound has its own
explicit binding: home Freedom to home, all other dialers to non-home, with ForceIP. Extra direct
fallbacks, direct/cyclic dialer references, unbound interfaces, foreign DAT paths, duplicate fields,
and a direct global catch-all are rejected. Built-in DNS must be routed to non-home before DNS
hijacking. Tests compile a real policy through `XrayConfigCompiler` and validate the generated bytes
before exercising tampered configuration variants.

The implementation is compiled and checked through injected tests. Real administrator prompts,
installation, TCP/UDP/DNS/IPv4/IPv6 takeover, dual-interface failure behavior and system restoration
are not exercised by automated verification. Those remain explicit user integration acceptance;
compilation or a mocked green state is never reported as evidence of a working live takeover.

## Safe validation

```sh
dotnet build src/EgressController.Mac -c Release
EGRESS_MOCK_ONLY=1 dotnet run --project tests/EgressController.Mac.Tests -c Release
python3 build/Build-MacNative.py
```

The managed suite uses only in-memory application trees, artwork sources and protocol data. It never
enumerates installed applications or reads live interfaces/DNS. The native script builds the dylib
and runs CTest probes limited to ABI version, syntactic path validation, buffer-contract checks
and synthetic routing-table byte classification.
These tests do not invoke the application, artwork or SystemConfiguration exports. Assertions stay
enabled in Release. This proves compilation and the tested boundary contracts; it does not prove
that a privileged helper or real TUN is operational.

The affected managed suite covers **143 tests**, including the pure native deployment probe. Its
synthetic stream matrix covers thin 32/64-bit and FAT32/FAT64 byte orders, non-executable file types,
universal dylibs, mixed slices, Java magic, truncation, overlap, integer bounds, malformed commands
and execute-mode exclusion. Additional tests cover injected IPC/runtime flows, malformed and stale
replies, heartbeat timeout/recovery, installation-plan quoting and default mock gate denial. No
installed application, real socket peer, real route or DNS state was read for this verification.

## IDE and publish deployment

`EgressController.Mac.csproj` imports `build/MacNative.targets`. On macOS, an ordinary build first
compiles the native bridge through CMake, then exposes the generated library as transitive content
with `CopyToOutputDirectory` and `CopyToPublishDirectory`. Desktop, helper, tests and other
`ProjectReference` consumers receive `libEgressMacNative.dylib` beside their assemblies without a
loader-path environment variable. The native loader retains its assembly/safe-directory search
policy; it does not search arbitrary repository/cache paths.

Build trees live beneath the Mac project's `obj/mac-native/<configuration>/<architecture>`.
Native source/header/build-script changes invalidate the incremental target; a missing library is
rebuilt. Design-time IDE evaluation does not run CMake. The script uses the PATH CMake or standard
Homebrew install paths so Finder-launched Rider does not require a shell profile. These ordinary
builds only compile the library. Standalone `Build-MacNative.py` still builds and runs the pure ABI
CTest suite in `artifacts/macos-native`; no library is installed globally.

Packaging consumes the exact native payloads produced by Desktop/helper publish and checks they
match before signing; it never overwrites them with the standalone verification artifact.
`NativeDeploymentTests` loads the transitive output through the production assembly's normal
native search rules and calls only the constant ABI-version export, even in mock-only mode.

The Desktop `Properties/launchSettings.json` provides a first, parameterless production profile
and an explicit `--preview` profile with `EGRESS_MOCK_ONLY=1`. Rider's **Generate Configurations**
imports these when an older run configuration already exists. Production GUI debugging does not
ship/install the helper. Use the packaged NativeAOT `.app` for explicit live takeover; the
authenticated helper boundary is unchanged.

## Passive connection observations

The helper continuously drains Xray's inherited stdout/stderr pipes. `XrayConnectionOutputPump`
keeps at most 4,096 characters for one line, discards oversize lines through the next newline and
continues draining even if parsing fails. `XrayConnectionObserver` retains at most 128 sanitized
records in memory; raw output is never sent to the GUI or written to per-connection disk logs.
Only the pinned TUN/access/dispatcher message grammar is admitted. Actual source/destination,
transport, outbound and rule tags are retained. Session correlation is scoped to a generation;
new TUN processing events receive distinct identities even if a core session identifier collides.
Non-TUN access records, credential-shaped destinations and unrecognized output are ignored.

`MacHelperWireReply.Connections` is optional and piggybacks on existing authenticated replies.
It does not introduce a network control listener or an observation request that could interrupt
the main-instance heartbeat. The helper catches snapshot failures outside lifecycle handling.
The client exposes `ConnectionSnapshot` from a detached bounded cache; malformed optional JSON,
invalid rows and missing snapshots only mark observations unavailable. They do not invalidate an
otherwise valid helper status, close the authenticated connection or restart/restore the core.
The payload's cumulative string and record limits keep escaped JSON below the existing 1 MiB
frame limit. Availability, known record eviction and capability limitations are explicit.

The stock v26.10.29 binary does **not** supply a complete active-connection table, close events,
per-connection byte counters or successful process PID/path observations. These fields remain
unknown; an access record never implies a currently active connection or zero traffic. The GUI
can label a recorded application rule but cannot turn that into an observed executable/PID.
This lossy history must not be fed to the authoritative usage ledger or guardian.

These limits are grounded in the pinned official sources: the [TUN handler](https://github.com/XTLS/Xray-core/blob/v26.10.29/proxy/tun/handler.go)
uses counters shared by the inbound; the [access-message model](https://github.com/XTLS/Xray-core/blob/v26.10.29/common/log/access.go)
contains endpoint/detour facts, while the [dispatcher](https://github.com/XTLS/Xray-core/blob/v26.10.29/app/dispatcher/default.go)
emits session/rule decisions. The [routing API registration](https://github.com/XTLS/Xray-core/blob/v26.10.29/app/router/command/command.go)
does not attach a routing-statistics channel, and its protocol has no per-connection counters or
process identity. Adding a Unix-domain gRPC endpoint therefore cannot supply the missing facts.

Synthetic tests cover source/rule association, IPv6, session collisions, sensitive-line rejection,
bounded concurrent snapshots, pipe failure/oversize recovery, malformed optional JSON, worst-case
escaped payload size and independent healthy helper status. Controller tests instantiate only an
in-memory environment: telemetry faults cannot request restoration. No core, socket, installed
helper, network state or user configuration is accessed by these tests.

The independent configuration validator also admits the compiler's exact Shadowsocks obfs/http
RAW header mapping and explicit boolean proxy TCP Fast Open. It rejects other header types,
response overrides, extra headers, Host injection/port mismatch, TLS combinations, non-Shadowsocks
disguises and system-outbound TCP Fast Open. Generated policy fixtures pass before tamper tests.

## Resource bootstrap proxy

Resources bootstrap through an explicit `SystemProxy` exit; they do not require selected interfaces,
the new Xray core or another client's TUN. The core's own home/non-home binding remains independent.
`NativeMacResourceProxyResolver` captures a static CFNetwork decision for each HTTPS destination,
including redirects, against one lazily captured environment/static binary-plist snapshot per operation, then gives the transport one fixed proxy. Cached resources do not capture proxy settings. Failure never tries the next proxy
or a direct fallback. Only an explicit `kCFProxyTypeNone` decision or an applicable environment
bypass selects direct access. Static HTTP/HTTPS tunneling decisions use HTTP CONNECT; SOCKS uses
SOCKS5. PAC, unknown types, empty results and read errors fail closed with fixed actionable messages.
The bridge never downloads/executes PAC or consults Keychain.

Applicable `https_proxy`/`HTTPS_PROXY` and then `all_proxy`/`ALL_PROXY` override system settings.
Malformed nonempty overrides fail closed. `no_proxy`/`NO_PROXY` exact-host and leading-dot bypasses
follow the .NET environment-proxy behavior; lowercase values take precedence. An HTTP-only override
does not apply to these HTTPS resources. Credentials remain in proxy authentication, are removed
from the proxy URI, and cannot appear through the native proxy DTO's `ToString()`.

This resolver deliberately avoids `HttpClient.DefaultProxy`: the [.NET macOS implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/MacProxy.cs)
does not recognize static SOCKS decisions, cannot distinguish missing PAC results from direct access,
and executes PAC synchronously. Static native decoding avoids those fallback/cancellation gaps.
The transport requires an explicitly supplied resolver; it has no implicit default-proxy path.

The native proxy export is compiled but never invoked during automated validation. Pure JSON and
injected environment tests cover static decisions, ordered fallback rejection, PAC/invalid errors,
HTTPS/ALL overrides, bypasses, secret redaction, capacity limits and the default denied gate.
