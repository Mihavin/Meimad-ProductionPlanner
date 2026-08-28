# CNC setup-verification commissioning record

This is the required per-Machine acceptance record for the protected Meimad setup-verification handshake. Automated tests and simulators do not make a Machine production-ready. Keep verification disabled until every required row is `PASS`, evidence is linked, and both sign-offs are complete.

## Machine and controller identity

| Field | Recorded value |
|---|---|
| Meimad Machine ID / number | Server Machine ID `5b332822830545d19950a43743779237`; display name `Haas VF-3ss`; separate shop number NOT RECORDED |
| Manufacturer / model | Haas VF-3SS (partial no-motion spike evidence only) |
| Controller / software version | NGC `100.21.000.1001` |
| Controller serial number | NOT RECORDED |
| Commissioning date / work order | Partial capture 2026-08-26; production commissioning work order NOT RECORDED |
| DPRINT transport / endpoint | VF-3SS `192.168.0.56:8080`; Setting 261 `TCP Port`; Setting 263 `8080`; TCP connection observed |
| Challenge / verify / finalizer protected program numbers | Partial bench used O9001 / O9002; both were free before load and protected afterward. Macro-v6 proposes O9001 / O9002 / O9003, but O9003 remains unapproved and untested on the Machine. |
| Custom G-code alias, if used | NONE in the partial bench; direct `G65 P9001` / `G65 P9002` calls |
| Approved protected variable mappings | `#10500-#10503` approved collision-free for the partial bench; macro-v6 proposes persistent event counter `#10504`, which remains unapproved, uninitialized, and untested on the Machine |
| Expected protected-macro version | NOT RECORDED |
| Sequence persistence/reset rule | `PERSISTENT_COUNTER` selected: initialize once to positive value 1, increment across OLC/SVS/SVF/CST/CEN, never wrap/reseed, and fail closed at an invalid/decreased/exhausted value. Physical VF-3SS persistence and the exact commissioned variable remain NOT RECORDED. |

Allowed result values are `PASS`, `FAIL`, and `NOT_TESTED`. A comment alone is not evidence.

## Required checks

