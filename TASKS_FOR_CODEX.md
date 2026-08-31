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
- Transition: `Queue for Setup` -> `Ready for Setup`.
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
2. A forward gap/jump in sequence numbers may create diagnostic/anomaly evidence, but must still allow an otherwise valid event to be processed.
3. A reset/rollback after controller reboot, macro-variable loss, service work, manual reset, or wrap must not permanently brick the Machine integration; recover automatically and record the discontinuity diagnostically when useful.
4. Preserve real duplicate/idempotency protection. Same logical retransmission stays idempotent; same sequence/source identity with conflicting payload must be detected and must not double-apply state.
5. Out-of-order or delayed events must be judged primarily by event-specific correlation and workflow context, not by a global exact-next-sequence expectation.
6. Sequence numbers must not decide Production Run, NC release, Offset Loader release, verification session, or cycle authority when stronger explicit correlation exists.
7. Do not replace this with another fragile global counter, epoch, secret, or manual synchronization ritual.

### Architecture/documentation cleanup
Revisit `PERSISTENT_COUNTER` assumptions anywhere sequence continuity is a hard gate, including as needed:
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

Keep a sequence field if useful as evidence; delete only the exact synchronized-counter requirement.

### Preserve
- NC identity verification.
- Offset Loader verification/authorization and stale-loader protection.
- Verification release/nonce or other explicit event correlation still part of the approved protocol.
- Duplicate-event idempotency.
- Conflicting-event anomaly detection.
- Production-cycle correctness and no double counting.

### Acceptance tests
Add tests proving at least:
- normal contiguous sequences work;
- skipped and large-forward-jump sequences do not block valid events;
- controller reset/wrap is automatically recoverable;
- duplicate retransmission remains idempotent;
- conflicting reuse is detected and does not double-apply state;
- delayed/out-of-order evidence cannot bind incorrectly to a newer context;
- cycle counting cannot double-count because of reset/reuse;
- no commissioning procedure requires manually setting the sequence variable to the Server's expected next value.

### Completion report
Report where strict continuity was enforced, what now provides idempotency/correlation, which sequence anomalies are diagnostic only, migrations/config changes, updated commissioning steps, and test results.

---

## 5. Implement tablet power / deep-sleep / Wi-Fi behavior by workflow status

Replace the previous tablet-power state list with the following **authoritative seven-state policy**. Implement it as an explicit centralized state-driven policy in the ESP32/e-ink tablet code. Do not keep older conflicting mappings.

### Product rules
- Wi-Fi is OFF by default unless a state explicitly requires a refresh session.
- Screen/UI awake state and Wi-Fi connectivity are separate concerns.
- `Ready for Setup` and `In Setup` must remain responsive for the setup operator without keeping Wi-Fi connected.
- Deep-sleep states must consume minimal battery and wake only from the explicitly allowed sources.
- Do not add background 15-second polling.

### Authoritative state policy

#### 1. Ready for Setup
- **No deep sleep.** Tablet remains awake for local interaction.
- Wi-Fi **OFF** by default.
- Wi-Fi turns **ON by physical button**.
- After button press, connect to the server and keep Wi-Fi ON until either:
  - the configured Wi-Fi/session timeout expires, or
  - the server reports transition to `In Setup`.
- Then apply the new state policy and turn Wi-Fi OFF.
- No periodic polling while waiting if no button was pressed.

#### 2. In Setup
- **No deep sleep.** Tablet remains awake so the setup operator can browse tool tables/pages.
- Wi-Fi **OFF** by default.
- Wi-Fi turns **ON by physical button** for an explicit refresh/check.
- After the refresh completes or times out, turn Wi-Fi OFF again.
- No background periodic polling.

#### 3. In Setup Run
- Enter **deep sleep**.
- Wi-Fi OFF while sleeping.
- Wake by **physical button only**.
- No periodic refresh.

`In Setup Run` is intentionally distinct from `In Setup`: once the setup activity has reached the run/check phase where continuous tablet interaction is no longer required, battery saving takes priority.

