# Implementation Plan

Implemented Kitaron status correction: `OrderClosed` is a coded value (`1 = open`, `2 = closed`) rather than a Boolean. Code `2` maps to inactive, recognized Boolean closure fields close on nonzero, and cancellation still takes precedence.

The eleven-item task list extends existing authority boundaries: staged Case/Order import, a Batch route guard, Batch-Operation backlog/criticality projection, schema-v26 external delay and master-calendar layering, shared icons/application icon, STEP bounding/reference tools, and the row TV dashboard.

## Server database maintenance

Implemented without a schema migration: Windows Setup reads database/WAL/shared-memory and reusable-page size through the Server, downloads a new integrity/restore-verified backup over the factory-LAN HTTP API with SHA-256 verification, and previews/deletes only raw CNC telemetry, normalized Machine state history, and CNC connection events. The Server owns the fixed deletion catalog and half-open UTC semantics; arbitrary table/type input is rejected. Purge requires the active Edit Mode generation, operator identity, reason, positive expected count, a verified backup created before deletion, exact recount in the write transaction, and a structured audit. No maintenance route deletes planning, release, workflow, cycle/output, anomaly, current-state, or audit data. Scheduled retention, compaction policy, encryption, human administrative authentication/TLS, and active-database restore remain separate unresolved operational decisions.

## CNC operational workflow workstream

**Persistent CNC workflow mode variable: REMOVED.** **Protected temporary setup
verification variables: SUPPORTED** only for the configured, separately commissioned
handshake; they never persist or determine Server workflow state.

Schema v63 is the current redesign. CNC Machine identity is only Planner
`MachineID`, configured fixed IP, and controller MAC; Machine Secret/key concepts
are removed. Exact Offset Loader completion creates untimed `ARMED`, the first
matching NC-start `SVR` creates timed `PENDING`, and matching success creates
reusable `SUCCEEDED` authority until a new Offset Loader supersedes it. Event
sequence is evidence only and automatically tolerates reset/wrap/gap without
becoming identity, verification, or workflow authority. V10 is a new no-motion
bench candidate and must still pass bounded physical commissioning before enablement.

Milestone A is implemented in schema v49: the persistent CNC Setup/Production variable, its settings/snapshot projections, public read/reset routes, write permission, Windows controls, and macro-write audit table are removed. Immutable `production_run_workflow_events` retain Server receipt time, separate Machine time, source/idempotency identity, optional sequence/release/device/user evidence, and JSON metadata. Tablet status is projected from those events rather than run counters or CNC mode.

Milestone B is implemented in schema v50. It adds immutable Offset Loader releases and an explicit current pointer for the active Production Run/Machine; strict CNC-safe DPRINT v1 parsing; idempotent ingestion of a valid current `OFFSET_LOADER_COMPLETED`; per-source monotonic sequence gap/out-of-order anomalies without invented events; and protected, optimistic per-Machine verification configuration exposed only to Windows planning configuration. A newly created release makes older releases non-current without modifying their approved NC or tool-table releases. Configuration may be stored while disabled, but enabling it does not prove or deploy protected controller macros.

The Milestone C identity decision remains accepted, while the canonical postprocessor contract now uses protocol v2 `[[MEIMAD:<KEY>]]` placeholders. The server-blind post emits no authoritative Planner identity, including Part/Operation names. The Server validates the immutable template, assigns the unique NC identity, and resolves Machine-specific event/verification content only in the Production Package copy. The old `(MEIMAD PACKAGE ... V1)` grammar is retained as a named historical compatibility parser. Algorithm v1 and all physical evidence remain unchanged; those results do not establish a production interlock, so verification stays disabled until the Machine-specific configuration is commissioned.

Milestone D Server behavior is implemented through schema v63. `OLC` and untimed `ARMED` commit atomically with the exact Run/Machine/NC/Offset Loader/nonce binding. The assigned tablet derives the fixed-width response for ARMED or unexpired PENDING. `SVR` starts PENDING and its timeout; exact `SVS` establishes success. No Machine credential or response is stored. Sequence discontinuity is retained only as evidence. Physical panel/readability and CNC execution blocking remain commissioning gates.

Task 15 is implemented in schema v53 and the existing Windows client. The **User
Terminals** page provides View-Mode monitoring for identity, binding, access state,
last contact, firmware, battery, Wi-Fi, current Production Run, event-derived workflow,
and official package revision. Only the active Edit Mode holder can register, bind,
reassign, mark spare, enable, or disable. No tablet credential is created, displayed, rotated, or revoked. Tablet ping/status requests
record bounded firmware/IP/RSSI headers alongside existing battery/contact telemetry;
no secret, credential hash, plaintext existing token, or planning mutation is exposed.

Task 16 is implemented in schema v54. `POST /api/tablets/{tablet_id}/events`
accepts only `{ "event_type": "SEND_TO_QC" }`, authenticates the path-matched
enabled tablet, atomically resolves its bound Machine and first current backlog
Production Run, requires event-derived `IN_SETUP_RUN`, and writes one immutable
Server-timestamped workflow event. The event's eligibility-derived source identity
collapses sequential and concurrent retries to the original timestamp. Schema v55
allows a new inspection attempt only after `QC_FAIL` returns the Run to setup. Tests cover
wrong TabletID, premature state, unsupported event, forbidden target/time
fields, concurrent retry collapse, status transition, and preservation of
planning/run/package facts. Physical long-D4 execution remains unverified.

Task 17 is implemented in schema v55 and the Windows client. The read-only
**QC Queue** lists every Production Run whose latest workflow event is
`SEND_TO_QC`, including Machine, atomic output parts/operations, Server receipt
time, and the latest packaged setup worker when known. The active Edit Mode
holder can record PASS or FAIL with a local user identity and optional bounded
reason. Both actions append immutable Server-timestamped events: FAIL projects
`IN_SETUP_RUN` and permits reinspection; PASS projects
`READY_FOR_PRODUCTION`, with its receipt time exposed as
`production_approved_at`. Tests cover monitoring without Edit Mode, authority,
payload and state rejection, E-Ink denial, user/reason audit evidence,
FAIL/resend/PASS, and preservation of planning facts.

Task 18 is implemented without a schema change. Strict DPRINT `CST`/`CEN`
ingestion begins only after `QC_PASS`, resolves exactly one assigned active Run
program, validates supplied Run/program evidence when present, and requires END
to be the immediately consecutive same-source event for its START. A valid END
atomically retains the immutable workflow fact and advances the existing
idempotent Production Run cycle/output records, including every coupled output
and parent status. Duplicate delivery cannot double-count. Haas part counters
remain monitoring diagnostics and no longer mutate official production quantity.

Task 19 is implemented in schema v56. START/START records an immutable,
Server-derived `CYCLE_INTERRUPTED` event linked to the prior and triggering
Machine events, then makes the triggering START the new open attempt. The
interrupted attempt never counts and is not later subtracted. An END without a
matching START, or with a nonconsecutive sequence, is retained once and receives
typed immutable anomaly evidence without changing quantities. Existing sequence
gap/out-of-order rows are preserved by the ordered migration; retries cannot
duplicate interruption or anomaly facts.

Task 20 is implemented without a schema change. Manual Production Run cycle
commands and validated CNC `CYCLE_END` observations now call one shared
schema-v47 accounting component inside their caller-owned SQLite transaction.
That component alone applies exact target/overproduction guards, increments all
coupled outputs, completes programs/runs, propagates Batch Operation, Batch, and
Order status, appends the idempotent cycle row, and writes the structured audit
fact. Windows retains Edit Mode/version checks; CNC retains post-QC START/END,
Machine/Run/program resolution, sequence, anomaly, and source-event checks.
For a connected CNC, the first exact `CYCLE_START` after `QC_PASS` now resolves
the assigned planned Run Program and atomically changes the Run, Program, and
Outputs to `IN_PROGRESS`, `ACTIVE`, and `IN_PRODUCTION` before retaining the
open START. It does not credit quantity. Manual Start remains the path for
Machines without a configured CNC connection and non-CNC work.
The TV and Timeline now read the same Production Run output/cycle authority.
TV projects `IN PRODUCTION`, produced/target parts, and the completed-attempt
average. Timeline keeps configured cycle time until the first validated pair,
then derives the arithmetic mean of the current Program's completed attempt
series and applies it only to remaining cycles. Interrupted/open/anomalous
attempts do not enter that average; no calculated average is persisted.

Task 21 is implemented in schema v57 without a tablet command. A valid current
Offset Loader event for the next assigned Run atomically closes the most recent
unclosed prior production session on that Machine. The immutable closure row and
derived `PRODUCTION_SESSION_CLOSED` workflow event retain the triggering Run/event,
Server closure time, separate observed/effective end values, inference flag, and
basis JSON. A latest valid END uses its raw Machine timestamp when available or
Server receipt otherwise. An open latest START may use only the minimum duration
from an earlier validated START/END pair; unavailable evidence remains null and is
never presented as measured. Retries cannot create a second closure.

Task 22 is implemented in schema v58 as append-only raw timing evidence. Each
retained sequenced START creates one immutable attempt with Run/program/Machine,
source identity and sequence, Server receipt time, and optional Machine time. A
validated cycle record closes it as `COMPLETED`; the existing START/START boundary
closes it as `INTERRUPTED`; otherwise it remains `OPEN`. Schema triggers keep these
facts consistent across ingestion paths and migration backfills existing workflow
evidence. Duration, idle (`next START - previous END`), distributions, outliers,
setup/QC duration, efficiency, and downtime stay derived; no statistical formula
is persisted as policy.

Task 23 is implemented as a read-only Server diagnostic projection. The endpoint
requires a related Machine/Production Run pair and returns a globally bounded set
of immutable workflow and existing anomaly facts in authoritative Server-time
order. Stable IDs, optional Machine time, sequence, attempt state, anomaly flag,
and deterministic plain-language messages make setup, verification, QC, cycle,
interruption, and closure history readable without exposing raw metadata/DPRINT
or creating a second mutable timeline store. It requires no Edit Mode.

Tasks 24-27 are implemented through schema v61. The append-only
`operational_anomalies` ledger and bounded read queue cover NC identity,
Offset Loader, verification, cycle, source-sequence/duplicate, Run-resolution,
and tablet availability types without mutating planning data.
`offset_loader_not_executed` is detected when a verification result arrives
without any setup-verification session. Types whose evidence is otherwise only
silence (`offset_loader_interrupted`, `tablet_offline`) are accepted by the same
immutable ledger for a future authoritative adapter/monitor signal; the Server
does not fabricate them because Offset Loader v1 has no START event and no
tablet-offline threshold is approved. Tablet disablement, expiry, verification failure,
cycle interruption, sequence, duplicate, stale-release, pre-QC, and identity/Run
errors are detected from current authoritative evidence.
Protected-macro success/failure resolves only the current unexpired session;
wrong macro version, Run, or NC evidence blocks verification. Active editors
may invalidate the current verification session or revoke the current Offset
Loader pointer with a mandatory reason. Configuration changes, Offset Loader
creation/revocation, verification invalidation, tablet recovery/identity
rotation, and QC decisions retain user-attributed audit evidence. There is no
verification bypass. On 2026-08-27 the running VF-3SS configuration was found
enabled despite the physical quarantine and was disabled through the ordinary
audited Edit Mode API without replacing its protected secret. Settings version 3
now reads `enabled=false`. The 0.1.43 Server MSI adds bounded Windows Service
crash recovery and passes build/package verification; applying it remains an
administrator deployment step because the non-elevated upgrade was correctly
refused.

The Windows **Setup > CNC Connection > Protected setup verification** panel
exposes these recovery paths to the active editor. It requires the Production
Run and reason for invalidation/revocation, accepts the approved NC and tool-table
release IDs for a replacement Offset Loader, and labels the group as audited
recovery with no bypass. Replacement-tablet assignment remains
on **User Terminals**, and QC retry remains the ordinary FAIL/correct/resend flow.

Tasks 28-30 are implemented development tooling. The loopback TCP CNC simulator
loads explicit JSON scenarios with a required connection-scoped Machine ID,
per-event relative timestamp, Run/NC/Offset Loader identities, sequence, delay,
and duplicate controls, then emits strict DPRNT lines; it can also write an exact
ASCII/CRLF transcript without opening a socket and is not a Server
mutation endpoint. The browser E-Ink simulator covers all fixed workflow states,
verification/failure/expiry, offline last-known-good, low battery, revision
change, and only the official scoped `SEND_TO_QC` POST. The integrated automated
scenario exercises READY_FOR_SETUP through production, verification fail/retry,
QC fail/retry/pass, completed/interrupted/duplicate/gapped cycles, next-setup
session closure, anomaly/audit evidence, and the human debug timeline.

Task 31 is intentionally not complete. The mandatory record is
`docs/cnc-commissioning-checklist.md`; it records four physical `PASS` results,
one physical `FAIL`, and nine `NOT_TESTED` checks. The passes cover public-vector
arithmetic, observed Setting 23 operator-access protection, the wrong-response
alarm/cleanup path, and the approved temporary-variable mapping/cleanup. The
blocking failure is that the VF-3SS
accepted an otherwise correct response after at least 130 seconds at M109, while
the audit also proved that the `#3001` event sequence cannot remain monotonic over
reboot/wrap. Macro candidates v3-v5 and packages v1-v3 are quarantined. The
local-only generator can reproduce the failed candidate and its hashes for audit,
but now refuses generation and Machine-specific ZIP creation by default unless
the explicit `-AcknowledgeQuarantinedAuditOnly` switch is supplied. That switch
does not approve installation or enablement. An internally reviewed input/timer barrier,
one reviewed event-sequence domain, and a newly numbered macro that repeats the
exact release token and nonce in SVS/SVF are required. The hardened Server rejects
the quarantined v3-v5 result format so a delayed old challenge cannot resolve a
new one. A new bounded no-motion retest, the remaining
record fields, and both sign-offs are still required. Task 32 reconciles the
documentation with the implementation while retaining this physical gate.
External HFO/vendor approval is not required; the review authority is the site's
qualified CNC controls engineer together with the Meimad production owner.

**Resolved (2026-09-04):** the placeholder-grammar conflict above was decided in favor of `docs/postprocessor-production-package-contract.md`. The uncommitted `POSTPROCESSOR_ID` placeholder key, its optional/0-or-2 multiplicity changes to `OFFSET_LOADER_RELEASE_ID`/`EVENT_CONTEXT`, and its threading through `NcPackageTemplateTransformer.cs`, `ProductionPackageModels.cs`, and `SqliteProductionPackageRepository.cs` were reverted to match the documented contract exactly-once grammar, with matching test fixtures restored. The unrelated Rule 35 fix bundled in the same working-tree change — `manual_dummy_verification_required` failing closed when Manual/Dummy Tool Offsets is requested without Server Verification, plus the loader-file MANUAL DUMMY/MEASURED annotation line — was kept, since it is independent of the placeholder grammar and was verified correct. All `ProductionPackages` tests pass after the revert.

## Multi-output Production Run workstream

Task 1 was accepted on 2026-08-23. Tasks 2–10 are implemented: schema v45 adds reusable Manufacturing Programs, v46 adds Production Runs and assignment migration, and v47 adds durable idempotent cycle observations. Server APIs cover composition, allocation, assignment, readiness, execution, cancellation, and reads. Planning Board and Timeline expose compressed run/program projections; Windows provides the multi-select Production Run dialog. Task 18 restricts automatic CNC advancement to a valid post-QC DPRINT START/END pair resolved to exactly one active program; normalized part counters remain diagnostic. The architecture impact map remains authoritative.

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

**Status:** Schema versions 1 through 74 implemented. The migrator switches the database to WAL journaling at startup so polling readers and CNC/Kitaron writers no longer block each other. Verified database backup is implemented; released G-code storage must be included in the operational backup policy.

### Scope

- Implemented ordered Server schema migrations through v74 (the `DatabaseMigrator` list is authoritative). V34 adds Machine execution/capacity/timing and Postprocessor compatibility; v35 adds released tool tables/process revisions/G-code and production pins; v36 adds immutable normalized released tools/count; v37 adds assignment release selection and append-only contextual offset readiness; v38 adds NC analysis/estimates; and v39 replaces manual material authority with locally verified Case receipts and explicit Production Batch reservations. Earlier migration contracts remain readable.

- Implemented deferred material reconciliation: a Batch may remain planned while `MISSING`/`UNVERIFIED`; Start readiness derives required pieces from planned quantity and becomes material-ready only with sufficient explicit local reservations. The Windows Batch tab records verified receipts and reservations and explains the three manual shortage paths. Existing Batch edit/create operations implement explicit reduce and ready/waiting split decisions, and dynamic Timeline projection recalculates quantity-dependent occupancy/dependencies on refresh. Automatic receipt selection, proportional allocation splitting, Kitaron stock reconstruction, and ERP warehouse authority remain intentionally absent.
- Implemented ordered migration metadata, `user_version`, startup application, and rejection of newer unsupported versions.
- Implemented the Server-local Kitaron connector: localhost-only UI/API, encrypted credentials, SQL read intent, metadata detection, editable versioned mapping, manual and interval-driven one-way synchronization, stable source links, atomic idempotent Case/Order/Case-Operation application, and result counts. A synchronized Case is explicitly Kitaron-managed: its imported master fields and complete direct Order set are read-only in WPF and protected by Server `409 kitaron_managed_read_only` guards; manual unlinked Cases retain normal editing. Order synchronization combines planning-view rows with matching `TSubOrder` delivery rows, retains distinct `<OrderNumber>/<RecordID>` quantities/dates and the unit sales Price in NIS from `CostShkalim` (since 2026-09-25; `PriceInCurr` was set on 35 of 20,455 rows and read every other order as 0), and projects Kitaron status as active/open, inactive when any recognized row/header closure or completion flag is set, or cancelled when `TSubOrder.StopProduction` is set. It does not substitute `PriceRow`, manufacturing cost, or BOM-cost fields. Exact duplicate source rows are collapsed by authoritative Case + full Order reference during snapshot reconciliation rather than rejected by a false globally-unique source-key constraint. Exact legacy plain rows are adopted without double-counting. Absent Orders are removed; their direct/derived Production Batches are first removed through the shared complete deletion graph so the planner can recreate current production manually. Regression coverage includes five rows for order `3000030679` totaling 72, row/header status and Price mapping, duplicate source rows, stale-order removal, and allocated-Batch cleanup. Draft/blocked mappings cannot run, and no Kitaron write exists. The initial analysis remains in [kitaron-database-mapping-analysis.md](kitaron-database-mapping-analysis.md) and [kitaron-initial-mapping.yaml](kitaron-initial-mapping.yaml).

- Corrected canonical Order discovery to select the Kitaron part first and import every `TSubOrder` row for it. Exact Order-number presence in the planning view is no longer required; regression coverage asserts that the generated read-only query cannot silently omit those open or closed delivery rows. The explicitly identified Kitaron test Order `הזמנה לדוגמא 1` is excluded at the canonical query and complete mapped-work-row boundaries, preventing its dummy Case/Operation data from claiming a manually managed Case or triggering authoritative cleanup of that Case's production history.
- Added immutable-history reconciliation for superseded Orders: dependent never-started Batches are still removed, while Orders/Batches referenced by structure-locked legacy or multi-output Runs are retained as historical evidence, reported as synchronization warnings, and no longer abort application of current Kitaron demand. Schema v67 marks those retained Orders as history-only. They remain directly readable for audit but are excluded from the normal Case Orders list and all current-demand consumers. Current demand under a Kitaron-managed Case now requires a current Order source link, so an unlinked legacy row is also excluded and remains read-only even when its older history marker is false. Successful partial syncs sweep unlinked orphans below every durable Kitaron Case link without deleting linked canonical Orders for omitted Cases; legacy import and Batch create/update also reject the hidden row. Regression coverage reproduces linked current, marked historical, unlinked allocated, and unlinked locked-history rows plus legacy aggregate and derived multi-output cases, and verifies cleanup, locked history, and exact current projection survive a successful sync.
- Added Batch-deletion preflight for both legacy and multi-output Runs. A Batch with any structure-locked Run now returns controlled `409 delete_blocked` before the deletion graph mutates, instead of leaking the SQLite immutability trigger as HTTP 500. Regression coverage verifies the Batch, Run, and Allocation all remain intact.
- The setup UI now exposes the canonical Order status and Price rules as locked connector-managed mappings, documents authoritative absent-Order cleanup, and no longer claims that a Kitaron-linked record remains immune from later authoritative refreshes.
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

