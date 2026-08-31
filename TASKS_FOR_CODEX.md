# TASKS FOR CODEX

This file is the working task buffer for Codex. Implement the tasks below in the repository. When a task set is completed, this file may be cleared/replaced with the next task set.

## 1. Simplify device identity and update AGENTS.md

Apply the agreed device-identification architecture consistently in documentation, server/client code, configuration, tests, and commissioning tooling.

### Tablet identity
- Tablet identity is **TabletID + MAC address**.
- Do not use tablet authorization tokens, tablet credentials, shared secrets, challenge-response secrets, or equivalent hidden credentials for tablet identity.
- Tablet IP is not a stable identity field.

### CNC machine identity
- CNC machine identity is **MachineID + fixed IP address + MAC address**.
- Do not use `Machine Secret` or equivalent machine credentials for machine identity.

### Important boundary
- Do **not** merge device identity with NC verification or Offset Loader verification/authorization.
- NC verification and Offset Loader verification/authorization remain separate mechanisms and must keep their own workflow semantics.
- Update `AGENTS.md` so future Codex work follows this architecture and does not reintroduce removed tablet/machine credentials.

### Acceptance
- No obsolete tablet token/credential or Machine Secret requirement remains in active architecture/code paths.
- Existing functionality that is unrelated to device identity is preserved.
- Update/add tests for the new identity rules.

---

## 2. Change CNC verification timeout semantics

The current implementation starts the verification timeout when `OFFSET_LOADER_COMPLETED` creates the verification session. This must change.

### Required state model
Use an explicit verification lifecycle equivalent to:

`ARMED -> PENDING -> SUCCEEDED`

with failure/expiry handling where appropriate.

### Required behavior
1. When the current Offset Loader executes/completes successfully:
   - create/refresh the verification context for the current Production Run / Machine / NC release / Offset Loader release;
   - verification becomes **ARMED**;
   - the response/challenge code may be made available to the tablet;
   - **do not start the 120-second verification timeout yet**.

2. On the **first start of the main NC program** for that armed verification context:
   - verification becomes **PENDING**;
   - set `created_at`/`started_at` as appropriate at this moment;
   - set `expires_at = first_main_nc_start + VerificationTimeoutSeconds`;
   - default timeout remains 120 seconds unless configured otherwise.

3. The operator enters the verification code during this window.

4. After successful verification:
   - state becomes **SUCCEEDED/VERIFIED**;
   - the same released NC program must continue to run without asking for the verification code again on subsequent cycle starts;
   - verification is one-time for that current Offset Loader / NC release context.

5. A newly executed/current Offset Loader must invalidate/reset the previous successful verification and create a new **ARMED** verification context.

6. Expiry must be based on the time since the **first main NC start**, not time since Offset Loader execution.

### Preserve
- Existing stale Offset Loader detection.
- NC identity matching.
- Macro version validation.
- duplicate event handling / idempotency.
- anomaly reporting.
- separation between Offset Loader evidence and NC verification.

### Implementation guidance
Inspect and update at least the relevant server persistence/workflow and Haas ingestion paths, including the current behavior around:
- `CncDprintEventIngestionService`
- `SqliteProductionRunWorkflowEventRepository`
- `SqliteCncVerificationFoundationRepository`
- verification session schema/state handling
- tablet/E-Ink projection of pending verification
- CNC simulator scenarios
- tests and commissioning scripts/configuration where the timeout semantics are assumed.

Do not simply extend the timeout. Change the trigger that starts it.

### Acceptance tests
Add tests that prove:
- Offset Loader completes; wait more than 120 seconds; first NC start still opens a fresh valid 120-second window.
- First NC start starts the timeout.
- Correct verification within the window succeeds.
- Late verification after the window expires is rejected/handled according to the existing failure model.
- After success, second and later NC starts do not require verification again.
- Executing a new Offset Loader resets prior success and requires one new verification.

---

## 3. Implement fact-driven Tool Room / Setup queue workflow

Use the same queue/view UX pattern already used for QA. Add/complete the corresponding workflow views for Tool Room and Setup without creating a separate standalone application unless the existing architecture requires it.

The guiding rule is: **almost all workflow states are derived automatically from facts/events. Manual actions are only used where a real human handoff must be recorded.**

### A. Queue for Tool Room — automatic
An operation enters **Queue for Tool Room** automatically when all prerequisites required for Tool Room work are present/current, including the released NC package and the required released tool data according to the existing readiness model.

Do not require a manual "send to Tool Room" status button when the facts already make it ready.

### B. Queue for Setup — automatic
When Tool Room finishes its work and uploads/releases the **Tool Offset Table**, the operation automatically leaves Tool Room work and appears in **Queue for Setup**.

No manual status transition should be required for this step.

### C. Handover to setup operator — manual, permissioned
The physical handover of the prepared tool cart from Tool Room to the setup operator is the deliberate manual event.

- Action is performed by **Tool Room Manager** (or the exact existing role that represents that authority; do not broaden it silently).
- Machine is already known from the planner assignment; do not ask the user to reselect it.
- Select/assign the setup operator.
- The setup-operator list must be filtered by the operator's skills/qualification for the assigned machine or machine group.
- Record at minimum operator/user identity and timestamp.
- Transition:
  - `Queue for Setup` -> `Ready for Setup`
- This handover does **not** mean setup has physically started on the CNC yet.

### D. Setup start — automatic from CNC fact
When the assigned/current **Offset Loader is actually started/executed on the CNC**, automatically transition:

`Ready for Setup` -> `Setup In Progress`

This must be driven by machine/Offset Loader telemetry, not a manual `Start Setup` button.

