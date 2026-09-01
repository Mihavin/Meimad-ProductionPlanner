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

## 6. Implement role-based preparation queues as the Production Package workflow foundation

Build the preparation-stage views that will become the operator-facing foundation for Production Package creation and release. Reuse the existing QA queue/view UX pattern instead of inventing a separate navigation model.

This task **clarifies and tightens the queue semantics in Task 3**. Where this task conflicts with a looser interpretation of Task 3, use the rules below.

### Product principle
Queue membership is a **derived projection of current facts**, not a manually pushed status.

The preparation chain is sequential:

`Planner machine assignment -> NC Creator -> Tool Room Manager -> Setup -> QA / later workflow`

An Operation must not appear in both Tool Room and Setup preparation queues at the same time. Completion of the gate for one role automatically removes it from that role's queue and makes it eligible for the next role.

Do not add "send to next department" buttons when the next queue can be derived from authoritative facts.

### A. Machine assignment remains Planner authority
The Production Planner owns Machine assignment.

- NC Creator, Tool Room Manager, and Setup users must not choose or change the Machine as part of this preparation workflow.
- If an Operation has **no concrete Machine assignment**, it does **not** appear in the NC Creator queue yet.
- Do not infer a Machine group or let NC Creator select a Machine just to make the Operation visible.

### B. NC Creator view / Programming Pending
Create a dedicated **NC Creator** role/view using the same general list/queue interaction pattern as QA.

An Operation appears in the NC Creator queue only when:
1. the Operation is assigned by Planning to a concrete Machine; and
2. there is no current released NC for that Operation that is valid for the assigned Machine under the existing release/post rules.

This is effectively **Programming Pending** for assigned work.

Rules:
- Unassigned Operations are excluded from this queue.
- The queue is independent of where the Operation sits chronologically in the Production Plan; if it is assigned and lacks the required released NC, it belongs in NC Creator's backlog.
- NC Creator may perform the existing NC creation/release workflow, but may not reassign the Machine.
- When a valid/current NC release for the assigned Machine exists, the Operation automatically leaves NC Creator's queue.

### C. Tool Room Manager view / Tool Preparation Pending
Create a dedicated **Tool Room Manager** view.

An Operation becomes visible here automatically when:
1. it has a concrete Machine assignment; and
2. it has the required current/released NC for that assigned Machine; and
3. Tool Room preparation is not yet complete according to the existing tool/readiness model, including the current Tool Offset Table requirement.

This is the Tool Room preparation gate.

Rules:
- The Operation must no longer appear in NC Creator once the NC gate is satisfied.
- Tool Room remains responsible while the required tool preparation / Tool Offset Table is missing or not current.
- Do not show the Operation in Setup yet.
- When Tool Room completes its work and the required **Tool Offset Table is uploaded/released/current**, the Operation automatically leaves Tool Room Manager's queue.

### D. Setup view / Setup Pending
Create a dedicated **Setup** view for setupists / setup responsibility.

An Operation becomes visible in Setup automatically only after the Tool Room gate is complete, meaning at minimum:
1. a concrete Machine assignment exists;
2. the required current/released NC for that assigned Machine exists; and
3. the required current Tool Offset Table / Tool Room preparation is complete.

This is **Setup Pending / Queue for Setup**.

Strict gate rule:
- Before Tool Room completion, Setup must not see the Operation as available for setup.
- Once Tool Room completion is established, Tool Room no longer needs the Operation in its pending queue and Setup becomes the active preparation owner.

Preserve the already agreed handoff/start semantics from Task 3:
- physical Tool Room -> setupist handover is the deliberate manual, permissioned event;
- the Machine is already known and is not reselected;
- setupist assignment is filtered by Machine skills;
- handover moves the operation to `Ready for Setup`;
- actual current Offset Loader execution on the CNC starts `Setup In Progress` automatically.

### E. Shared queue UX
Use one reusable queue/view pattern for role-specific preparation work, visually and behaviorally aligned with the existing QA view.

At minimum provide role-specific projections/views for:
- NC Creator / Programming Pending
- Tool Room Manager / Tool Preparation Pending
- Setup / Setup Pending
- existing QA queue

Prefer shared components, filtering infrastructure, row layout, status presentation, search/filter conventions, and detail-opening behavior rather than four unrelated screens.

Each role sees only the actions relevant to that stage.

### F. Production Package foundation
These views are the workflow surface from which the Production Package will be assembled/reviewed. Do **not** invent new package contents or a parallel package-state machine in this task if the existing release model does not already define them.

Instead:
- expose the current readiness facts/release references needed to understand why an Operation is in its current queue;
- make missing prerequisites explicit;
- preserve immutable/current release semantics;
- structure the UI/domain projection so a dedicated Production Package creation/release action can be added cleanly on top of these queues.

