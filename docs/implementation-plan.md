# Implementation Plan

- **Status:** Server foundation, SQLite schema v8, core planning-resource slices, Timeline API/Windows Timeline, read-only TV Dashboard, official job-package generation, E-Ink API/simulator, Single Edit Mode, verified backup, and Windows Case/Operation/Order/Batch/Machine creation workspaces implemented; package approval/retention and full conflict policy remain unimplemented
- **Sequence source:** Functional Specification v0.3, expanded with decision and verification gates

## 1. Delivery principles

- Resolve a decision before its implementation becomes expensive to reverse.
- Build the authoritative Server and contract before full clients.
- Keep every client API-only; never use direct SQLite access.
- Preserve manual planning and conflict explanation from the first vertical slice.
- Verify restore, concurrency, read-only security, and offline behavior as product behavior, not post-release chores.
- Stabilize and simulate E-Ink APIs before starting production firmware/hardware work.
- Do not claim a phase complete until its exit criteria are demonstrated.

## 2. Phase 0 - decisions and executable specifications

This phase precedes the source document's numbered implementation sequence.

### Deliverables

- Record approved technology stack, supported Windows versions, repository/build/test conventions, and deployment packaging.
- Resolve the blocking domain questions: Case/revision identity, timing ownership, Batch allocation equation, same-Case Batch rule, route snapshot/version semantics, statuses, quantities, duration/time representation, and lifecycle/delete policy.
- Define timeline calculation inputs, calendars, duration formula, dependency graph rules, conflict catalog, severity, and accepted-versus-rejected planning errors.
- Define identity, authorization, Single Edit Mode lease/recovery/queue/audit behavior, and LAN transport security.
- Define SQLite lifecycle, migration recovery, backup target/schedule/retention/encryption, RPO/RTO, and restore drill.
- Define Working Folder access, preview/cache rules, package publication, file allow-listing, integrity, and retention.
- Freeze an initial OpenAPI contract and create client/device contract fixtures.
- Choose initial scale and performance targets.

### Exit gate

Every decision required by Server phases 1-5 has an approved, dated record and the functional, architecture, data-model, API, and test documents agree. E-Ink-only decisions may remain open until phase 11, provided no earlier schema or API silently freezes them.

## 3. Source-defined implementation sequence

The following order is preserved exactly from the v0.3 functional source:

1. Server skeleton, configuration, logging, and health endpoint.
2. SQLite schema and server-owned migrations.
3. Domain model and API for Cases, Orders, Production Batches, Operations, Machines, and assignments.
4. Server-side Single Edit Mode.
5. Backup and restore verification.
6. Windows Planning Client against the API.
7. Machine Board and manual drag-and-drop backlog.
8. Time calculation and conflict engine.
9. Timeline View in the Windows client.
10. Read-only TV Dashboard web interface.
11. Server-side read-only E-Ink display/package API and simulator.
12. Separate ESP32 firmware/hardware prototype after the APIs stabilize.

## 4. Phase 1 - Server foundation

**Implementation status:** Server skeleton implemented with .NET 10, ASP.NET Core/Kestrel, validated host/port configuration, lifecycle logging, `/health`, normal executable hosting, context-aware Windows Service lifetime, and startup/health integration tests. Production service installation, identity/TLS, correlation middleware, detailed readiness, and operational deployment verification remain pending.

### Scope

- Create the chosen Server solution/project under `server/`.
- Load validated configuration without committing secrets.
- Add structured logging and correlation IDs.
- Add liveness/readiness health behavior.
- Support development console execution and a production Windows Service host path.
- Establish API versioning and safe error handling.

### Tests and exit gate

- Fresh developer setup builds and runs with documented commands.
- Invalid/missing configuration fails safely and explainably.
- Health distinguishes liveness from database/migration readiness without leaking secrets.
- Service install/start/stop/restart and crash recovery are verified on the supported Windows baseline.
- Factory-interface binding and firewall behavior meet the approved LAN-only policy.

## 5. Phase 2 - SQLite schema and migrations

**Status:** Schema versions 1 through 7 implemented. Verified backup is implemented; active-database recovery policy remains open.

