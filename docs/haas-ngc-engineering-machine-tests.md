# Haas NGC engineering behavior tests

These tests answer the six open controller-behavior questions in the internal
engineering decision record on the exact Haas VF-3SS / NGC control. The selected
event-sequence design is **`PERSISTENT_COUNTER`**. These tests do not approve the
verification system for production and do not replace the fourteen-row CNC
commissioning checklist.

## Safety boundary

The generated programs are no-motion engineering probes. They contain no axis
movement, spindle/feed command, tool change, coolant, probing, offset write,
production-cycle record, NC verification record, Machine secret, nonce, or
response algorithm.

Before loading anything:

1. Remove material and tools from the test context and keep the Machine in a
   controlled service window.
2. Confirm O1980-O1984, or the five configured alternatives, are free.
3. Confirm the M109 response variable is free. The current proposed value is
   `#10500`, which aliases legacy `#500`.
4. Confirm the selected persistent counter is free across probing, tool setting,
   postprocessors, operator macros, and installed options. The proposed value is
   `#10504`.
5. Confirm `#10504` is empty. Do not initialize or overwrite it if any prior
   sequence history exists.
6. Keep Server CNC verification disabled. A passive DPRNT capture may listen, but
   no test record is a production workflow event.
7. Record the exact controller serial, NGC version, program numbers, variables,
   pack manifest hash, named CNC engineer, and Meimad observer.
8. Load the reviewed files, then restore Setting 23/access protection according
   to the site's procedure.

Stop immediately on unexpected motion, an unexpected return, an unexpected
variable change, a missing alarm, or a DPRNT record outside the documented
`MEIMADENG/V/1` test namespace.

## Generate the test pack

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\new-haas-ngc-engineering-test-pack.ps1 `
  -MachineLabel HAAS-VF3SS `
  -OutputDirectory .\.diagnostics\haas-ngc-engineering-tests\HAAS-VF3SS `
  -ResponseVariable 10500 `
  -PersistentCounterVariable 10504 `
  -InitialCounterValue 1 `
  -AcknowledgeNoMotionRealMachineTests `
  -AcknowledgeOneTimePersistentCounterInitialization
```

The output contains five NC programs, a manifest, checksums, a plain-language
README, and a result template. Match every hash immediately before loading.

## Question-to-test matrix

| Engineering question | Real-Machine test | Pass condition |
|---|---|---|
| Fresh `#3001` read after M109 | O1980 direct timer test | After an independently timed wait of at least 20 seconds, `ELAPSEDMS` is at least 15000 and no alarm 920 occurs. |
| M109 loop plus `G103 P1` and blanks | O1980 source context | One `M109DIRECT` record appears only after input; early evaluation raises alarm 920. |
| Separate G65 finalizer barrier | O1981 calling O1982 | One `G65FINALIZER` record reports at least 15000 ms, followed by exactly one `FINALIZERRETURNED`; early/backward time alarms. |
| Reset/E-stop/Single Block/Block Delete/mode behavior | O1981 interruption matrix | Reset/E-stop never emits finalizer/return evidence; Single Block and Block Delete ON cannot skip or duplicate the finalizer. Exact mode behavior is recorded. |
| `#3001` reboot/assignment/wrap behavior | Record O1980 timer evidence before and after reboot; optional separately approved MDI assignment observation | Reboot behavior is recorded; the design never uses `#3001` for event sequence. Backward time fails closed. No wrap value is guessed. |
| Persistent evidence counter suitability | O1984 initializer and O1983 probe | Positive one-time initialization, exact +1 increments, retention across Reset/E-stop/reboot, and exact next post-reboot value. |

## Test A - direct M109 timer freshness

1. Start a separate stopwatch.
2. Run O1980.
3. When the M109 prompt appears, wait at least 20 seconds.
4. Enter digit `7` once.
5. Save the `M109DIRECT` DPRNT record.

Pass only when `ELAPSEDMS >= 15000`, the input is the controller's numeric
character code for `7`, the program reaches M30, and `#10500` is empty afterward.
Alarm 920 or a smaller elapsed value is a fail and prohibits the v6 retest.

Run this test three times. The three elapsed values need not be identical, but
each must satisfy the threshold and appear only after the operator input.

## Test B - separate G65 finalizer freshness

1. Start a separate stopwatch.
2. Run O1981, which calls O1982 after M109 and three blank barrier blocks.
3. Wait at least 20 seconds at the prompt, then enter digit `7` once.
4. Save both DPRNT records.

