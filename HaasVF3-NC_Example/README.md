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
| `9012.CNC` | O09012 | No-motion response-vector runner |
| `9013.CNC` | O09013 | Public-key decimal-fold calculator used by O09012 |
| `1234.CNC` | O01234 | Direct caller, identity 123401 |
| `4321.CNC` | O04321 | Second direct caller, identity 432101 |
| `1235.CNC` | O01235 | Nested caller, identity 123501 |

The six-digit values are test release identities supplied through `A...`; they are not intrinsic active-program identities and must not be treated as proof of the caller O-number.

## Controller preparation

1. Confirm the Macro option is available.
2. Confirm the approved protected program numbers are O09010 and O09011.
3. Set Haas DPRNT TCP settings 261/263 to the commissioning receiver.
4. Use the Server's configured passive DPRNT listener before running a caller.
5. Temporarily turn Setting 23 off only to load/inspect O09010 and O09011; then turn it on.
6. Verify ordinary operator access cannot view, edit, delete, or copy the protected programs.
7. Use Memory mode and run only one caller at a time.

For the current secretless lifecycle and V10 generator, follow
`docs/haas-protected-verification-spike.md`. These older sample callers are not
V10 commissioning artifacts.

## Expected lines

O01234:

`MEIMADSPIKE/CASE/1/PROBE/9010/IDENTITY/123401`

O04321:

`MEIMADSPIKE/CASE/2/PROBE/9010/IDENTITY/432101`

O01235 through O09011:

`MEIMADSPIKE/CASE/4/PROBE/9010/IDENTITY/123501`

The probe deliberately uses `MEIMADSPIKE/`, never `MEIMAD/V/1`; therefore the production event parser must not ingest these lines.

## Test sequence

1. Run O01234 four times; capture one line per run.
2. Run O04321 four times; confirm the supplied identity changes without editing O09010.
3. Run O01235 four times; confirm the nested G65 call preserves the supplied identity.
4. Start O01234 and press Reset during execution. Record whether a partial/stale line appears.
5. Power-cycle the control, restart capture, and repeat O01234. Record formatting and connection behavior.
6. With Setting 23 on, attempt ordinary operator view/edit/delete/copy of O09010 and O09011 and record the result.
7. Compare external Q500 observations with the selected caller before and after each case.

The evidence JSON reports these repetitions under `identityTransportGrade`. Four intentional matching runs of each case are sufficient. `responseVectorGrade` remains `NOT_RUN` until O09012 is run.

## Response-vector bench test

O09012 and O09013 are a non-production, no-motion arithmetic candidate. They use only published test keys; they do not contain a Machine secret, accept an operator response, unlock execution, or emit an operational `MEIMAD/V/1` event. O09013 temporarily returns the calculated public-test response through `#10500`, leaves `G103 P1` active so the caller's DPRNT cannot look ahead past cleanup, and O09012 clears that variable before the first vector, after every emitted vector, and at normal completion.

Have the responsible Haas/HFO person approve O09012, O09013, the temporary variable `#10500`, `FIX` behavior, and the selected protected O-numbers for the installed software before loading them. Then start a fresh capture and run O09012 once. It must emit exactly V01 through V07; `responseVectorGrade.status` must be `PASS`. A Reset can interrupt cleanup, so confirm `#10500` is cleared before and after a normal run and record its state after a deliberate Reset. This bench code is not the production challenge or verification macro.

Physical result on 2026-08-26: the VF-3SS emitted exactly V01 through V07 and `responseVectorGrade.status` was `PASS`, including `0282`. That proves the published arithmetic vectors on this controller; it does not by itself prove Reset/power cleanup, operator input, failure alarms, protected production-key access, or a machining interlock.

## Pass boundary

This pack can prove protected storage behavior, G65 argument transport, nested forwarding, DPRNT syntax/delivery, and Reset/power-cycle observations. It cannot prove an intrinsic caller-program variable, cryptographic verification, offset loading, or a production interlock.

Do not add G10 offset writes or a production `MEIMAD/V/1` event until the controller-specific program numbers, variable ranges, input method, arithmetic/rounding, alarm behavior, and cleanup have been approved and physically tested.
