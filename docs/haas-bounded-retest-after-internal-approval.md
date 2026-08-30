# Haas protected verification — bounded retest after engineering approval

## Status

**AUTHORIZED FOR BOUNDED NO-MOTION RETEST PREPARATION.** All desk gates were
closed on 2026-08-30 under `VF3SS-V6-2026-08-30`. This remains a no-motion retest
for the two critical findings in the 2026-08-27 code audit; it is not production
approval or permission to leave Server verification enabled. The quarantined
v3-v5 macros and v1-v3 ZIP packages must never be used for this retest.

External HFO/vendor approval is not a prerequisite. Approval in this document
means a written review
by the site's qualified CNC controls engineer and the Meimad production owner.

Target duration after every desk gate is signed: **30 minutes maximum at the CNC**.
Stop immediately on the first unexpected result. Do not edit a macro at the
Machine and do not create another candidate during the visit.

## Desk gates — all required before scheduling the Machine

- [x] The written internal engineering decision record is completed in
  [the engineering review worksheet](haas-internal-engineering-review-2026-08-27.md).
- [x] One post-M109 execution-barrier/input design is approved for the exact NGC
  version; the answer identifies what guarantees a fresh timer read after input.
- [x] One event-sequence contract is approved for OLC, SVS, SVF, cycle start, and
  cycle end. `PERSISTENT_COUNTER` is the selected product design; this gate remains
  open until the physical engineering tests prove the selected variable's
  persistence and the reviewers sign its reboot and exhaustion behavior.
- [x] Macro v6, never v3-v5, was generated separately and passed its structural
  package test. Full Debug/Release regressions must still be attached before the
  desk gate is signed.
- [x] Its SVS/SVF records repeat the exact six-digit NC identity, Offset Loader
  release token, and nonce, and a delayed prior-challenge result is rejected by
  the Server without resolving the current session.
- [x] The candidate manifest status explicitly says
  `BENCH_ONLY_INTERNAL_REVIEW_REQUIRED`;
  its SHA-256 is recorded and independently matched before loading.
- [x] Program numbers and every protected variable, including any event counter,
  passed the qualified CNC engineer/site collision review.
- [x] Server MSI `0.1.46` containing the audited DPRNT reconnect/reply filtering
  is installed; the service was Running and PID 15876 was the sole established
  DPRNT client on 2026-08-30.
- [x] Server `cnc_verification_settings.enabled` was confirmed `false` on
  2026-08-30.
- [x] While still disabled, the Server verification configuration was updated on
  2026-08-30 to macro version 6 with O9001/O9002/O9003 and #10500-#10504. The
  Server projection confirms the secret is configured, six response digits, and
  a 120-second timeout.
- [x] Spindle, feed, tool change, coolant, probing, offset writes, and production
  cycle events are absent from the exact test NC files.
- [x] Named CNC engineer and Meimad observer agree to the hard stops below.

Any unchecked item cancels the visit.

## Evidence header

Record these before Cycle Start. Do not record a Machine secret, Server secret,
bearer token, raw nonce, or protected arithmetic internals.

| Field | Value |
|---|---|
| Machine ID / serial | `5b332822830545d19950a43743779237` / serial not recorded |
| NGC version | `100.21.000.1001` |
| Candidate macro version | 6 |
| Superseded initial ZIP SHA-256 | `DB139836B1249C10410F3B10C44F4B49C515107476319A5A3EAB97F2D68BFB58` (rejected OLCs exposed missing current Server Offset Loader context; do not resume with this ZIP) |
| Approved R2 ZIP SHA-256 | `C2172BA1CAEB6ED0B0E5B933174E6749C73CDCB5443C719B2F62E851DC2247CD` |
| Approved R2 manifest SHA-256 | `66A42A78E8D62E22AE746829F18DA9A0A46D0A2833082193BA563D08F085D8D2` |
| R2 Server NC release / identity | `43cf09e19233496fa2424a0540b8e60b` / `742915` |
| R2 current Offset Loader release / token | `ed429301f4ff439db34338532e4328e8` / `782703` |
| R2 tool-table release | `b280420ea861472f88e19c1c5c002c87` |
| Challenge / verify / finalizer program numbers | O9001 / O9002 / O9003 |
| Approved four temporary variables | #10500 / #10501 / #10502 / #10503 |
| Approved persistent sequence variable / initial value | #10504 / 1 |
| Observed R2 pre-retest persistent sequence baseline | #10504 = 8 (2026-08-30; preserve, do not reinitialize) |
| Approved sequence contract | `PERSISTENT_COUNTER` (v6 candidate) |
| Server MSI version | 0.1.46 installed; 0.1.47 installers built and verified |
| Server service / sole DPRNT owner confirmed | YES, PID 15876 at desk check |
| Physical baseline Server/DPRNT observation | YES, one Server process and one established DPRNT connection, both PID 16764 (2026-08-30) |
| Physical baseline Server configuration | Disabled; macro v6; O9001/O9002/O9003; #10500-#10504; secret configured (2026-08-30) |
| R2 delta approval | Michael Vinetsky approved as CNC controls engineer and Meimad production owner, 2026-08-30 |
| CNC engineer / observer / UTC start | |

