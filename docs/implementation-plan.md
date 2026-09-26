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

- **OD-012 - Authentication/authorization:** Choose human identity, roles, TV/device credentials, administrator boundary, and audit requirements.
- **OD-013 - Edit lease:** Resolved that every implemented planning mutation requires the current client ID and generation. The stored lease deadline is the transfer-response deadline, not a heartbeat. Define heartbeat, disconnect/crash/restart behavior, and stale unsaved edits.
- **OD-014 - Transfer contention:** Resolved MVP to one pending requester, no queue, Reject returning the requester to Viewer, and a configurable 1–3600 second server timeout with a 30-second default. Define cancellation, notifications, takeover safeguards, history retention, and audit.

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