### E. Events and authorization
Separate events into two categories in the domain model/documentation:

**Automatic events** — generated from facts such as release creation, Tool Offset Table release/upload, Offset Loader execution, NC start, verification results, etc.

**Manual events** — only for explicit human actions such as physical Tool Room -> Setup handover/assignment. Every manual event must have an explicit allowed-role/permission rule and must retain actor + timestamp.

### UI
Create/use consistent queue UX for:
- `Queue for QA`
- `Queue for Tool Room`
- `Queue for Setup`

Keep the visual/interaction pattern consistent, but expose only the actions appropriate to each queue.

### Acceptance
- Queue membership can be recomputed from retained facts/events and does not depend on arbitrary manual status toggles.
- Tool Offset Table release automatically produces Queue for Setup eligibility.
- Tool Room handover is auditably manual and permissioned.
- Offset Loader execution automatically starts Setup In Progress.
- Setup operator assignment respects machine skills.
- Add/update tests for transitions, permissions, idempotency, and duplicate telemetry.

---

## 4. Make CNC event sequence numbers non-blocking evidence

The current protected Haas/CNC event design is too brittle around the persistent event-sequence counter. If the controller-side sequence value is lost, reset, jumps, wraps, is manually changed, or simply does not equal the exact value the Server expected, the workflow can fail and require manual counter resynchronization. Remove that operational dependency.

### Product rule
The CNC event sequence number is **evidence for diagnostics, duplicate detection, and anomaly reporting only**. It is **not workflow authority** and must never be the sole reason a valid, otherwise correctly correlated CNC event is rejected or the Production Run/verification workflow becomes unusable.

The operator must not have to edit a macro variable such as the configured persistent sequence variable (for example `#504`) just to make the Server and CNC agree again.

### Required behavior
1. Do **not** require `incoming_sequence == last_sequence + 1` as a prerequisite for processing an event.
2. A forward gap/jump in sequence numbers:
   - may create `cnc_event_sequence_gap` diagnostic/anomaly evidence;
   - must still allow the event to be processed if its Machine, Production Run/context, NC identity, Offset Loader/release correlation, event type, and other required event-specific evidence are valid.
3. A sequence reset/rollback after controller reboot, macro-variable loss, service work, manual reset, or wrap must not permanently brick the Machine integration.
   - establish a new observed baseline automatically from valid subsequent evidence, or otherwise handle the discontinuity without requiring operator-side counter synchronization;
   - record the discontinuity diagnostically when useful.
4. Preserve real duplicate/idempotency protection.
   - Retransmission of the same logical event must remain idempotent.
   - Reuse of the same sequence/source identity with a conflicting payload must not silently create a second contradictory workflow event; record/reject the conflict according to the existing anomaly model.
5. Out-of-order or delayed events must be judged primarily by their event-specific correlation and current workflow context, not by a global exact-next-sequence expectation.
6. Sequence numbers must not be used to decide which Production Run, NC release, Offset Loader release, verification session, or cycle is authoritative when stronger explicit correlation already exists.
7. Do not replace this with another fragile global counter, epoch, secret, or manual synchronization ritual unless the repository already has an independently required explicit correlation field for that event.

### Architecture/documentation cleanup
The repository currently documents `PERSISTENT_COUNTER` as a selected design and contains commissioning/tests around a persistent event-sequence variable. Revisit those assumptions everywhere they make sequence continuity a hard gate.

Update, as needed:
- `AGENTS.md`
- `docs/architecture.md`
- `docs/functional-spec.md`
- `docs/data-model.md`
- `docs/api-contract.md`
- `docs/implementation-plan.md`
- CNC commissioning/audit documents
- Haas package-generation scripts/macros
- simulator scenarios
- server ingestion/persistence logic
- tests

Keep a sequence field if it is still useful as evidence. The goal is **not necessarily to delete the field**; the goal is to delete the requirement that the Server and controller must maintain an exact synchronized, never-lost counter for normal operation.

### Preserve
- NC identity verification.
- Offset Loader verification/authorization and stale-loader protection.
- Verification release/nonce or other explicit event correlation that is still part of the current approved protocol.
- Duplicate-event idempotency.
- Conflicting-event anomaly detection.
- Production-cycle correctness and no double counting.

Do not weaken these protections merely to make sequence handling tolerant.

### Acceptance tests
Add tests proving at least:
- normal contiguous sequences still work;
- a skipped sequence value records a gap but does not block an otherwise valid next event;
- a large forward jump does not require manual resynchronization;
- a controller reboot/reset that returns the sequence to a low value does not permanently block subsequent valid events;
- wrap/reset behavior is recoverable automatically;
- duplicate retransmission remains idempotent;
- same sequence/source identity with conflicting payload is detected and does not double-apply state;
- delayed/out-of-order evidence cannot incorrectly bind to a newer verification/Offset Loader/Production Run;
- cycle counting cannot double-count because of sequence reset or reuse;
- no commissioning procedure requires manually setting the sequence variable to the Server's expected next value.

### Completion report
When complete, report:
- exactly where strict sequence continuity was previously enforced;
- what now provides idempotency/correlation for each CNC event type;
- which sequence-related anomalies remain diagnostic only;
- migrations/configuration changes, if any;
- updated commissioning steps;
- test results.

---

## General instructions

- First inspect the current implementation and existing tests before changing architecture.
- Prefer extending the existing event-driven workflow rather than creating parallel state systems.
- Preserve backward-compatible data where practical; add migrations when schema/state changes require them.
- Keep manual workflow authority minimal and explicit.
- Run the full relevant server/client test suites and report the results.
- At completion, summarize changed files, migrations, behavior changes, and any remaining physical-machine commissioning steps.