# Implementation Plan

The eleven-item task list extends existing authority boundaries: staged Case/Order import, a Batch route guard, Batch-Operation backlog/criticality projection, schema-v26 external delay and master-calendar layering, shared icons/application icon, STEP bounding/reference tools, and the row TV dashboard.

## Multi-output Production Run workstream

Task 1 was accepted on 2026-08-23. Tasks 2–10 are implemented: schema v45 adds reusable Manufacturing Programs, v46 adds Production Runs and assignment migration, and v47 adds durable idempotent cycle observations. Server APIs cover composition, allocation, assignment, readiness, execution, cancellation, and reads. Planning Board and Timeline expose compressed run/program projections; Windows provides the multi-select Production Run dialog; normalized CNC observations resolve and advance exactly one active program. The architecture impact map remains authoritative.

- **Status:** Server foundation, SQLite schema v26, staged legacy Excel working-plan import, core planning-resource and Setup slices, extended Operation timing, Machine maintenance/breakdown lifecycle, assignment-owned planning modes, derived Batch/Order lifecycles, one canonical embedded/separate-window Timeline, compact Machine Board, read-only TV Dashboard, official job-package generation, E-Ink API/simulator, Single Edit Mode, verified backup, and Windows Case/Operation/Order/Batch/Machine/Machine-Type/Calendar/Machine-Availability/administrative workspaces implemented; package approval/retention and full conflict policy remain unimplemented
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
- Resolve remaining blocking domain questions: Case/revision identity, cross-Batch allocation, aggregate route revision, quantities, actual-time history, and lifecycle/delete policy. Operation-owned timing, API seconds/Windows `HH:mm:ss`, schema-v9 dependency snapshots, and aggregate Batch status are resolved.
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

**Implementation status:** Server skeleton implemented with .NET 10, ASP.NET Core/Kestrel, validated host/port configuration, lifecycle logging, `/health`, normal executable hosting, context-aware Windows Service lifetime, and startup/health integration tests. Separate WiX MSI packages now build self-contained client/Server payloads; the Server package registers an automatic service and preserves mutable ProgramData state. Final service identity/TLS, code signing, correlation middleware, detailed readiness, and live install/upgrade/recovery verification remain pending.

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

**Status:** Schema versions 1 through 45 implemented. Verified database backup is implemented; released G-code storage must be included in the operational backup policy.

### Scope

- Implemented ordered Server schema migrations through v39. V34 adds Machine execution/capacity/timing and Postprocessor compatibility; v35 adds released tool tables/process revisions/G-code and production pins; v36 adds immutable normalized released tools/count; v37 adds assignment release selection and append-only contextual offset readiness; v38 adds NC analysis/estimates; and v39 replaces manual material authority with locally verified Case receipts and explicit Production Batch reservations. Earlier migration contracts remain readable.

- Implemented deferred material reconciliation: a Batch may remain planned while `MISSING`/`UNVERIFIED`; Start readiness derives required pieces from planned quantity and becomes material-ready only with sufficient explicit local reservations. The Windows Batch tab records verified receipts and reservations and explains the three manual shortage paths. Existing Batch edit/create operations implement explicit reduce and ready/waiting split decisions, and dynamic Timeline projection recalculates quantity-dependent occupancy/dependencies on refresh. Automatic receipt selection, proportional allocation splitting, Kitaron stock reconstruction, and ERP warehouse authority remain intentionally absent.
- Implemented ordered migration metadata, `user_version`, startup application, and rejection of newer unsupported versions.
- Implemented the Server-local Kitaron connector: localhost-only UI/API, encrypted credentials, SQL read intent, metadata detection, editable versioned mapping, manual and interval-driven one-way synchronization, stable source links, atomic idempotent Case/Order/Case-Operation application, and result counts. Draft/blocked mappings cannot run. Missing source rows are retained, matched manual targets are never refreshed, and no Kitaron write, Batch, assignment, backlog, or Timeline mutation exists. The initial analysis remains in [kitaron-database-mapping-analysis.md](kitaron-database-mapping-analysis.md) and [kitaron-initial-mapping.yaml](kitaron-initial-mapping.yaml).
- Implemented transactional migration execution, foreign keys on every Server connection, relationship/backlog indexes, selected uniqueness/check constraints, restrictive planning-record deletion, and version/timestamp storage fields.
- Added isolated deterministic test databases; no live database is committed.

### Tests and exit gate

- Fresh database creation and idempotent reapplication are tested.
- Fresh creation, idempotent reapplication, and supported prior-version data carry-forward are tested through schema v10, including Machine-Type backfill/linkage, the Setup Calendar singleton, and Order lifecycle normalization. Current automated totals are recorded in the verification report.
- Foreign-key orphan inserts and core storage constraints reject invalid state.
- Newer incompatible schema versions fail startup. Verified backup is now available; automatic pre-migration invocation and recovery from migration failure remain part of the operational recovery work.
- No client project references a SQLite provider or database path.

## 6. Phase 3 - domain model and resource API