### Scope

- Implemented the initial logical model as schema version 1, Case details as version 2, Order Notes as version 3, explicit Machine master fields/device-binding uniqueness as version 4, durable Single Edit Mode requests as version 5, immutable E-Ink package/file metadata as version 6, official package snapshots/asset roles as version 7, and optional path-only Machine pictures as version 8.
- Implemented ordered migration metadata, `user_version`, startup application, and rejection of newer unsupported versions.
- Implemented transactional migration execution, foreign keys on every Server connection, relationship/backlog indexes, selected uniqueness/check constraints, restrictive planning-record deletion, and version/timestamp storage fields.
- Added isolated deterministic test databases; no live database is committed.

### Tests and exit gate

- Fresh database creation and idempotent reapplication are tested.
- Fresh creation, idempotent reapplication, supported prior-version data carry-forward, Order Notes, and version 4 Machine master shape are tested. Every later migration must test all supported upgrade paths.
- Foreign-key orphan inserts and core storage constraints reject invalid state.
- Newer incompatible schema versions fail startup. Verified backup is now available; automatic pre-migration invocation and recovery from migration failure remain part of the operational recovery work.
- No client project references a SQLite provider or database path.

## 6. Phase 3 - domain model and resource API

**Status:** Partially implemented. Case and Order create/read/update, append-only Case Operation creation, Production Batch atomic creation/read/snapshots, Machine create/update, basic recurring weekly Working Calendars, explicit Machine Assignment services/APIs, guarded Case/Operation/Order/Batch/Machine deletion, and read-only persisted Timeline projection are implemented. The Windows client exposes editor-only forms, fixed-choice domain dropdowns, Machine edit, and confirmation-protected deletes. Deletes are transactionally blocked by protected relationships and never touch external files. Assignment supports compatible assign, unassign, stable moves, and execution transitions. Orders remain demand only and no automatic scheduling is implemented. Calendar update/archive, Batch updates, downtime mutation, route edit/reorder, arbitrary dependency fan-in/out, and dependency snapshots remain pending.

### Scope

- Implement Cases and ordered Case routes.
- Implement Orders as demand only.
- Implement Production Batches, explicit allocations, and route snapshots.
- Implement Batch Operations, Machines, assignments/backlog positions, calendars, and downtime.
- Implement current-working timing according to the approved ownership model.
- Implement resource reads/mutations from the frozen contract.
- Return validation and domain conflicts as stable codes and safe explanations.

### Tests and exit gate

- Cover Case/Order/Batch separation and reject direct Order-to-Machine assignment.
- Cover one-order, split-order, multi-order, stock-inclusive, and stock-only examples.
- Cover every allocation boundary and lifecycle transition.
- Cover route reorder, route snapshot/version behavior, and changes after Batch creation.
- Cover Machine capability/calendar/downtime validation according to approved rules.
- Verify atomic transactions, optimistic concurrency, and safe error envelopes.
- Verify original Working Folder content is never modified and generated output remains below `_MeimadPlanner`.

Implemented Case verification covers create/read/update, filtered collection reads, ordered CaseOperation reads, Server-streamed previews, optimistic versioning, persistence after database reopen, existence without Orders, required Working Folder validation, unavailable external paths without filesystem creation, absence of SQLite BLOB columns, and rejection of stale edit generations before the write. Edit authority is checked within the same immediate SQLite transaction as each mutation. Authentication, POST idempotency, cursor pagination, and full Edit Mode lifecycle remain required before the Case API is production-ready.

Implemented Order verification covers field/status/date validation, parent foreign-key enforcement and missing-parent errors, create/read/list/update over HTTP, optimistic versioning, persistence after database reopen, Edit Mode enforcement, absence of Machine fields, and Case `isActive` changing as Order demand becomes active or complete. The minimal Order lifecycle is `active`, `complete`, and `cancelled`; archive/delete rules, quantity units, Order Number uniqueness, and Work Finish Date cutoff semantics remain open.