Implemented Case verification covers create/read/update, filtered collection reads, ordered Case Operation reads, Server-streamed previews, multihomed UNC fallback for configured drive mappings, optimistic versioning, read-only setup/cycle sums with null-as-zero and empty-route-zero behavior, persistence after database reopen, existence without Orders, required Working Folder validation, unavailable external paths without filesystem creation, absence of SQLite BLOB columns, and rejection of stale edit generations before the write. Case Operation PATCH verifies partial-field merging, immutable route position, ETag conflicts, full-graph validation, and unchanged existing Batch snapshots. Edit authority is checked within the same immediate SQLite transaction as each mutation. Authentication, POST idempotency, cursor pagination, and full Edit Mode lifecycle remain required before the Case API is production-ready.

Implemented Order behavior adds the Server-derived `active` / `in_production` / `complete` lifecycle across every allocated Batch, atomic recomputation with Batch create/delete and Start/Suspend/Finish/Reset, aggregate completion coverage, zero-operation protection, and optimistic edit guards for quantity below allocated work or a contradictory linked status. New/unallocated Orders reject manually entered production tokens, and Batch creation rejects allocation to cancelled demand. Legacy already-linked cancellation is preserved until an explicit matching status assertion resumes derivation. Current automated verification is recorded in the verification report; archive rules, quantity units, Order Number uniqueness, Work Finish Date cutoff semantics, and cross-Batch over-allocation/reallocation remain open.

Implemented Production Batch verification covers one-Order, partial-Order, multi-Order, stock-inclusive, scrap-inclusive, and stock-only shapes; exact totals; positive rows; scrap-only rejection; duplicate semantic rows; integer overflow; missing/cross-Case Orders; atomic rollback; Edit Mode; database reopen; Case activity; optimistic Batch Number/planned-quantity/complete-allocation replacement with preserved instantiated route; and confirmed deletion across assignments, pause history, assignment overrides, package records, allocations, and Operations with backlog compaction. New and zero-operation Batches are `waiting`; Start/Suspend/Finish/Reset atomically derive and persist `waiting` / `in_production` / `complete`, increment the Batch version when that derived token changes, Reset retains the active assignment and backlog position, and Finish removes the active assignment and compacts the backlog. Cross-Batch over-allocation, aggregate route revision, archive policy, and richer execution history remain open.

Implemented Machine behavior covers master-data normalization, unique numbers, stable Working Calendar and Machine Type references, explicit `CNC_GCODE`/`MANUAL` execution mode, managed one-or-many Postprocessor mappings, usable tool capacity, Machine timing parameters, normal route compatibility, explicit reasoned/audited cross-type assignment override, active/inactive enforcement, optimistic PATCH, and running-backlog-head protection. Existing Machines migrate conservatively to `MANUAL` with null unknown values and a neutral factor of `1.0`; no CNC or Postprocessor mapping is inferred. Working Calendar behavior covers editor CRUD, guarded references/usages, multiple same-day work windows or one overnight window, contained breaks, dated exceptions, dedicated Setup selection, and optional cached Israeli-holiday overlay. Explicit online refresh writes the local cache and preserves manual overrides; Timeline/resource reads remain offline. Machine Type behavior covers unique names, reusable capabilities, rename/reference guards, optimistic update, and guarded delete. Combined/multiple overnight-window, overtime, and Calendar archive policy, device binding mutation, bulk reorder, downtime, plan revisions, and full conflict calculation remain open.

Implemented G-code/readiness behavior covers immutable process, tool-table/tool-row and Postprocessor-specific release history; native structured CSV/JSON and Cimatron MHT tool-table ingestion; explicit or uniquely resolved assignment release selection; local verified-material receipts and Batch reservations; exact Machine/process/release offset readiness; capacity; compatibility; centralized component evaluation; board/timeline explanation; transactional Start blocking; and exact production pins. The Windows Case Operations screen exposes only Release, never Draft. Not-ready work stays planned and requires a manual correction; no program, tool, process, assignment, reservation, or Batch quantity is silently changed. Detailed readiness boundaries remain documented in [G-code release and readiness architecture](gcode-readiness-architecture.md).

## 7. Phase 4 - Single Edit Mode (superseded)

**Implementation status:** Superseded on 2026-09-27 by user accounts, permissions and parallel editing (see "User accounts replace Single Edit Mode" at the end of this plan). The history below describes the retired mechanism.

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

The Planning Board now includes board-wide operation search and right-click operation navigation. Timeline operation blocks navigate either to the Case Operation editor or to a filtered Planning Board. User Terminals support name edits and reference-safe deletion. Generic Skills, Workstation Types, Workstations, and External Resources support create/edit/reference-safe delete with optimistic versions; existing Calendar and Machine edit/delete controls remain the authoritative management paths.

### Scope