**Status:** Partially implemented. Existing resource APIs remain in place, with centralized contextual production readiness now implemented for managed Batch Operations. G-code, tool table, Machine/Postprocessor compatibility, capacity, exact-context offset confirmation, and local verified-material reservation are projected with component states/messages. Planning Board and Timeline remain plannable before readiness; first Start is blocked transactionally until ready. Detailed tool identity/life, actual offset values, authoritative ERP warehouse reconciliation, receipt correction/adjustment workflow, route reorder, arbitrary dependency fan-in/out, and aggregate route revision remain pending.

### Scope

- Implement Cases and ordered Case routes.
- Implement Orders as demand only.
- Implement Production Batches, explicit allocations, and route snapshots.
- Implement Batch Operations, Machines, reusable Machine Types, assignments/backlog positions, calendars/Setup Calendar selection, and downtime.
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

Implemented Case verification covers create/read/update, filtered collection reads, ordered Case Operation reads, Server-streamed previews, optimistic versioning, read-only setup/cycle sums with null-as-zero and empty-route-zero behavior, persistence after database reopen, existence without Orders, required Working Folder validation, unavailable external paths without filesystem creation, absence of SQLite BLOB columns, and rejection of stale edit generations before the write. Case Operation PATCH verifies partial-field merging, immutable route position, ETag conflicts, full-graph validation, and unchanged existing Batch snapshots. Edit authority is checked within the same immediate SQLite transaction as each mutation. Authentication, POST idempotency, cursor pagination, and full Edit Mode lifecycle remain required before the Case API is production-ready.

Implemented Order behavior adds the Server-derived `active` / `in_production` / `complete` lifecycle across every allocated Batch, atomic recomputation with Batch create/delete and Start/Suspend/Finish/Reset, aggregate completion coverage, zero-operation protection, and optimistic edit guards for quantity below allocated work or a contradictory linked status. New/unallocated Orders reject manually entered production tokens, and Batch creation rejects allocation to cancelled demand. Legacy already-linked cancellation is preserved until an explicit matching status assertion resumes derivation. Current automated verification is recorded in the verification report; archive rules, quantity units, Order Number uniqueness, Work Finish Date cutoff semantics, and cross-Batch over-allocation/reallocation remain open.

Implemented Production Batch verification covers one-Order, partial-Order, multi-Order, stock-inclusive, scrap-inclusive, and stock-only shapes; exact totals; positive rows; scrap-only rejection; duplicate semantic rows; integer overflow; missing/cross-Case Orders; atomic rollback; Edit Mode; database reopen; Case activity; optimistic Batch Number/planned-quantity/complete-allocation replacement with preserved instantiated route; and confirmed deletion across assignments, pause history, assignment overrides, package records, allocations, and Operations with backlog compaction. New and zero-operation Batches are `waiting`; Start/Suspend/Finish/Reset atomically derive and persist `waiting` / `in_production` / `complete`, increment the Batch version when that derived token changes, Reset retains the active assignment and backlog position, and Finish removes the active assignment and compacts the backlog. Cross-Batch over-allocation, aggregate route revision, archive policy, and richer execution history remain open.

Implemented Machine behavior covers master-data normalization, unique numbers, stable Working Calendar and Machine Type references, explicit `CNC_GCODE`/`MANUAL` execution mode, managed one-or-many Postprocessor mappings, usable tool capacity, Machine timing parameters, normal route compatibility, explicit reasoned/audited cross-type assignment override, active/inactive enforcement, optimistic PATCH, and running-backlog-head protection. Existing Machines migrate conservatively to `MANUAL` with null unknown values and a neutral factor of `1.0`; no CNC or Postprocessor mapping is inferred. Working Calendar behavior covers editor CRUD, guarded references/usages, multiple same-day work windows or one overnight window, contained breaks, dated exceptions, dedicated Setup selection, and optional cached Israeli-holiday overlay. Explicit online refresh writes the local cache and preserves manual overrides; Timeline/resource reads remain offline. Machine Type behavior covers unique names, reusable capabilities, rename/reference guards, optimistic update, and guarded delete. Combined/multiple overnight-window, overtime, and Calendar archive policy, device binding mutation, bulk reorder, downtime, plan revisions, and full conflict calculation remain open.

Implemented G-code/readiness behavior covers immutable process, tool-table/tool-row and Postprocessor-specific release history; native structured CSV/JSON and Cimatron MHT tool-table ingestion; explicit or uniquely resolved assignment release selection; local verified-material receipts and Batch reservations; exact Machine/process/release offset readiness; capacity; compatibility; centralized component evaluation; board/timeline explanation; transactional Start blocking; and exact production pins. The Windows Case Operations screen exposes only Release, never Draft. Not-ready work stays planned and requires a manual correction; no program, tool, process, assignment, reservation, or Batch quantity is silently changed. Detailed readiness boundaries remain documented in [G-code release and readiness architecture](gcode-readiness-architecture.md).

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

**Implementation status:** Compact WPF connection/health/Edit Mode header, dedicated Setup page with staged legacy Excel import, Case/Operation/Order-edit/Batch workspace, compact manual Machine Planning Board with player-style Start/Pause/Finish/Reset controls and assignment-mode context actions, and one embedded/separate-window read-only Timeline are implemented. The separate window shares the embedded view model and is not a backward/manual layer. Authentication and remaining unresolved record workflows remain.