Implemented Production Batch verification covers one-Order, partial-Order, multi-Order, stock-inclusive, scrap-inclusive, and stock-only shapes; exact totals; positive rows; scrap-only rejection; duplicate semantic rows; integer overflow; missing/cross-Case Orders; atomic rollback; Edit Mode; database reopen; Case activity; and CaseOperation field snapshots remaining unchanged after source edits. Batch status creation is currently `planned`. Batch Operations implement `not_started`, `in_progress`, `suspended`, and `completed` transitions through active-editor commands; Finish removes the active assignment and compacts the backlog. Dependency snapshot/version, Batch mutation, cross-Batch over-allocation/completion, aggregate Batch completion, archive/delete, and richer execution history remain open.

Implemented Machine verification covers master-data normalization, unique numbers, valid Working Calendar references, process/axis/capability compatibility, active/inactive enforcement, display/device projections, optimistic PATCH, Edit Mode, and protection against master changes that invalidate assigned work. Working Calendar verification covers active-editor creation, opaque Server IDs, list/reopen persistence, unique names, timezone/workday/time validation, and weekly horizon expansion. Assignment verification covers stable IDs, compatible assignment, insert/move within a backlog, move between compatible Machines, unassign/compaction, contiguous zero-based positions, preserved unrelated relative order, inactive/incompatible targets, invalid positions, and HTTP behavior. Calendar update/exceptions, device binding mutation, bulk reorder, downtime, plan revisions, and conflict calculation remain open.

## 7. Phase 4 - Single Edit Mode

**Implementation status:** Core server coordination complete. Human authentication, caller-class authorization, notifications, disconnect/crash policy, audit, and retention remain.

### Scope

- Implemented one atomic server-side Edit Mode state machine with Viewer, Editor, and RequestingEdit caller states.
- Implemented status, request/outcome, Release, Reject, configurable timeout transfer, and voluntary release routes.
- Implemented active client/generation enforcement inside every current planning write transaction.
- Implemented one pending requester; additional contenders receive `edit_request_pending` and may retry.
- Add approved holder liveness, disconnect/crash recovery, notification, request retention, authentication, authorization, and audit behavior.

### Tests and exit gate

- Verified under concurrent acquisition attempts that exactly one generation can mutate and only one request remains pending.
- View clients continue to read while another client edits.
- Verified Release transfer, Reject retention, voluntary release, and automatic transfer using the configurable server timeout (30-second default).
- Verified stale holders cannot write after transfer.
- Verified repeated requests and simultaneous opposing decisions. Disconnect, crash, restart, notification, and wall-clock rollback policy remain open.
- TV and E-Ink caller classes can neither acquire Edit Mode nor mutate planning state.

## 8. Phase 5 - backup and verified restore

**Implementation status:** Core online backup, integrity verification, isolated restore verification, configurable destination, and count retention complete. Scheduling, authenticated operations, encryption, full recovery, and measured RPO/RTO remain.

### Scope

- Implemented SQLite online backup under server control using server-local staging.
- Implemented configurable folder and count-based retention with a 14-backup default.
- Implemented direct integrity/foreign-key verification and restore-to-isolated-test-database verification.
- Kept restore verification unable to target or overwrite the active database.
- Add schedule, authenticated operator trigger, destination access policy, encryption, alerting, active-database recovery workflow, and disaster-recovery drill steps.

### Tests and exit gate

- Verified backup consistency while normal writes continue.
- Verified both published and restored data with SQLite integrity and foreign-key checks.
- Verified corrupt managed backup rejection, active-database restore-target rejection, timestamp naming, and retention scoping.
- Full-disk, inaccessible network target, interrupted publication, and clean-host restore drills remain operational tests.
- Restore is exercised on a clean host within approved RPO/RTO, not merely unit-tested.

## 9. Phase 6 - Windows Planning Client shell

**Implementation status:** WPF connection/identity/health/Edit Mode, Case/Operation/Order/Batch workspace, manual Machine Planning Board with Start/Suspend/Finish controls, and read-only Timeline are implemented. Authentication and remaining record-mutation workspaces remain.

### Scope