- Implemented a .NET 10 WPF client under `client-windows/`.
- Implemented persisted Server-root configuration, simple local identity, stable client ID, compact status/lock header, typed HTTP health/Edit Mode client, transfer actions, and safe offline behavior. Connection configuration is edited on the Setup page rather than occupying the operational header.
- Implemented Case Pool search/customer/active filters and Server-owned ordering by Part Number, closest active/in-production Order delivery, or Customer; unobstructed Server-delivered preview thumbnails with missing-preview text only; complete editor-only Case creation/editing with Working Folder and picture selection; Case Operation creation/optimistic editing with dependency target/group input; optimistic Order create/edit; explicitly allocated Production Batch creation; and Open Working Folder using the API-supplied path. Operation timing uses total-hours `HH:mm:ss` in the UI while API/storage remain seconds; read-only Case totals are Server-derived operation sums.
- Implemented a bounded Case STEP viewer using OpenCascade B-rep face tessellation and WPF depth-buffered rendering with Shaded, Visible edges, and Wireframe presentation modes, geometry-only one-time load Fit, stable orthographic orbit/zoom, optional bounding box, a shared solid/edge projection, signed-volume center-of-gravity orbit for closed bodies, vertex measurement, and local PNG snapshot selection. It leaves engineering files unchanged and uses an explicitly labeled edge/point fallback only when no solid faces can be tessellated.
- Added a depth-sorted WPF software triangle surface over the STEP hardware viewport, using the same model/camera projection so a blank driver-dependent `Viewport3D` frame cannot hide a successfully tessellated solid.
- Audited and completed the Hebrew and Russian Windows catalogs against the English source (3,590 entries per language): static XAML, workflow and status literals, Server messages shown in the client, dialogs, and the NC viewer page. Composed messages translate through catalog templates, sentence by sentence and line by line, so embedded names and numbers stay unchanged. Message boxes and file dialogs go through localized wrappers, and Hebrew message boxes read right to left. The NC viewer page translates its interface text from the host catalog and asks the host to translate composed text; it skips the NC program, data cells, and form values.
- NC viewer programs of a Case Operation are saved in the Case Working Folder under `Gcode\<Case name>\<Operation number>\<process revision>\<postprocessor name>\<local revision>\`. Save, Use for G-code release, and Release to Server write to the folder of the release the program becomes, computed from a fresh read of the Case, Operation, and G-code catalog; missing folders are created; Save as and Save copy as open that folder or the viewed release's own folder. After a release from the viewer or the Release G-code form, a program saved under the Operation's `Gcode` folder moves to the folder of the numbers the Server assigned. A different existing file is replaced only after confirmation, and a release is refused while the Case has no usable Working Folder. Tests cover the numbering, the folder layout and safe names, the refusal, the confirmation, and the move.
- Corrected localization of content realized after load. WPF raises Loaded only in subtrees that declare Loaded handlers, so tab pages first shown after startup, item templates, code-built Timeline blocks, and context menus stayed in English until the next language change. Elements are now localized on their first layout size, a selected tab page is localized after layout, and a context menu is localized when it opens. Localized values are observed through bindings that the element holds, replacing `DependencyPropertyDescriptor.AddValueChanged`, which kept every watched element in memory.
- Replaced the Windows localization timer/tree sweep with loaded-element, selected-tab, and coalesced language-change updates. Catalog exact, template, and sentence-segment paths are indexed and cached; language preference writes are coalesced off the UI thread. Timeline Canvas updates and STEP viewport redraws are coalesced at render priority and deferred while hidden, the five-second refresh is background-priority and non-reentrant, and unchanged edit-session snapshots no longer raise redundant view-model notifications.
- Implemented a dedicated Setup page for connection Save/Connect/Refresh, Working Calendar create/edit/delete with usage/work/break/exception authoring and Setup Calendar selection, Machine create/edit/deactivate/delete, reusable Machine Type create/edit/delete, Postprocessor create/edit/delete, and explicit per-Machine execution/capacity/timing/Postprocessor configuration. Machines select named Machine-enabled Calendar and Machine Type records through stable IDs; users do not type IDs. The Case Operation required-Machine dropdown dynamically unions registered Machine process/axis/Machine capability/Machine-Type capability tokens, offers blank Any, and retains a selected legacy token.
- Schema v11-v14 and the Setup page add Employee/Resource CRUD, employee full/partial-day availability exceptions, cached Israeli-holiday CRUD/online refresh/policies, and optimistic report/email settings. Schema v20 implements the minimal weekly material-order report. Schema v21 implements the employee-efficiency report from explicit employee planned/actual measurements and calendar-derived capacity. Both reports use configurable recipients/day/time/timezone, anonymous SMTP-relay delivery, once-per-week scheduling, and Edit-Mode-gated Send Now. The Timeline uses active employees as individual transient capacity. No persisted worker-to-Operation assignment, skill qualification expiry, authenticated SMTP secret storage, payroll, or employee ranking is implemented.
- Implemented a temporary fixed-mapping Setup **Excel Case + Order Import**. It asks for one `.xlsx` worksheet, performs a bounded read-only preview, applies Case A/O/F/D and Order B/L/E/N mappings, reports each valid/matched/skipped row, and exposes one explicit `Import Cases and Orders` action. Part Number identifies Cases and Case + Order Number identifies Orders. The current Windows client sends no planning selections, Machine mappings, Batches, Operations, allocations, assignments, backlog, Timeline, or planning-mode changes. Existing records are matched without silent updates; invalid rows are skipped with reasons while valid rows commit atomically. The former five-step wizard remains bypassed compatibility code and is not part of the visible workflow. Kitaron integration is expected to replace this temporary tool.
- Kept business rules, planning data, edit authority, and all SQLite access on the Server.
- Add authenticated identity/session and route/Batch update, assignment-form, Working Calendar combined-overnight/overtime/archive/automatic-holiday policy, and downtime mutation workflows against the API.

### Tests and exit gate

- Client tests cover health/Edit Mode parsing, Case query routes, ETag/generation-protected Case saves, Case/tab population, Order/Batch create payloads and editor generation, explicit combined/stock/scrap allocation entry, external-folder launch, required headers/generation, safe Server errors, settings persistence/validation, local identity, and unavailable-Server behavior.
- The WPF stability audit realizes every nested tab and repeatedly switches English/Hebrew/Russian while requiring a responsive dispatcher, correct current text/direction/selection, bounded per-interaction and whole-pass time, no idle localization work, coalesced language traversals, and stable visit/write counts across identical passes. It also verifies one Timeline render for a 50-Machine collection burst, zero Timeline renders for an unchanged hide/show cycle, one deferred STEP redraw after repeated hidden resizes, a bounded cold localization initialization, and zero redundant notifications for unchanged session attachment.
- A second WPF localization audit selects Hebrew and then Russian while Cases is shown, then shows every main-window tab and nested tab without forcing layout, failing on any visible text that is still an untranslated catalog key. It also covers a page first shown after load, text added later, a run-composed TextBlock that keeps its bound run, a context menu before and after a language change, and the return to English. A release test fails when observing a localized element keeps it in memory. Catalog tests require every literal XAML label to have Hebrew and Russian entries, every placeholder to survive translation, the target script, preserved line breaks, and no source-code fragments.
- Excel-wizard tests cover automatic draft as preview-only state; exact A/O/F/D Case, B/L/E/N Order, and P/H Batch mappings; aggregation of repeated related Order rows; one Batch per Part+Batch Number; explicit related-Order allocations balanced to summed remaining demand; exact-Machine/unique-compatible-Operation assignment; ambiguous Pool/Skip fallback; preservation of explicit decisions; and the absence of automatic Commit, Machine/route invention, compatibility override, or existing-record update.
- Editing controls are disabled without current authority or confirmed Server health.
- Verified the client assembly has no SQLite reference and its local settings contain no database path.
- Full planning-route validation, stale resource revisions, reconnect compatibility, and production-use sizing remain future exit work.

## 10. Phase 7 - Machine Board and drag-and-drop backlog

**Implementation status:** Core compact board slice implemented. The Server supplies a snapshot-consistent unassigned operation pool and Machine backlogs with planned quantity, sorted allocated Order references, and nullable `setup + QA + aggregate load/unload + quantity x cycle` estimate. The total uses the same existing manual/automatic load-event count as Timeline calculation but is not a phase schedule. The Windows client performs manual assign, reorder, cross-Machine move, unassign, and explicit player-style execution commands through Edit Mode, reloads only after acceptance, and shows incompatible/rejected feedback. The board's conflict panel is now served by the same Timeline engine over a fixed 30-day horizon from `readAt`. Search/filter, downtime display, projected start/finish, plan revisions, and concurrency preconditions beyond Edit Mode generation remain pending.

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

Implemented tests cover projection membership/order, exact API command targets, editor-only interaction, incompatible rejection with an unchanged local board, and existing repository invariants for stable contiguous backlog order. The board conflict panel reads the Phase 8 Timeline projection for `[readAt, readAt + 30 days)`: status `current` with severity-ordered structured conflicts (`unassigned_operation` omitted because the pool already shows it), or `unavailable` with an empty list if the calculation throws, so an empty list is never presented as a conflict-free plan. The Windows panel shows the severity as text next to each message. Server tests cover a blocking calendar conflict on assigned work and the omitted unassigned code.

## 11. Phase 8 - time and conflict engine

**Implementation status:** Pure domain calculation and the read-only canonical Timeline API are implemented. The persistence mapper reloads current SQLite Machine Assignment IDs, schema-v24 planning modes, fixed backlog positions, and recorded cross-Machine transfer/pause timestamps on every request, then consumes calendars, resources, downtime, immutable Batch timing/dependency snapshots, quantity, allocated-Order Work Finish Date/Order Number facts, and the Server snapshot `readAt` cursor. A `not_started` `forward` or `manual` assignment earliest-fits at or after that cursor; a future `backward` assignment latest-fits before the earliest linked delivery cutoff. When a Backward intended start is missed without Start, the same assignment transiently falls forward from the cursor, preserves its persisted mode/backlog, shifts downstream graph consequences, returns `backward_start_missed`, and returns the deadline warning if late. No feasible fit becomes an identified cursor-anchored `blocked` marker; an elapsed historical horizon emits end-boundary blocked markers rather than fabricated not-started forecasts. Mixed modes share one fixed graph and Machine-lane reservation set. The API has no global `mode` selector; a supplied mode query is rejected as assignment-owned. It requires no planned-start/planned-end fields, performs no Timeline mutation, and reports outside/infeasible deadlines structurally. The projection emits exactly one identified current operation or blocked-waiting block per active assigned Operation ID, folds waiting/downtime/prior-Machine facts into its phases/detail, anonymizes ordinary capacity bands, and logs/removes duplicate producer blocks rather than introducing a hold/backward/manual layer. It also returns each Machine's merged/clipped `nonWorkingWindows` outside the interval list, using the complement of the exact Working Calendar expansion already supplied to scheduling. A final same-Machine invariant preserves actual/hold/history, demotes conflicting forecasts to blocked waiting, and emits structured overlap conflicts without changing persisted priority. Fixed-point propagation carries each unresolved row through later Machine backlog positions, Sequential descendants, and locked-simultaneous forecast members until no leapfrog remains. Suspended actual work stops at pause start; moving it starts the current hold no earlier than the true cross-Machine move event, or the replacement assignment's creation time after an unassign/reassign. The engine never mutates/reorders inputs. The pure engine accepts multiple predecessor edges for one child, but schema-v9 persisted Batch snapshots expose one predecessor source field, so multi-parent authoring requires a separately approved schema/API evolution. Plan revision/cache and broader production conflict policy remain pending.

All three modes derive duration from setup, QA, the existing per-event load/unload values, and normal production cycles. For not-started managed Operations, Task 7 combines prepared-tool magazine loading, fixture installation, and first-piece prove-out into setup, then applies normal cycle time to `max(planned quantity - 1, 0)` so the first part is not counted twice. Load/unload cadence still uses the full planned quantity. Legacy Operations and started/history rows keep their stored setup plus full-quantity cycle behavior. Manual loading is one initial load then one cycle per part. Automatic loading with N is one initial/repeated load followed by up-to-N-cycle production groups, for `ceil(plannedQuantity / N)` load events; automatic loading without N has zero load events and one production run. Before materializing phases, the Timeline returns blocking structured conflict `load_unload_occurrence_limit_exceeded` if any operation would require more than 10,000 non-zero-duration load/unload occurrences. Its message directs the planner to increase an automatic every-N cadence or split the Batch; a manual operation can instead be split or switched to an approved automatic cadence. This reversible calculation safety guard does not mutate quantity or allocation and remains subject to a broader configurable-cap policy. Each worker-required load event independently reserves a regular worker. Forward/manual calculate that cadence chronologically, while Backward allocates the same segments in reverse before returning chronological phase spans. Different operations of one Case/Batch may be assigned to different Machines. The Server expands recurring local Machine/resource calendars, including one overnight window, with breaks, dated exceptions, cached holidays, timezone/DST conversion, and the managed Setup Calendar; the upgrade-only legacy setup JSON remains readable until the first managed selection or explicit clear. Missing setup selection falls back visibly to Machine availability. Planned/open/restored downtime retains its typed reason, Sequential dependencies use the predecessor finish regardless of mode, and locked groups retry for common Machine/resource availability. Information/Debug logs expose Timeline input counts, assignment ID/mode/timing inputs, calculated results, and duplicate-block detection. Only the explicit `GET /api/v1/timeline` read records `timeline_conflict_detected` / `resource_wait_detected` diagnostics, in one write transaction that is skipped entirely when every per-day key already exists; the Planning Board, TV, E-Ink, and job-package projections reuse the calculation without writing. Forward/manual worker contention remains earlier Work Finish Date then natural Order Number; Backward adds shorter duration before the Order Number tie-break. Combined/multiple overnight-window, overtime, and Calendar archive policy, recurring downtime/cancellation, plan revision/cache, and broader production-conflict severity remain pending.

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

**Implementation status:** Core LAN-served dashboard implemented. The dependency-free fullscreen UI uses one compact dark band per display-enabled Machine and shows Machine identity/connection, current Part, Batch/Operation, Operation name, large Started/Paused/Waiting/Completed text, calculated completion, and a thin progress bar. Idle bands explicitly report no current Operation. Part pictures, conflicts, and queued Operations are intentionally hidden. It conditionally auto-refreshes, visibly retains the last snapshot while offline, and has no edit controls. The GET-only projection supports ETags. Authentication, display groups, offline-device telemetry, target-TV visual acceptance, and managed kiosk deployment remain pending.

### Scope

- Create a read-only web/kiosk dashboard under `client-tv-dashboard/`.
- Show the current Operation per Machine in the compact factory-board band with textual execution state and setup or calculated part/Batch completion.
- Implement approved automatic refresh, offline detection, reconnect, and kiosk deployment.

### Tests and exit gate

- Dashboard credential cannot invoke any mutation or Edit Mode path.
- Stale/offline state is obvious and never presented as fresh.
- Target TV resolutions, browser/kiosk environment, viewing distance, and refresh cadence pass visual acceptance.
- Status remains legible without color.

## Haas VF-3 NGC integration phases 1-2

**Implementation status:** Protocol-independent Server connection platform and Haas production adapter implemented; full real VF-3 production acceptance remains pending. Schema v43 adds one primary `MachineConnection`, normalized current state, meaningful-change history, connection events, and retained raw telemetry on top of the v42 immutable header/Bench/audit tables. `HAAS_NGC` now explicitly selects MDC or read-only MTConnect as its normalized telemetry provider. MTConnect `/probe` and `/current` HTTP/XML parsing, single-device validation, state/program/counter/macro-range normalization, compact diagnostics, dedicated API test, Server polling, Setup source selection, and live-agent commissioning tests are implemented. The separate generic MTConnect, OPC UA, and Custom adapters remain registry-only unsupported options. Filename, MTConnect `PROGRAM`, and Cycle Start have no business-transition role.

**DPRNT file source and FANUC FOCAS (schema v75):** The DPRNT source is now adapter-independent (`TCP`, `FILE`, or `NONE`). `FILE` tails a controller-written print file over a UNC share (Mazak Matrix `print.txt` with DPR14 = 4) and applies a clear policy (`ON_OFFSET_LOADER`, `AFTER_READ`, `NEVER`) with loss-safe truncation; `HAAS_NGC.telemetryProvider=DPRNT` serves controllers without MDC/MTConnect. `FANUC_FOCAS` is a read-only adapter over the FANUC FOCAS 2 library with a fake-client unit test suite, API validation tests, and a table-rebuild migration test; it is implemented at the Server contract level only. Real-controller acceptance (Mazak print-file behaviour under DPR14, share permissions for the service account, FOCAS library installation, `cnc_exeprgname`/`cnc_rdparam` behaviour on the specific control series, and the commissioning-gated program-head upload path) remains a physical commissioning requirement and is not claimed here. The 2026-09-18 postprocessor documentation audit (`docs/postprocessor-writer-documentation-audit-2026-09-18.md`) added two DPRNT-path fixes with tests — both readers strip ASCII control codes such as the FANUC `POPEN`/`PCLOS` DC2/DC4, and the generic `dprnt.host` lets a serial-to-Ethernet bridge with its own address carry the TCP source — and replaced the Haas-only writer guide with a controller-neutral specification. The same day, schema v76 added the Machine NC dialect (`HAAS_NGC`, `FANUC_MACRO_B`, `MAZAK_MATRIX_EIA`, `OKUMA_OSP`): the Production Package Creator renders the verification hook, event context, cycle events, and Offset Loader through a per-dialect profile (Okuma OSP uses `CALL`/`PUT`/`WRITE C`/`VC` variables and an `O1990.MIN` loader), verification variable ranges are validated per dialect in the service and in the SQLite triggers, the Machine API and Setup expose the dialect, and the writer guide examples for SolidCAM (GPPL `{ nl, '...' }`) and Cimatron (GPP `.exf` `OUTPUT J`) were corrected. The protected verification subprograms for FANUC, Mazak, and Okuma and the exact OSP `PUT`/`WRITE` device routing remain physical commissioning work (OD-037).

**Cycle markers mandatory and client auto-update (0.1.117, 2026-09-18):** `NcPackagePlaceholderSchema` now rejects a canonical template with neither `CYCLE_START` nor `CYCLE_END` (`production_package_cycle_marker_required`), so every package build carries part counting; older releases without the pair fail their next build and must be re-released. The Server MSI bundles the client MSI of the same version with a JSON manifest (`client-installer\`), `ClientInstallerService` serves it on `/api/v1/client-installer` after recomputing and checking the SHA-256, and the Windows client compares the version pair once per session: a newer client package is downloaded, verified, and installed through a detached `msiexec /passive` script that restarts the client (`ClientUpdatePolicy`, `ClientInstallerLauncher`, `ClientUpdateWindow`); a newer client than the Server's package is never downgraded. Covered by Server API tests (manifest, download, invalid manifest, version normalization) and client tests (policy, script, API client); the physical UAC/restart sequence on a client PC is verified at the next factory upgrade.

**Customer portal push in the Server (2026-09-18):** the separate cloud portal (`meimad-client-cloud-portal`: Firebase Auth, Firestore, Cloud Run ingest at `meimad-ingest-832857266466.us-central1.run.app`, Hosting at `meimad-client-portal.web.app`) is fed by `ClientPortalPushHostedService` inside the Server instead of the portal project's stand-alone Python agent. `ClientPortal` options (disabled by default; `IngestUrl` must be https; secret from `SharedSecretFile`; `Customers[]` mapping exact `Case.customer` values to portal ids) are validated at startup; the service posts only the customer-safe Order fields per exactly matched customer, logs rejections, and never throws out of its loop. Covered by `ClientPortalPushServiceTests` (real Sqlite repositories, stub HTTP). The portal's customer accounts remain admin-provisioned in Firebase; the Server holds no Google credential.

The real-machine Definition of Done is intentionally not claimed. Haas publicly documents Q500/Q600/E and Local Net Share, but not a guaranteed active MEMORY/USB/Remote-Net-Share program-to-SMB-file mapping. Complete and record the read-only VF-3 tests in `haas-active-program-header.md` before enabling production polling. Tool Table transfer transport remains site/controller-specific; the implemented reset endpoint requires explicit successful-transfer confirmation before the audited zero/write/read-back sequence.

## 14. Phase 11 - E-Ink API and simulator

**Status:** Server official package generator/read side, browser simulator, and scoped idempotent `SEND_TO_QC` command are implemented. Approval/retention, physical-device behavior, physical commissioning evidence, and API stability approval remain open.

### Scope

- Implemented structured JSON as the explicit v1 Server/simulator baseline; pre-rendered assets require a later compatible contract decision.
- Implemented device registration/assignment, TabletID identity, MAC discovery/mapping, enable/disable, small conditional version check, Machine screen, exact-revision package manifest/file reads, and time config. Schema v62 clears legacy E-Ink credential hashes.
- Implemented active-editor publication for an assigned Batch Operation with immutable snapshot metadata, safe source/logical/storage paths, configurable allow-list/size limits, staged output, SHA-256, context revalidation, and failure cleanup. Approval roles/UI, signatures, retention, and superseded-revision access remain open.
- Implemented a dependency-free physical-firmware browser simulator for the selected TRMNL 7.5-inch 800×480 monochrome UC8179 profile. Its panel canvas reproduces the firmware's classic TFT_eSPI 5×7 bitmap glyph table, integer `textSize` scales, exact production/Service drawing coordinates, and fixed line geometry instead of approximating them with browser fonts. It uses hardware-MAC registration, the TabletID-identified physical tablet status/event adapter, and the exact D1/D2/D4 short/1.2-second-hold mappings plus Reset. It fail-safe clears a potentially retained verification code when status contact is lost. Bench-only workflow/offline/low-battery fixtures are visibly outside the tablet body and never mutate Server data. Package manifest/file/checksum, physical SD staging/atomic activation, deep sleep, and local annotations remain separate API/device tests rather than simulated physical screen features.
- Implement the approved TabletID-identified `POST /api/tablets/{tablet_id}/events` command for exact `SEND_TO_QC`: resolve the TabletID-bound Machine and unique `IN_SETUP_RUN` Production Run on the Server, reject client-supplied target/time fields, atomically persist one append-only Server-timestamped event plus audit record, derive `IN_QC` and a new status revision, and return the first accepted timestamp on same-run retries. Do not require Edit Mode or mutate planning/run lifecycle/package data.
- Implemented the TabletID-identified `GET /api/tablets/{tablet_id}/status` projection with `nc_run.id` bound to a real Production Run ID, exact firmware snake-case JSON, TabletID path scoping, contact/battery recording, and deterministic content revision. The event-derived mapping includes `QC_FAIL -> IN_SETUP_RUN` and `QC_PASS -> READY_FOR_PRODUCTION`; the Windows QC Queue owns those guarded transitions. A multi-output Program returns `tablet_projection_ambiguous` until the single-part physical payload is deliberately extended. The payload also carries `tools`, the active rows of the released tool table resolved for the Run (the table pinned at Run start, otherwise the active process revision's table), and firmware 0.1.6 renders them as paginated tool/description/pocket rows.
- Task 11 approves voltage-only battery metadata as a separate non-planning scope. Firmware sends it on every existing ping/status/event HTTP request without changing event JSON. The MAC-only discovery call validates and records the latest bounded voltage/percentage metadata; bounded history, retention, and read/admin policy remain separate work before introducing a telemetry POST route.

### Tests and exit gate

- Device A cannot read Device B or another Machine/package by guessing IDs.
- Unchanged version checks avoid package transfer and display-refresh work.
- Changed revisions download and activate atomically only after full verification.
- Interrupted, corrupt, oversized, revoked, missing, and malformed data retain prior valid content.
- Package files never expose source filesystem paths or unrelated confidential data.
- No checklist/comment upload route exists.
- `SEND_TO_QC` accepts only the path-matched enabled device, cannot select another Machine/run, is valid only for a unique `IN_SETUP_RUN`, is idempotent across lost-response retries, retains the first Server timestamp, advances only the tablet status revision, and cannot mutate planning/package/run lifecycle facts.
- Every other E-Ink POST/PUT/PATCH/DELETE remains forbidden.
- Contract and simulator remain compatible across the approved firmware-support window.
- API is declared stable for the prototype with versioning/change policy documented.

## 15. Phase 12 - separate ESP32 hardware/firmware prototype

This phase belongs in a separately approved device project after phase 11's API stability gate.

Current prototype status: the ESP32 project now compiles a text-only 800x480
production screen with Machine/part/Operation/status hierarchy and fixed
three-row tool pages. The status adapter supplies the live header/work/status;
live tools remain explicitly unavailable until the official package tool source
is connected. A seven-tool `LAYOUT DEMO` fixture exercises three pages without
claiming official data. The firmware also persists the last completed status
revision plus tablet identity in NVS, skips all panel drawing for an equal
same-tablet revision, forces reassignment/changed-revision refresh, saves only
after `update()` returns, and logs every actual refresh duration. A failed
status read preserves the same-tablet retained screen except that a possibly
visible setup-verification code is cleared once to a persisted unavailable
fail-safe that forces repaint after the next valid response. The adapter and
renderer require the `IN_SETUP` verification projection, preserve leading-zero
codes, and show explicit expired/invalidated/unavailable blocking states.
Model/status/pagination, revision-decision, and power-policy assertions compile
in the separate contract-test image. The centralized state policy keeps
`READY_FOR_SETUP`/`IN_SETUP`/`IN_SETUP_RUN` awake with Wi-Fi off and no recurring
poll; D1 starts a bounded refresh session. `IN_QC`/`IN_PRODUCTION` deep-sleep
with button wake only, while canonical post-QA `READY_FOR_PRODUCTION` adds a
configurable 60-second one-shot timer refresh. ESP32-S3 EXT1 remains configured
for the three active-low buttons in sleeping states. `BLOCKED`/`UNKNOWN` retain
the conservative 120-second fallback. Physical upload/readability/current,
battery-only wake, and configured workday/shift enforcement remain open.
The first input abstraction now compiles a 40-ms debounce, release validation,
wake logging, short-D1 Refresh, 1.2-second D1 Service/Debug, D2 Previous Tool
Page, short-D4 Next Tool Page, and
1.2-second D4 `SEND_TO_QC`. The send action requires freshly read
`IN_SETUP_RUN`, submits once per wake, refreshes status, and renders temporary
accepted/confirmed/rejected/uncertain feedback. It remains physically and
end-to-end unverified because the Server compatibility routes are pending. The
wake runtime logs reason, available UTC time, battery voltage, and RTC-retained
pre-sleep policy state. Timer/cold/Refresh/send paths perform bounded network
work; page-only/invalid button wakes keep Wi-Fi off, and every network path
turns Wi-Fi off after HTTP work and again before deep sleep. Compile evidence
does not replace measured radio/deep-sleep current or wake testing, so this
phase remains open.

Task 12 adds a separate `xiao-esp32s3-plus-demo` compile target for backend-free
screen development. It uses persistent compiled fixtures for every initial
workflow state plus Wi-Fi error, Server error, unregistered-tablet, and low-
battery screens. The target makes no Wi-Fi or HTTP calls and cannot submit a
tablet event; it remains disabled in the normal configurable firmware build.

Task 13 adds a shared firmware serial-logging boundary with stable category
prefixes (`BOOT`, `WAKE`, `WIFI`, `API`, `DISPLAY`, `BUTTON`, `BATTERY`, and
`SLEEP`). Lifecycle, network, API, display-refresh, action, and sleep records
now use structured key/value messages through that boundary.

Task 11 samples voltage once per wake, sends it as metadata on each firmware
HTTP call, and shows `LOW BATTERY` at or below the provisional 3.30 V threshold.
It intentionally omits percentage until a measured AA discharge curve exists.
The Server currently may ignore the metadata; per-tablet history and retention
remain separate Server work.

### Scope

- Select MCU, panel/controller, power design, SD subsystem, input method, service connector, enclosure, mount, and environmental protection.
- Implement provisioning, credential storage, timekeeping, deep sleep, battery measurement, conditional polling, staged package handling, panel UI, failure state, local annotations, and a confirmation-protected `SEND_TO_QC` input flow.
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
9. Verify `SEND_TO_QC` on physical input: no optimistic `IN_QC`, bounded retry after an uncertain result, idempotent acknowledgment, readable pending/rejected/success states, and no accidental double action.
10. Run a one-week one-Machine pilot with numeric success criteria.
11. Order additional units only after the pilot and battery target pass.

## 16. Cross-cutting test strategy

### Role preparation queues and Production Packages

**Implementation status:** Implemented. Preparation stages remain derived
projections. Schema v64 adds immutable, Server-owned Machine-specific
Production Packages, artifacts, one current pointer per Operation, and retained
invalidation/supersession evidence. Tool Room package creation is one deliberate
action with creator/time audit and no approval workflow. Only a successfully
activated exact current package satisfies Ready for Setup; opening/exporting it
does not start setup or change state. The shared Windows queue adds the required
NC Creator, Tool Room, and Setup role context actions without Machine selection
or a second G-code release path.

Canonical NC releases use deterministic protocol-v2 `[[MEIMAD:<KEY>]]`
placeholders. The postprocessor remains server-blind and Part/Operation names
are resolved from current master data. Package build injects the configured hook
only for verification-enabled CNC work; disabled CNC output has no verification
code or residual token, and manual output has no CNC executable. Manifest schema
v2 records authoritative names/identities, source and generated hashes, actor,
Server time, offset mode, supersession, and the relevant Machine capability
snapshot. Connection state changes delivery options only. Machine assignment,
effective NC, Tool Table/source mode, and verification-content configuration
mismatches invalidate current-package eligibility immediately. Legacy package
marker V1 remains an exact compatibility parser for immutable history.

**Open product decision:** Manual Machines and CNC configurations without an
executable Offset Loader still need a separately approved authoritative
setup-start signal if no suitable existing Machine event is available. No fake
loader or client-only manual status was added.

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
- **OD-005 - Route versioning:** Partially resolved: optimistic Case Operation PATCH changes approved scalar/one-link dependency fields but not route position. Schema v9 snapshots identity/display/Machine-type/timing/dependency fields into Batch Operations, so existing Batches are unaffected by edits, while a newly created Case Operation is appended automatically to every open Batch of the Case (decided 2026-09-09). Define aggregate route revision and route-reorder concurrency before implementing reorder; arbitrary multi-record dependencies remain OD-010 work.
- **OD-006 - Lifecycles:** Partially resolved: allocated Orders implement Server-derived `active` / `in_production` / `complete` across all allocated Batches, persisted atomically with Batch create/delete and operation execution. Completion requires full allocation coverage, at least one operation in every allocated Batch, and all related operations completed; manual production status for new/unallocated demand, contradictory linked status edits, and new allocation to cancelled demand are rejected. Legacy already-linked `cancelled` is migration- and recompute-preserved until an explicit matching status assertion resumes derivation, pending a fuller cancellation policy. Production Batches implement Server-derived `waiting` / `in_production` / `complete`, with zero-operation Batches waiting and suspended work in production. Batch Operations implement `not_started` / `in_progress` / `suspended` / `completed`, with completed work removed from the active Machine backlog. Define actual-time/history needs, Order Number/date/unit rules, cancellation/reallocation, archive/delete/cascade policy, and audit.
- **OD-007 - Tool package boundary:** What creates tool-cart/checklist content when full tool inventory is outside MVP?
- **OD-008 - Existing data:** Resolved for the implemented transition: the shared legacy `.xlsx` backlog is imported through a read-only preview and explicit operator mapping/Skip workflow, followed by one Edit-Mode-gated atomic commit. Suggestions are never approvals; no route/timing/date is invented, existing assignments are not moved, imported rows append after existing Machine backlog positions in workbook order, and the original file is not modified or stored. Production rollout still requires a backup, a full operator rehearsal on a copy, sign-off on every warning/skipped row, and reconciliation of imported counts against the source workbook.

### Planning engine

- **OD-009 - Time model:** Partially resolved in the pure engine to setup/QA/load-unload/production phases, explicit half-open UTC windows, earliest-feasible placement in fixed backlog order, downtime subtraction, split work intervals, and individual employee contention. Setup workers require a Machine skill token; QA and regular-worker phases require their roles. Calendars, breaks, exceptions, and cached holidays constrain each resource. Simultaneously ready contenders use earliest allocated-Order Work Finish Date then naturally smaller Order Number, and waiting states the deciding rule without persisting or reordering anything. Persisted named-worker assignment, skill qualification expiry, in-progress work, overtime policy, rounding, and recalculation triggers remain open.
- **OD-010 - Dependencies:** Domain graph supports stable relationship records, fan-in/out, exact dependency meanings, sequential cycle rejection, and locked groups. The create/PATCH API maps at most one referenced prior operation into the existing Case Operation row and validates the rehydrated full graph transactionally. Schema v9 snapshots that relationship into each Batch, and the pure engine reads only the snapshot while applying Sequential precedence, no constraint for Parallel-capable/Independent, and common start/finish plus shorter-member reservation for Locked simultaneous. Multi-record fan-in/out persistence and richer cross-Machine feasibility remain open.
- **OD-011 - Conflict policy:** Normal structural assignment compatibility rejects a first incompatible command with `machine_type_override_required`; an active editor may explicitly resubmit a different active type with mandatory reason, producing an immutable audit snapshot without changing the route. Unsafe Machine or linked Machine Type edits still reject. The pure engine reports deterministic blocking calculation/input conflicts, but the broader catalog, severity/urgency, Work Finish Date cutoff, plan-revision stability, and API presentation remain open.

### Single Edit Mode and identity

- **OD-012 - Authentication/authorization:** Resolved 2026-09-27 for Windows users: Server-held accounts with user name and password, administrator-managed user types granting fixed permissions, everyone signed in may view; TV and tablets keep their credential-free scopes. Open: password policy beyond length, LAN transport security (OD-017), and account audit history.
- **OD-013 - Edit lease:** Superseded 2026-09-27: there is no edit lease; parallel changes are checked per record, per Machine backlog, per NC release and per QC state. Formerly resolved that every implemented planning mutation requires the current client ID and generation. The stored lease deadline is the transfer-response deadline, not a heartbeat. Define heartbeat, disconnect/crash/restart behavior, and stale unsaved edits.
- **OD-014 - Transfer contention:** Superseded 2026-09-27 (no Edit Mode transfer). Formerly resolved MVP to one pending requester, no queue, Reject returning the requester to Viewer, and a configurable 1–3600 second server timeout with a 30-second default. Define cancellation, notifications, takeover safeguards, history retention, and audit.

### API, files, and deployment

- **OD-015 - Technology stack:** Server resolved to .NET 10 with ASP.NET Core/Kestrel and xUnit integration tests; Windows client resolved to .NET 10 WPF; TV resolved to dependency-free HTML/CSS/JavaScript served by the Server. Supported Windows/browser versions, long-term dependency/update policy, and production support baseline remain open.
- **Localization implementation:** The complete Windows WPF surface and TV dashboard support English, Hebrew, and Russian. The Windows client embeds generated catalogs for static XAML plus user-visible workflow/status literals, localizes each element on its first layout size, each selected tab page after layout, and each context menu when it opens, observes bound display properties so live status changes are localized, persists the selected language under the user profile, and mirrors windows for Hebrew RTL. Message boxes, file dialogs, and the embedded NC viewer page follow the selected language. Automated WPF audits select all 39 tab pages and open all five windows in both translated languages, and show every main-window page after a language change without forcing layout, failing when a known English UI string remains. Technical/domain values remain unchanged.
- **OD-039 - Display names for enumerated Server codes:** Open. The functional specification says Server API values are never translated, yet pickers list raw codes such as `regular_worker`, `sick_day`, `non_working`, `sunday`, `SEQUENTIAL`, `in_production`, and `CNC_GCODE`. The implemented, reversible interim shows Hebrew and Russian display names for these codes through catalog entries only; the stored, sent, and validated value stays the code, and English still shows the code. Codes quoted literally by help text, validation messages, or controllers (NC dialects, DPRNT sources and clear policies, controller part-counter sources, time-zone IDs) stay untranslated. Decide whether to keep translated display names, and then whether English should show readable names too, or to revert to raw codes in every language by removing those catalog entries.
- **OD-016 - API baseline:** `docs/api-contract.md` contains the Proposed MVP baseline. Case and Case Operation create/read/update, allocation-safe Order create/read/update/derived lifecycle, Batch creation/read/derived lifecycle, Machine/Machine-Type master data, recurring Working Calendar CRUD, dedicated Setup Calendar selection, staged legacy Excel preview/commit, Machine backlog/assignment and assignment planning-mode PATCH, Single Edit Mode, Timeline, TV, and E-Ink read/device-administration subsets are implemented. Approve and convert the remaining contract to OpenAPI; identity, plan revision/concurrency beyond resource ETags, paging/horizons, idempotency retention, and compatibility window remain decisions.
- **OD-017 - Network security:** Choose host/port discovery, HTTP versus HTTPS, certificate trust, firewall, CORS/CSRF where applicable, secrets storage, service identity, and installation/update strategy.
- **OD-018 - Working Folders:** Define supported path types, credentials/permissions, availability behavior, allowed files, preview generation/refresh, and `_MeimadPlanner` ownership/cleanup.
- **OD-019 - Backup:** Resolved configurable destination, online-backup behavior during writes, count retention, integrity/foreign-key checks, and isolated restore verification. Define schedule, authentication/authorization, encryption, destination access, migration coordination, alerting, active-database recovery procedure, clean-host drill, RPO, and RTO.
- **OD-020 - Observability/NFR:** Define expected Cases, Orders, Batches, Machines, concurrent Windows clients, TVs/tablets, response/recalculation targets, uptime, offline threshold, and log/privacy retention.

### CNC postprocessor and protected verification

- **OD-034 - Controller-profile contract alignment:** The consolidated Haas NGC postprocessor/macro specification records implementation mismatches that remain commissioning blockers: generic Server alias validation permits 1–999 while Haas documents narrower alias rules and excludes G00/G65/G66/G67; generic variable validation does not prove that the response variable is M109-valid or that challenge variables persist safely across O9001/O9002; the hook parser tolerates an omitted A-argument decimal although the Haas call contract requires it; and no universal first-article/QC hold-and-resume strategy is commissioned. Resolve these only with the reviewed replacement input/timer and sequence-epoch design, controller-profile validation, updated release tests, and bounded physical evidence. Keep verification disabled and macro candidates v3–v5 quarantined.

Schema v60 resolves the generic M109-range validation and adds explicit finalizer
and persistent-sequence mappings. OD-034 remains open for site collision review,
the Haas alias/profile mismatch, exact hook syntax tightening, and the uncommissioned
first-article/QC operating strategy.

- **OD-035 - Verification timeout, tablet wake, and failure-event delivery (source correction implemented; physical proof open):**
  The 2026-08-30 R3 physical attempt exposed two incompatible timing assumptions.
  The Server challenge lifetime, CNC late-response boundary, and firmware IN_SETUP
  automatic wake are each 120 seconds, so a sleeping tablet can first poll only
  after the code expires and a 130-second negative test cannot retain a result
  against a still-pending Server session. The same attempt raised CNC alarm 903
  after the SVF DPRNT block but the Server received no SVF, so source order alone
  does not prove TCP delivery before `#3000`. Decide and document separate Server
  expiry/CNC-entry/poll margins and a physically proven pre-alarm DPRNT delivery
  barrier or alternate failure-evidence design. The reversible source decision is
  now: setup-time background polling is removed in favor of an operator-triggered
  bounded Wi-Fi session; response exposure still ends at session expiry; a late SVS remains rejected; an exactly
  correlated late SVF is retained as failure evidence; and v8 inserts a one-second
  no-motion dwell after SVF/G103 and before alarm 903. The later V8 attempt proved
  SVF delivery but exposed a blocking Server-secret/controller-key mismatch and
  failed battery operation on the replacement tablet. V8 is quarantined; V9
  recovery requires one newly rotated secret to generate both sides. Physical
  proof of battery power and the aligned response remains required. Preserve sequence
  gaps as anomalies; never reset or reseed `#10504` to repair history.
  Verification remains disabled.