### Scope

- Implemented a .NET 10 WPF client under `client-windows/`.
- Implemented persisted Server-root configuration, simple local identity, stable client ID, compact status/lock header, typed HTTP health/Edit Mode client, transfer actions, and safe offline behavior. Connection configuration is edited on the Setup page rather than occupying the operational header.
- Implemented Case Pool search/customer/active filters and Server-owned ordering by Part Number, closest active/in-production Order delivery, or Customer; unobstructed Server-delivered preview thumbnails with missing-preview text only; complete editor-only Case creation/editing with Working Folder and picture selection; Case Operation creation/optimistic editing with dependency target/group input; optimistic Order create/edit; explicitly allocated Production Batch creation; and Open Working Folder using the API-supplied path. Operation timing uses total-hours `HH:mm:ss` in the UI while API/storage remain seconds; read-only Case totals are Server-derived operation sums.
- Implemented a bounded Case STEP viewer using OpenCascade B-rep face tessellation and WPF depth-buffered rendering with Shaded, Visible edges, and Wireframe presentation modes, geometry-only one-time load Fit, stable orthographic orbit/zoom, optional bounding box, a shared solid/edge projection, signed-volume center-of-gravity orbit for closed bodies, vertex measurement, and local PNG snapshot selection. It leaves engineering files unchanged and uses an explicitly labeled edge/point fallback only when no solid faces can be tessellated.
- Added a depth-sorted WPF software triangle surface over the STEP hardware viewport, using the same model/camera projection so a blank driver-dependent `Viewport3D` frame cannot hide a successfully tessellated solid.
- Replaced the Windows localization timer/tree sweep with loaded-element, selected-tab, and coalesced language-change updates. Catalog exact, template, and sentence-segment paths are indexed and cached; language preference writes are coalesced off the UI thread. Timeline Canvas updates and STEP viewport redraws are coalesced at render priority and deferred while hidden, the five-second refresh is background-priority and non-reentrant, and unchanged edit-session snapshots no longer raise redundant view-model notifications.
- Implemented a dedicated Setup page for connection Save/Connect/Refresh, Working Calendar create/edit/delete with usage/work/break/exception authoring and Setup Calendar selection, Machine create/edit/deactivate/delete, reusable Machine Type create/edit/delete, Postprocessor create/edit/delete, and explicit per-Machine execution/capacity/timing/Postprocessor configuration. Machines select named Machine-enabled Calendar and Machine Type records through stable IDs; users do not type IDs. The Case Operation required-Machine dropdown dynamically unions registered Machine process/axis/Machine capability/Machine-Type capability tokens, offers blank Any, and retains a selected legacy token.
- Schema v11-v14 and the Setup page add Employee/Resource CRUD, employee full/partial-day availability exceptions, cached Israeli-holiday CRUD/online refresh/policies, and optimistic report/email settings. Schema v20 implements the minimal weekly material-order report. Schema v21 implements the employee-efficiency report from explicit employee planned/actual measurements and calendar-derived capacity. Both reports use configurable recipients/day/time/timezone, anonymous SMTP-relay delivery, once-per-week scheduling, and Edit-Mode-gated Send Now. The Timeline uses active employees as individual transient capacity. No persisted worker-to-Operation assignment, skill qualification expiry, authenticated SMTP secret storage, payroll, or employee ranking is implemented.
- Implemented a temporary fixed-mapping Setup **Excel Case + Order Import**. It asks for one `.xlsx` worksheet, performs a bounded read-only preview, applies Case A/O/F/D and Order B/L/E/N mappings, reports each valid/matched/skipped row, and exposes one explicit `Import Cases and Orders` action. Part Number identifies Cases and Case + Order Number identifies Orders. The current Windows client sends no planning selections, Machine mappings, Batches, Operations, allocations, assignments, backlog, Timeline, or planning-mode changes. Existing records are matched without silent updates; invalid rows are skipped with reasons while valid rows commit atomically. The former five-step wizard remains bypassed compatibility code and is not part of the visible workflow. Kitaron integration is expected to replace this temporary tool.
- Kept business rules, planning data, edit authority, and all SQLite access on the Server.
- Add authenticated identity/session and route/Batch update, assignment-form, Working Calendar combined-overnight/overtime/archive/automatic-holiday policy, and downtime mutation workflows against the API.

### Tests and exit gate