### G. Derived state, not duplicated mutable status
Where practical, do not persist `PROGRAMMING_PENDING`, `TOOL_ROOM_PENDING`, or `SETUP_PENDING` as independently editable flags.

Derive queue membership from authoritative facts such as:
- Machine assignment;
- current/released NC availability for that Machine;
- current tool data / Tool Offset Table readiness;
- handover events;
- current Offset Loader execution;
- later QA/workflow events.

If a persisted projection/cache is required for performance, it must be reconstructible from authoritative state/events and must not become a competing source of truth.

### Acceptance tests
Add tests proving at least:
- unassigned Operation appears in none of these preparation queues;
- assigning a Machine to an Operation with no valid released NC makes it appear in NC Creator;
- NC Creator cannot change the Machine through this workflow;
- releasing the required NC automatically removes it from NC Creator and makes it eligible for Tool Room Manager;
- while Tool Room preparation / Tool Offset Table is incomplete, the Operation is visible in Tool Room Manager and not in Setup;
- releasing/currentizing the required Tool Offset Table automatically removes it from Tool Room Manager and makes it visible in Setup;
- an Operation is never simultaneously pending in Tool Room and Setup;
- handover and Offset Loader execution preserve the transitions already specified in Task 3;
- queue membership is recomputable from authoritative facts after restart/migration;
- existing QA behavior remains intact.

### Completion report
Report:
- the exact predicates used for each role queue;
- which existing QA components/patterns were reused;
- server/API projection changes;
- client views/components added or changed;
- any schema/migration changes;
- tests executed and results;
- any unresolved Product Package composition questions that should be decided before implementing the actual package creation action.

---

## 7. Implement server-owned Production Package creation and role actions

Implement the actual **Production Package** workflow on top of the role queues from Task 6. This task defines the package composition, server storage, context-menu actions, readiness boundary, and Machine-dependent build behavior.

This task **supersedes earlier Task 3 / Task 6 wording where they say that Tool Offset Table completion or physical handover alone makes the Operation `Ready for Setup`**. The authoritative new rule is:

> A valid current Production Package for the exact Operation + assigned Machine is what makes the Operation **Ready for Setup**.

Opening, downloading, or copying the package does not change workflow state.

### A. Production Package is scoped to one Operation and one assigned Machine
A Production Package is a server-owned immutable snapshot prepared for one concrete Operation on its currently assigned Machine.

The package must bind at minimum:
- `ProductionPackageId`;
- Operation / Production Run identity according to the current domain model;
- assigned `MachineId`;
- exact current NC release when applicable;
- exact current Tool Table / Tool Offset Table release used by Tool Room;
- verification mode/capabilities used during build;
- generated Offset Loader release when applicable;
- creation timestamp and actor;
- artifact hashes/checksums and a manifest sufficient to prove exactly what was packaged.

Do not treat a package as reusable across Operations. A package created for one Operation must never become current for another Operation, even when the same NC program or tool data happens to be reused.

### B. Server stores the actual package artifacts
Unlike the earlier metadata-only idea, the Server must retain the actual current Production Package artifacts.

Store the exact files produced for the package in a server-managed package directory/storage area and keep them immutable once the package becomes current.

The Setup user may open/copy/export these files, but those actions must never mutate the authoritative server copy.

A package should be activated atomically only after all required artifacts are generated, written, and verified successfully. Do not expose a half-built package as Ready for Setup.

Keep historical/superseded packages according to the existing retention model; do not silently delete audit evidence merely because a new package becomes current.

### C. Machine-capability-driven package composition
Determine package composition automatically from the assigned Machine configuration. Do not ask the Tool Room user to manually choose a package type.

#### CNC + Server Verification Enabled
The package contains at least:
1. the package-specific runnable NC file derived from the exact released NC source/template;
2. the finalized current Tool Table / Tool Offset Table artifact required for that Operation;
3. a newly generated **package-specific Offset Loader** prepared for the existing Server verification protocol.

The Offset Loader is unique to this package/context and must not be valid as the current loader for a different Operation/package.

Bind the package to the existing verification architecture rather than creating a second challenge/response mechanism.

#### CNC + Server Verification Disabled
The package contains at least:
1. the runnable NC file;
2. the finalized Tool Table / Tool Offset Table artifact.

Do **not** generate an executable verification Offset Loader and do **not** inject verification calls/codes into the runnable NC when Server Verification is disabled.

#### Manual Machine
Do not generate CNC verification code or an executable CNC Offset Loader.

Build the Machine-specific manual package from the artifacts that are meaningful for manual setup, at minimum the finalized Tool Table / tool-offset/setup information already represented by the current model. If a third human-readable offset/setup sheet is required, make it a manual setup artifact, not CNC executable code.