- Implemented a .NET 10 WPF client under `client-windows/`.
- Implemented persisted Server-root configuration, simple local identity, stable client ID, typed HTTP health/Edit Mode/Case client, transfer actions, and safe offline behavior.
- Implemented Case Pool search/customer/active filters, Server-delivered preview thumbnails, complete editor-only Case creation/editing with Working Folder and picture selection, append-only Case Operation creation with dependency target/group input, editor-only Order creation, explicitly allocated Production Batch creation, and Open Working Folder using the API-supplied path.
- Implemented editor-only Working Calendar creation and API listing plus Machine creation from the Planning Board. The Machine selects a named calendar from a dropdown; users do not type calendar IDs. Fixed domain presets use dropdowns, while capabilities remain explicit free-form tokens.
- Kept business rules, planning data, edit authority, and all SQLite access on the Server.
- Add authenticated identity/session and route/Order/Batch update, assignment-form, Working Calendar update/exceptions, and downtime mutation workflows against the API.

### Tests and exit gate

- Client tests cover health/Edit Mode parsing, Case query routes, ETag/generation-protected Case saves, Case/tab population, Order/Batch create payloads and editor generation, explicit combined/stock/scrap allocation entry, external-folder launch, required headers/generation, safe Server errors, settings persistence/validation, local identity, and unavailable-Server behavior.
- Editing controls are disabled without current authority or confirmed Server health.
- Verified the client assembly has no SQLite reference and its local settings contain no database path.
- Full planning-route validation, stale resource revisions, reconnect compatibility, and production-use sizing remain future exit work.

## 10. Phase 7 - Machine Board and drag-and-drop backlog

**Implementation status:** Core board slice implemented. The Server supplies a snapshot-consistent unassigned operation pool and Machine backlogs. The Windows client performs manual assign, reorder, cross-Machine move, and unassign commands through Edit Mode, reloads only after acceptance, and shows incompatible/rejected feedback. Search/filter, downtime, calculated times, plan revisions, concurrency preconditions beyond Edit Mode generation, and calculated conflicts remain pending.

### Scope

- Implement Case/Batch pool, Active/Assigned/Not Assigned filters, search, Machine columns/backlogs, status cards, downtime, and conflict summary.
- Translate drag/drop into explicit atomic assignment/reorder commands.
- Send explicit Start/Suspend/Finish operation commands and reload only the accepted Server state.
- Preserve planner intent when the Server reports conflicts; never silently move another item.

### Tests and exit gate

- Assign, unassign, cross-Machine move, within-backlog reorder, stale concurrent view, and rejected mutation behave deterministically.
- The same submitted order is stored and returned unless the command is rejected.
- Capability, dependency, timing, and downtime conflicts appear with text/icon explanations.
- Large approved backlog/Case counts meet the performance target.

Implemented tests cover projection membership/order, exact API command targets, editor-only interaction, incompatible rejection with an unchanged local board, and existing repository invariants for stable contiguous backlog order. Because the pure time engine is not connected to persisted board inputs, the panel reports conflict calculation as unavailable rather than treating an empty list as a conflict-free plan.

## 11. Phase 8 - time and conflict engine

**Implementation status:** Pure domain calculation and read-only Timeline API are implemented. The persistence mapper consumes current fixed Machine backlogs, recurring weekly local Machine calendars or legacy explicit UTC windows, explicit UTC setup availability, downtime, Batch timing/quantity, and current route dependencies; the engine emits setup/production/idle/reserved/downtime intervals plus deterministic conflicts and never mutates/reorders inputs. Timezone conversion is performed on the Server for the requested horizon. The provisional duration formula is quantity multiplied by cycle time. Calendar exceptions/breaks/overnight shifts, immutable dependency snapshots, plan revision/cache, Work Finish Date risk, capability conflict projection, and production conflict severity policy remain pending.

### Scope

- Implement approved duration formula, Machine calendars, breaks/exceptions, downtime, backlog sequencing, Work Finish Date risk, and recalculation triggers.
- Implement Sequential, Parallel-capable, Independent, and Locked simultaneous dependencies exactly.
- Generate stable conflict codes, severity, affected records, and explanations.
- Produce immutable/read-consistent plan projections for clients.

