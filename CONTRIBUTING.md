# Contributing

EgressController is a Windows-only .NET application. Keep selected applications guarded until
TUN takeover, the selected configuration, both live adapter bindings and both Internet probes
are healthy. Probe https://cp.cloudflare.com/generate_204 through the bound direct and proxy
outbounds every 30 seconds, independently and in parallel. On failure wait five seconds before
each of two retries. Only three consecutive failed attempts publish a failure; any success stops
retrying. Keep the last completed verdict throughout retries, including existing protection.
Success permits processes only when all other conditions pass. TUN loss and live adapter outages
still protect immediately. Canceled or stale results cannot change health. Adapter outages must be detected from live
snapshots, never the cached configuration bindings.
DNS configuration is fixed to Cloudflare. Do not add DoH health queries or make DNS response status
an application-protection condition. Internet probes never reload TUN or select DNS resolvers.
Keep explicit adapter/port bindings and proxy loop prevention. Recovery downloads must remain
possible while application protection is active. Do not change system network state during tests.

## Development setup

- Windows 10 version 2004 or newer (Windows 11 is recommended)
- The .NET SDK selected by `global.json`
- Visual Studio Build Tools with the Desktop development with C++ workload for NativeAOT
- A Windows 10/11 SDK containing `MakeAppx.exe` for MSIX packaging

```powershell
dotnet restore EgressController.slnx
dotnet build EgressController.slnx -c Release --no-restore -p:EgressMockOnly=true
./build/Invoke-Tests.ps1 -Configuration Release -NoBuild
```

Create local release artifacts with:

```powershell
./build/Package.ps1 -Version 0.1.0
```

This produces a NativeAOT portable ZIP and an unsigned validation MSIX. A production-installable
MSIX must be signed by a trusted certificate whose subject matches the manifest publisher.

## Pull requests

Keep pull requests focused, add tests for behavior changes, and describe any effect on routing,
process discovery, System Proxy ownership, or fail-closed behavior. Never commit runtime state,
connection logs, signing certificates, credentials, or machine-specific evidence.

Default builds and CI are mock-only. Never start a real TUN or kill user processes as validation.
Use `IProcessControl`, fake HTTP handlers, fake core clients, and injected lifecycle delegates.