- Client tests cover health/Edit Mode parsing, Case query routes, ETag/generation-protected Case saves, Case/tab population, Order/Batch create payloads and editor generation, explicit combined/stock/scrap allocation entry, external-folder launch, required headers/generation, safe Server errors, settings persistence/validation, local identity, and unavailable-Server behavior.
- The WPF stability audit realizes every nested tab and repeatedly switches English/Hebrew/Russian while requiring a responsive dispatcher, correct current text/direction/selection, bounded per-interaction and whole-pass time, no idle localization work, coalesced language traversals, and stable visit/write counts across identical passes. It also verifies one Timeline render for a 50-Machine collection burst, zero Timeline renders for an unchanged hide/show cycle, one deferred STEP redraw after repeated hidden resizes, a bounded cold localization initialization, and zero redundant notifications for unchanged session attachment.
- Excel-wizard tests cover automatic draft as preview-only state; exact A/O/F/D Case, B/L/E/N Order, and P/H Batch mappings; aggregation of repeated related Order rows; one Batch per Part+Batch Number; explicit related-Order allocations balanced to summed remaining demand; exact-Machine/unique-compatible-Operation assignment; ambiguous Pool/Skip fallback; preservation of explicit decisions; and the absence of automatic Commit, Machine/route invention, compatibility override, or existing-record update.
- Editing controls are disabled without current authority or confirmed Server health.
- Verified the client assembly has no SQLite reference and its local settings contain no database path.
- Full planning-route validation, stale resource revisions, reconnect compatibility, and production-use sizing remain future exit work.

## 10. Phase 7 - Machine Board and drag-and-drop backlog

**Implementation status:** Core compact board slice implemented. The Server supplies a snapshot-consistent unassigned operation pool and Machine backlogs with planned quantity, sorted allocated Order references, and nullable `setup + QA + aggregate load/unload + quantity x cycle` estimate. The total uses the same existing manual/automatic load-event count as Timeline calculation but is not a phase schedule. The Windows client performs manual assign, reorder, cross-Machine move, unassign, and explicit player-style execution commands through Edit Mode, reloads only after acceptance, and shows incompatible/rejected feedback. Search/filter, downtime display, projected start/finish, plan revisions, concurrency preconditions beyond Edit Mode generation, and calculated conflicts remain pending.

### Scope

- Implement Case/Batch pool, Active/Assigned/Not Assigned filters, search, compact Machine columns/backlogs, text/icon status cards, planned quantity, Order references, input-derived estimate, downtime, and conflict summary.
- Translate drag/drop into explicit atomic assignment/reorder commands.
- Send explicit Start/Suspend/Finish/Reset operation commands and reload only the accepted Server state.
- Preserve planner intent when the Server reports conflicts; never silently move another item.

### Tests and exit gate

- Assign, unassign, cross-Machine move, within-backlog reorder, stale concurrent view, and rejected mutation behave deterministically.
- The same submitted order is stored and returned unless the command is rejected.
- Capability, dependency, timing, and downtime conflicts appear with text/icon explanations.
- Large approved backlog/Case counts meet the performance target.

Current Timeline placement uses the Server response's `readAt` as its calculation cursor. Every persisted `not_started` Forward or Manual assignment earliest-fits at or after that cursor; the resulting work cascades through the unchanged stored backlog and Sequential dependencies. A future Backward assignment still latest-fits before delivery, but when its intended Backward start is missed without Start it transiently falls forward from the cursor, retains its persisted mode/backlog position, shifts downstream consequences, returns `backward_start_missed`, and warns if it finishes after delivery. Work with no feasible fit remains an identified blocked marker at or after the cursor. A wholly elapsed horizon does not fabricate historical `not_started` forecasts. An invalid, paused, or infeasible earlier row blocks every later stored backlog row from leapfrogging. Waiting distinguishes Machine/setup/day-shift calendars, skilled setup/QA/regular resources, maintenance, breakdown, pause, and sequential predecessors.

Implemented tests cover projection membership/order, exact API command targets, editor-only interaction, incompatible rejection with an unchanged local board, and existing repository invariants for stable contiguous backlog order. Because the pure time engine is not connected to persisted board inputs, the panel reports conflict calculation as unavailable rather than treating an empty list as a conflict-free plan.

## 11. Phase 8 - time and conflict engine

**Implementation status:** Pure domain calculation and the read-only canonical Timeline API are implemented. The persistence mapper reloads current SQLite Machine Assignment IDs, schema-v24 planning modes, fixed backlog positions, and recorded cross-Machine transfer/pause timestamps on every request, then consumes calendars, resources, downtime, immutable Batch timing/dependency snapshots, quantity, allocated-Order Work Finish Date/Order Number facts, and the Server snapshot `readAt` cursor. A `not_started` `forward` or `manual` assignment earliest-fits at or after that cursor; a future `backward` assignment latest-fits before the earliest linked delivery cutoff. When a Backward intended start is missed without Start, the same assignment transiently falls forward from the cursor, preserves its persisted mode/backlog, shifts downstream graph consequences, returns `backward_start_missed`, and returns the deadline warning if late. No feasible fit becomes an identified cursor-anchored `blocked` marker; an elapsed historical horizon emits end-boundary blocked markers rather than fabricated not-started forecasts. Mixed modes share one fixed graph and Machine-lane reservation set. The API has no global `mode` selector; a supplied mode query is rejected as assignment-owned. It requires no planned-start/planned-end fields, performs no Timeline mutation, and reports outside/infeasible deadlines structurally. The projection emits exactly one identified current operation or blocked-waiting block per active assigned Operation ID, folds waiting/downtime/prior-Machine facts into its phases/detail, anonymizes ordinary capacity bands, and logs/removes duplicate producer blocks rather than introducing a hold/backward/manual layer. It also returns each Machine's merged/clipped `nonWorkingWindows` outside the interval list, using the complement of the exact Working Calendar expansion already supplied to scheduling. A final same-Machine invariant preserves actual/hold/history, demotes conflicting forecasts to blocked waiting, and emits structured overlap conflicts without changing persisted priority. Fixed-point propagation carries each unresolved row through later Machine backlog positions, Sequential descendants, and locked-simultaneous forecast members until no leapfrog remains. Suspended actual work stops at pause start; moving it starts the current hold no earlier than the true cross-Machine move event, or the replacement assignment's creation time after an unassign/reassign. The engine never mutates/reorders inputs. The pure engine accepts multiple predecessor edges for one child, but schema-v9 persisted Batch snapshots expose one predecessor source field, so multi-parent authoring requires a separately approved schema/API evolution. Plan revision/cache and broader production conflict policy remain pending.