### Tests and exit gate

- Use table-driven examples for every dependency mode and boundary time.
- Locked simultaneous members have identical projected start/finish; shorter Machines remain reserved to group end.
- Calendar, shift, break, downtime, time-zone/DST, rounding, missing timing, capability, and deadline cases match approved examples.
- Cycles/impossible graphs are rejected according to policy.
- Recalculation never changes assignment or backlog order.
- Determinism and performance meet the approved dataset/SLA.

Implemented deterministic tests use fixed UTC timestamps and cover setup-calendar gating, split intervals, downtime, Machine idle time, all four dependency meanings, locked-group reservations, dependency cycles, insufficient availability, same-Machine simultaneous infeasibility, repeatable serialization, and unchanged input/backlog order.

## 12. Phase 9 - Windows Timeline View

**Implementation status:** Implemented as a read-only WPF projection over `GET /api/v1/timeline`. It renders labeled setup, production, idle, downtime, and reserved intervals, Server conflicts, and dependency edges for only the selected Batch. The client contains no calculation or scheduling rules. Plan-revision consistency, zoom, local-time selection, richer navigation, and performance targets remain open.

### Scope

- Render the server timeline projection by Machine/time horizon.
- Show Batch/Operation identity, dependencies, simultaneous groups, downtime, conflicts, urgency, and freshness.
- Provide navigation to the authoritative edit surfaces; do not add a second scheduling model.

### Tests and exit gate

- Timeline and Board show one plan revision consistently.
- Zoom/filter/time-zone behavior preserves interval correctness.
- Overlap, simultaneous reservation, downtime, and conflict markings are understandable without color alone.
- Approved large horizon renders within the UI performance target.

## 13. Phase 10 - TV Dashboard

**Implementation status:** Core LAN-served dashboard implemented. The dependency-free fullscreen UI has large rows per display-enabled Machine, top-backlog current/next work and their Server-owned execution status, calculated conflicts, urgent Batches, current/upcoming downtime, conditional auto-refresh, visible offline retention, and no edit controls. The GET-only projection supports ETags. Authentication, display groups, offline-device telemetry, target-TV visual acceptance, and managed kiosk deployment remain pending.

### Scope

- Create a read-only web/kiosk dashboard under `client-tv-dashboard/`.
- Show server freshness, current time, per-Machine current/next/projected finish or conflict/idle/setup state, and factory summary counts.
- Implement approved automatic refresh, offline detection, reconnect, and kiosk deployment.

### Tests and exit gate

- Dashboard credential cannot invoke any mutation or Edit Mode path.
- Stale/offline state is obvious and never presented as fresh.
- Target TV resolutions, browser/kiosk environment, viewing distance, and refresh cadence pass visual acceptance.
- Status remains legible without color.

## 14. Phase 11 - E-Ink API and simulator

**Status:** Server official package generator/read side and browser simulator implemented. Approval/retention, physical-device behavior, full offline fixture simulation, and API stability approval remain open.

### Scope

- Implemented structured JSON as the explicit v1 Server/simulator baseline; pre-rendered assets require a later compatible contract decision.
- Implemented device registration/assignment, credential hashing/rotation/revocation, small conditional version check, Machine screen, exact-revision package manifest/file reads, and time config.
- Implemented active-editor publication for an assigned Batch Operation with immutable snapshot metadata, safe source/logical/storage paths, configurable allow-list/size limits, staged output, SHA-256, context revalidation, and failure cleanup. Approval roles/UI, signatures, retention, and superseded-revision access remain open.
- Implemented a dependency-free browser simulator for version-first polling, the structured Machine view, conditional refresh, manifest/file display, SHA-256 verification, and preserving the last rendered screen on request failure. Physical SD staging/atomic activation and local-only annotations remain device work; richer injected offline/corruption fixtures remain future simulator work.
- Add telemetry only if the open decision explicitly approves a separate non-planning write scope.

### Tests and exit gate

