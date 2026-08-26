# Haas VF-3 NGC no-motion commissioning pack

## Safety status

These programs are for supervised commissioning only. They contain no axis motion, spindle start, coolant command, tool change, work-offset write, tool-offset write, or production-success prefix.

Keep Meimad CNC verification disabled. Run only after the machine is stopped, the work area is clear, and the responsible Haas/HFO person has reviewed the files for the installed NGC software.

The existing `1500.CNC` is a real machining program and is **not part of this no-motion test pack**.

## Files to upload

| File | O-number | Purpose |
|---|---:|---|
| `9010.CNC` | O09010 | Candidate protected DPRNT probe |
| `9011.CNC` | O09011 | Nested macro wrapper |
| `1234.CNC` | O01234 | Direct caller, identity 123401 |
| `4321.CNC` | O04321 | Second direct caller, identity 432101 |
| `1235.CNC` | O01235 | Nested caller, identity 123501 |

The six-digit values are test release identities supplied through `A...`; they are not intrinsic active-program identities and must not be treated as proof of the caller O-number.

## Controller preparation

1. Confirm the Macro option is available.
2. Confirm the approved protected program numbers are O09010 and O09011.
3. Set Haas DPRNT TCP settings 261/263 to the commissioning receiver.
4. Start the passive capture script before running a caller.
5. Temporarily turn Setting 23 off only to load/inspect O09010 and O09011; then turn it on.
6. Verify ordinary operator access cannot view, edit, delete, or copy the protected programs.
7. Use Memory mode and run only one caller at a time.

Example capture command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\haas-verification-spike.ps1 `
  -MachineLabel VF3SS-COMMISSIONING `
  -HostName <machine-ip> `
  -MdcPort 5051 `
  -DprntPort 8080 `
  -CaptureSeconds 120 `
  -OutputPath .\.diagnostics\haas-verification\vf3ss-generic-hook-001.json
```

Do not pass any secret, response, nonce, state, or release-token variable to the script.

## Expected lines

O01234:

`MEIMADSPIKE/CASE/A/PROBE/9010/IDENTITY/123401`

O04321:

`MEIMADSPIKE/CASE/B/PROBE/9010/IDENTITY/432101`

O01235 through O09011:

`MEIMADSPIKE/CASE/D/PROBE/9010/IDENTITY/123501`

The probe deliberately uses `MEIMADSPIKE/`, never `MEIMAD/V/1`; therefore the production event parser must not ingest these lines.

## Test sequence

1. Run O01234 five times; capture one line per run.
2. Run O04321 five times; confirm the supplied identity changes without editing O09010.
3. Run O01235 five times; confirm the nested G65 call preserves the supplied identity.
4. Start O01234 and press Reset during execution. Record whether a partial/stale line appears.
5. Power-cycle the control, restart capture, and repeat O01234. Record formatting and connection behavior.
6. With Setting 23 on, attempt ordinary operator view/edit/delete/copy of O09010 and O09011 and record the result.
7. Compare external Q500 observations with the selected caller before and after each case.

## Pass boundary

This pack can prove protected storage behavior, G65 argument transport, nested forwarding, DPRNT syntax/delivery, and Reset/power-cycle observations. It cannot prove an intrinsic caller-program variable, cryptographic verification, offset loading, or a production interlock.

Do not add G10 offset writes or a production `MEIMAD/V/1` event until the controller-specific program numbers, variable ranges, input method, arithmetic/rounding, alarm behavior, and cleanup have been approved and physically tested.
