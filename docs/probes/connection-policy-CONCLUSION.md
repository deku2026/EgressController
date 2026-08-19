# Connection Policy Probe — Step 03

## Result

**Conclusion: B — the user-mode API is not a safe per-instance backend for V1.**

`FwpmConnectionPolicyAdd0` can express outbound route policy and accepts application/user/package
conditions, but its supported condition set has no process PID or process start-time key. An
executable-path (`FWPM_CONDITION_ALE_APP_ID`) policy therefore applies to both a Managed instance
and an ordinary instance of the same executable. V1 must not enable this backend or claim raw
TCP/UDP per-session routing.

The probe deliberately performs no policy mutation. It opens and closes a filter-engine session
only, so it cannot leave a WFP policy behind. Run it on the target machine to record the current
permission result:

```powershell
dotnet run --project EgressController/probes/EgressController.Probe.ConnectionPolicy -c Release
```

The printed `FwpmEngineOpen0` result distinguishes standard-user access from an unavailable
engine. In either case the product decision remains fail-closed: HTTP/HTTPS continue through the
Local Proxy; raw TCP/UDP/QUIC have no per-instance guarantee.

The API scope and supported condition list are documented by Microsoft in
[FwpmConnectionPolicyAdd0](https://learn.microsoft.com/en-us/windows/win32/api/fwpmu/nf-fwpmu-fwpmconnectionpolicyadd0).
