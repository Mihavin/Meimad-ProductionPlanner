# Haas protected verification — bounded retest after engineering approval

## Status

**DO NOT RUN YET.** This is a prewritten, no-motion retest for the two critical
findings in the 2026-08-27 code audit. It is not an approved macro, an installation
instruction, or permission to enable Server verification. The quarantined v3-v5
macros and v1-v3 ZIP packages must never be used for this retest.

External HFO/vendor approval is not a prerequisite. Approval in this document
means a written review
by the site's qualified CNC controls engineer and the Meimad production owner.

Target duration after every desk gate is signed: **30 minutes maximum at the CNC**.
Stop immediately on the first unexpected result. Do not edit a macro at the
Machine and do not create another candidate during the visit.

## Desk gates — all required before scheduling the Machine

- [ ] The written internal engineering decision record is completed in
  [the engineering review worksheet](haas-internal-engineering-review-2026-08-27.md).
- [ ] One post-M109 execution-barrier/input design is approved for the exact NGC
  version; the answer identifies what guarantees a fresh timer read after input.
- [ ] One event-sequence contract is approved for OLC, SVS, SVF, cycle start, and
  cycle end. `PERSISTENT_COUNTER` is the selected product design; this gate remains
  open until the physical engineering tests prove the selected variable's
  persistence and the reviewers sign its reboot and exhaustion behavior.
- [x] Macro v6, never v3-v5, was generated separately and passed its structural
  package test. Full Debug/Release regressions must still be attached before the
  desk gate is signed.
- [ ] Its SVS/SVF records repeat the exact six-digit NC identity, Offset Loader
  release token, and nonce, and a delayed prior-challenge result is rejected by
  the Server without resolving the current session.
- [ ] The candidate manifest status explicitly says
  `BENCH_ONLY_INTERNAL_REVIEW_REQUIRED`;
  its SHA-256 is recorded and independently matched before loading.
- [ ] Program numbers and every protected variable, including any event counter,
  passed the qualified CNC engineer/site collision review.
- [ ] A fresh Server MSI containing the audited DPRNT reconnect/reply filtering is
  installed; the service is Running and is the only DPRNT client.
- [ ] Server `cnc_verification_settings.enabled` remains `false` except inside the
  isolated commissioning context authorized for this retest.
- [ ] Spindle, feed, tool change, coolant, probing, offset writes, and production
  cycle events are absent from the exact test NC files.
- [ ] Named CNC engineer and Meimad observer agree to the hard stops below.

Any unchecked item cancels the visit.

## Evidence header

Record these before Cycle Start. Do not record a Machine secret, Server secret,
bearer token, raw nonce, or protected arithmetic internals.

| Field | Value |
|---|---|
| Machine ID / serial | |
| NGC version | |
| Candidate macro version | |
| Candidate ZIP SHA-256 | |
| Challenge / verify / finalizer program numbers | |
| Approved four temporary variables | |
| Approved persistent sequence variable / initial value | |
| Approved sequence contract | `PERSISTENT_COUNTER` (v6 candidate) |
| Server MSI version | |
| Server service / sole DPRNT owner confirmed | |
| CNC engineer / observer / UTC start | |

## Physical script — no motion

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