- **OD-036 - NC dialect for Server-injected blocks:** Resolved 2026-09-18, the day it was
  recorded. Schema v76 adds `machines.nc_dialect` (`HAAS_NGC` default, `FANUC_MACRO_B`,
  `MAZAK_MATRIX_EIA`, `OKUMA_OSP`), exposed as `ncDialect` on the Machine API and as
  "NC dialect" in Setup. `Application/GCode/NcDialects.cs` renders the verification hook,
  `EVENT_CONTEXT`, the cycle-event blocks, and the Offset Loader per dialect: Haas keeps
  `G65`/`DPRNT`/`G103`; FANUC and Mazak use the same custom macro B without `G103` and with
  `#500–#999` variables; Okuma OSP uses `CALL O9002 PA=`, `PUT '...'` + `WRITE C`,
  `VC1–VC200`, `IF [...] Nlabel`/`GOTO`, and an `O1990.MIN` loader ending in `M02`.
  `CncVerificationFoundationService` and the rebuilt v76 triggers validate the five
  variables per dialect, a dialect change is refused while verification is enabled, and
  legacy V1 releases build only for `HAAS_NGC`. Not chosen: a FANUC non-buffered M-code
  barrier (the cycle block simply has none; register one on the control if event timing
  must match motion) and emitting `EVENT_CONTEXT` as a comment (every dialect prints it).
  Physical commissioning of the non-Haas subprograms and the `MEIMAD/V/2/CONTEXT`
  ingestion log noise remain open under OD-037.

- **OD-037 - Okuma OSP-P200/P300 commissioning and release validation:** the Server side
  of the OSP dialect is implemented (OD-036), but nothing has run on the Genos L200E-M
  (OSP-P200L). Confirm on the control the exact `PUT`/`WRITE C` statement form and the
  output-device parameter that routes it to the RS-232 port (serial-to-Ethernet bridge,
  Server DPRNT source TCP with `dprnt.host`) or to a text file on the OSP-P Windows side
  (Server DPRNT source FILE); write and commission the protected `O9001`–`O9003`
  subprograms in User Task 2 (also for FANUC and Mazak); and decide whether
  `NcPackagePlaceholderSchema` should recognize the `$NAME.MIN%` transfer header line,
  which it currently treats as executable (`verification_placeholder_not_first`). The
  `OkumaOspDialect` class centralizes the rendered syntax so a spelling correction is a
  one-line change. A future Okuma API / THINC telemetry adapter is a separate decision.

- **OD-038 - NC viewer machine definitions and dialect translations:** Resolved 2026-09-23
  as a reversible implementation. The vendored Chevalier NC engine is extended, without
  editing vendored files, by Meimad machine/control definitions
  (`shared/Meimad.Planner.NcEngine/Definitions`) for the Mazak Variaxis i-500 (Matrix 2),
  Okuma Genos L200E-M (OSP-P200L-R), Haas ST-25Y (classic control), Haas VF-3SS (NGC) and
  generic FANUC 0i-MC 3-axis / 4-axis-A vertical mills, by control translations
  (`meimad-dialects.js`) and lathe subprogram inlining (`meimad-subprograms.js`), and by
  schema v78 `machines.nc_viewer_machine` (API `ncViewerMachine`, Setup "NC viewer machine").
  Decisions and their limits: (1) travels, rapids, spindle speeds and tool-changer data come
  from the manufacturers' published specifications (Mazak Variaxis i-500 and Matrix 2, Okuma
  Genos L200E-M / OSP-P200L, Haas ST-25Y and VF-3SS pages and manuals); rotary centres,
  reference positions and work-offset placements are placeholders marked `verified: false`
  and must be measured on the Machine before machine-frame positions or travel checks are
  trusted; (2) the vendored mill tilt solver moves B/C only, so the Variaxis A tilt is
  interpreted as a B axis about X and shown as B; (3) Okuma OSP is interpreted through the
  FANUC lathe interpreter after translation: LAP `G85`/`G87` are exact two-block `G71`/`G72`/
  `G70` equivalents, `G86` copy turning is approximated by one `G73` pass, `G88` LAP threads,
  `G84` cutting-condition changes, `G34`/`G35` variable leads and `G180`-`G191` compound
  cycles are not simulated (reported as messages), `CALL` arguments (`PA=`) are not passed,
  and inch/metric follows the machine parameter, not `G20`/`G21` (home moves on OSP);
  (4) Haas lathe one-block cycles are converted to FANUC two-block cycles with the same
  values, tapers (`I`/`K`) are drawn straight, and Haas `G76` chamfer/angle defaults follow
  the FANUC parameter values; (5) the Server analysis uses the NC viewer machine shared by
  the postprocessor's Machines (auto-detect Machines do not disagree) so the Planning Board
  estimate and the viewer agree; the adapter revision moved to `m2`, so every release is
  re-analyzed once in the background. Not chosen: editing vendored interpreters (kept
  byte-identical for upstream sync), a Server endpoint listing viewer machines (client and
  Server read the same installed catalog), and per-Machine program-memory folders on the
  Server (viewer-local settings; the Server inlines only from the release folder).

- **OD-035 - Turning tool-offset input on Haas NGC and Mazak:** Schema v79 writes measured tool offsets through `G10 L10–L13` (milling on Haas NGC, FANUC macro B, Mazak Matrix), `G10 P1xxxx X Z` (FANUC turning) and `VTOFH/VTOFD` or `VTOFX/VTOFZ` (Okuma OSP). No offset-input syntax is commissioned for turning on Haas NGC (`G10 L10 P<n> X Z` per the Haas lathe manual, unverified) or Mazak Matrix turning; those packages carry the `tool-offsets.json` sheet with `toolOffsetsLoadedByProgram: false` and the setupist enters the values. Decide the exact lathe syntax per control and add it to the NC dialect layer with tests; do not document it as a limitation.

- **OD-036 - FANUC, Mazak and Okuma verification subprogram commissioning:** The Server now generates the protected O9001-O9003 subprograms per Machine (`verification-macros`). Haas NGC reproduces the commissioned V10 macros byte for byte (tests/fixtures/verification-macros/haas-v10). The FANUC/Mazak rendering assumes `POPEN`/`PCLOS` around each DPRNT, a `#3006=1` message stop with the code entered into the response variable, and `#3001` as the millisecond clock; the Okuma OSP rendering assumes User Task 2 `PUT`/`WRITE C`, `CALL ... PA=`, `RTS`, `IF [...] Nname`, `MOD`/`FIX`/`ROUND`, scratch common variables VC190-VC199, a rolling nonce and an `M00` prompt. None has run on a physical control. Commission each control with verification disabled (bench Offset Loader, no motion) and correct the spellings in the dialect layer before enabling verification; the spec's statement that Okuma statement spellings are confirmed on the control stands until then.

### TV Dashboard

- **OD-021 - Kiosk target:** Hosting is resolved to the LAN-only Server; default refresh is configurable at 15 seconds and failed refresh retains the last snapshot with an offline banner. Choose authentication, browser/kiosk management, screen resolutions, viewing distance, shop-floor current-job lifecycle, local-date urgency semantics, and offline-display telemetry.

### E-Ink package and protocol

- **OD-022 - Package publication:** Partially resolved: the current Windows Edit Mode holder publishes a caller-named immutable revision for an assigned Batch Operation; Machine/Case/Batch/Operation metadata is snapshotted and a correction creates another revision. Define a distinct preparer/approver role or approval UI, audit, revision naming/ordering policy, and whether reassignment should require explicit republish confirmation.
- **OD-023 - Package format:** Partially resolved: schema v7 stores immutable snapshot/file metadata, asset roles, safe logical/storage-relative paths, stable file IDs, lengths, media types, timestamps/order, and SHA-256. Generation supports in-folder preview, allow-listed NC/text sources, JSON tool table/offsets, and UTF-8 instructions with configurable limits; reads re-verify bytes. Define signatures, additional formats/encoding, compression, range/resume, device staging/activation, rollback, backup inclusion, superseded-revision access, and retention/garbage collection.
- **OD-024 - Rendering boundary:** Structured JSON is the implemented v1 Server package/read baseline. The physical-firmware simulator now mirrors the separate approved status compatibility adapter instead of implying that package views exist on the real tablet. The physical layout renders local text, uses fixed three-row tool pages, and does not implement images; its official tool-row source is not yet connected. Decide whether final firmware consumes the v1 Machine/package projections unchanged or keeps a compatible adapter, and define package-to-tool mapping, bitmap/fonts, pagination, localization, Unicode/RTL, and compatibility window.
- **OD-034 - Color product name versus commissioned monochrome profile:** The integrated product baseline still names a unified Color E-Ink tablet, while `firmware/esp32-eink-mvp/include/hardware_config.h` selects the real TRMNL 7.5-inch OG 800×480 monochrome UC8179 panel. The firmware simulator now follows the real monochrome profile. Decide before multi-device purchase whether monochrome becomes the product baseline or a later color panel replaces it; keep rendering profile-driven and preserve text/symbol status cues in either case.
- **OD-025 - Telemetry and tablet-originated events:** Partially resolved: schema v49 supplies the shared append-only workflow-event storage and event-driven tablet projection, including migration of prior tablet event rows. `SEND_TO_QC` remains the only tablet-originated operational command. The implemented schema-v54 POST route requires an enabled path-matched TabletID, takes no target/time fields, resolves the bound Machine and current `IN_SETUP_RUN` Production Run on the Server, uses Server UTC, changes only the tablet workflow projection to `IN_QC`, and returns the original event on sequential or concurrent retries. Firmware physical-button binding, fresh-state eligibility, single-wake submission guard, follow-up refresh, and temporary confirmation rendering are compiled; physical verification is pending. Define ingestion policy for the other event sources, history retention/read access, hardware-range validation, and battery percentage calibration.
- **OD-026 - Device lifecycle:** Resolved for MVP: an active editor can register a spare or Machine-bound tablet, allocate its TabletID, map an optional MAC, permit one enabled E-Ink binding per Machine, and rebind/enable/disable it. Schema v62 clears legacy E-Ink credential hashes. Tablet authentication is deliberately excluded; define physical reassignment and lost-device/cached-data procedures without inventing a hidden credential.
- **OD-027 - Time/sync:** Partially resolved for Server configuration: time-zone ID, workdays, one shift window, poll interval, retry attempts/backoff, and revision are configurable/readable. Setup states no longer perform automatic polling. Canonical post-QA `READY_FOR_PRODUCTION` requests a configurable 60-second timer wake, while `BLOCKED`/`UNKNOWN` retain a 120-second fallback. Firmware logs UTC only when already valid and does not yet consume Server time configuration or enforce the mandatory workday/shift gate. Clock/NTP/RTC, zone portability/DST/holidays/exceptions, multiple windows, jitter, stale thresholds, and clock-loss behavior remain required before production automatic polling.
- **OD-036 - Readiness wording versus tablet workflow:** Resolved for the current implementation by retaining distinct canonical tokens. Tablet `READY_FOR_SETUP` is the awake pre-setup/setup-operator state. Tablet `READY_FOR_PRODUCTION` is emitted only after `QC_PASS` and selects the post-QA 60-second policy. The separate planning-readiness label “Ready for Production” must not be used as an ambiguous firmware policy key. If product wording later makes both screens visually identical, the canonical tokens must remain distinct.

### E-Ink hardware and interaction

- **OD-028 - Hardware selection:** Final MCU, panel/controller/size/resolution/orientation/colors/refresh behavior, AA chemistry and power topology, regulator/brownout, SD, USB service access, mount, and environmental rating.
- **OD-029 - Input contradiction:** Partially resolved for the production screen: the first firmware maps active-low short D1/GPIO2 to Refresh, a provisional 1.2-second D1 hold to Service/Debug, D2/GPIO3 to Previous Tool Page, short D4/GPIO5 to Next Tool Page, and a 1.2-second D4 hold to the deliberate `SEND_TO_QC` gesture. It debounces, rejects multi-button/unreleased input, logs the action, and uses the long hold plus post-send notice as the initial confirmation pattern. Final enclosure labels/hold ergonomics and physical behavior remain unverified. The concept also requires checkboxes, five local statuses, free-text comments, broader Back/Next, and revision-cleanup prompts; choose touch/additional controls/text-entry behavior and map those remaining interactions.
- **OD-030 - Local data:** Define persistence across battery replacement, annotation migration/clear behavior on revision, reassignment behavior, filesystem/capacity/wear/corruption protection, history limit, and encryption.
- **OD-031 - Firmware update:** Define safe update/service procedure if OTA remains deferred.
- **OD-032 - Measured acceptance:** Define numeric battery-life/current, wake/download/refresh time, readability distance/lighting/temperature, ghosting, storage, failure recovery, and one-week pilot success thresholds.
- **OD-033 - Source version:** Correct or confirm the E-Ink concept's v0.1 title versus v0.3 footer.

## Production Batch cancellation implementation record

Implemented the guarded Windows **Cancel production** action and optimistic Server endpoint. The atomic repository mutation cancels the selected Batch execution graph, resets modern/legacy Done projections to zero, closes active execution intervals, removes Machine assignments and material reservations, compacts backlogs, recomputes linked Orders, and preserves immutable cycle/workflow evidence plus a structured cancellation event. Single-Batch cancellation fails closed for a coupled multi-Batch Production Run. API, client, persistence, lifecycle, active-board/dashboard/material-report filtering, and regression tests are aligned.

## Task 8 implementation record - generic resources and manual-offset pilot

Schema v65 and the Server/Windows vertical slice retain specialized Machines; add data-managed Skills, Employee mappings, Workstation types/instances, External Resources, generic Operation requirements, provisional/actual assignment history, external send/return history, and Machine package capability. Windows Setup now exposes the five resource master-data areas under **Resource Types & Skills**, including persisted Employee-to-Skill assignment. The deterministic allocator handles simultaneous Workstation/Employee demand, backward preparation, forward successors, fixed/actual reservations, request pins, load projection, external lead/buffer semantics, and post-schedule delivery risk.

Production Package creation now exposes explicit `Manual / Dummy Tool Offsets`; existing 10/14/15 Machine records are enabled through migration data. NC-template verification, CNC challenge/response, exact binding, and Offset Loader authorization are unchanged.

Deployment work remains: configure real resources/Skills/calendars and process requirements, review pilot packages, and perform bounded no-motion commissioning on Machines 10/14/15. Detailed Windows CRUD screens for every new master and a persisted whole-factory recalculation command remain follow-on UX work; the APIs, schema, pure allocator, and package choice are implemented foundations.

## Tool preparation implementation record - schema v79

Implemented the Tool Room's editable tool table (2026-09-24). Server: `SchemaV79ToolPreparationMigration` (`machines.tool_diameter_offset_kind`, append-only `tool_preparations`/`tool_preparation_tools`/`tool_preparation_components`, `production_packages.tool_preparation_id`, `production_package_artifacts` rebuilt for `TOOL_OFFSETS`/`TOOL_OFFSET_PROGRAM`), `ToolPreparationValidator`/`ToolPreparationService`/`SqliteToolPreparationRepository`, `GET|PUT /api/v1/batch-operations/{operationId}/tool-preparation` (client identity headers, no Edit Mode, optimistic `expectedVersion`), the Machine field `toolDiameterOffsetKind`, `NcDialect.ToolOffsetLines`/`ToolOffsetProgram` per control, and Production Package generation that requires the measurements for `MEASURED` packages, embeds `tool-offsets/tool-offsets.json`, writes the offsets into the Offset Loader or a separate program, records the version in the manifest and treats a newer version as staleness. Windows client: `ToolPreparationWindow` (rows grid, shape/dimension fields, components grid, `ToolShapePreview` schematic, save/reload with conflict handling), `PreparationQueueViewModel.OpenToolTableAsync` routing, the Setup field, and Hebrew/Russian catalog entries. Tests cover the migration, validation, versions/conflicts, dialect syntax, package requirement/staleness/offset kind, the view models and the geometry. Physical acceptance of the written offsets on Machines 10/14/15 (a no-motion dry run reading the offset pages after the loader) remains a commissioning step. Follow-up (2026-09-24): the readiness context reader projects the latest preparation into the Tool Offsets component (`READY` when every required tool of the current Tool Table is measured, otherwise `MISSING` with the count; a recorded confirmation still counts); the NC viewer applies the Tool Room table to the program's tools through the engine's own tool-table validation (opened with the Batch Operation from the queues and the Planning Board); and the preview no longer invents a holder (only the cutter hangs from the gauge line, with the measured length as an empty axis). Follow-up (2026-09-24, later): the viewer's tools now come from the Operation's released Tool Table whenever the opened release has one (`NcViewerOperationToolTable`: every released row, the Tool Room's shapes and measurements when the Batch Operation is known, the catalog's released rows otherwise, e.g. from the Case tab's release history), and rows without a Tool Room shape are read by the engine adapter's `toolDefinitionsFromRows` (`NcEngineRuntime.InferToolsFromDescriptions`) instead of the program's tool comments; the status bar names the Tool Table revision and Tool Room version.

