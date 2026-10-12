# Contributing

EgressController is a Windows-only .NET application. Keep selected applications guarded until
TUN takeover, the selected configuration, both live adapter bindings, Cloudflare DoH and the
proxy adapter's Internet probe are healthy. Check Cloudflare once a minute (or manually), and
probe the bound proxy-direct outbound every ten seconds after each completed round. Keep the
last completed result during rechecks: failures close selected processes, success permits them
only when all other conditions pass. Canceled or stale results cannot change health. Adapter
outages must be detected from live snapshots, never the cached configuration bindings.
DNS uses only Cloudflare; probes never reload TUN or change resolvers. Keep explicit adapter/port
bindings and proxy loop prevention. Recovery downloads must remain possible while application
protection is active. Do not change system network state during tests.

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