All three modes derive duration from setup, QA, the existing per-event load/unload values, and normal production cycles. For not-started managed Operations, Task 7 combines prepared-tool magazine loading, fixture installation, and first-piece prove-out into setup, then applies normal cycle time to `max(planned quantity - 1, 0)` so the first part is not counted twice. Load/unload cadence still uses the full planned quantity. Legacy Operations and started/history rows keep their stored setup plus full-quantity cycle behavior. Manual loading is one initial load then one cycle per part. Automatic loading with N is one initial/repeated load followed by up-to-N-cycle production groups, for `ceil(plannedQuantity / N)` load events; automatic loading without N has zero load events and one production run. Before materializing phases, the Timeline returns blocking structured conflict `load_unload_occurrence_limit_exceeded` if any operation would require more than 10,000 non-zero-duration load/unload occurrences. Its message directs the planner to increase an automatic every-N cadence or split the Batch; a manual operation can instead be split or switched to an approved automatic cadence. This reversible calculation safety guard does not mutate quantity or allocation and remains subject to a broader configurable-cap policy. Each worker-required load event independently reserves a regular worker. Forward/manual calculate that cadence chronologically, while Backward allocates the same segments in reverse before returning chronological phase spans. Different operations of one Case/Batch may be assigned to different Machines. The Server expands recurring local Machine/resource calendars, including one overnight window, with breaks, dated exceptions, cached holidays, timezone/DST conversion, and the managed Setup Calendar; the upgrade-only legacy setup JSON remains readable until the first managed selection or explicit clear. Missing setup selection falls back visibly to Machine availability. Planned/open/restored downtime retains its typed reason, Sequential dependencies use the predecessor finish regardless of mode, and locked groups retry for common Machine/resource availability. Information/Debug logs expose Timeline input counts, assignment ID/mode/timing inputs, calculated results, and duplicate-block detection. Forward/manual worker contention remains earlier Work Finish Date then natural Order Number; Backward adds shorter duration before the Order Number tie-break. Combined/multiple overnight-window, overtime, and Calendar archive policy, recurring downtime/cancellation, plan revision/cache, and broader production-conflict severity remain pending.

Task 6 is implemented through schema v38. A tolerant versioned NC parser stores release-level raw metrics once; a separate evaluator appends auditable Machine-specific calculations on release, compatible assignment, and Machine timing/configuration changes. Operation release history displays calculated per-part times for each Machine, and Planning Board cards display the selected per-part cycle with an NC/manual/override source label. Planning Board and Timeline use `valid NC estimate > preserved manual cycle snapshot` only for not-started work. Parser warnings and low/unavailable confidence are visible without blocking release/readiness. Manager override and actual-time calibration are deferred because the current product has no manager-override estimate field.

Task 7 is implemented without a schema change. `SetupOccupancyEstimator` owns the auditable component formula and accepts manager/NC/manual cycle precedence; repository projection currently supplies NC/manual because persisted manager-override authoring remains deferred. Configurable defaults belong to setup-process configuration, not Machine physics, and can later be replaced by a setup-worker profile. Planning Board and Timeline both consume the same result, while completed/in-progress timing is left untouched.

Task 8 is implemented without a schema change. The Planning Board now provides the missing readiness-input UI, while Case release history, Setup Machine compatibility, Planning assignment, and Timeline remain the existing screens/services. Production-data transactions append granular schema-v22 audit events. One API acceptance scenario verifies Machine configuration, 15/20 capacity, NC/setup estimates, missing and confirmed readiness inputs, local-post preservation, new-process staleness/incompatibility, 25/20 exact blocking, immutable historical downloads, and event coverage. Existing migration idempotency/prior-version tests and WPF startup tests remain the upgrade/UI gate.

### Scope

- Implement approved duration formula, Calendar combined-overnight/overtime policy, downtime recurrence/cancellation, Work Finish Date risk, and recalculation triggers.
- Implement Sequential, Parallel-capable, Independent, and Locked simultaneous dependencies exactly.
- Generate stable conflict codes, severity, affected records, and explanations.
- Produce immutable/read-consistent plan projections for clients.

### Tests and exit gate