Pass only when one `G65FINALIZER` record reports `ELAPSEDMS >= 15000`, it is
followed by exactly one `FINALIZERRETURNED`, O1981 reaches M30, and `#10500` is
empty. Alarm 921 means missing arguments, 922 means time moved backward, and 923
means the finalizer timer was evaluated too early. Any of those is a fail.

Run this test three times before proceeding.

## Test C - interruption and control-mode matrix

Use a fresh O1981 start for every row. Do not continue an interrupted instance.

| Condition | Action at M109 | Required observation |
|---|---|---|
| Reset | Press Reset before entering a character | No `G65FINALIZER` and no `FINALIZERRETURNED`; program stops; `#10500` empty. |
| E-stop | Press E-stop before entering a character, then use the approved recovery | No finalizer/return record; program stops; `#10500` empty after recovery. |
| Single Block | Enable before Cycle Start and advance deliberately; wait 20 seconds at M109, then enter `7` | Exactly one finalizer and one return record; no block is skipped. |
| Block Delete ON | Enable before Cycle Start; wait 20 seconds, then enter `7` | Same valid result as Test B. No protective line is optional-block prefixed. |
| Mode change | At M109, attempt only the mode change allowed by the site's controller procedure | Record whether the control rejects the change or stops execution. It must never return through the finalizer without accepted input. |

Repeat O1981 directly after each Reset/E-stop case without an earlier program
resume. It must start a new prompt; there must be no delayed or phantom finalizer
record from the interrupted instance.

## Test D - one-time persistent-counter initialization

This is the only authorized initialization in the selected design. Zero/unset is
invalid; the initial value is the recorded positive integer `1`.

1. Verify `#10504` is empty and attach the collision review.
2. Run O1984 once.
3. Confirm one `COUNTERINITIALIZED ... VALUE/1` record and `#10504 = 1`.
4. Run O1984 a second time only to test the one-time guard.

The second run must raise alarm 927 and must leave `#10504 = 1`. If it overwrites
the value or returns normally, fail the design and stop.

## Test E - counter persistence and continuity

1. Run O1983 twice. Required records are `BEFORE/1/AFTER/2` and then
   `BEFORE/2/AFTER/3`.
2. Press Reset while no program is active. Confirm `#10504 = 3`.
3. Exercise E-stop and the approved recovery while no program is active. Confirm
   `#10504 = 3`.
4. Record the last counter value, perform the approved controller reboot, and
   confirm `#10504 = 3` after startup.
5. Run O1983 once. The only accepted result is `BEFORE/3/AFTER/4`.
6. Restart/reconnect the Server-side passive DPRNT listener. Confirm that neither
   connection state nor Server restart changes `#10504`.

Alarm 924 means the counter is unset, 925 means invalid/non-integer/below one,
and 926 means exhausted. The counter must never wrap or be silently repaired.
The production candidate stops at 899999; this limit is verified structurally,
not by burning or resetting the commissioned counter near exhaustion.

## Test F - `#3001` behavior boundary

Record the O1980/O1982 `STARTMS` and `ENDMS` values before and after the approved
controller reboot. This establishes the exact reboot behavior on this controller.
The selected `PERSISTENT_COUNTER` design does not depend on `#3001` continuity.
The v6 finalizer treats a negative elapsed value as failure.

The generated pack deliberately never assigns `#3001` and never forces a guessed
wrap boundary. If the internal CNC engineer requires an assignment test, perform
the following only as a separately approved MDI/service observation with all
other macros stopped:

1. Record the current `#3001`.
2. Attempt the controller-approved form of assigning zero.
3. Record whether the control accepts it or raises an alarm and record the exact
   alarm text.
4. Restore/restart the timer only by the documented controller procedure.

Do not infer wrap behavior from an assumed numeric limit and do not set a near-wrap
value without controller documentation for this exact NGC version. The engineering
answer may state that wrap was not forced because the selected design is independent
of timer continuity.

## Completion record

Copy `RESULTS-TEMPLATE.md` from the generated pack into the controlled evidence
location. Attach the sanitized DPRNT log, alarm photos, variable screenshots,
manifest/checksum file, and reboot observation. Never attach a Machine secret,
nonce, verification code, or protected arithmetic.

Update the [internal engineering decision record](haas-internal-engineering-review-2026-08-27.md)
and [CNC commissioning checklist](cnc-commissioning-checklist.md) only from those
observations. A simulator or source inspection cannot convert a physical row to
`PASS`.

Grade the completed result record before the v6 desk review:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\audit-haas-ngc-engineering-results.ps1 `
  -ResultsPath <completed-RESULTS.md> -RequirePass
```

The gate accepts only thirteen `PASS` rows with exact observations and evidence,
all required identity/collision fields, a 64-character manifest hash, and an exact
`Decision: READY` declaration.