## Canonical format audit and verification macro packages (2026-09-24)

Audit of the canonical `[[MEIMAD:...]]` implementation (`NcPackagePlaceholderSchema`, `NcPackageTemplateTransformer`, `ProductionPackageService`, `NcDialects`) against the postprocessor specification and the package contract. Confirmed: placeholder grammar and multiplicity, the standalone hook before the first executable block, the standalone event context, the required ordered cycle pair, active verification code refused in templates, identity resolution to six-digit numbers, the dialect-specific hook/context/cycle/Offset Loader syntax, and the verification-disabled path with no active hook and no loader. Two gaps were closed: (1) the cycle markers expanded to part-counting events only with Server Verification enabled, so a connected Machine without verification counted nothing - part counting now follows the Machine's enabled DPRNT connection (`ProductionPackagePartCounting` in the build context; configured event-sequence variable, else the dialect default; manifest fields `partCounting*`); (2) the generated Offset Loader carried no printed correlation - it now prints its own `MEIMAD/V/2/CONTEXT/.../OFFSETRELEASE/<token>` line before the challenge call (`POPEN`/`PCLOS` on FANUC and Mazak, `PUT`/`WRITE C` on Okuma). The protected verification subprograms are generated per Machine in the dialect layer (`NcVerificationMacroGenerator`, `GET /api/v1/machines/{machineId}/verification-macros`, zip or JSON); the Haas rendering reproduces the commissioned V10 macros byte for byte, FANUC/Mazak/Okuma renderings are recorded as OD-036 pending commissioning. The opt-in test tool `VerificationMacroExportTool` (`MEIMAD_EXPORT_MACROS_TO`) writes one zip per live CNC Machine from the Server's configurations.

## NC viewer stock and material removal (2026-09-24)