- Device A cannot read Device B or another Machine/package by guessing IDs.
- Unchanged version checks avoid package transfer and display-refresh work.
- Changed revisions download and activate atomically only after full verification.
- Interrupted, corrupt, oversized, revoked, missing, and malformed data retain prior valid content.
- Package files never expose source filesystem paths or unrelated confidential data.
- No checklist/comment upload route exists.
- Contract and simulator remain compatible across the approved firmware-support window.
- API is declared stable for the prototype with versioning/change policy documented.

## 15. Phase 12 - separate ESP32 hardware/firmware prototype

This phase belongs in a separately approved device project after phase 11's API stability gate.

### Scope

- Select MCU, panel/controller, power design, SD subsystem, input method, service connector, enclosure, mount, and environmental protection.
- Implement provisioning, credential storage, timekeeping, deep sleep, battery measurement, conditional polling, staged package handling, panel UI, failure state, and local annotations.
- Use one configurable firmware build for all Machines.

### Verification and exit gate

1. Measure deep-sleep current.
2. Measure a version-check wake cycle.
3. Measure full package download and display refresh.
4. Verify AP provisioning/reset and credential revocation/reassignment.
5. Verify interaction for every checklist, status, comment, navigation, and revision-clear action with the chosen inputs.
6. Verify persistence through sleep and, if possible, across battery replacement; record whether the selected storage design supports it.
7. Fault-inject power loss, network loss, invalid token, clock loss, SD removal/corruption, malformed manifest, checksum failure, oversized file, and panel refresh failure.
8. Verify readability at defined distance/lighting/temperature.
9. Run a one-week one-Machine pilot with numeric success criteria.
10. Order additional units only after the pilot and battery target pass.

## 16. Cross-cutting test strategy

- **Domain tests:** invariants, allocation, lifecycle, route/dependency graphs, and no-silent-repair behavior.
- **Property/model tests:** allocation conservation, ordering, dependency interval relationships, and deterministic recalculation.
- **Database tests:** constraints, transactions, migrations, concurrent access through Server only, and integrity.
- **API tests:** schema, validation, authorization, edit generation, ETag, idempotency, safe errors, and file boundaries.
- **Concurrency tests:** Edit Mode races, stale writers, transfer decisions, restart, and notification ordering.
- **Contract tests:** Windows, TV, E-Ink simulator, and later firmware against the frozen OpenAPI/fixtures.
- **Security tests:** credential scope, revoked devices, path traversal, oversized input, secret/log redaction, and read-only caller attempts against every mutation.
- **Operational tests:** service install/update/restart, backup/restore drill, disk/network failures, and monitoring.
- **UI tests:** freshness/offline state, production readability, keyboard/touch needs where applicable, and status without color.
- **Hardware tests:** measured current, brownout, storage corruption, panel refresh, environmental readability, and one-week pilot.

## 17. Definition of done for an implementation phase

A phase is complete only when:

- Its approved requirements and decisions are documented.
- Code, migration, API contract, and tests agree.
- Negative/failure paths are verified in proportion to risk.
- Security and read-only boundaries are tested server-side.
- User-facing status and errors are understandable.
- Operations/runbook changes are documented.
- No source file, secret, live database, generated package, or build output is accidentally committed.
- The repository states honestly what is implemented and what remains target design.

## Open decisions

The following questions are unresolved in the provided source documents. IDs should be retained when decisions are recorded.

### Product and domain