Do not invent NC requirements for a Manual Machine.

### D. Network connectivity affects delivery, not verification policy
A Machine may be network-connected or not connected.

Use network capability to decide which delivery actions are available:
- when a supported direct Machine-transfer path exists and the Machine is connected, it may be offered as an additional delivery option;
- otherwise the user must still be able to open/export/copy the package through the normal file workflow.

Do **not** silently turn Server Verification off merely because the Machine is temporarily disconnected. Verification policy comes from the Machine configuration. If a configuration combination cannot actually support verification, expose a clear blocking/configuration error instead of silently building a weaker package.

### E. Verification placeholders are resolved during package build
Change the NC preparation model so that verification-specific executable content is a **package-build concern**, not something permanently forced into every runnable NC regardless of Machine configuration.

The released NC source/template must contain explicit, stable, machine-readable placeholders/markers at every protocol-defined location where package-specific Meimad verification content may be required.

Requirements:
- do not use fragile free-text search/replace heuristics;
- validate placeholder presence/uniqueness/structure when the NC is released or before package creation;
- for a verification-enabled CNC package, resolve the placeholders into the required current verification hooks/content and produce the runnable package NC;
- for a verification-disabled CNC package, resolve/remove the verification placeholders so the resulting runnable NC contains no unnecessary Server-verification code;
- preserve the immutable identity of the original NC release and record the exact generated package artifact hash separately.

This changes the earlier permanent rule that every released runnable NC must already contain an always-active generic verification hook. Update `AGENTS.md`, architecture/specification documents, commissioning tooling, examples, tests, and postprocessor guidance so they agree with the package-build placeholder model. Do not leave contradictory rules in the repository.

Do not weaken NC identity matching or the existing exact Run/Machine/NC/Offset Loader binding when verification is enabled.

### F. NC Creator queue context-menu actions
For an Operation in the NC Creator / Programming Pending view, provide role-appropriate context-menu actions at minimum:
- **Open Case**;
- **Open Operation**;
- **Upload G-code**.

`Upload G-code` must navigate into the existing Operation/NC upload-release workflow, ideally directly to the relevant NC/G-code section. Do not implement a second independent G-code upload/versioning system inside the queue view.

Machine assignment remains Planner authority.

### G. Tool Room Manager context-menu actions
For an Operation in the Tool Room preparation view, provide at minimum:
- **Open Case**;
- **Open Operation**;
- **Open Tool Table**;
- **View NC File** as read-only text using the current released NC relevant to the assigned Machine;
- **Create Production Package**.

`Create Production Package` is the deliberate Tool Room action that assembles the final Machine-specific server package from the current authoritative releases/configuration.

Before building, validate that every required prerequisite for the assigned Machine type is current. If not, explain exactly what is missing/stale rather than creating a partial package.

When creation succeeds:
- store the package and its manifest on the Server;
- mark it current for this exact Operation + Machine;
- automatically make the Operation **Ready for Setup**;
- remove it from Tool Room pending ownership according to the derived queue predicates.

Do not require a second manual "send to Setup" action.

### H. Setupist context-menu actions
Keep the Setup queue menu intentionally small for now. Provide at minimum:
- **Open Case**;
- **Open Operation**;
- **Open Production Package**.

`Open Production Package` must open/browse the exact current package files for that Operation. The setupist can copy them to removable media, a local disk, or the Machine using the available shop-floor workflow.

Opening/copying/downloading the package is **not** a workflow transition and must not produce a fake setup-start event.

Additional photo/checklist/web-app functionality is outside this task unless already required by existing behavior.

### I. Ready for Setup and Setup start semantics
The current valid Production Package is the readiness boundary:

`Tool Room preparation -> Create Production Package -> Ready for Setup`

Do not require package download or physical file copy to reach `Ready for Setup`.

For verification-enabled CNC work, preserve the existing machine-driven transition where execution of the package's current Offset Loader is authoritative evidence that setup has actually started and can move the workflow into `Setup In Progress` / the corresponding current setup state.

For Machine types that do not use an executable Offset Loader, do not invent a fake loader merely to obtain a setup-start event. Preserve an existing suitable start signal if one already exists; otherwise report that start-trigger choice as a product decision still required rather than silently creating a new manual status button.

### J. Package invalidation / supersession
A current package must become stale/superseded when any fact it was built from changes materially, including at minimum:
- assigned Machine changes;
- current NC release changes or is invalidated;
- current Tool Table / Tool Offset Table release changes;
- verification-enabled configuration changes in a way that affects package contents;
- a new Production Package is deliberately created for the same Operation + Machine.