## Physical script — no motion

### Physical observations — 2026-08-30

| Check | Result |
|---|---|
| Baseline temporary variables | PASS: #10500-#10503 empty |
| Persistent sequence baseline | PASS: #10504 = 6 and preserved |
| Baseline Server/DPRNT ownership | PASS: one Server process owns the sole established DPRNT connection |
| Baseline verification configuration | PASS: disabled, macro v6, O9001/O9002/O9003, #10500-#10504, secret configured |
| Fresh late-response challenge | NOT_TESTED: OLC sequences 7 and 8 reached the Server but were rejected as `stale_offset_loader`; the assigned test Run had no current Offset Loader release. The operator's initially visible tablet value was therefore not accepted as fresh evidence. O01990 was not run. |
| Safe cleanup after rejected challenges | PASS: after expiry O01990 raised alarm 903 without an M109 prompt, #10500-#10503 were empty, #10504 remained 8, and O01990 did not continue as a verification success |
| R2 controller load | PASS: R2 ZIP hash matched; O1990/O1991 replaced; byte-identical O9001/O9002/O9003 retained; Setting 23 ON; #10500-#10503 empty; #10504 = 8 |
| R2 first challenge setup attempt | NOT_TESTED: CNC advanced #10504 from 8 to 9, but `OLC-HAAS-VF3SS-9` already existed in rejected-event history and was handled idempotently; no pending session or tablet response was created; O1990 was not run |
| R2 second challenge setup attempt | NOT_TESTED: OLC sequence 10 was rejected as `stale_offset_loader`; controller inspection proved O01991 still contained the superseded `A483920 B654321` call instead of R2 `A782703 B742915`. The tablet polled after rejection and correctly displayed no response. O1990 was not run. |
| Corrected R2 O01991 reload | PASS: exact call `G65 P9001 A782703. B742915.` confirmed on the controller; #10504 = 10; program not run after reload |
| Corrected R2 O01990 verification | PASS: exact first hook `G65 P9002 A742915. (MEIMAD VERIFY V1)` and matching harmless return marker confirmed; program not run during inspection |
| R2 late-response physical result | **FAIL**: operator reported several attempts, #10504 advanced to 20, alarm 903 appeared, #10500 retained ASCII value 55 while #10501-#10503 were empty, and O01990 was reported to reach M30. Server evidence retained OLC sequences 11/13/15/16/17/19 and SVF sequences 12/14/18 with no SVS, so the claimed 130-second attempt is not independently isolatable. Retained response authority alone violates the hard stop. Candidate quarantined; no later physical sections authorized. |
| R2 final cleanup | PASS: Server verification disabled; direct O01990 raised alarm 903 without M109; #10500-#10503 empty; #10504 remained 20; O01990 did not reach M30 |
| Final live Server safe-state check | PASS: `enabled=false`, expected macro version remains 6, O9003/#10504 mappings retained, and one established Server-owned DPRNT connection observed at 2026-08-30T12:25:58Z |
| Unapproved v7 state change | Operator reported v7 loaded before R3 review approval. Live Server check at 2026-08-30T12:28:23Z confirmed `enabled=false` but expected macro version had been changed to 7. Execution is not authorized; loaded-file and controller-state audit pending. |
| V7 loaded-state report | O9001/O9002/O9003 and O1990/O1991 reported replaced; Setting 23 ON; #10500-#10503 empty; #10504 = 20; no program executed after load. Safe loaded state only; hashes and execution remain unapproved. |
| R3 v7 delta approval | Michael Vinetsky approved as qualified CNC controls engineer and Meimad production owner for a future bounded no-motion retest on 2026-08-30; not production approval |
| Approved R3 v7 ZIP SHA-256 | `94D4A6127EC22D75B13615F0F7F33C85E89D72E584E2E1915C6F5071EAF0C699` |
| Approved R3 v7 manifest SHA-256 | `1D738DA4A991A4E7385BB8CBA23803C21DD377A7C9E442EC372AE8CC7F74071C` |
| R3 package/loaded-state gate | PASS: ZIP hash matched; loaded files reported from exact HAAS-VF3SS-R3 directory; no execution since load; #10500-#10503 empty; #10504 = 20 |
| R3 late-response CNC behavior | **PASS (CNC only)**: exactly one attempt; O01991 reached M30; #10504 advanced 20 to 21; first M109 prompt appeared; after 130 seconds O01990 raised alarm 903, #10500-#10503 were empty, #10504 advanced to 22, and O01990 did not reach M30. This proves the v7 cleanup and fail-closed controller behavior for this attempt; it does not approve the end-to-end integration. |
| R3 Server evidence | **FAIL**: Server retained `OLC-HAAS-VF3SS-21` but flagged `cnc_event_sequence_gap` because the last retained sequence was 19 and 20 was missing. The verification session expired. No SVS was retained, but no `SVF-HAAS-VF3SS-22` reached the Server despite the CNC alarm. The persistent counter must not be reset or rewritten to conceal the gap. |
| R3 physical-tablet projection | **FAIL**: the response appeared on the web simulator only. OLC-21 was retained at 2026-08-30T12:37:56Z; the physical tablet's next recorded Server contact was 2026-08-30T12:40:41Z, after the 120-second challenge expiry. The firmware's 120-second IN_SETUP wake interval races the 120-second challenge lifetime, so automatic wake is not a valid commissioning path. |
| R3 overall disposition | **FAIL / STOP**: do not proceed to timely-response, interruption, reboot, or production-enable sections. Disable verification, clear the alarm after confirming temporary variables remain empty, retain #10504 = 22, and perform written Server/firmware/DPRNT review before another bounded physical test. |