| # | Check and acceptance criterion | Result | Exact observed behavior | Evidence |
|---:|---|---|---|---|
| 1 | Operator role cannot casually view or edit the protected O9000/custom-code implementation. Service access remains documented. | PASS | After loading O9001/O9002, Setting 23 was restored ON and ordinary access was blocked. The setting remained ON and both programs were retained after controller reboot. This proves the tested protection behavior only; service-access ownership still belongs in the final site work order. | Operator STEP 2 and STEP 12 observations recorded 2026-08-27; exact candidate hashes remain local and quarantined. |
| 2 | Protected execution obtains the stable identity of the calling approved NC release. Record the exact system variable/mechanism and behavior for main, subprogram, MDI, restart, and memory/network execution. | NOT_TESTED | The accepted product fallback is one generic first-executable-block hook with an immutable six-digit NC identity; actual controller behavior is unproved. | — |
| 3 | DPRINT delivers strict `MEIMAD/V/1` records reliably, including after reconnect. Duplicate delivery is idempotent and a missing sequence creates an anomaly. | NOT_TESTED | Simulator and automated ingestion tests pass; transport hardware is unproved. | — |
| 4 | Two successive valid Offset Loader executions create different fresh nonces and different displayed response codes. Neither raw nonce nor Machine secret is exposed to the tablet. | NOT_TESTED | Server algorithm tests pass; controller calculation is unproved. | — |
| 5 | CNC protected macro and Server independently calculate the same fixed-width decimal response for approved test vectors, including leading zeroes. | PASS | The physical VF-3SS no-motion candidate emitted V01–V07 and matched the independent reference, including four-digit `0282`. This proves public-vector arithmetic only, not production key protection, entry, cleanup, or interlock. | [Protected verification spike](haas-protected-verification-spike.md#physical-evidence-review--2026-08-26-partial-capture); [response algorithm](haas-verification-response-algorithm.md#published-independent-vectors) |
| 6 | Wrong operator response raises a blocking alarm before any material-cutting block can execute, clears the response variable, and emits verification failure. | PASS | In the no-motion O1990 first-block-hook test, a wrong response raised `MEIMAD VERIFY FAILED`, O1990 stopped before its after-hook/M30 path, SVF was captured, and `#10500-#10503` were empty. This proves the wrong-response path for the quarantined bench candidate; it does not cure the separate late-correct-response failure. | Operator STEP 6 and STEP 7 observations plus sanitized local SVF capture, 2026-08-27. |
| 7 | A superseded/old Offset Loader release cannot verify and produces a visible stale-loader anomaly without altering planning data. | NOT_TESTED | Server-side stale-release checks pass; controller path is unproved. | — |
| 8 | A still-approved old NC release with a newly measured/current Offset Loader verifies successfully; no NC rewrite is performed for remeasurement. | NOT_TESTED | Physical validation required. | — |
| 9 | Executing/generating a newer Offset Loader invalidates the previous verification session and code. | NOT_TESTED | Server automated test passes; controller behavior is unproved. | — |
| 10 | Power loss during offset writes cannot emit `OFFSET_LOADER_COMPLETED`; power loss after challenge does not leave cutting enabled or a reusable response. | NOT_TESTED | Physical interruption test required. | — |
| 11 | Server/controller restart and communication recovery fail closed and do not fabricate success, silently bypass verification, or lose last-known-good tablet content. | NOT_TESTED | Physical restart matrix required. | — |
| 12 | Configured temporary variable numbers do not collide with probing, tool setting, postprocessor output, operator macros, or other installed options. Values are cleared at the documented boundaries. | PASS | Before the bench load, O9001/O9002 were confirmed free and `#10500-#10503` were explicitly approved for the Machine. Reset, E-stop, failure, and reboot observations then showed the v4/v5 consume-before-input design clearing all four variables. Any future sequence counter is outside this approval and requires a new collision review. | Operator STEP 1, STEP 7, STEP 8 cleanup, STEP 11, STEP 12, and STEP 13 observations, 2026-08-27. |
| 13 | Sequence behavior across normal cycles, RESET, E-stop, program restart, controller reboot, and wraparound is recorded. Server gaps/out-of-order/duplicates remain evidence, never invented events. | FAIL | On the VF-3SS, v3 retained challenge variables after RESET at M109. V4 cleared persistent authority before M109; RESET and E-stop alarm 107 stopped input, and controller reboot cleared the variables. V5 nevertheless accepted the correct response after at least 130 seconds at the first prompt, proving its post-input timeout check ineffective. `SEQ` also uses power-on timer `#3001` and therefore is not monotonic across reboot/wrap. All candidates are quarantined. | [Code audit](cnc-verification-code-audit-2026-08-27.md); local sanitized Reset/power/E-stop evidence and operator observations 2026-08-27. |
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
- Generated candidate manifest/checksums: local bench pack available under the
  git-ignored `.diagnostics` tree; its manifest and adjacent
  `DO-NOT-RUN-QUARANTINED.txt` now explicitly preserve the physical-timeout and
  reboot/wrap sequence quarantine; installation checksum NOT RECORDED
- Controller engineering probes: generator
  `scripts/new-haas-ngc-engineering-test-pack.ps1`, structural test
  `scripts/test-haas-ngc-engineering-test-pack.ps1`, fail-closed result grader
  `scripts/audit-haas-ngc-engineering-results.ps1`, and physical procedure
  `docs/haas-ngc-engineering-machine-tests.md`. No physical result is recorded yet.
- Sanitized running-Server configuration check: Machine
  `5b332822830545d19950a43743779237`, `enabled=false`, settings version `3`, secret
  still configured, recorded 2026-08-27 20:51 UTC through the ordinary audited
  edit API and reconfirmed read-only on 2026-08-28. The same read-only audit
  queried all 16 configured Machines and found zero enabled CNC-verification
  configurations. No secret, key, token, nonce, or response was exported.
- Windows Service check: installed 0.1.40 service was `Running` / `Automatic` but
had no failure actions. The verified 0.1.43 Server MSI adds bounded recovery;
  its non-elevated upgrade attempt was refused with Windows Installer 1730, so the
  installed service remains unchanged pending an administrator-run upgrade.

## Decision and sign-off

Current commissioning decision: **NOT READY — PHYSICAL COMMISSIONING INCOMPLETE**

| Role | Name | Date | Decision/signature |
|---|---|---|---|
| CNC service/commissioning engineer | — | — | — |
| Meimad production owner | — | — | — |

After both approvals, record the exact commissioned values in this file or a Machine-specific copy, set the Server Machine configuration to those values, enable verification, and run the one-Machine pilot. A failed row returns verification to disabled until corrected and re-tested.

The fail-closed record audit is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\audit-cnc-commissioning-checklist.ps1 -RequireReady
```

It accepts `READY` only when all 14 numbered checks are `PASS`, every check has
evidence, all Machine/controller fields are recorded, both roles are signed, and
the declared decision agrees. It validates record consistency; it cannot create
or replace physical evidence.