#### 4. In QA
- Enter **deep sleep**.
- Wi-Fi OFF while sleeping.
- Wake by **physical button only**.
- No periodic refresh.

#### 5. Ready for Production
- Enter **deep sleep**.
- Wi-Fi OFF while sleeping.
- Wake automatically every **60 seconds** for one refresh, or wake by physical button.
- On 60-second timer wake:
  - enable Wi-Fi;
  - perform exactly one server refresh;
  - update the local projection/display only if relevant data changed;
  - disable Wi-Fi;
  - return to deep sleep.
- On button wake, perform the explicit interactive refresh and then return to the correct state policy.
- The 60-second interval should be configurable where practical, with 60 seconds as the default.

#### 6. In Production
- Enter **deep sleep**.
- Wi-Fi OFF while sleeping.
- Wake by **physical button only**.
- No periodic refresh.

#### 7. Complete
- Treat the operation as completed and no longer requiring active tablet interaction.
- Enter **deep sleep**.
- Wi-Fi OFF.
- Do not perform periodic server refreshes.
- Preserve button wake only if the existing product UX requires viewing the completed operation/history; otherwise remain asleep according to the existing completed-item lifecycle.
- Do not invent a new active polling requirement for `Complete`.

### Transition behavior
The power policy must be reapplied immediately whenever a server refresh returns a different workflow state.

Examples:
- `Ready for Setup` + button -> Wi-Fi ON -> server reports `In Setup` -> apply `In Setup`: stay awake, Wi-Fi OFF.
- `In Setup` -> server/machine workflow reaches `In Setup Run` -> apply `In Setup Run`: deep sleep, button wake only.
- `In QA` -> server reports `Ready for Production` -> apply `Ready for Production`: deep sleep with 60-second timer wake enabled.
- `Ready for Production` -> server reports `In Production` -> cancel 60-second periodic wake and apply button-only deep sleep.
- `In Production` -> `Complete` -> remain deep-sleep oriented with no periodic refresh.

### Implementation guidance
Inspect the current tablet firmware/client implementation and define one policy mapping from workflow status to at least:
- `sleep_mode`
- `wifi_default`
- `wake_sources`
- `periodic_refresh_interval`
- `button_refresh_behavior`
- `wifi_session_timeout`

Prefer one centralized state-policy table/state machine rather than scattered conditional logic.

Preserve existing proven GPIO/button wake behavior and e-ink page navigation. Wi-Fi OFF must not prevent browsing already-cached local tool/setup data in awake states.

### Acceptance tests / simulator scenarios
Add tests proving at least:
- `Ready for Setup` stays awake indefinitely with Wi-Fi OFF until the user presses the button.
- `Ready for Setup` button press enables Wi-Fi and disables it after timeout if no transition occurs.
- `Ready for Setup` button press followed by `In Setup` transition keeps the tablet awake and turns Wi-Fi OFF.
- `In Setup` stays awake, Wi-Fi OFF, and supports button refresh.
- `In Setup Run` enters deep sleep and has no periodic wake.
- `In QA` enters deep sleep and wakes only by button.
- `Ready for Production` wakes every 60 seconds, refreshes exactly once, and returns to deep sleep; button wake also works.
- Transition from `Ready for Production` to `In Production` disables the 60-second timer wake.
- `In Production` wakes only by button.
- `Complete` has no periodic polling/refresh and remains deep-sleep oriented.
- no 15-second background setup polling remains anywhere.

### Completion report
Report:
- the final workflow-status-to-power-policy mapping;
- firmware/client files changed;
- how timer wake and button wake are configured;
- Wi-Fi session timeout behavior;
- how state changes cancel/enable wake timers;
- tests/simulator results;
- any remaining physical-device commissioning step.

---

## General instructions

- First inspect the current implementation and existing tests before changing architecture.
- Prefer extending the existing event-driven workflow rather than creating parallel state systems.
- Preserve backward-compatible data where practical; add migrations when schema/state changes require them.
- Keep manual workflow authority minimal and explicit.
- Run the full relevant server/client test suites and report the results.
- At completion, summarize changed files, migrations, behavior changes, and any remaining physical-machine commissioning steps.