### 1. Baseline cleanup and ownership (3 minutes)

1. Confirm the exact no-motion test pair contains no motion, spindle, tool-change,
   coolant, probing, offset-write, CST, or CEN command.
2. Clear the approved temporary handshake variables through the reviewed recovery
   procedure. Do not clear or initialize an event counter unless the approved
   sequence contract explicitly requires it and records the audited action.
3. Start one sanitized Server capture. Confirm there is exactly one established
   DPRNT consumer and the expected macro version is configured.

Pass: variables are at the approved baseline, the Server is the sole consumer,
and there is no verification success. Otherwise stop.

### 2. Late correct response must fail closed (7 minutes)

1. Run the matched no-offset-write test Offset Loader once.
2. Confirm one OLC is received and the tablet displays a six-digit response.
3. Run the matched no-motion test NC and wait at the first input prompt for
   `configured timeout + 10 seconds`, measured by an independent stopwatch.
4. Enter the otherwise correct six digits.

Pass only if all are true:

- the verification macro raises the reviewed blocking alarm before returning to
  the test NC;
- one SVF and zero SVS records are accepted for this attempt;
- the response and all temporary challenge variables are empty afterward;
- no marker after the verification hook and no machining/cycle event executes.

If the program returns, SVS appears, no alarm appears, or any authority remains,
stop. Mark the candidate `FAIL` and keep verification disabled.

### 3. Timely correct response and sequence adjacency (5 minutes)

1. Clear the alarm only through the reviewed recovery procedure.
2. Run the matched test Offset Loader once, then the matched test NC once.
3. Enter the correct response promptly.

Pass only if the attempt produces exactly one OLC followed by exactly one SVS,
returns to the harmless after-hook marker, clears all temporary authority, and
uses adjacent values in the single approved event-sequence domain. Any SVF,
duplicate, gap, out-of-order anomaly, or retained authority is a stop.

### 4. Reset during input (4 minutes)

1. Create a fresh challenge and start the matched test NC.
2. At the first M109 prompt, press Reset once.
3. Run the test NC directly without a new Offset Loader challenge.

Pass only if Reset cannot produce SVS or an after-hook return, all temporary
authority is empty, and the direct retry fails visibly before the harmless marker.
Otherwise stop.

### 5. Controller reboot sequence contract (8 minutes)

1. Record only the last sanitized event ID, sequence/epoch evidence, and Server
   receipt time. Do not record nonce or response data.
2. Reboot the controller using the approved site procedure; do not alter the
   Server database or sequence history.
3. Re-establish the sole DPRNT connection and run one timely no-motion OLC/SVS
   attempt.

For `PERSISTENT_COUNTER`, pass only if the first post-reboot event continues at
the exact next counter value. For `EXPLICIT_EPOCH`, pass only if the new epoch is
unambiguous, the Server starts the documented epoch scope, and buffered prior-epoch
events cannot be mistaken for current evidence. In both designs, duplicates remain
idempotent and the Server must not silently repair a gap or out-of-order event.

### 6. Stop and preserve evidence (3 minutes)

1. Disable verification again regardless of the result.
2. Confirm all temporary handshake variables are empty and no alarm remains.
3. Export the sanitized DPRNT capture, Server debug timeline, anomaly queue, exact
   hashes, and photos of the late-response and Reset results.
4. Record each result in `docs/cnc-commissioning-checklist.md`. Do not change the
   overall decision to `READY` unless all fourteen checks, every Machine field,
   both sign-offs, and the one-Machine pilot are independently complete.

## Hard stops

- No source edit, MDI workaround, variable substitution, timeout extension, or
  second candidate is allowed at the Machine.
- No production NC, offsets, tools, spindle, feed, coolant, or material cutting.
- No competing terminal/capture client while the Server owns DPRNT.
- No “close enough” result: missing alarm, retained authority, wrong sequence,
  ambiguous epoch, or unexpected return is `FAIL`.
- The 30-minute limit ends the visit; incomplete checks remain `NOT_TESTED`.
