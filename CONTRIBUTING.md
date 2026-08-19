# Contributing

EgressController is a Windows-only .NET application. Changes should preserve its two core safety
properties: the selected eSIM route never falls back to the ordinary upstream, and System Proxy
state is restored only when EgressController still owns it.

## Development setup

- Windows 10 version 2004 or newer (Windows 11 is recommended)
- The .NET SDK selected by `global.json`
- Visual Studio Build Tools with the Desktop development with C++ workload for NativeAOT
- A Windows 10/11 SDK containing `MakeAppx.exe` for MSIX packaging

```powershell
dotnet restore EgressController.slnx
dotnet build EgressController.slnx -c Release --no-restore
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