- Use table-driven examples for every dependency mode and boundary time.
- Locked simultaneous members have identical projected start/finish; shorter Machines remain reserved to group end.
- Calendar, shift, break, downtime, time-zone/DST, rounding, missing timing, capability, deadline, manual per-part load, automatic every-N reload, no-frequency automatic, and per-event regular-worker reservation cases match approved examples.
- Cycles/impossible graphs are rejected according to policy.
- Recalculation never changes assignment or backlog order.
- Determinism and performance meet the approved dataset/SLA.

Implemented deterministic tests use fixed UTC timestamps and cover same- and different-Machine Sequential constraints, visible dependency waiting, one-parent/multiple-child and multi-parent calculation inputs, parallel-capable/independent meanings, locked-group reservations, unassigned/missing predecessor, dependency cycles, insufficient availability, same-Machine simultaneous infeasibility, employee contention by due date and natural Order Number, manual per-part load cadence, automatic every-N periodic reload, automatic without a frequency, per-event regular-worker reservation, reverse Backward allocation with chronological phase output, recalculation after manually supplied backlog reorder, repeatable serialization, and unchanged input/backlog order.

## 12. Phase 9 - Windows Timeline View

**Implementation status:** Implemented as a compact read-only WPF projection over `GET /api/v1/timeline`. It renders calculated/actual operation work in the primary band and assignment-owned blocked-waiting annotations in a separate lower band. One phase-aware host represents each current assignment: `PRODUCTION` is blue (`#1E88E5`), `SETUP` yellow (`#FBC02D`), `QC` green (`#43A047`), every returned `PART RELOAD` phase purple (`#7B1FA2`), locked reservation orange, and gaps between returned work phases transparent rather than continuous work. Repeated reload phases stay in the one assignment host rather than creating another operation object. Generic idle and ordinary anonymous `waiting` capacity intervals remain Server-calculated/API-returned but are suppressed from the default canvas; blank row space communicates waiting/idle. This does not suppress conflict explanation, diagnostic detail, or future explicit debug display. Assignment-owned `BLOCKED`, paused hold, downtime, and actual history remain visible. The additive per-Machine `nonWorkingWindows` collection renders as gray full-row backgrounds before the time grid, foreground blocks, and arrows, so calendar closures are visible without becoming operation objects or lane participants. Paused hold stays visible as one purple assignment object. Deterministic interval partitioning gives every partial overlap a visible sublane instead of painting later blocks over earlier ones; zero-duration blocked facts at a horizon boundary use distinct minimum-width point-marker sublanes without changing their timestamps. It also shows one-line Machine number/name labels, assignment mode/delivery/calculated-time tooltip data, one visible operation name/number marker, Server conflicts, and dependency edges for only the selected Batch. The factory-local two-row hour ruler uses the additive Timeline `displayTimeZoneId`, `dayStartsAtLocal`, and `dayEndsAtLocal` metadata for header-only configured DAY/DARK bands. They are display-only shift-day context, not astronomical daylight or a scheduling/calendar input, and do not add or alter intervals, blocks, or Machine rows; older clients can ignore the fields. The red labelled `NOW` line estimates Server now from Timeline `readAt` plus elapsed local time. A shared 30-second throttle refreshes the Server projection only when assigned `not_started` forecast or blocked work exists, and the embedded tab plus reusable separate window share that refresh rather than double-poll. It has no client scheduling, API/database mutation, or operation/assignment/interval identity. Closing the window affects neither the planner nor its data, and no mode-specific view is created. Planning Board context actions PATCH the existing assignment to `backward`, `forward`, or `manual` using Edit Mode and its exact ETag, then refresh this same projection. The client contains no calculation or scheduling rules. Plan-revision consistency, zoom, local-time selection, richer navigation, and performance targets remain open.

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

**Implementation status:** Core LAN-served dashboard implemented. The dependency-free fullscreen UI has one large row per display-enabled Machine and shows only its current Operation, part picture, Started/Paused/Waiting/Completed state, and setup or calculated part/Batch completion. Conflicts and queued Operations are intentionally hidden. It conditionally auto-refreshes, visibly retains the last snapshot while offline, and has no edit controls. The GET-only projection supports ETags. Authentication, display groups, offline-device telemetry, target-TV visual acceptance, and managed kiosk deployment remain pending.

### Scope

- Create a read-only web/kiosk dashboard under `client-tv-dashboard/`.
- Show the current Operation per Machine with its picture, execution state, and setup or calculated part/Batch completion.
- Implement approved automatic refresh, offline detection, reconnect, and kiosk deployment.

### Tests and exit gate

- Dashboard credential cannot invoke any mutation or Edit Mode path.
- Stale/offline state is obvious and never presented as fresh.
- Target TV resolutions, browser/kiosk environment, viewing distance, and refresh cadence pass visual acceptance.
- Status remains legible without color.

## Haas VF-3 NGC integration phases 1-2