When a package is no longer valid/current, `Ready for Setup` must be recomputed accordingly. Do not continue presenting an old package as ready after one of its bound inputs changed.

A newly generated Offset Loader must have a fresh package/release identity and must supersede the prior loader for that Operation context according to the existing verification rules.

### K. Security / integrity / authority boundaries
- Production Package creation is a Server-authoritative operation, even when initiated from the Windows Tool Room view.
- Keep package files immutable on the Server after successful creation.
- Use checksums/hashes in the manifest and verify generated artifact integrity before activation.
- Do not introduce Machine Secrets, replacement shared secrets, or a parallel verification protocol.
- Do not let clients choose a different Machine while creating/opening the package; use the Planner-assigned Machine.
- Do not let a Setup client edit the authoritative server package in place.

### L. Package responsibility and audit — minimal, no approval workflow
Do **not** introduce a separate Production Package supervisor, approver, sign-off stage, or multi-step approval workflow in this task.

The responsibility model for now is intentionally simple:
- the **Tool Room role** creates the Production Package through `Create Production Package`;
- successful package creation itself is the authoritative completion of Tool Room package preparation for that Operation;
- the Server automatically records the identity of the user who created the package and the Server timestamp;
- the immutable package snapshot/manifest is the audit record.

At minimum, the audit record must retain:
- `ProductionPackageId`;
- creator/user identity;
- creation Server timestamp;
- exact Operation / Production Run identity;
- assigned Machine identity;
- exact NC release used, when applicable;
- exact Tool Table / Tool Offset Table release used;
- exact generated Offset Loader release, when applicable;
- Machine capability / verification mode used to build the package;
- artifact hashes/checksums and manifest identity;
- later supersession/invalidation relationship when the package stops being current.

Opening, viewing, copying, exporting, or sending the package does not require separate package approval and does not transfer package authorship. If access/use events are already part of the existing operational audit infrastructure they may be retained, but do not create a new mandatory approval bureaucracy around the package.

A user may hold multiple application roles; do not assume that package creation requires a distinct human supervisor merely because role authorization exists elsewhere in the system.

### Acceptance tests
Add tests proving at least:
- NC Creator `Upload G-code` routes to the existing Operation NC workflow rather than duplicating release logic;
- Tool Room can open the current Tool Table and read-only NC;
- a verification-enabled CNC package contains NC + current tool/offset table + a unique generated Offset Loader and is bound to the exact Operation/Machine/releases;
- a verification-disabled CNC package contains NC + tool/offset table and contains no active Server-verification injection/Offset Loader;
- a Manual Machine package contains only applicable manual setup artifacts and no CNC verification executable;
- package creation is atomic and a failed build never creates `Ready for Setup`;
- successful current package creation automatically produces `Ready for Setup`;
- successful package creation records creator/user identity and Server creation timestamp;
- the package audit record retains the exact bound releases/configuration/artifact identities needed to reconstruct what was created;
- no supervisor approval/sign-off is required to make a successfully built package current;
- opening/copying/downloading a package does not change workflow state;
- the same Offset Loader cannot become current for another Operation/package;
- Machine reassignment invalidates/supersedes the current package;
- new NC release invalidates/supersedes the current package;
- new Tool Table/Tool Offset Table release invalidates/supersedes the current package;
- verification configuration changes invalidate a package when its generated contents are affected;
- verification placeholders are validated deterministically and do not depend on arbitrary text searching;
- verification-disabled package NC output has no leftover active verification code/placeholders;
- Setup `Open Production Package` resolves only the exact current package for that Operation;
- disconnected Machines still support file-based package access/export and are not silently downgraded from configured verification policy.

### Completion report
Report:
- the final Production Package data model and server storage layout;
- exact package composition rules by Machine type / verification mode;
- placeholder grammar and package-build transformation rules;
- how package identity binds to Operation, Machine, NC release, tool/offset release, and Offset Loader;
- exact derived predicate for `Ready for Setup` after this task;
- context-menu actions added to NC Creator, Tool Room, and Setup views;
- invalidation/supersession rules;
- package creator/audit fields retained and confirmation that no separate supervisor approval stage was introduced;
- migrations and documentation changes, especially any `AGENTS.md` rule replaced by this decision;
- tests executed and results;
- any unresolved setup-start signal for Manual or non-Offset-Loader Machines.

---

## General instructions

- First inspect the current implementation and existing tests before changing architecture.
- Prefer extending the existing event-driven workflow rather than creating parallel state systems.
- Preserve backward-compatible data where practical; add migrations when schema/state changes require them.
- Keep manual workflow authority minimal and explicit.
- Run the full relevant server/client test suites and report the results.
- At completion, summarize changed files, migrations, behavior changes, and any remaining physical-machine commissioning steps.