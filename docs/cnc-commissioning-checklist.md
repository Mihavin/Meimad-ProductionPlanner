# CNC setup-verification commissioning record

This is the required per-Machine acceptance record for the protected Meimad setup-verification handshake. Automated tests and simulators do not make a Machine production-ready. Keep verification disabled until every required row is `PASS`, evidence is linked, and both sign-offs are complete.

## Machine and controller identity

| Field | Recorded value |
|---|---|
| Meimad Machine ID / number | NOT RECORDED |
| Manufacturer / model | Haas VF-3SS (partial no-motion spike evidence only) |
| Controller / software version | NGC `100.21.000.1001` |
| Controller serial number | NOT RECORDED |
| Commissioning date / work order | Partial capture 2026-08-26; production commissioning work order NOT RECORDED |
| DPRINT transport / endpoint | NOT RECORDED |
| Challenge / verify protected program numbers | NOT RECORDED |
| Custom G-code alias, if used | NOT RECORDED |
| Approved protected temporary-variable range | NOT RECORDED |
| Expected protected-macro version | NOT RECORDED |
| Sequence persistence/reset rule | NOT RECORDED |

Allowed result values are `PASS`, `FAIL`, and `NOT_TESTED`. A comment alone is not evidence.

## Required checks

| # | Check and acceptance criterion | Result | Exact observed behavior | Evidence |
|---:|---|---|---|---|
| 1 | Operator role cannot casually view or edit the protected O9000/custom-code implementation. Service access remains documented. | NOT_TESTED | Physical controller required. | — |
| 2 | Protected execution obtains the stable identity of the calling approved NC release. Record the exact system variable/mechanism and behavior for main, subprogram, MDI, restart, and memory/network execution. | NOT_TESTED | The accepted product fallback is one generic first-executable-block hook with an immutable six-digit NC identity; actual controller behavior is unproved. | — |
| 3 | DPRINT delivers strict `MEIMAD/V/1` records reliably, including after reconnect. Duplicate delivery is idempotent and a missing sequence creates an anomaly. | NOT_TESTED | Simulator and automated ingestion tests pass; transport hardware is unproved. | — |
| 4 | Two successive valid Offset Loader executions create different fresh nonces and different displayed response codes. Neither raw nonce nor Machine secret is exposed to the tablet. | NOT_TESTED | Server algorithm tests pass; controller calculation is unproved. | — |
| 5 | CNC protected macro and Server independently calculate the same fixed-width decimal response for approved test vectors, including leading zeroes. | PASS | The physical VF-3SS no-motion candidate emitted V01–V07 and matched the independent reference, including four-digit `0282`. This proves public-vector arithmetic only, not production key protection, entry, cleanup, or interlock. | [Protected verification spike](haas-protected-verification-spike.md#physical-evidence-review--2026-08-26-partial-capture); [response algorithm](haas-verification-response-algorithm.md#published-independent-vectors) |
| 6 | Wrong operator response raises a blocking alarm before any material-cutting block can execute, clears the response variable, and emits verification failure. | NOT_TESTED | Physical block-order and alarm behavior required. | — |
| 7 | A superseded/old Offset Loader release cannot verify and produces a visible stale-loader anomaly without altering planning data. | NOT_TESTED | Server-side stale-release checks pass; controller path is unproved. | — |
| 8 | A still-approved old NC release with a newly measured/current Offset Loader verifies successfully; no NC rewrite is performed for remeasurement. | NOT_TESTED | Physical validation required. | — |
| 9 | Executing/generating a newer Offset Loader invalidates the previous verification session and code. | NOT_TESTED | Server automated test passes; controller behavior is unproved. | — |
| 10 | Power loss during offset writes cannot emit `OFFSET_LOADER_COMPLETED`; power loss after challenge does not leave cutting enabled or a reusable response. | NOT_TESTED | Physical interruption test required. | — |
| 11 | Server/controller restart and communication recovery fail closed and do not fabricate success, silently bypass verification, or lose last-known-good tablet content. | NOT_TESTED | Physical restart matrix required. | — |
| 12 | Configured temporary variable numbers do not collide with probing, tool setting, postprocessor output, operator macros, or other installed options. Values are cleared at the documented boundaries. | NOT_TESTED | Machine program inventory and service review required. | — |
| 13 | Sequence behavior across normal cycles, RESET, E-stop, program restart, controller reboot, and wraparound is recorded. Server gaps/out-of-order/duplicates remain evidence, never invented events. | NOT_TESTED | Controller persistence/reset behavior required. | — |
| 14 | Reported protected-macro version equals the Server's Machine configuration. A mismatched version blocks verification and displays `CNC VERIFICATION MACRO UPDATE REQUIRED`. | NOT_TESTED | Server automated test passes; controller DPRINT required. | — |

## Evidence package

Attach or link sanitized evidence; never record the Machine secret, bearer token, raw verification nonce, or protected calculation internals.

- Controller settings/export reference: NOT RECORDED
- Protected-program installation checksum and controlled storage reference: NOT RECORDED
- DPRINT packet/log capture: NOT RECORDED
- Server debug-timeline export: NOT RECORDED
- Anomaly-queue export: NOT RECORDED
- Wrong-code alarm photo/video and block-order proof: NOT RECORDED
- Power-cycle/restart results: NOT RECORDED
- Variable collision review: NOT RECORDED

## Decision and sign-off

Current commissioning decision: **NOT READY — PHYSICAL COMMISSIONING INCOMPLETE**

| Role | Name | Date | Decision/signature |
|---|---|---|---|
| CNC service/commissioning engineer | — | — | — |
| Meimad production owner | — | — | — |

After both approvals, record the exact commissioned values in this file or a Machine-specific copy, set the Server Machine configuration to those values, enable verification, and run the one-Machine pilot. A failed row returns verification to disabled until corrected and re-tested.