**Implementation status:** Protocol-independent Server connection platform and Haas production adapter implemented; full real VF-3 production acceptance remains pending. Schema v43 adds one primary `MachineConnection`, normalized current state, meaningful-change history, connection events, and retained raw telemetry on top of the v42 immutable header/Bench/audit tables. `HAAS_NGC` now explicitly selects MDC or read-only MTConnect as its normalized telemetry provider. MTConnect `/probe` and `/current` HTTP/XML parsing, single-device validation, state/program/counter/macro-range normalization, compact diagnostics, dedicated API test, Server polling, Setup source selection, and live-agent commissioning tests are implemented. The separate generic MTConnect, OPC UA, and Custom adapters remain registry-only unsupported options. Filename, MTConnect `PROGRAM`, and Cycle Start have no business-transition role.

The real-machine Definition of Done is intentionally not claimed. Haas publicly documents Q500/Q600/E and Local Net Share, but not a guaranteed active MEMORY/USB/Remote-Net-Share program-to-SMB-file mapping. Complete and record the read-only VF-3 tests in `haas-active-program-header.md` before enabling production polling. Tool Table transfer transport remains site/controller-specific; the implemented reset endpoint requires explicit successful-transfer confirmation before the audited zero/write/read-back sequence.

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
- **OD-002 - Timing ownership:** Resolved for MVP: current setup/cycle values are owned by Case Operations. Case fields are read-only sums in integer seconds, with null treated as zero and an empty route returning zero. Windows presents total-hours `HH:mm:ss`; these sums are descriptive and never replace dependency-aware Timeline duration.
- **OD-003 - Batch scope:** Resolved for the implemented slice: one Batch belongs to one Case and every Order allocation must belong to that same Case. Cross-Case Batches are rejected atomically.
- **OD-004 - Allocation equation:** Partially resolved: `plannedQuantity = order allocations + stock + scrapAllowance`; positive rows are explicit, scrap cannot stand alone, partial Order allocations are allowed, and an existing Order cannot be edited below its aggregate allocated quantity. Define whether new/cumulative cross-Batch over-allocation is permitted, allocation replacement, reallocation, cancellation, and quantity unit.
- **OD-005 - Route versioning:** Partially resolved: optimistic Case Operation PATCH changes approved scalar/one-link dependency fields but not route position. Schema v9 snapshots identity/display/Machine-type/timing/dependency fields into Batch Operations, so existing Batches are unaffected. Define aggregate route revision and route-reorder concurrency before implementing reorder; arbitrary multi-record dependencies remain OD-010 work.
- **OD-006 - Lifecycles:** Partially resolved: allocated Orders implement Server-derived `active` / `in_production` / `complete` across all allocated Batches, persisted atomically with Batch create/delete and operation execution. Completion requires full allocation coverage, at least one operation in every allocated Batch, and all related operations completed; manual production status for new/unallocated demand, contradictory linked status edits, and new allocation to cancelled demand are rejected. Legacy already-linked `cancelled` is migration- and recompute-preserved until an explicit matching status assertion resumes derivation, pending a fuller cancellation policy. Production Batches implement Server-derived `waiting` / `in_production` / `complete`, with zero-operation Batches waiting and suspended work in production. Batch Operations implement `not_started` / `in_progress` / `suspended` / `completed`, with completed work removed from the active Machine backlog. Define actual-time/history needs, Order Number/date/unit rules, cancellation/reallocation, archive/delete/cascade policy, and audit.
- **OD-007 - Tool package boundary:** What creates tool-cart/checklist content when full tool inventory is outside MVP?
- **OD-008 - Existing data:** Resolved for the implemented transition: the shared legacy `.xlsx` backlog is imported through a read-only preview and explicit operator mapping/Skip workflow, followed by one Edit-Mode-gated atomic commit. Suggestions are never approvals; no route/timing/date is invented, existing assignments are not moved, imported rows append after existing Machine backlog positions in workbook order, and the original file is not modified or stored. Production rollout still requires a backup, a full operator rehearsal on a copy, sign-off on every warning/skipped row, and reconciliation of imported counts against the source workbook.

### Planning engine

- **OD-009 - Time model:** Partially resolved in the pure engine to setup/QA/load-unload/production phases, explicit half-open UTC windows, earliest-feasible placement in fixed backlog order, downtime subtraction, split work intervals, and individual employee contention. Setup workers require a Machine skill token; QA and regular-worker phases require their roles. Calendars, breaks, exceptions, and cached holidays constrain each resource. Simultaneously ready contenders use earliest allocated-Order Work Finish Date then naturally smaller Order Number, and waiting states the deciding rule without persisting or reordering anything. Persisted named-worker assignment, skill qualification expiry, in-progress work, overtime policy, rounding, and recalculation triggers remain open.
- **OD-010 - Dependencies:** Domain graph supports stable relationship records, fan-in/out, exact dependency meanings, sequential cycle rejection, and locked groups. The create/PATCH API maps at most one referenced prior operation into the existing Case Operation row and validates the rehydrated full graph transactionally. Schema v9 snapshots that relationship into each Batch, and the pure engine reads only the snapshot while applying Sequential precedence, no constraint for Parallel-capable/Independent, and common start/finish plus shorter-member reservation for Locked simultaneous. Multi-record fan-in/out persistence and richer cross-Machine feasibility remain open.
- **OD-011 - Conflict policy:** Normal structural assignment compatibility rejects a first incompatible command with `machine_type_override_required`; an active editor may explicitly resubmit a different active type with mandatory reason, producing an immutable audit snapshot without changing the route. Unsafe Machine or linked Machine Type edits still reject. The pure engine reports deterministic blocking calculation/input conflicts, but the broader catalog, severity/urgency, Work Finish Date cutoff, plan-revision stability, and API presentation remain open.

