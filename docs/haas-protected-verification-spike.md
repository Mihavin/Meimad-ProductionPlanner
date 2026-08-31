# Haas V10 setup-verification commissioning boundary

V10 is a no-motion bench candidate. Generate it with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-haas-verification-local-config.ps1 `
  -MachineId <planner-machine-id> -MachineLabel HAAS-VF3SS `
  -OutputPath .\.diagnostics\vf3ss-verification-v10.local.json `
  -MacroVersion 10 -SampleNcIdentity 742915 -SampleOffsetReleaseToken 782703

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\new-haas-verification-v10-bench-pack.ps1 `
  -ConfigPath .\.diagnostics\vf3ss-verification-v10.local.json `
  -OutputDirectory .\.diagnostics\haas-v10-bench\HAAS-VF3SS `
  -AcknowledgeBenchOnlyCandidate
```

The candidate has no Machine credential. Machine configuration requires the Planner MachineID, fixed CNC IP, and controller MAC. This identifies the connection only; NC and Offset Loader authorization remain exact separate checks.

Required bounded physical observations before enablement:

1. OLC creates ARMED and the tablet displays the response after more than the configured timeout duration without expiry.
2. The first NC hook emits SVR, starts PENDING, and accepts the exact response within the configured window.
3. A second start of the successful exact binding produces no second prompt.
4. A new Offset Loader supersedes the earlier binding.
5. Wrong NC, release, nonce, response, and late response fail closed before motion.
6. Reset/E-stop/power-cycle and sequence clear/reset do not create Server authority; sequence discontinuities appear only as diagnostic evidence.

Keep verification disabled until the exact generated package has completed internal source review and this physical matrix.