- **OD-001 - Case identity:** Does one Case represent a part number across revisions or one part-number/revision pair?
- **OD-002 - Timing ownership:** Are setup/cycle values Case-level, per Case Operation as shown in the prototype, or both with defined precedence?
- **OD-003 - Batch scope:** Resolved for the implemented slice: one Batch belongs to one Case and every Order allocation must belong to that same Case. Cross-Case Batches are rejected atomically.
- **OD-004 - Allocation equation:** Partially resolved: `plannedQuantity = order allocations + stock + scrapAllowance`; positive rows are explicit, scrap cannot stand alone, and partial Order allocations are allowed. Define cross-Batch over-allocation, allocation replacement, reallocation, cancellation, completion, and quantity unit.
- **OD-005 - Route versioning:** Partially resolved: append-only Case Operation creation does not retrofit existing Batch Operations; BatchOperation identity/display/Machine-type/timing fields remain immutable creation snapshots. Define aggregate route revision and multi-record dependency snapshots before Case Operation edit/reorder APIs.
- **OD-006 - Lifecycles:** Partially resolved: Orders implement `active` / `complete` / `cancelled`; Batch creation uses `planned`; Batch Operations implement `not_started` / `in_progress` / `suspended` / `completed`, with completed work removed from the active Machine backlog. Define aggregate Batch transitions, actual-time/history needs, Order Number/date/unit rules, archive/delete/cascade policy, and audit.
- **OD-007 - Tool package boundary:** What creates tool-cart/checklist content when full tool inventory is outside MVP?
- **OD-008 - Existing data:** Is migration/import from the shared Excel backlog required, and what is its source/quality/acceptance process?

### Planning engine

- **OD-009 - Time model:** Partially resolved in the pure engine to already-resolved setup/production durations, explicit half-open UTC windows, earliest-feasible placement in fixed backlog order, downtime subtraction, and split work intervals. Define `setup + quantity x cycle` or another derivation, setup reuse/shared capacity, in-progress work, persisted recurring shifts/breaks/holidays/overtime, local time/DST conversion, rounding, and recalculation triggers.
- **OD-010 - Dependencies:** Domain graph supports stable relationship records, fan-in/out, exact dependency meanings, sequential cycle rejection, and locked groups. The create-only API maps at most one referenced prior operation into the existing Case Operation row and validates the rehydrated full graph transactionally. The pure engine applies Sequential precedence, no constraint for Parallel-capable/Independent, and common start/finish plus shorter-member reservation for Locked simultaneous. Multi-record fan-in/out persistence, route-edit/version behavior, dependency snapshots, and richer cross-Machine feasibility remain open.
- **OD-011 - Conflict policy:** Structural assignment compatibility rejects incompatible commands. The pure engine reports deterministic blocking calculation/input conflicts, but the full catalog, severity/urgency, Work Finish Date cutoff, warning acceptance, plan-revision stability, and API presentation remain open.

### Single Edit Mode and identity

- **OD-012 - Authentication/authorization:** Choose human identity, roles, TV/device credentials, administrator boundary, and audit requirements.
- **OD-013 - Edit lease:** Resolved that every implemented planning mutation requires the current client ID and generation. The stored lease deadline is the transfer-response deadline, not a heartbeat. Define heartbeat, disconnect/crash/restart behavior, and stale unsaved edits.
- **OD-014 - Transfer contention:** Resolved MVP to one pending requester, no queue, Reject returning the requester to Viewer, and a configurable 1–3600 second server timeout with a 30-second default. Define cancellation, notifications, takeover safeguards, history retention, and audit.

### API, files, and deployment

- **OD-015 - Technology stack:** Server resolved to .NET 10 with ASP.NET Core/Kestrel and xUnit integration tests; Windows client resolved to .NET 10 WPF; TV resolved to dependency-free HTML/CSS/JavaScript served by the Server. Supported Windows/browser versions, long-term dependency/update policy, and production support baseline remain open.
- **OD-016 - API baseline:** `docs/api-contract.md` contains the Proposed MVP baseline. Case, Order, Batch creation/read, Machine master/backlog, assignment, Single Edit Mode, TV, and E-Ink read/device-administration subsets are implemented. Approve and convert the remaining contract to OpenAPI; identity, plan revision/concurrency beyond resource ETags, remaining lifecycles, paging/horizons, idempotency retention, and compatibility window remain decisions.
- **OD-017 - Network security:** Choose host/port discovery, HTTP versus HTTPS, certificate trust, firewall, CORS/CSRF where applicable, secrets storage, service identity, and installation/update strategy.
- **OD-018 - Working Folders:** Define supported path types, credentials/permissions, availability behavior, allowed files, preview generation/refresh, and `_MeimadPlanner` ownership/cleanup.
- **OD-019 - Backup:** Resolved configurable destination, online-backup behavior during writes, count retention, integrity/foreign-key checks, and isolated restore verification. Define schedule, authentication/authorization, encryption, destination access, migration coordination, alerting, active-database recovery procedure, clean-host drill, RPO, and RTO.
- **OD-020 - Observability/NFR:** Define expected Cases, Orders, Batches, Machines, concurrent Windows clients, TVs/tablets, response/recalculation targets, uptime, offline threshold, and log/privacy retention.