### Single Edit Mode and identity

- **OD-012 - Authentication/authorization:** Choose human identity, roles, TV/device credentials, administrator boundary, and audit requirements.
- **OD-013 - Edit lease:** Resolved that every implemented planning mutation requires the current client ID and generation. The stored lease deadline is the transfer-response deadline, not a heartbeat. Define heartbeat, disconnect/crash/restart behavior, and stale unsaved edits.
- **OD-014 - Transfer contention:** Resolved MVP to one pending requester, no queue, Reject returning the requester to Viewer, and a configurable 1–3600 second server timeout with a 30-second default. Define cancellation, notifications, takeover safeguards, history retention, and audit.

### API, files, and deployment

- **OD-015 - Technology stack:** Server resolved to .NET 10 with ASP.NET Core/Kestrel and xUnit integration tests; Windows client resolved to .NET 10 WPF; TV resolved to dependency-free HTML/CSS/JavaScript served by the Server. Supported Windows/browser versions, long-term dependency/update policy, and production support baseline remain open.
- **Localization implementation:** The complete Windows WPF surface and TV dashboard support English, Hebrew, and Russian. The Windows client embeds generated catalogs for static XAML plus user-visible workflow/status literals, continuously discovers newly rendered controls in tabs and dialogs, watches bound display properties so live status changes are localized, persists the selected language under the user profile, and mirrors windows for Hebrew RTL. An automated WPF audit selects all 39 tab pages and opens all five windows in both translated languages, failing when a known English UI string remains. Technical/domain values remain unchanged.
- **OD-016 - API baseline:** `docs/api-contract.md` contains the Proposed MVP baseline. Case and Case Operation create/read/update, allocation-safe Order create/read/update/derived lifecycle, Batch creation/read/derived lifecycle, Machine/Machine-Type master data, recurring Working Calendar CRUD, dedicated Setup Calendar selection, staged legacy Excel preview/commit, Machine backlog/assignment and assignment planning-mode PATCH, Single Edit Mode, Timeline, TV, and E-Ink read/device-administration subsets are implemented. Approve and convert the remaining contract to OpenAPI; identity, plan revision/concurrency beyond resource ETags, paging/horizons, idempotency retention, and compatibility window remain decisions.
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
- **OD-025 - Telemetry and tablet-originated events:** The source says server-to-device/read-only but optionally reports battery/firmware and last-seen. A reversible physical-firmware adapter now models a proposed `SEND_TO_QC` request, but the Server route and button action remain unimplemented. Decide whether telemetry exists and whether `SEND_TO_QC` is a non-authoritative operational signal or an official execution mutation; define authorization, idempotency, persistence, audit, and Single Edit Mode interaction. Any approved operational write must use a separate narrowly scoped endpoint and remain outside planning data. Do not derive the proposed tablet lifecycle tokens until their mapping to Production Runs, setup, QA, readiness, and blocking facts is approved.
- **OD-026 - Device lifecycle:** Partially resolved on the Server: an active editor can register a spare or Machine-bound device, the Server returns a high-entropy token only at create/rotation, stores its SHA-256, permits one enabled E-Ink binding per Machine, and supports rebind/revoke/rotate. Define dedicated administrator authorization/audit, device-side secure storage, token expiry, lost-device/cached-data response, and physical reassignment workflow.
- **OD-027 - Time/sync:** Partially resolved for Server configuration: time-zone ID, workdays, one shift window, poll interval, retry attempts/backoff, and revision are configurable/readable. Define clock/NTP/RTC, zone portability/DST/holidays/exceptions, multiple windows, manual force-refresh behavior, jitter, stale thresholds, and clock-loss behavior.

### E-Ink hardware and interaction

- **OD-028 - Hardware selection:** Final MCU, panel/controller/size/resolution/orientation/colors/refresh behavior, AA chemistry and power topology, regulator/brownout, SD, USB service access, mount, and environmental rating.
- **OD-029 - Input contradiction:** The concept specifies only Refresh and Page buttons but also checkboxes, five local statuses, free-text comments, Back/Next, and confirmation. Choose touch/additional controls/text-entry behavior and map every interaction.
- **OD-030 - Local data:** Define persistence across battery replacement, annotation migration/clear behavior on revision, reassignment behavior, filesystem/capacity/wear/corruption protection, history limit, and encryption.
- **OD-031 - Firmware update:** Define safe update/service procedure if OTA remains deferred.
- **OD-032 - Measured acceptance:** Define numeric battery-life/current, wake/download/refresh time, readability distance/lighting/temperature, ghosting, storage, failure recovery, and one-week pilot success thresholds.
- **OD-033 - Source version:** Correct or confirm the E-Ink concept's v0.1 title versus v0.3 footer.