Implemented the stock definition (box, cylinder, STL; relative to a work offset), the 2.5D/turning material removal simulation in a Web Worker with ON/OFF and resolution controls, playback from the selected NC row, single-tool playback that skips the other tools' moves, a progress bar, the `<program>.stock.json` sidecar and the machined-stock STL hand-over into the next Operation's `Stock` folder. Vendored viewer files stay unmodified: a wrapper module captures the upstream scene. Known limits recorded for a later scope decision: the lathe stock is two radius profiles (no internal undercuts); an STL stock is rasterized to the top surface of the columns; rapid moves never cut and gouges are not flagged. Follow-up (2026-09-24, later): tilted-axis cutting is simulated. The mill stock became a grid of material columns (topmost slab plus undercut slabs per column, `subtractColumn`), a vertical tool keeps the exact 2.5D capsule cut, and a tilted tool (3+2 positions, simultaneous rotary moves with per-point axes from `toolAxisInWorkpiece` over the rotary sweep, cutting from below) is stamped along its motion as convex parts (cylinder body of the tool table length or the program's tool length offset, ball, cone, stepped bull nose) whose column intervals are subtracted; the stamp spacing keeps scallops under an eighth of a cell on the tool's side and half a cell along its axis, and undercut slabs are rendered as cell boxes. The simulation is a planning aid in the Case Working Folder and never reaches the Server.

## Tool catalog and enlarged tool type list - schema v80 (2026-09-25)

Implemented the factory tool catalog and the shared tool type list. Server: `SchemaV80ToolCatalogMigration` (`catalog_tools` with a never-reused internal number/code, `catalog_tool_external_ids`; `tool_preparation_tools` rebuilt with the enlarged type check, `hand` and a restrictive `catalog_tool_id`, its components table rebuilt with it under foreign-key enforcement off plus `foreign_key_check`; `DatabaseMigrator.MigrateAsync(upToVersion)` lets a test prepare v79 data first), `ToolShapeTypes` with families (milling: flat, ball, bull-nose, chamfer, face, slot/disc, T-slot, thread, dovetail, lollipop and engraving cutters; hole making: drill, spot drill, center drill, tap, reamer, boring head, countersink, counterbore; ISO turning: external and internal turning, external, internal and face grooving, parting, external and internal threading; probe; other), `ToolHands`, the turning/threading dimension keys, `CatalogToolValidator`/`ToolCatalogService`/`SqliteToolCatalogRepository`, `GET|POST /api/v1/tool-catalog`, `GET|PUT|DELETE /api/v1/tool-catalog/{toolId}`, `GET /api/v1/tool-catalog/types` (identity headers, no Edit Mode, optimistic version, `409 tool_catalog_in_use`), and `hand`/`catalogToolId` on the tool preparation. Windows client: the Tool Catalog tab (`ToolCatalogViewModel`, `ToolCatalogView`: search by internal id, name or external id, type filter, editor with type-specific dimension fields, preview, ISO codes, external ids, active flag), the catalog picker in the Tool Room window, `ToolPreparationCatalog`/`ToolDimensionSet` shared by both, preview parts for the new types, and the NC viewer maps (`NcViewerOperationToolTable.MillType/LatheType`, hand and cutting width for lathes). Tests cover the migration with v79 rows preserved, the catalog API (codes, search, versions, validation, delete protection, preparation link), the view models, the geometry and the viewer maps. Scope note: the catalog describes tools and holds no quantities; tool inventory stays deferred (rule 26). The ISO insert and holder codes are free text attributes; parsing them into dimensions is a later step.

## Kitaron Order price and started-production protection (2026-09-25)

Two production findings fixed together. (1) Every Kitaron Order arrived with price 0: the canonical Order price read `TSubOrder.PriceInCurr`, which the commissioned database sets on 35 of 20,455 rows; the invoiced unit price is `CostShkalim` (NIS; `CostDolar` is its USD twin, `FullCost` the unit price after the order discount, `InvSum`/`InvSumDol` confirm it per unit). The reader now prefers `CostShkalim`, stores a zero source value as no price (`NULLIF`), the connector mapping names the field `Unit Price (NIS)`, and the Windows Case view labels the column `Price (NIS)`. (2) The periodic synchronization had been failing since the morning of 2026-09-25 with `PlanningDeletionBlockedException`: a linked Order whose Kitaron status changed to cancelled had a Batch with a started Production Run, and the update path deleted the Order's Batches without the locked-run guard the obsolete-Order path already had. `SqliteKitaronSyncRepository.ResolveOrderAsync` now keeps such a Batch (counted in the result message and as a warning) and applies the Order's new facts; `KitaronConnectionApiTests` covers both the price column choice and the kept Batch.

## Kitaron connector audit (2026-09-25)

Read-only comparison of the live Server database (schema v80, after installer 0.1.134) with the Kitaron database `KitaronData229`, the same queries the connector runs.

- **Orders: complete and exact.** 18,958 canonical `TSubOrder` rows are in the connector's part scope; 120 are skipped by rule (quantity 0 or no supply date); all 18,838 valid rows exist in Meimad under the current link key and match on reference, quantity (net of partial supply), supply date, status and price (0 mismatches). 29 current-key Orders have no canonical row today (rows that became invalid or `work:` rows of the planning view) and are retained by design.
- **Prices: fixed since 0.1.134.** 18,322 Orders carry the NIS unit price (`CostShkalim`), 1,902 Kitaron rows carry no price (blank), 10 manual Orders keep their entered value.
- **Defect found - stale Orders from the first connector version.** 1,365 Order links use the old record-id-only key (before the `<part>\u001f<RecordID>` key of schema v66). The connector never refreshes them because their parts are outside today's part scope (no planning-view work, no BOM tree), and 1,132 of them are closed in Kitaron but still show `active` in Meimad (25,475 units of phantom demand; `kitaron_status` is null on all of them). No Batch refers to them. Open decision **OD-037**: either (a) migrate the old links to the current key and let the connector refresh linked rows by `RecordID` even when their part is out of scope (status, quantity, date, price), or (b) mark the old-key Orders `inactive`/complete once. Recommended: (a), because it also keeps future out-of-scope rows honest; it changes the documented "parts outside the scope are frozen" rule and needs the spec updated with it.
- **Route operations: imported from the wrong source.** `case_operations` come from the planning view `VQWorkPlanningForStationF4`, which lists only open work (285 rows, 114 parts today), so 6,200 of 6,374 linked Cases have no operations, 60 Cases with active Kitaron demand have none, and the imported names carry the Kitaron action type prefix (`ייצור` + line break) while Machine Type, setup and cycle stay empty (mapping rows disabled/blocked). Kitaron's route master is `TDetailDirectionList` (part, revision → `DirectionHeaderID`) → `TDetailDirectionHeader` → `TDirection` (`ActionNumber`, `OperationDescription`, `StationID` → `TStation.Station`/`StationType`, `TimeProduction` and `DirectionTime` in minutes, filled on 220 / 127 of 58,569 rows): 4,316 parts have a route, 4,191 of the linked Cases, 13.5 rows per part on average, most of them non-machining stations (inspection, packing, deburring, engineering). Open decision **OD-038**: import the route master with (1) a station → Meimad Machine Type / Workstation type lookup table maintained in Setup, (2) a rule for which stations become Case Operations (machining stations only, or every station as a Workstation step), (3) minutes → seconds for the few filled times, (4) `OperationDescription` as the name. The current planning-view import stays until then. **Resolved 2026-09-25 with option 3** (every route step; see the record below).
- **Material orders: 633 of 9,706 purchase lines are not imported.** 566 rows have no material item (`RowMaterialID` null: services and free-text purchases) and 101 have quantity 0; the connector skips them with a warning. Recommended: import item-less rows with the `Information` text as the material name and a `no item` flag; keep skipping zero quantities.
- **Batches and allocations: consistent.** 22 Batches, 45 allocations, no orphan allocation, one allocation on the history-only Order retained for a locked run, no allocation on a cancelled Order.
- **Components: complete.** 2,067 BOM edges, all linked.
- **Warnings (562 per run) are not persisted**; they are the parent-Case operation skips, invalid material rows and invalid order rows. Recommended: keep the last run's warning list in `kitaron_sync_state` (or a small table) so the setup page can show them.

The audit scripts (read-only, dotnet file-based apps for SQL Server and SQLite) lived in the session scratchpad; the database report generator produced `docs/database-structure-report.md` from a live schema dump.

## Kitaron route master import and auxiliary steps on the Timeline - schema v81 (2026-09-25)

Implements OD-038 as option 3, "every route step":

- **Route source.** `SqlServerKitaronSourceReader` reads the complete route master (`RouteStepQuery`: `TDetailDirectionList` → `TDetails`, `TDetailDirectionHeader`, `TDirection`, `TOperation`) and the station statistics (`StationQuery`: `TStation` with route, `WorkPlanning` and `SupplierID` row counts). `KitaronRoutePlanner` (pure, unit-tested) chooses one header per part, orders and de-duplicates the steps, and classifies them by the station decision; `KitaronSyncService.BuildPlan` merges the result with the planning-view rows, which remain the source only for parts without a route.
- **Station lookup.** `kitaron_stations` is discovered by every synchronization (`SqliteKitaronStationRepository.UpsertDiscoveredAsync`) and decided through `GET/PUT /api/v1/kitaron/stations` from the Windows client (**Setup → Kitaron Stations**, `KitaronStationsViewModel`): role, Machine Type, Workstation type or External Resource, default minutes per part/batch, capacity. Undecided stations import nothing and are counted (`route_steps_skipped`).
- **Requirements.** Auxiliary steps become `operation_resource_requirements` rows with `name`, `step_number` and `duration_per_unit_seconds` (v81), linked as `operation_requirement` in `kitaron_sync_links`, predecessor-chained per side of the machining operation, deactivated when the route drops them, suppressed when a planner deletes them (`SqliteResourceMasterDataRepository.SuppressKitaronRequirementAsync`, also used by Case Operation deletion). `PATCH`/`DELETE /api/v1/resource-requirements/{id}` and the Case workspace panel (`OperationRequirementsViewModel`) let the planner edit them.
- **Timeline.** `TimelineAuxiliaryProjector` feeds the existing `AutomaticResourceScheduler` with every scheduled Batch Operation's active requirements anchored to the calculated Machine start/finish, Workstation calendars, Employee Skills (`employee_skills`) and External Resource lead times, and returns resource lanes, per-Batch predicted completion and the `auxiliary_*`/`delivery_at_risk` conflicts. Pins (`PUT/DELETE /api/v1/timeline/auxiliary-pins`) are stored as `PINNED` resource schedule work; the projection is never persisted. The Windows Timeline draws the lanes below the Machines with pin/unpin in the block menu.
- **Tests.** `KitaronRoutePlannerTests`, `KitaronRouteSyncTests` (discovery, decisions, import, idempotence, deletion/suppression, deactivation/reactivation, operation deletion), `KitaronRouteImportMigrationTests`, `TimelineAuxiliaryProjectionTests` (before/after placement and sizing, missing configuration, external lead time and delivery risk, pins), client `KitaronStationsViewModelTests`, `OperationRequirementsViewModelTests` and Timeline resource lane tests.
- **Deliberately not done.** The connector page on the Server still only reports counts; station decisions need the Windows client. Steps whose Machine anchor lies outside the requested horizon are not placed. Employee requirements are placed only through Skills; per-Machine qualifications stay with setup/QA workers. The planning-view operation import is untouched for parts without a route (to be retired once every linked part has a route).

## Kitaron route sequence and route order - fixes after 0.1.136 (2026-09-25)

A planner reported that imported route operations arrive as `INDEPENDENT`. A read-only check of the live database and of Kitaron found four defects in the 0.1.136 route import:

- **Operations were independent.** All 5,521 Kitaron-owned Case Operations had `INDEPENDENT` and no predecessor. The sync now makes every imported route a sequence (first `INDEPENDENT`, the rest `SEQUENTIAL` after the previous route operation), applied on creation and on a Kitaron change; the operation's place in the route is part of its source hash, so the first sync after the upgrade applies the sequence to all existing imported operations. Planner `PARALLEL_CAPABLE`/`LOCKED_SIMULTANEOUS` choices are never replaced; a cycle-closing link is skipped.
- **The route master was ordered by `NumOrder`.** `NumOrder` is not Kitaron's process order: it disagrees with the `ActionNumber` order in 4,157 of 4,335 routes (16W1120-13: purchasing step 10 has `NumOrder` 27, setup inspection 80 has 43). Steps are now ordered by `ActionNumber`, so the anchors, directions and chains of auxiliary steps follow the real route. Operations that arrived after a Case already had operations had been appended at the end (16W1120-13 read 30, 40, 90, 100, 110, 50, 60, 70, 120); the sync now keeps the operations of Kitaron-linked Cases in operation-number order.
- **Empty Kitaron values erased planner values.** Because the new route hash differed from the planning-view hash, the 0.1.136 syncs that followed the station decisions (2026-09-25 13:50 to 14:06 UTC) rewrote every imported operation of a newly routed part and set setup and cycle times that planners had typed to empty, and the batch-time propagation then emptied not-started Batch Operations. Comparison with the 2026-09-15 backup: 30P450025601-001 OP30 (14400/1200 s) and OP50 (10800/1380 s), 30P450197800-001 OP40 (18000/1200 s) and OP60 (7200/900 s), 4341-1271-001 OP50 (14400/600 s), OP70 (14400/180 s) and OP90 (14400/180 s), TNK2102301-001 OP30 (14400/900 s) and OP50 (14400/600 s), plus the not-started Batch Operations of 30P450025601-001 batches 1 and 123. 30P450190103-001 OP120 (7200/1200 s) and OP140 (3600/600 s) keep their values in their started Batch Operations. The not-started Batch Operations of 16W1120-13, 16W1120-14, 16W121-21 and 16W121-22 (batch 10, created 2026-09-16) and the Case Operation times behind them were entered after the last backup and cannot be recovered. The sync now never replaces a Meimad value with an empty Kitaron value (setup, cycle, Machine Type) and copies a Case Operation time into not-started Batch Operations only when it exists. No automatic restore was made; the planner re-enters the values.
- **Planner edits of imported steps were reverted every sync**, and a middle step or operation could not be deleted because its successor depended on it. Imported steps now take Kitaron facts only when Kitaron changes them; deleting a step or an imported operation closes the chain over it, and the sync keeps that link (a step left out passes its predecessor on).

Tests: `KitaronRoutePlannerTests.The_route_follows_the_operation_numbers_not_the_kitaron_NumOrder`, and in `KitaronRouteSyncTests` the sequence with planner values kept, the upgrade from 0.1.136 state, deletion re-linking, and planner edits of imported steps.

Open, not in this change: auxiliary steps placed after a machining operation (for example a setup inspection between OP30 and OP90) do not yet hold back the next machining operation on the Timeline; the Machine calculation runs first and the steps are placed around it. Kitaron lists instruction rows on the generic production station (16W1120-13 OP50/60/70/120, "FOR LINE DATA SEE ..."), so with that station decided as Machine they import as operations.

## Kitaron station remapping updates the route - 2026-09-25

A planner re-decided the generic production station ייצור from Machine to Ignore and the operation lists did not change: the synchronization never removed an imported operation, so 2,334 Kitaron-owned operations the route no longer produced stayed, and 6,807 steps stayed as inactive rows. Now:

- For Cases whose route comes from the route master (`KitaronSyncPlan.RoutePartNumbers`), a Kitaron-owned operation the current route and decisions no longer produce is removed with its link and default Manufacturing Program; its Kitaron-owned followers are re-linked to its predecessor. It is kept, and named in the result message, while a Production Batch or Run, a G-code/process/tool-table release, a Manufacturing Program output, a remaining step, a locked group, or a following operation the synchronization does not own uses it. Kept and re-linked operations get the link markers `not-produced`/`relinked`, so the sequence is applied in full when the route produces them again. Planning-view operations are never removed this way.
- A Kitaron-owned step the route no longer produces is removed (its followers re-linked); only a pinned step is deactivated. This replaces the earlier deactivate-and-reactivate rule.
- Saving a station decision calls `KitaronSyncService.RequestRun`; the periodic service wakes and runs the synchronization within seconds when the connector is enabled and the mapping Ready, and runs once more if a synchronization from the Server page was already running.
- Live data at the time: 14 of the 2,334 operations are held by waiting or started Batches (16W1120-13/-14 OP30/40/90, 16W121-21 OP30/40/70, 16W121-22 OP30, 1199C423-001 OP20/40/60, 30P450190103-001 OP140) and 2 by releases; the rest are removed by the first synchronization of this version.

Tests: `KitaronRouteSyncTests.Remapping_a_station_removes_its_operations_and_steps_unless_production_data_holds_them`, `Planning_view_operations_are_not_removed_when_the_open_work_changes`, `A_station_decision_asks_the_server_to_synchronize_now`, and the updated `A_deleted_kitaron_step_stays_out_and_a_step_missing_from_the_route_is_removed`.

## Kitaron operation-list and batch authority - 2026-09-25 (schema v82)

The owner decided: the operation lists of Kitaron and Meimad Planner must be identical; operation data may be edited in Meimad but operations cannot be added or deleted; planner-added operations in Kitaron-managed Cases are removed; all Production Batches, Machine backlogs and assignments are removed once ("I will replan it all after"); batches are imported from Kitaron with an automatic material check; station remapping or parameter changes update the operation list.

- **Migration v82** archives to `wipe_v82_archive` and deletes the planning/execution graph (validated on a copy of the live database: 1,526 rows archived, no foreign-key violations, integrity ok, 80 triggers restored), drops `kitaron_suppressed_operations`, adds `cases.kitaron_route_locked`, `kitaron_batch_material_checks`, and the `production_batch` link entity.
- **Source.** `SqlServerKitaronSourceReader` reads open work orders (`TRootCard` joined to open, not stopped `TSubOrder`), their order links (`TOrderLinkRoot`) and material lines (`TBOMWithdrawalByRoot`). At the time, 195 work orders were open and 49 had material lines; none had stock issued.
- **Rules.** See the functional specification section "Kitaron operation-list and batch authority". A run created only by the assignment trigger does not count as started; started means a non-waiting batch, a started Batch Operation, a structure-locked run, or a package.
- **Superseded.** The deletion/suppression behavior recorded in "Kitaron station remapping updates the route" and in OD-038 option 3 is replaced: imported operations and steps can no longer be deleted by a planner.
- **Tests.** `KitaronBatchSyncTests`, `KitaronBatchPlanTests`, `KitaronBatchAuthorityMigrationTests`, and updated `KitaronRouteSyncTests`, `KitaronConnectionApiTests`, `PlanningDeletionApiTests`.
- **Open.** Material is judged from Kitaron's own calculation rows; parts without them show `unknown` rather than a Meimad-computed stock balance, because ERP remains the stock authority.

### Batch allocation by due date - 2026-09-26

Work order 41043 (56 pieces) was allocated entirely to order line 40449 (demand 8), because every open work order in Kitaron links to exactly one order line (195 of 195) with the whole quantity. The import now ignores that single link for allocation: it fills the part's active Orders by earliest due date, each up to its open demand not taken by an older work order, puts the rest in stock, and keeps the cutting reserve as scrap allowance. A batch still unplanned re-applies at the next synchronization because its allocation hash changes. Tests: `KitaronBatchPlanTests`.

### Material Orders page - 2026-09-26

The owner reported the material order lists empty. The weekly material order report lists material for Production Batches due next week, so it was empty after the v82 reset; the 9,073 imported Kitaron purchase lines had no screen. Added `GET /api/v1/kitaron/material-orders` and the read-only Material Orders tab with Kitaron's status text and a derived delivery status. Live data at the time: 5,067 received, 2,766 closed short, 1,020 late, 219 open, 1 partially received. Many late lines are old lines never closed in Kitaron; the page reports them as Kitaron has them. Tests: `KitaronMaterialOrderApiTests`, `MaterialOrdersViewModelTests`.

### Batch import audit and Kitaron batch authority - 2026-09-26

Audit of the live data against Kitaron (read-only): 357 work orders had an open route; 95 were imported. 162 were skipped because their sales-order line is closed, which matches Kitaron itself (system setting 242 = NO keeps closed lines out of its planning). 49 wait because their Case has no operations (the route's stations are undecided or Ignore, for example ייצור, or the part has no route). 48 are work orders on parent (assembly) Cases, which cannot own batches. 3 (41452, 41453, 41511) were missed because their parts were outside the planning view and the BOM trees; the fix makes every open work order's part a synchronized Case. The owner then decided that planner-created batches are removed, batches come only from Kitaron, each batch is assigned its material order, and batches carry a pending / released state (schema v83). Open decision: whether assembly work orders on parent Cases should become batches, which needs parent Cases to own operations. Tests: `KitaronBatchSyncTests`, `KitaronBatchPlanTests`, `KitaronMaterialOrderApiTests`.

### Work Order naming, assembly operations, station tabs, network folder - 2026-09-26

Owner decisions: Work Order = batch (client wording only; identifiers unchanged); assemblies carry their own operations and Work Orders (removes the parent-Case restrictions in the Case repository, component service, Work Order service, route planner, planning-view import, component sync and the Work Order import blocker); the Planning Board adds Internal stations and External operations tabs from the Timeline resource lanes; all Case links are stored relative to a network folder defined in Setup (schema v83 `network_folder_settings`). v83 was validated on a copy of the live database (version 83, no foreign-key violations, integrity ok). Russian translations of the renamed texts were derived by term substitution and may need grammar review. Tests: `NetworkFolderPathTests`, updated `CaseComponentApiTests`, `KitaronRoutePlannerTests`, `KitaronConnectionApiTests`.

### Work Orders without operations - 2026-09-26

Work order 41508 (16W1120-13) was not imported: every machining step of its route is at station ייצור, decided as Ignore, so the Case had no operations and the import waited (53 of 195 open work orders). The owner decided to import such work orders anyway and keep them pending while they have no operations. The import no longer requires Case Operations; a Work Order with none cannot be released; the synchronization instantiates the operations once the route produces them. Test: `KitaronBatchSyncTests.A_work_order_without_operations_is_imported_pending_and_gets_them_from_the_route`.

### Network folder with a drive-root alias - 2026-09-26

The owner's share is mapped as a whole drive (J: = \\192.168.0.240\data). A drive-root alias such as J:\ did not match J:\customers files\... because the comparison kept the trailing separator; aliases now compare without it, so the example stores `customers files\DPD\...`. Case browse dialogs open at the Case working folder or the network folder; G-code and tool-table selection stays unrestricted. Test: `NetworkFolderPathTests.A_drive_mapped_to_the_share_root_is_stored_relative`.

### Notes to production skipped - 2026-09-26

Investigation (read-only): ייצור (station 38) carries 2,075 machining steps in the route master and never records a specific machine; `TSubRootCardSub` is empty and `TChartsOperation` covers 18 of 3,625 ייצור steps with MACHINE = ייצור. Decision: skip route steps whose operation name is "הערה לייצור" (574 steps). Other note names exist and are still imported: הערה לביקורת (431), הערה לסימון (144), הערה (about 90), הערה להרכבה (46), NOTE / NOTE FOR CRITICALITY PART (23) and a few more. Test: `KitaronRoutePlannerTests.Notes_to_production_are_not_imported_as_operations`.

### Production Note operations and manual material-order verification - 2026-09-26

The owner chose to keep "הערה לייצור" steps as operations marked "Production Note" instead of skipping them, and to verify Work Order material orders by hand after the audit showed Kitaron has no purchase-to-work-order link. Implemented: `Domain.CaseOperations.ProductionNote`, route planner and route-sequence handling, exclusion from the Planning Board, Machine assignment, Timeline and the release count; schema v84 `work_order_material_orders`, `/api/v1/batches/{id}/material-orders` endpoints, client panel. Tests: `KitaronRoutePlannerTests.Notes_to_production_become_production_note_operations_outside_the_sequence`, `KitaronBatchSyncTests.A_production_note_stays_in_the_work_order_but_off_the_board_and_off_machines`, updated `KitaronBatchSyncTests` and `KitaronBatchPlanTests`.

### Planning Board pool shows only released Machine work - 2026-09-27

Owner request: the Planning Board / Machines / Unassigned list shows only Machine-related operations of Work Orders with the released status. Live data (read-only) at the time: 481 unassigned operations, 473 of them on pending Work Orders; 2 of 199 Work Orders released. 151 operations have no Machine Type. All come from the Kitaron general production stations ייצור and PRODUCTION, and all have no time. They are specification text such as "MACHINE FINISH PER PS551170" (75), "FOR CONTOUR SEE REPORT 16PR006" and "SEE NOTE 8". "Machine related" is therefore implemented as having a non-blank Machine Type. Production Notes stay excluded. A Machine Type no Machine accepts still counts, because the cross-type override can place it.

**Open point:** a few untyped ייצור steps are real machining ("כרסם שלב 1", milling stage 1). They now need a Machine Type before they can be planned. If the owner wants untyped operations in the pool, `PlanningBoardPool.Admits` is the single place to change. Machine backlogs are not filtered. Implemented in `SqlitePlanningBoardRepository` / `PlanningBoardPool`; the client explains an unassigned operation that leaves the board and keeps it undoable. Tests: `PlanningBoardEnrichmentTests.Planning_board_pool_lists_only_machine_operations_of_released_work_orders`, `MachinePlanningBoardViewModelTests.Unassigned_operation_hidden_from_pool_is_explained_and_undoable`; board tests that expected pool rows now release their Work Order first.

### Cimatron cutter workbooks in the Tool Catalog - 2026-09-27

The Tool Catalog imports and exports Cimatron cutter workbooks (NC-Process > Cutters > Menu > Export / Import, XLS). The import previews before it saves and matches cutters by their `Cimatron` external id, then by name. The export fills Cimatron 2026's empty External Cutters workbook, embedded in the Server. Implemented in `CimatronCutterLibrary`, `CimatronCutterWorkbookWriter` and `CimatronToolTransferService`, plus `POST /api/v1/tool-catalog/import/cimatron` and `GET /api/v1/tool-catalog/export/cimatron` and the Tool Catalog buttons. It was merged to main through PR #15.

The review after the merge found that the import always set the type Cimatron describes, while the export writes several catalog types as one Cimatron cutter kind. An export and re-import therefore turned face mills, counterbores and boring heads into end mills, engravers into chamfer mills, T-slot mills into slot mills and spot drills into center drills. Fixed: `CimatronCutterLibrary.MergedToolType` keeps the existing type when it exports as the same technology, tip and taper as the cutter. Tests: `CimatronToolTransferApiTests` (four scenarios) and `A_reimport_keeps_catalog_types_Cimatron_folds_into_one_cutter_kind`.

**Not yet verified:** that Cimatron's Menu > Import reads an exported workbook needs a trial on the CAM PC. The tests only read the export back with the Server's own reader.

### Production Notes never block - 2026-09-27

Owner rule: a Production Note is a note only and must not affect the Timeline or block anything. Live data showed four ways notes still did:

1. Work Order operations kept the Machine Type they were launched with. On Case 16W1120-13 the planner marked the text steps as Production Note and typed OP30 Mill 3x and OP90 Mill 5x. Work Order 41508 kept all nine operations untyped in one chain (34 differing operations overall). So its notes sat on the Timeline as unassigned work, OP90 waited behind them, and its note OP120 was still on a Machine.
2. The Timeline drops notes, so an operation whose predecessor was a note got `dependency_snapshot_missing` and was blocked. Three Case Operations on 16W1120-14 and 16W121-21 follow a note.
3. Work Order and Order completion counted notes, which never complete.
4. A note left on a Machine at backlog position 0 would stop the next operation from starting (Start requires position 0).

Implemented:

- The Case Operation edit copies the Machine Type to not-started Batch Operations along with the times. The Kitaron synchronization does the same every run (`SynchronizeNotStartedBatchOperationMachineTypesAsync`), which heals existing Work Orders.
- `SqliteMachineAssignmentRepository.ReleaseProductionNoteAssignmentsAsync` takes not-started notes off their Machines and compacts the backlog as a manual unassignment does.
- `SqliteTimelineSourceRepository` resolves predecessors past notes.
- Batch status, cycle accounting and the Order lifecycle ignore notes.

Tests: `TimelineApiTests.Production_notes_in_a_chain_never_block_the_operations_that_follow_them`, `CaseOperationCreateApiTests.Classifying_a_case_operation_reaches_not_started_work_orders_and_takes_notes_off_machines`, `KitaronBatchSyncTests.A_synchronization_brings_the_case_classification_to_not_started_work_orders`, and a note added to `MachineOperationExecutionApiTests.Start_suspend_resume_and_finish_advance_machine_backlog`, whose Work Order still completes.

**Observed, not changed:** the production-run cycle accounting sets a finished Work Order to `completed`, while manual execution sets `complete` (`ProductionBatchValidator.CompleteStatus`). The Order roll-up in the cycle accounting checks for `completed`.

### Pending Work Orders follow the Case, released ones are frozen - 2026-09-27

Owner rule: a pending Work Order takes its operation list from the Case; releasing locks the list; returning to pending refreshes it.

Owner decisions on the open points:

- A released Work Order is frozen completely, times and Machine Type included. This replaces the v33 rule that Case time edits reach every not-started Batch Operation, and the Machine Type propagation added earlier the same day.
- Returning to pending is refused once production has started (`work_order_started`).

Live data at the time: 197 pending and 2 released Work Orders, none started. In pending Work Orders, 177 not-started operations differed from their Case and 171 Case Operations were missing. 41377 and 41506 are pending and each has an operation with a production package and a tool preparation.

Implemented in `SqliteWorkOrderRouteRefresh`, which updates rows in place so machine assignments, readiness inputs and tool preparations stay attached:

- **Not-started operations** take every Case Operation field. Numbers and positions follow the Case order through temporary values.
- **Missing Case Operations** are added.
- **Operations whose Case Operation is gone** are removed with their planning graph: assignment, planned legacy run, schedule pins, pauses and overrides. Machine backlogs are then compacted.
- **Kept as they are:** started operations, and operations that a production package, E-Ink package, bench session or locked run refers to.
- **Callers:** Case Operation create, update and delete; release (refresh, then freeze); unrelease (refresh); and every Kitaron synchronization for all pending Work Orders.
- **Kitaron sync changes:** it no longer deletes and re-instantiates the operations of a changed unplanned work order. A route operation Kitaron dropped takes pending copies with it, and a released Work Order keeps it (reason "Production Batch").
- **Removed:** `AppendToOpenBatchesAsync` and the not-started time and Machine Type synchronizers.

Tests: `WorkOrderRouteReleaseTests` (pending follows every change and keeps placement; release freezes and unrelease refreshes; a started Work Order cannot go back) and `KitaronRouteSyncTests.A_pending_work_order_follows_the_route_and_lets_a_removed_operation_go`. Rewritten for the new rule: `CaseServicePersistenceTests`, `CaseOperationCreateApiTests`, and the released Work Orders in `PlanningDeletionApiTests`, `KitaronRouteSyncTests` and `PlanningBoardEnrichmentTests`.

After the upgrade, the first Kitaron synchronization refreshes every pending Work Order. The two released ones (41508, 41512) stay as they are until they are set back to pending.

### Tool requirements for a period - 2026-09-27

Owner request: a list of the tools needed in a defined period, taken from the tool tables of the operations planned on the Timeline and organized in families by type, size and name. It takes the part material into account, and whether the same tool works on several machines or can migrate from machine to machine.

Owner decisions:

- A tool is the same when name and diameter match; the holder is ignored.
- Tools are counted separately per material group.
- The list is a client tab with Excel export.

**Data found (read-only):** 27 tool table releases (24 Cimatron `.TOOLS.mht`, 3 CSV) with 207 rows. The release stored only the tool name. The stored Cimatron reports also carry Dia, LENGTH, CUT, Shank and Holder, or, in a second template, Tool Diameter and Clear Length. Only 1 of 6,375 Cases has a material; 191 of 199 Kitaron work orders name a raw material whose description ("AL 7050-T7451…", "TI-6AL-4V…", "15-5PH…") classifies it.

**Implemented:**

- `ReleasedToolTableParser.ReadGeometryAsync` re-reads the immutable stored file. The service caches it per release.
- `MaterialGroups`, `ToolNaming` (identity key, size from the name, type from the name prefix) and `ToolRequirementCalculator` (interval partitioning; a free copy already in the Machine stays, otherwise the longest-free copy moves).
- `ToolRequirementService` with `SqliteToolRequirementSourceRepository`.
- `GET /api/v1/tool-requirements` and `/export`, with the `ToolRequirementWorkbookWriter` producing an `.xlsx` with shared strings.
- The client Tool Requirements tab.

A check of all 24 stored Cimatron reports gave sizes for every row, and the name prefixes typed every tool. Tests: `ToolRequirementCalculatorTests`, `ToolRequirementApiTests` (Timeline, tool tables, Kitaron material, two copies, missing tool table, Excel read back) and `ToolRequirementsViewModelTests`.

**Open points:**

- No transfer or presetting time is added between two uses of a migrating copy.
- A copy's holder is ignored, per the decision.

## User accounts replace Single Edit Mode (2026-09-27, schema v86)

**Owner decisions (2026-09-27):**

- Sign-in: user name and password; accounts live on the Server; an administrator creates users and resets passwords.
- Edit Mode: replaced by per-record checks. Users work in parallel; a change based on stale data is refused with who/when/what and advice; the Planning Board order is checked per Machine.
- Permissions: a fixed permission list; administrators create user types and tick permissions; a user can have several types. Initial types: QC, Programmer, Tool Room manager, Planning, Technologist; the built-in Administrator may do everything.
- Viewing: everyone signs in; TV dashboard and tablets are unchanged.

**Implemented:**

- `SchemaV86UserAccountsMigration`: `user_accounts`, `user_types`, `user_type_permissions`, `user_account_types`, `user_sessions`, `machine_backlog_changes`; the Administrator type and the five initial types. `edit_tokens` stays unused.
- `AccountService`/`SqliteAccountRepository`: PBKDF2-SHA256 passwords, 12-hour sliding sessions (token hash only), 5-failure/5-minute lockout, first-administrator bootstrap, last-administrator guard, versioned user and type edits.
- `SignInMiddleware` (401/403 for every `/api/` route except auth, installer, tablets, TV, the local Kitaron setup page, and loopback `GET /api/v1/machines…` for the upgrade script), `PlanningHttpSupport.TryAuthorize*` with the permission per endpoint, and `SignedInActor` in place of the Edit Mode checks. The Edit Mode endpoints, service, timeout worker, repository and options are removed.
- Conflicts: `EditConflictException` and `ConflictResponseMiddleware` answer `409 edit_conflict` with `conflict { resource, changedBy, changedAt, advice }` and add the same object to existing stale-version refusals, naming the last saver from the in-memory `ChangeJournal`. QC decisions on an already decided part, G-code releases with a stale `expectedLatestReleaseId`, and moves with a stale Machine `backlogStamp` are refused with explanations; a planner's own consecutive moves are allowed.
- Windows client: sign-in window (first administrator, temporary password change, voluntary change), header with the signed-in user, Sign in/Password/Sign out, per-screen rights from the account's permissions, administrator-only Users page (users, user types, permission checkboxes), a global conflict message, backlog stamps on moves and the expected release on G-code releases; he/ru texts.
- Also fixed on the way: a pooled SQLite connection left inside a transaction by a cancelled request is rolled back before reuse (the live log showed "cannot start a transaction within a transaction" on the preparation queues); the Planning Board shows a dropped card at once and reloads in the background (the drop request takes milliseconds; the full board read with its Timeline conflict calculation takes 0.6–1.2 s).

**Tests:** `AccountApiTests`, the conflict tests in `EInkApiTests`, `GCodeReleaseApiTests`, `MachineApiTests`, `CaseApiTests`, `SqliteDatabasePoolTests`; client `MainWindowViewModelTests`, `UserAdministrationViewModelTests`, `PlannerApiClientTests`, board drop tests. Existing API tests run as an administrator through the test-only `UseSignedInTestServer`; their former Edit Mode checks now check permissions.

**Open points:**

- Account changes are not yet in a separate audit history (the row keeps `updated_by`/`updated_at`).
- The change journal behind "changed by" for ordinary version conflicts is in memory and forgets on a Server restart.
- The Windows client does not keep the session across restarts; each start signs in.
- LAN transport security (HTTP) is unchanged (OD-017): passwords and tokens cross the factory LAN unencrypted.

## Kitaron push (2026-09-28, schema v87)

**Owner decisions (2026-09-28):** push `OperationQty`, `StartDateReal`, `FinishDateCalc`, `SetupTimeReal` ("maybe more") from a new Setup tab; `SetupTimeReal` = from the operation's start to QC PASS; `OperationQty` = good quantity made; the Planner's value overwrites Kitaron's; write with the existing `kit` login. This is the explicit scope decision AGENTS.md rule 26 requires for ERP write-back; synchronization itself stays read-only.

**Findings (read-only, 2026-09-28):** `kit` is sysadmin/db_owner. `TSubRootCard` has INSERT/UPDATE/DELETE triggers. Kitaron's production and setup reporting triggers write `StartDateReal` (earliest reported start) and `SetupTimeReal` (minutes of setup reports), its update trigger copies `StartDateReal` into `StartDate`, and its automatic work planning treats an operation with `StartDateReal` as started and subtracts `SetupTimeReal` from the remaining setup; `PCopyOperDataToRoot` writes `OperationQty`; nothing writes `FinishDateCalc`. Every other date/quantity column (`FirstStartDate`, `StartDateIdeal`, `StartDate`, `FinishDate`, `MinStartDate`, `PriorityOper`, …) is rewritten by `AutoWorkPlanning` or reporting, so it is not offered. `TimeProductionP`, `DirectionTimeP` and `SetupTimeReal` are minutes (this settles the unit question in `kitaron-initial-mapping.yaml`). On the live data 659 of 667 Kitaron-linked Planner operations match exactly one `TSubRootCard` row; the 8 others carry an operation number of the Case route that the Work Order's route card lacks.

**Implemented:** `SchemaV87KitaronPushMigration`; `KitaronPushCatalog`, `KitaronPushPlanner` (matching, overwrite rule, factory-local times, skip reasons), `KitaronPushService` (settings validation, preview, logged run), `KitaronPushHostedService` (automatic interval), `SqlServerKitaronPushTarget` (catalog-only columns, parameters, one transaction, exactly-one-row updates), `TimelineKitaronPushForecast`, `SqliteKitaronPushRepository`; `/api/v1/kitaron/push` routes; the Windows Setup → Kitaron Push tab with Preview, Push now and the run log; he/ru texts.

**Tests:** `KitaronPushPlannerTests`, `KitaronPushServiceTests` (real Planner database, fake Kitaron), `KitaronPushApiTests`, client `KitaronPushViewModelTests`; a temporary read-only probe against the live Planner snapshot and Kitaron (no writes).

**Open points:**

- The first real push has not been made; start with Preview, then Push now once, and check the operations in Kitaron.
- Kitaron records no Planner user for a pushed value; the Planner's push log (who, when, old and new value) is the record.
- Operations whose number exists only in the Case route (not on the Work Order's route card) are not pushed.

## Kitaron-open Orders stuck complete (2026-09-28)

**Finding (read-only, live data):** 746 Orders that Kitaron reports open (`kitaron_status = 'active'`) were stored `complete` without any Batch allocation, so they did not count as demand and child Cases derived nothing from them (for example `30P450171003-501` → `30P450171100-001`). An earlier Kitaron status reading had closed them; the sync then kept a stored `complete`/`in_production` whenever Kitaron said `active` and never compared the status again.

**Implemented:** while Kitaron reports an Order `active`, the sync derives its Planner status from the Order's own production facts (`SqliteOrderLifecycle.ReadFactsAsync` + `OrderLifecycle.Derive`), also when first linking an existing Planner Order, and a differing stored status makes the Order update. The next sync repairs the 746 Orders; the 16 `in_production` ones have started operations and keep that status.

**Tests:** `KitaronBatchSyncTests.An_order_open_in_kitaron_takes_the_status_of_its_own_production_not_a_stale_complete` (fails without the fix).

**Owner decision (2026-09-28), replacing the same day's decision to keep them out:** every open Kitaron Work Order (route open, not stopped) is imported, also when its sales-order line is closed or stopped or it has none (production for stock and the like). On the live data this adds 164 Work Orders with a closed line (none stopped, none without a line; 1 opened in 2023, 2 in 2025, 161 in 2026), for 362 in all, for example 41448 of `30P450171100-001`.

**Implemented:** `SqlServerKitaronSourceReader` reads work orders, their links and material lines by the work order's own state and reports whether the line is open (`KitaronSourceWorkOrder.OrderLineOpen`). `BuildBatches` allocates work orders with an open line first and the others after them, each group oldest first, so existing Batch allocations do not change and a work order for stock only takes demand the others leave; what no Order needs is stock. A child part's work order still allocates only to Orders of its own Case, so work orders like 41448 (whose Case has only demand derived from its parent) become stock. Tests: `KitaronBatchPlanTests.A_work_order_whose_order_line_is_closed_is_imported_and_takes_only_demand_the_others_leave`, `Work_orders_are_read_by_their_own_state_whatever_their_sales_order_line`; the three queries were run read-only against Kitaron (362 work orders, 362 links, 291 material lines).

## Locked-simultaneous groups as a flow line (2026-09-28)

**Owner decision (2026-09-28), replacing AGENTS.md rule 10's "same start and finish; group duration is the longest member duration":** the operations of a locked-simultaneous group run as a flow line. "Operation 80 finishes part 1 - operation 100 starts part 1." An intermediate request the same day (all cycles start together in lockstep, one step apart) was withdrawn by the owner.

**Implemented (Timeline engine only; no schema or API change):**

- Members are ordered by operation number (`TimelineDependency.SimultaneousPosition`, set by the projection from the group's route order).
- The members still start together at the common start (setup, first-part QA), as before. Part k starts on a member no earlier than the previous member's finish of part k and this member's finish of part k-1; each member keeps its own cycle time. A manual load waits for its part, an automatic load of N parts waits for all N, and a worker-required load still reserves a regular worker (the first member's loads are placed first).
- Parts already made are the first ones (`PlannedQuantity` minus the remaining cycle quantity), so a member's remaining parts keep their numbers; a part the previous member does not make counts as available.
- All members finish at the group finish (the last member's last part). A member's Machine is shown reserved from the group start until its first work (a member without setup waits for its first part) and from its last part to the group finish; waits between parts are waiting intervals "Waiting for part k from the previous operation of the locked-simultaneous group."
- Backward placement moves the group's finish back by the forward overshoot and then later by the remaining slack, so the flow ends as late as it fits.

**Choices made without a separate decision (reversible):** setups start together at the group start, as before, rather than each member setting up just in time for its first part; the group finish, not each member's last part, ends every member's reservation; a group already in progress keeps the existing actual/hold handling.

**Tests:** `TimelineCalculationEngineTests` (`Locked_simultaneous_part_goes_to_the_next_operation_when_finished_and_machines_stay_reserved`, `Locked_simultaneous_parts_flow_in_route_order_at_each_machines_own_cycle` for a slower first and a slower second operation, `Locked_simultaneous_manual_load_waits_for_the_part_and_the_one_worker`, periodic loads, worker retry), `TimelineBackwardCalculationTests` (latest flow placement), `TimelineApiTests.Missed_all_backward_locked_group_falls_forward_together`.

**Live data:** the only locked group is `30P450171100-001` operations 80 (cycle 7 h) and 100 (cycle 3 h), group 1; its first batch arrives with Work Order 41448.

## Several NC files per operation: subprograms (2026-09-28, schema v88)

**Owner decisions (2026-09-28):** "Need an option to add several gcode files for one operation (subprograms etc)." Files are chosen by detection, then edit (the called subprograms found next to the program are ticked; the programmer may untick or add files); a called program that is not included is reported and the release goes ahead; subprograms keep their own program numbers (no renumbering).

**Implemented:**

- Shared `NcSubprogramCalls` (in `Meimad.Planner.NcEngine`, used by Server and client): `M98 P` / `G65 P` calls (FANUC eight-digit `P`), a file's program number (first code line `O` / `:`, else a leading number in its name), missing calls through nested files, and the file for a number in a folder (engine file names, then declared numbers).
- Server: `SchemaV88NcSubprogramsMigration` (`gcode_release_subprograms`, `gcode_releases.missing_subprogram_calls_json`, `NC_SUBPROGRAM` package artifacts); multipart `subprogramFiles` on both release routes; `GCodeArtifactStore` stores them in the release folder (numbered files as `O<number>`, so the NC engine analysis follows the calls); `GCodeService` refuses placeholders and duplicate numbers and records missing calls; catalog/response fields and a subprogram download route; `ProductionPackageService` copies them unchanged and verified.
- NC engine wrapper: the program's folder is also searched by declared `O` number (after the engine's file names), so the NC viewer finds a subprogram released as `pocket.nc` the way the release detection does.
- Client: the Release G-code form's subprogram list (ticked detection, **Add subprogram files…**, note of calls not included) and a Subprograms column in the release history; the NC viewer's Release to Server dialog has the same list; placement after a release moves subprograms from the program's folder and copies others of the Operation's Gcode folder; a release opened in the viewer writes its missing subprogram files into its revision folder. he/ru texts.

**Tests:** Server `GCodeReleaseApiTests` (subprograms stored, numbered, downloadable, analysed, missing calls reported; placeholder and duplicate-number refusals leave nothing), `ProductionPackageApiTests.Package_copies_the_release_subprograms_unchanged_beside_the_runnable_program`, `NcSubprogramCallsTests`, migration version 88; client `CaseWorkspaceViewModelTests.Choosing_a_program_ticks_the_subprograms_it_calls_and_the_release_sends_the_ticked_ones`, `NcProgramFoldersTests.Released_subprograms_follow_the_program_and_shared_ones_are_copied`.

**Open points:** Okuma `CALL O<name>` and Siemens-style named subprogram calls are not detected (they are not numbered `M98`/`G65` calls); such files can still be added by hand. The Offset Loader and verification macros (O9001–O9003) are commissioned on the machine and appear as calls not included only if a legacy template names them literally.

## Refresh a Work Order from its Case on request (2026-09-28)

**Owner decision (2026-09-28):** "Add an option to refresh operation in work order (reload from case operations) without move it to pending status."

**Implemented:** `POST /api/v1/batches/{batchId}/refresh-operations` runs `SqliteWorkOrderRouteRefresh` for one pending or released Work Order (`RefreshOnRequestAsync`; complete and cancelled are refused with `409 work_order_closed`) and keeps its release state; the refresh rules are those of a pending Work Order, so started work, packages, bench sessions and locked production history are never changed. The Case form's Work Orders tab has **Refresh from Case**; the status line reports updated, added and removed operations and any number conflicts. he/ru texts.

**Tests:** `WorkOrderRouteReleaseTests.Refresh_from_case_reloads_a_released_work_order_without_returning_it_to_pending`; client `CaseWorkspaceViewModelTests.Refresh_from_case_reloads_a_released_work_order_and_reports_what_changed`.

## Finished operations and Redo on the Planning Board (2026-09-28)

**Owner decision (2026-09-28):** "In planning board show the finished production operations with options in additional tab: Redo (reset quantity and back to production backlog)." "Production backlog" is taken as the unassigned backlog (pool): Machine selection stays manual (AGENTS.md rule 3), so a redone operation is not put back on a Machine automatically.

**Implemented:** `GET /api/v1/planning-board/finished-operations` and `POST /api/v1/batch-operations/{id}/redo` (`planning.board`, expected version). Redo reuses the Reset path of suspended work for the operation's own Production Run (`PLANNED`, structure unlocked, cycles and produced quantity 0, outputs `ALLOCATED`), clears the operation's actual start/finish/Machine and production pin, releases any active Machine assignment (a CNC-completed operation may still hold one) and compacts that backlog, recomputes Batch and Order status and logs `operation_redone` with the previous values. Cycle observations and workflow events are kept as history. Refused for a cancelled Work Order and for an operation made by a run that is not its own. The Windows Planning Board has a Finished tab (Work Order, part, operation, Machine, start, finish, Done / planned, Redo with confirmation). he/ru texts.

**Tests:** `MachineOperationExecutionApiTests.A_finished_operation_is_listed_and_redo_returns_it_to_the_pool_with_its_quantity_reset`; client `MachinePlanningBoardViewModelTests.The_finished_tab_lists_finished_operations_and_redo_sends_one_back_to_the_backlog`.

**Open points:** the Production Run keeps its number and its earlier QC and setup-verification events; a new QC is recorded by the next SEND_TO_QC / QC decision. Redo of multi-output runs is not offered.

## Locked-simultaneous setups when a setup worker is free (2026-09-28)

**Finding (live data, read-only):** Work Order 41448 (`30P450171100-001`) OP80 on Machine 07 and OP100 on Machine 05 form a locked group; the only active setup worker qualified for both Machines is Nadav Taizlend (Aria Goldberg is inactive). Because the group's setups had to start at the same moment, the group never found a common start (`insufficient_availability`), and after OP80 started OP100 stayed blocked (`dependency_unresolved`).

**Owner decision (2026-09-28), replacing the reversible choice "setups start together at the group start" of the flow-line section above:** each member of a locked-simultaneous group is set up when a qualified setup worker is free; only the parts flow in order.

**Implemented:** `ScheduleLockedGroup` places the members in route order from the group's earliest start, sharing the worker bookings, with no common-start retry (`FindCommonStart` removed). The group starts at its first member activity; each member's Machine is reserved from then until its own first work ("until a setup worker is free for this Machine" or "until the first part arrives") and after its last part until the group finish. A run on a copy of the live database places OP100 (Nadav sets it up while OP80 runs) and the group's blocking conflicts disappear.

**Tests:** `TimelineCalculationEngineTests.Locked_simultaneous_members_are_set_up_one_after_the_other_by_the_one_qualified_setup_worker`; the other locked-group tests keep their results.

## Employee workload calculator (2026-09-28)

**Owner decision (2026-09-28):** "Employees tabs in Setup. Add employee workload calculator with report." Chosen: **planned load** from the Timeline (not recorded work), shown **on screen and as a printable page**.

**Implemented:** the Timeline engine now tags each worker interval (setup, first-part QA, load/unload) with the Employee that it books; the projection keeps a Server-internal list of Employee bookings and availability, including the station steps the auxiliary allocator places on Employees. It is not serialized on the Timeline response. `EmployeeWorkloadService` and `GET /api/v1/resources/workload?from=&to=` compute this for whole factory days (at most 92):
- working time is the Timeline availability of each active Employee;
- booked time is split by kind;
- the load is given per period and per day, with a level and the work items;
- only time from now on counts (`countedFrom`), because the Timeline plans forward from now;
- overlapping bookings add up, so a double booking shows above 100 %.

Nothing is stored. Windows **Setup → Employees / Resources** now has inner tabs **Employees** (the former page) and **Workload**:
- **Workload** has a period (default today plus 13 days), **Calculate**, the Employee table with level text and colour, and the selected Employee's load per day and booked work;
- **Print report** writes a self-contained HTML page (summary, day matrix, work per Employee; right-to-left in Hebrew) to `%TEMP%\MeimadPlanner\Reports` and opens it in the browser to print or save as PDF;
- he/ru texts are included.

**Tests:** `TimelineApiTests.Employee_workload_totals_the_timeline_bookings_against_working_hours`; client `EmployeeWorkloadViewModelTests` (calculation and selection, period validation and Server refusal, report content in both directions).

**Open points:**
- Recorded (actual) work is not included; MVP timing holds only current values (AGENTS.md rule 12).
- The load is the Timeline's prediction and changes with every planning change.
- Station steps booked only on a Workstation, with no Employee, do not count for any Employee.

## Report email sign-in (2026-09-28, schema v89)

**Finding:** the report email sender connected to the SMTP server without signing in (`UseDefaultCredentials = false`, no credentials), so only an open factory relay worked; a personal mailbox such as Gmail refused it.

**Owner decision (2026-09-28):** add the mailbox sign-in ("yes" to the proposal: user name and password in the email settings, the password stored encrypted on the Server and never sent back, and a Send test email button).

**Implemented:**
- Migration v89 adds `smtp_user_name` and `smtp_password_protected` to `report_email_settings`.
- The password is protected with ASP.NET Core Data Protection, like the Kitaron SQL password. `ReportEmailSmtp` builds the SMTP client for both weekly reports and the test email, and signs in when a user name and a password are saved.
- `PUT /api/v1/report-email-settings` keeps a saved user name or password that a client does not send.
- `POST /api/v1/report-email-settings/test` sends a test email and reports the mail server's reply on failure.
- Port 465 is refused because `System.Net.Mail` has no implicit TLS.
- In Windows **Setup → Reports / Email**:
  - SMTP user name, and an SMTP password box that is emptied after saving;
  - a saved/not-saved note and **Remove the saved password**;
  - **Send test email**;
  - a Gmail hint.
- he/ru texts are included.

**Tests:**
- `ReportEmailSignInApiTests`: encrypted storage, never returned, kept and removed, validation. The test email signs in to a loopback fake SMTP server with the exact password, and its refusal comes back as 502 with the server reply.
- Client: `SetupViewModelTests.The_email_password_is_sent_once_forgotten_after_saving_and_the_test_email_names_the_recipients`.
- The migration version tests move to 89.

**Open points:**
- A Microsoft personal account (Outlook.com / Hotmail) no longer accepts password sign-in from programs. It would need OAuth, which is not implemented; Gmail App Passwords or a company relay work.
- A backup restored on another machine needs the password entered again.

## Newer G-code for running work (2026-09-29, schema v90)

**Owner decisions (2026-09-29):** "The new Gcode file upload (release or local version change): Work Order - refresh from case > Readiness = Not ready (gcode outdated). The process switch to setup mode (if it was in production state), like a new one > create new production package, need to run verification if enabled, pass QC etc." Chosen:
- the change reaches Work Orders on **Refresh from Case**;
- made parts are **kept**;
- a newer release for the **same postprocessor or a new process revision** counts;
- the Machine's loaded program is **refused** at its next verified start.

**Implemented:**
- Migration v90 adds the workflow event `SETUP_RESTARTED` (the event table is rebuilt with its indexes and triggers re-created as they were) and `batch_operation_setup_restarts`.
- `SqliteSetupRestart` finds the started operations of a Work Order whose production release has a newer local version, and applies the restart inside the on-request refresh:
  - the `SETUP_RESTARTED` event, and revoking the current Offset Loader and superseding its verification sessions;
  - retiring the current package (`NC_RELEASE_REPLACED`) and clearing the selected release;
  - the restart row and an `operation_setup_restarted` event.
- `GET /api/v1/batches/{id}/refresh-operations/preview` lists them first; the refresh response reports `setupRestarts` and `processRevisionNotSwitched`.
- Readiness:
  - an operation back in setup keeps its pinned process revision and tool table, and chooses its release again;
  - G-code shows `OUTDATED` until a new package pins the new release;
  - a started operation with a newer local version that has not been refreshed says to refresh the Work Order.
- Package build accepts the restart state, and package activation re-pins the release and resolves the restart.
- The tablet, preparation queue, E-Ink status and debug timeline map `SETUP_RESTARTED` to ready for setup.
- The Windows client asks before a refresh sends running operations back to setup and reports what happened (he/ru).

**Tests:**
- `SetupRestartApiTests` covers the whole path:
  - preview, refresh and restart facts, with made parts kept and the readiness message;
  - the new package pinning the new release, and no second restart;
  - a new process revision reported and not switched;
  - finishing resolving an open restart.
- `SetupRestartMigrationTests` checks that the v90 rebuild keeps events and triggers and admits the new type.
- Client: `CaseWorkspaceViewModelTests.Refresh_from_case_asks_before_sending_running_operations_back_to_setup`.
- The migration version tests move to 90.

**Open decision (owner):** a **new process revision** for an operation already in production is not switched. AGENTS.md rule 27 and the database make a started Production Run's process revision immutable. Two options would need a decision:
- end the run with its made parts and plan the remainder as a new Production Run;
- or an explicit exception to rule 27.

**Also not covered:**
- Operations produced by combined multi-output runs.
- The Timeline does not yet add a new setup phase for an operation back in setup.
- A cycle that was running on the Machine at the moment of the refresh is not counted.

## Measured tools in the NC viewer (2026-09-29)

**Owner decision (2026-09-29):** "After the measurement of the tools in Tool Room Tool Table, the tool table in NC viewer should be exact same as Tool Room Tool table. The tool in NC Viewer should show it exact as measured, include holder and components. If tools are not measured yet - keep as it now."

**Implemented (client only, no Server or schema change):**
- `NcViewerOperationToolTable.From` adds an `NcViewerToolAssembly` for every tool of a saved Tool Room version with a measured length or diameter. It holds the Tool Room row (identifier, offset, measured values, type, hand, notes, components in assembly order) and the segments of the Tool Room's own drawing (`ToolShapeBuilder`).
- The session answers `meimadToolAssemblies`.
- The page script `meimad-tool-assembly.js`:
  - adds the *Tool Room: measured tools* section under the viewer's tool table;
  - follows the wrapper's tool-pose hook: it finds the active T number (the playing segment, else the segment ending at the tip), hides the vendored cutter group and draws the assembly at the same position and orientation (inch programs scaled).
- Unmeasured tools keep the vendored drawing. The vendored files are unchanged. he/ru texts are included.

**Tests:**
- `NcViewerOperationToolTableTests.Measured_tools_carry_the_tool_room_assembly_and_unmeasured_tools_keep_the_viewer_tool`.
- A Node smoke run of the page script against the vendored three.js (the assembly replaces the cutter for a measured tool and the vendored cutter returns for an unmeasured one).
- The WebView2 page itself still needs a check on a workstation.

**Not covered:**
- Turning tools are listed but not drawn as assemblies.
- A release opened from the Case workspace has no Work Order operation, so no measurements.

## Spindle side of milling tools in the Tool Room (2026-09-29, schema v91)

**Owner request (2026-09-29):** the holder definition in the Tool Room tool table should show the BT40 adaptor with the pull stud (selectable from a library) relative to the gauge line; the holder is a cylinder of a defined diameter whose length is calculated from the measured tool length (HL = measured length − OHL − TCL; example: pull stud HAAS 40 45° M16, BT-40 adaptor, gauge line, tool-changer feature Ø63 × 25, holder Ø40 × HL, flat mill D10 CL22 SD10 OL80 OHL50). **Decisions:** the library lives on the Server and is edited in Setup; the adaptor and pull stud are a Machine default, with a per-tool override in the Tool Room.

**Implemented:**
- Migration v91:
  - `spindle_adaptors`, `pull_studs` (seeded with BT40 and HAAS BT40 45° M16 from Haas/Shars data) and `machine_spindle_interfaces`;
  - per-tool `spindle_adaptor_id` / `pull_stud_id` on `tool_preparation_tools`;
  - the shape key `outsideHolderLength`.
- Endpoints `GET /api/v1/spindle-library`, create/replace/delete for adaptors and pull studs, and `PUT /api/v1/machines/{id}/spindle-interface`. The tool preparation read carries the library and the Machine default.
- Client:
  - `ToolShapeBuilder` draws the pull stud (knob, neck, collar), the `TAPER` and the flange, and a holder cylinder whose HL is driven by the measured length and OHL; the tool shows OHL;
  - the Tool Room preview draws above the gauge line;
  - the Tool Room has per-tool adaptor and pull-stud pickers and shows the calculated HL;
  - the Setup tab *Spindle Adaptors & Pull Studs* holds the library and the Machine defaults (lathes are not listed);
  - the NC viewer assemblies include the spindle side (`TAPER` drawn as a truncated cone);
  - he/ru texts.

**Tests:** `SpindleInterfaceApiTests` (library, versions, permissions, Machine default, per-tool override, entries in use); client `ToolSpindleGeometryTests` (the owner's example: HL = 45 and the tip at the measured 120 mm; undriven holder; turning unchanged; override and NC viewer assembly) and `SpindleLibraryViewModelTests`. Migration version tests move to 91.

**Not covered:**
- Collets are listed but not drawn in spindle mode (the formula has no collet term).
- Other extensions are subtracted from HL.
- Seeded dimensions should be checked against the actual tooling.

### BT40 pull stud library (2026-09-29, schema v92)

Owner request: "Find the dimensions for all BT40 pullstuds in web and add it to the pullstuds lib."

Migration v92 inserts:
- MAS 403 P40T-1/-2/-3 at 45°/60°/90°, solid and with a Ø4 coolant hole (-1H/-2H/-3H): L 60, L1 35, L2 28, flange Ø23, Ø17, knob Ø15, neck Ø10, M16;
- JIS B6339-40P at 15°: L 54, L1 29, Ø23/Ø17, Ø7 hole;
- Mazak BT40 at 45°: L 44.1, L1 19.1, Ø22/Ø17, Ø7 hole.

Sources: the Jimmore/JIC pull stud catalog, pp. 45–46, and the Showa Tool MAS table. An existing name is skipped.

The v91 HAAS entry had used L2 (27.94, flange to grip) as its length above the holder. It is corrected to L1 34.93 with flange Ø23, since it is the MAS P40T-1 geometry, unless it was edited in Setup.

The JIS B6339 PA and PB and DIN 69872 studs are left out: they are for DIN 69871 holders, not BT40.

Open points:
- For JIS 40P and Mazak, the knob diameter is taken as the catalog's D1 and the neck is not published (drawn dashed).
- Check all entries against the studs in use.

## NC time in the postprocessor status (2026-09-29)

**Owner request:** "If the Gcode added to operation, show the NC time in postprocessor status (Cycle and Setup estimation)."

**Implemented (no schema change):**
- The G-code catalog names the Machine of every NC cycle estimate (`machineNumber`, `machineName`).
- Each estimate carries a setup estimate calculated with the Planning Board's `SetupOccupancyEstimator` for one piece: the required tools of the release's tool table × the tool loading time, the Case Operation's setup time as the fixture setup, and the first piece at the first-piece factor.
- The Windows postprocessor status gains *NC cycle time* and *Setup estimate* columns for the current release, per Machine number. The other release details show Machine numbers instead of ids.

**Tests:** `GCodeReleaseApiTests.Released_nc_is_analyzed_once_and_evaluated_per_machine_without_overwriting_manual_cycle` extended (the catalog's setup estimate equals the Planning Board's 172.5 s); client `PostprocessorStatusTimeTests`.

## Real Machine times and operation statistics (2026-09-29)

**Owner request:**
> Time used for timeline and showed on assigned to machine operation in Planning Board: No NC, no Measured on Machine > Show estimation from operation. NC exist, but no machine measrement - NC Time. Machine Measurment exists - Show the real Machine time. Collect all times, show it in Case / Operation Tab / Rigth clic on operation >> show statistics - new window should show the collected times. Option "Apply time on operation" should be on NC Time and Real time. It should ovveride the Cycle, Setup, QC or Part Loading time in the operation. Keep history.

**Owner decisions:**
- Only times of the same Case Operation on the same Machine count.
- A real time is the median of the last 10 measurements.
- Cycle, setup, QC and loading are all used automatically.
- Apply writes to the Case Operation (pending Work Orders follow, released ones on Refresh from Case), with history.

**Implemented (schema v93):**
- `SqliteOperationTimeMeasurements` reads the measurements (see the API contract) and caches the medians.
- The Planning Board and the Timeline source use real > NC > Operation for cycle, setup, QC and load/unload of assigned operations.
- `GET/POST /api/v1/cases/{caseId}/operations/{operationId}/time-statistics[/apply]`, and the immutable `case_operation_time_changes` history. Manual edits of the four times are recorded too.
- Windows client:
  - the Case → Operations grid's right-click **Show statistics…** opens the statistics window: times by Machine with *Apply NC* / *Apply real*, the collected measurements, and the history;
  - Planning Board cards show *Real median* and *Real setup*, and the time tooltip names the real QC and load/unload medians.

**Decisions made while implementing (reversible):**
- The CNC setup is measured from the Offset Loader run to Send to QC, so tool loading before the Offset Loader is not included.
- Applying a real setup writes the fixture part (median − tool loading − first piece), so the setup estimate does not count them twice.
- *Apply NC* exists for the cycle only.
- Gaps between cycles count as loading only when a worker loads every part.

**Tests:** `OperationTimeStatisticsApiTests` covers:
- the priority chain on the board and the timeline;
- the statistics, apply, permission, stale version, pending Work Order refresh and history;
- manual edits in the history;
- the median of the last ten.

## Planning Board emulates Machine telemetry (2026-09-29)

**Owner request:**
> Planning Board should emulate the real machine telemetry: Assigned to machine operation / right click menu changes: Show the production statuses manual report (radio buttons) Ready for Setup, Setup Run, Passed to QC, Ready For Production, In Production. Remove the setup start, setup end, manual part time update and production end options. Remove the Play / stop / Pause / reset buttons. Add option "mark operation as Finished" (the operation was finished before, no need to keep it in plan).

**Owner decisions:**
- The statuses are offered for Machines without DPRNT output. A Machine connection option switches the DPRNT output on or off.
- They are recorded as Production Run workflow events.
- Pause is removed entirely.

**Implemented (schema v94):**
- Server:
  - `POST /api/v1/batch-operations/{id}/workflow-status`;
  - the new event types `MANUAL_READY_FOR_SETUP` and `MANUAL_SETUP_RUN`;
  - the DPRNT output switch (`dprnt.enabled` / `dprntEnabled`);
  - `workflowStatus` and `manualWorkflowReporting` on the Planning Board;
  - Finish from not started or paused, with a manual production session closed on Finish;
  - the Timeline drops finished setup/QC phases of running operations;
  - measured times from manual statuses.
- Windows client:
  - the operation's right-click menu shows the five statuses as radio items, disabled with an explanation on DPRNT Machines, plus **Mark operation as Finished**;
  - the card shows the current status in place of the player buttons;
  - the manual report items and the pause dialog are removed;
  - Setup → Machine connection has a **DPRNT output** checkbox.

**Decisions made while implementing (reversible):**
- Ready for Setup and Setup Run get their own event types, because an Offset Loader event would arm CNC verification (AGENTS rules 32/33).
- Passed to QC, Ready for Production and In Production reuse `SEND_TO_QC`, `QC_PASS` and `PRODUCTION_SESSION_OPENED`. A hand-reported Ready for Production therefore counts as a QC pass (for example, in the Kitaron push), with the planner as the user.
- Setup Run starts the operation under the normal start rules.
- The start/suspend/reset/manual-report endpoints stay on the Server for older clients.

**Tests:**
- `ManualWorkflowStatusApiTests`: the event chain, QC queue, board, finish closing the session, collected times, the DPRNT gate and switch, finish before start;
- client `RealTimesAndManualWorkflowTests`;
- migration version tests.

## 12h shift rotation and Shift Roster (2026-09-29, schema v95)

**Owner request:**
> Need to improve the calendar to manage 12h shifts. The employee can work some days the night some days the day shift. Need to manage the Shift Employees rotation.

**Owner decisions (2026-09-29):**
- Rotation model: a repeating pattern that crews follow at an offset, plus per-date overrides per employee.
- The factory has no fixed cycle today; shifts are decided week by week, so the weekly Shift Roster is the main tool and a rotation's pattern may stay empty.
- Shift times: day 07:00–19:00, night 19:00–07:00.
- An absence (vacation, sick day, ...) on a date removes the whole shift that starts on that date, including a night shift's hours after midnight.

**Implemented (schema v95):**
- Server:
  - Working Calendar `scheduleKind` `rotation` beside `weekly`, with `rotation` = named shifts (an end earlier than the start ends the next morning; contained breaks), an optional repeating `pattern` of shift codes or `off` from `anchorDate`, and `crews` with `offsetDays`. The kind is fixed at creation. A rotation is an employee Calendar: it has no machine usage and cannot be the Setup or Israel Master Calendar. Its dated exceptions are closures only.
  - `employee_resources.shift_crew_code`: an employee on a rotation with crews follows exactly one of them; other employees have none.
  - `employee_shift_roster_entries`: one dated shift code or `off` per employee and date, with version, actor and time. `GET /api/v1/shift-roster?from&to` (at most 62 days) returns each rotation employee's pattern day, roster entry and resulting shift per date; `PUT /api/v1/shift-roster` saves a batch atomically with the `Plan machines` permission. Each change carries the version it was read at (null: no entry), and a stale change refuses the whole batch with `409 edit_conflict` naming who changed the day and when.
  - `EmployeeShiftCalculator` expands weekly and rotation employee Calendars into shifts dated by their start. The Timeline, the auxiliary resource allocator and `GET /resources/{id}/availability` use it, so one rule applies everywhere; this also fixes the availability endpoint returning no time for an overnight Calendar.
  - Guards: a crew an employee follows, or a shift the Shift Roster uses from yesterday on, cannot be removed from the rotation; an employee cannot move to a Calendar that cannot keep their roster days from yesterday on. A rotation used by a Machine, Workstation, External Resource or external delay yields the blocking Timeline conflict `rotation_calendar_not_allowed`; an employee on a crewed rotation without a crew yields the warning `employee_shift_crew_missing`.
- Windows client:
  - Setup → Calendars: **Calendar kind** (weekly schedule or 12h shift rotation) with shifts, pattern, pattern start date and crews as text lines, like the existing window and exception fields.
  - Setup → Employees: **Shift crew** for employees on a crewed rotation.
  - New **Shift Roster** tab: a Sunday-to-Saturday week of every rotation employee, with the pattern, a shift or Off per day, week navigation, **Repeat last week** and Save/Discard. The cell text names the shift, absence or closure; colour only repeats it.
  - Rotation Calendars are not offered as Setup, Master, Workstation, External Resource or external-delay Calendars.

**Decisions made while implementing (reversible):**
- An explicit roster entry is applied as entered on a closure or holiday; closures and non-working/partial-working holidays change only pattern days.
- A partial absence whose time is earlier than an overnight shift's start is in that shift's after-midnight part, as breaks already are.
- A full-day absence on a date no longer removes the after-midnight hours of the previous evening's night shift. This changes existing weekly overnight employee Calendars, as decided above.
- The roster uses the existing `Plan machines` permission, like downtimes, rather than a new permission.
- Pattern day `i` for crew offset `o` is `((date − anchorDate) − o) mod patternLength`.

**Tests:**
- `EmployeeShiftCalculatorTests`: crew offsets, night shifts across midnight, roster override, roster-only rotation, full and partial absences on a night shift, shift breaks, closures and holidays, crewless employee, weekly overnight availability.
- `ShiftRotationValidatorTests`, `ShiftRosterApiTests` (roster read/save/clear, stale-change conflict, permission, guards, employee-only use), `ShiftRotationMigrationTests`, migration version tests.
- Client `ShiftRosterViewModelTests` (save with versions, repeat last week, viewer, Sunday week, rotation form parsing).
- Not verified: the WPF main-window startup test times out against the live Server that requires sign-in, before and after this change, so the new tab's layout was not rendered by a test.

## NC viewer: called programs, single step, stops and macro execution (2026-09-30)

**Owner request:**
> In NC viewer, if Macro programm called, show the macro program in separate window (split the Gcode horizontally). Add Single step, Stop and Optional stop buttons. Ensure all macro commands are supported and executed as well.

**Implemented:**
- NC engine (`shared/Meimad.Planner.NcEngine`):
  - `meimad-macro.js`, a custom macro executor for lathe programs, replaces the static subprogram inlining (`meimad-subprograms.js` is removed). It executes variables (#1-#33 per macro level, #100-#199 and #500-#999, vacant #0, `#[...]` indirect), system variables (modal #4001-#4130, positions #5001-#5065, work offsets #5201-#5335, tool offsets #2001-#2999, timers and date, part counts), `WHILE`/`DO`/`END`, endless `DO`, `IF`..`GOTO`/`THEN`, `GOTO` (also to a computed sequence), block delete, `G65` (argument specifications I and II), `G66`/`G66.1`/`G67`, `M98` (with `L` and the FANUC eight-digit `P`), `M97`, `M99`/`M99 P`, FANUC custom macro calls by G codes (parameters 6050-6059), M codes (6071-6089) and T codes (6001#5), `#3000` alarms, `#3006` stops, `DPRNT`/`BPRNT` output and `POPEN`/`PCLOS`/`SETVN`. The lathe interpreter draws the flat result; sequence numbers stay unique so a roughing cycle in a loop or a repeated subprogram keeps its own contour.
  - Mill programs keep the vendored mill interpreter, which executes the same macro set. The adapter wraps three prototype methods at run time (no vendored file changes) to report `M00`/`M01`/`#3006`/`#3000` stops, tag every segment with its called program and row, keep the calling row in the main editor for called programs of the same file, and run FANUC custom macro calls by G/M codes.
  - The viewer model carries `meimadStops` (the segment each stop halts before), `meimadUnits` (called programs' texts) and `meimadPrints`. The host no longer turns `DPRNT`/`BPRNT` into comments.
  - `NcEngineInfo.AdapterRevision` 3: lathe analyses change (loops and calls are executed, dwell is counted per executed block), so stored release analyses are recalculated once.
- Windows NC viewer page:
  - The source column splits horizontally when the program calls another program: the called program (read-only CodeMirror) follows playback and toolpath selection; Hide / Called program and a resizable splitter.
  - **Single step**, **Stop** and **Optional stop** (remembered on this PC) in the playback bar, with a stop notice in the 3D view. The rules are in `meimad-playback-core.js`; `meimad-playback.js` drives the vendored playback through `cncPreviewSeek` and the 3D wrapper's playback and new selection events.

**Decisions made while implementing (reversible):**
- **Stop** ends playback and rewinds to the program start (reset); the existing **Pause** stays the feed hold.
- `M00`, `#3006` and a `#3000` alarm always stop playback; `M01` stops only with **Optional stop** on, which starts off.
- In single step, a stop block is its own step, as on the control in single block mode.
- A macro reading a tool offset (#2001-#2999) gets 0 with a note, because the preview does not know the control's offset memory; work offsets come from the viewer's saved home offsets.

**Tests:**
- `NcEngineMacroExecutionTests`: lathe loops/calls/locals/system variables/prints/stops, a roughing cycle in a loop, custom G-code macro calls from parameters, mill stops and call tagging (Haas NGC and FANUC); updated `NcEngineRuntimeTests` (the Okuma IF is executed, DPRNT reaches the engine).
- Client `PlaybackCoreTests` (V8): block timeline, M01 with and without optional stop, M00, single step halting on a stop block, call rows.
- The page glue was checked in headless Edge with the published page scripts and a fake viewer hook (split pane, macro row marker, M00 halt, Stop, single steps, Hide). Not verified: the WebView2 page inside the running client.

## NC viewer: subprogram tree, breakpoints, tool at the selected row, macro variables window (2026-09-30)

**Owner request:**
> need to show all subprograms (and sub sub...) - all Renishow macros and O9013 ... O4999 etc from the machine memory and attached subprograms. Need macro variables table, opened in external window, shows all used variables values. Add option to add brake point in code, also subprograms. Selecting row in Gcode should point the tool in the row position and show the current tool tool path

**Implemented:**
- NC engine:
  - `meimadCallTree` (viewer preview only): every program called at any depth, found by a static scan (`meimad-macro.js scanCalls`: G65/G66, M98 by number or name, custom G/M/T calls) resolved like the executor (same file, program folder with release subprograms, machine program memory folder); nested programs are listed even when the executed branch never calls them; computed and missing calls are marked; each program is expanded once. Their texts join `meimadUnits`.
  - `meimadTrace`: every executed row (`main|row` for the program's own file, `<file>|row` for a called file) with its playback position, local-variable level and executed-block step; every variable write with its level and row (G65/G66 arguments included); system variable reads with their values; the used variables. The lathe executor records it directly; the mill interpreter through run-time hooks on `getVariable`, `setVariable`, `pushFrame` and `executeLine`. Segments carry `meimadLocals`. Analysis output is unchanged (no adapter revision).
- Windows NC viewer:
  - Called-program list (indented tree) in the call pane header.
  - Breakpoint gutter in the NC program and called-program editors; playback halts before each execution of a breakpoint row, with a notice; single step treats it like a stop.
  - **Tool follows row** (on by default, remembered): selecting a row seeks the playback to the row (end of its move, or its position for a statement without motion; again = next execution) and sets the tool filter to the row's tool.
  - **Macro variables** opens `MacroVariablesWindow` (WPF, owned by the viewer, filterable) fed by the page through the `meimad:variables` channel; it updates with playback and row selection and tells the page when it closes.

**Decisions made while implementing (reversible):**
- Breakpoints live for the viewer session (not saved with the program).
- Selecting a row with Tool follows row on narrows the tool filter to that row's tool; *All cutting tools* restores the full view.
- The tree lists what the program text calls; a macro that computes its program number (G65 P#1) is shown as computed and its target appears once it runs.

**Tests:** `NcEngineMacroExecutionTests` (tree with memory macros behind an untaken branch, attached subprogram, same-file program, computed call; mill trace levels; lathe trace order and system reads), client `PlaybackCoreTests` (breakpoints, row targets, variables per level/step) and `MacroVariablesTests`; the page glue was checked in headless Edge (tree list, gutter breakpoints in both editors, halt, row follow with tool filter, variables payload). Not verified: the WebView2 page and the WPF window inside the running client.

## NC viewer: row-by-row single step, next called program, breakpoints and variables of all programs (2026-09-30)

**Owner feedback (after 0.1.169):**
> the Subprogram window should show the active called subprogram, the single step should step in the subprogram. Show the active or next be called macro program in Subprogram window. Option of break point should be active also in main program. The Macro Variables table should show all used variables, include memory subprograms

**Implemented:**
- Single step now follows the engine's executed-row trace instead of the drawn segments: every executed row is a step (macro statements, calls, M99, rows of called programs at any depth), landing after the row's own move or, for a row without motion, where the tool stands when it runs. A breakpoint row is reached before it runs, a stop row after; the next step then runs it. A halt while playback runs lands single step on the halted row, so stepping continues from a stop or breakpoint. Halts on rows already stepped past at the same playback position are not repeated by Run (`haltsUpTo`).
- The called-program pane shows the active row of the called program while stepping or running inside it, with the calling row marked in the NC program (amber), and previews the next program to be called (with its calling row) while the tool is in the main program, from the start and after Stop.
- Breakpoints toggle on the row number as well as the dot column, and with F9 on the cursor row, in both editors; the NC program's markers are re-rendered after the renderer replaces the text, and the editor is attached through a MutationObserver if it appears late.
- The macro variables list is the union of the executed variables and every `#n` named in the program text and in every called program's text (`referencedVariables`), so a Renishaw macro's variables appear even when its branch never ran; the window's position reads "Before/After row n" while stepping. Engine stops carry the executed-block step so halts at one playback position keep the control's order.

**Decisions made while implementing (reversible):**
- Single step includes rows without motion (like SBM = 1 on a FANUC); there is no separate "skip macro statements" mode.
- Landing after a row puts the tool at the end of the row's move; landing before a breakpoint row puts it at the start.
- The next-call preview shows the first called program of the tree when nothing further is called from the current position.

**Tests:** rewritten `PlaybackCoreTests` (execution order and current row, step targets, caller and next call, halts in execution order with passed halts, variables before/after a step per level); the page glue was checked in headless Edge (breakpoints from the pane's line numbers and F9 in the main editor, six steps into the macro up to its breakpoint, the M01 skipped with optional stop off, Run halting on the main-program breakpoint without repeating the M01, variables at the stepped row). Not verified: the WebView2 page inside the running client.