### TV Dashboard

- **OD-021 - Kiosk target:** Hosting is resolved to the LAN-only Server; default refresh is configurable at 15 seconds and failed refresh retains the last snapshot with an offline banner. Choose authentication, browser/kiosk management, screen resolutions, viewing distance, shop-floor current-job lifecycle, local-date urgency semantics, and offline-display telemetry.

### E-Ink package and protocol

- **OD-022 - Package publication:** Partially resolved: the current Windows Edit Mode holder publishes a caller-named immutable revision for an assigned Batch Operation; Machine/Case/Batch/Operation metadata is snapshotted and a correction creates another revision. Define a distinct preparer/approver role or approval UI, audit, revision naming/ordering policy, and whether reassignment should require explicit republish confirmation.
- **OD-023 - Package format:** Partially resolved: schema v7 stores immutable snapshot/file metadata, asset roles, safe logical/storage-relative paths, stable file IDs, lengths, media types, timestamps/order, and SHA-256. Generation supports in-folder preview, allow-listed NC/text sources, JSON tool table/offsets, and UTF-8 instructions with configurable limits; reads re-verify bytes. Define signatures, additional formats/encoding, compression, range/resume, device staging/activation, rollback, backup inclusion, superseded-revision access, and retention/garbage collection.
- **OD-024 - Rendering boundary:** Structured JSON is the implemented v1 Server/simulator baseline. Decide whether physical firmware uses it unchanged or adds a compatible pre-rendered asset profile; then define panel profile, bitmap/palette/fonts, pagination, localization, Unicode/RTL, and compatibility window.
- **OD-025 - Telemetry:** The source says server-to-device/read-only but optionally reports battery/firmware and last-seen. Decide whether telemetry exists; if so, use a separate narrowly scoped write endpoint and keep it outside planning data.
- **OD-026 - Device lifecycle:** Partially resolved on the Server: an active editor can register a spare or Machine-bound device, the Server returns a high-entropy token only at create/rotation, stores its SHA-256, permits one enabled E-Ink binding per Machine, and supports rebind/revoke/rotate. Define dedicated administrator authorization/audit, device-side secure storage, token expiry, lost-device/cached-data response, and physical reassignment workflow.
- **OD-027 - Time/sync:** Partially resolved for Server configuration: time-zone ID, workdays, one shift window, poll interval, retry attempts/backoff, and revision are configurable/readable. Define clock/NTP/RTC, zone portability/DST/holidays/exceptions, multiple windows, manual force-refresh behavior, jitter, stale thresholds, and clock-loss behavior.

### E-Ink hardware and interaction

- **OD-028 - Hardware selection:** Final MCU, panel/controller/size/resolution/orientation/colors/refresh behavior, AA chemistry and power topology, regulator/brownout, SD, USB service access, mount, and environmental rating.
- **OD-029 - Input contradiction:** The concept specifies only Refresh and Page buttons but also checkboxes, five local statuses, free-text comments, Back/Next, and confirmation. Choose touch/additional controls/text-entry behavior and map every interaction.
- **OD-030 - Local data:** Define persistence across battery replacement, annotation migration/clear behavior on revision, reassignment behavior, filesystem/capacity/wear/corruption protection, history limit, and encryption.
- **OD-031 - Firmware update:** Define safe update/service procedure if OTA remains deferred.
- **OD-032 - Measured acceptance:** Define numeric battery-life/current, wake/download/refresh time, readability distance/lighting/temperature, ghosting, storage, failure recovery, and one-week pilot success thresholds.
- **OD-033 - Source version:** Correct or confirm the E-Ink concept's v0.1 title versus v0.3 footer.
