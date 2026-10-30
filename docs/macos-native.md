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
operations. `EGRESS_MOCK_ONLY=1` also denies explicit live gates. The UI preview must enter its mock
mode before composition and inject in-memory adapters. The bridge does not export DNS writes,
route writes, process termination, startup changes, service installation or Xray execution.

## ABI and ownership

`native/macos/include/egress_mac.h` defines ABI 1. Imports use cdecl, fixed-width status values,
UTF-8 input strings and caller-owned byte buffers. A null buffer with zero capacity queries the
required byte count. `BUFFER_SMALL` never writes the output buffer. Counts exclude a trailing NUL.
The caller allocates and frees its own output; native allocations never cross the ABI. `realpath`
memory is freed by the same native function. Objective-C exceptions are converted to stable status
codes. C# bounds output at 16 MiB and allows at most three attempts if a snapshot changes while copying.

## Privileged helper is not shipped

The current delivery defines an AOT-friendly protocol and validation boundary; it does **not** install
or implement a privileged launchd server. `UnavailableMacHelperTransport` always reports
`helper-not-installed`, with `TunTakenOver=false` and `DnsRestored=false`. A mock may never convert
this into a live readiness claim.

IPC frames use a four-byte network-order payload length and at most 1 MiB UTF-8 JSON. The request
validator requires a verified peer executable, authorized UID, PID plus process-start identity,
session nonce and increasing sequence. Only inspect, apply-validated-config and stop-and-restore
operations exist. Apply requests reference owned configuration/core directories, SHA-256 content
digests and two distinct stable network service IDs. No shell command, arbitrary launch arguments,
subscription URL or generic filesystem operation is accepted.

`MacHelperConfigurationValidator` independently checks the compiler's generated JSON. It admits one
native TUN inbound and no local HTTP/SOCKS/control listeners, API, arbitrary file logs or unreviewed
fields. The TUN global interface controller is explicitly disabled with `autoOutboundsInterface: ""`
(omission would mean `auto` in the pinned Xray version). Each Freedom/proxy/DNS outbound has its own
explicit binding: home Freedom to home, all other dialers to non-home, with ForceIP. Extra direct
fallbacks, direct/cyclic dialer references, unbound interfaces, foreign DAT paths, duplicate fields,
and a direct global catch-all are rejected. Built-in DNS must be routed to non-home before DNS
hijacking. Tests compile a real policy through `XrayConfigCompiler` and validate the generated bytes
before exercising tampered configuration variants.

Before a real helper can be enabled, its server must supply actual peer credentials and signature/
installation identity verification; enforce owner-only Unix socket permissions; open content with
no-follow, ownership and mode checks; hash the opened descriptors to avoid path races; validate the
generated Xray configuration semantics; restrict all network mutations to that plan; durably record
the DNS/route recovery journal before mutation; and authenticate recovery after crashes/reboots.
The current lexical validator is not a substitute for those OS-level checks. DNS snapshot framing
validation is also not permission to restore an untrusted property list. Real TCP/UDP/DNS/IPv4/IPv6,
dual-interface failure behavior, privileges and recovery have not been exercised by mock tests.

## Safe validation

```sh
dotnet build src/EgressController.Mac -c Release
EGRESS_MOCK_ONLY=1 dotnet run --project tests/EgressController.Mac.Tests -c Release
python3 build/Build-MacNative.py
```

The managed suite uses only in-memory application trees, artwork sources and protocol data. It never
enumerates installed applications or reads live interfaces/DNS. The native script builds the dylib
and runs CTest probes limited to ABI version, syntactic path validation and buffer-contract checks.
These tests do not invoke the application, artwork or SystemConfiguration exports. Assertions stay
enabled in Release. This proves compilation and the tested boundary contracts; it does not prove
that a privileged helper or real TUN is operational.

The affected managed suite passed **81 mock tests** after the executable-type correction. Its
synthetic stream matrix covers thin 32/64-bit and FAT32/FAT64 byte orders, non-executable file types,
universal dylibs, mixed slices, Java magic, truncation, overlap, integer bounds, malformed commands
and execute-mode exclusion. No installed application file was read for this verification.

`artifacts/macos-native/libEgressMacNative.dylib` must be staged beside the managed/AOT executable
when packaging live platform access. No runtime library is installed globally by the build script.
