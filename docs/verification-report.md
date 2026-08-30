# Architectural and correctness verification report

This report retains earlier milestone evidence below. The current Tasks 24-32 repository aggregate results are the separately executed Debug and Release suites recorded here.

- **Baseline verification date:** 2026-08-11
- **Current change review date:** 2026-08-28
- **Repository:** `Meimad-ProductionPlanner`
- **Baseline runtime tested:** .NET SDK 10.0.303, Release
- **Requirement evidence matrix:** [Tasks 24-32 completion audit](tasks-24-32-completion-audit.md)
- **Verdict:** Fresh separately executed Debug and Release evidence is 863 tests in each configuration (619 Server and 244 Windows Client), with no failures or skips. The added Server-maintenance slice reports database/WAL/shared-memory size without exposing the path, downloads a new restore-verified backup over the factory-LAN HTTP route with SHA-256 verification, and allows preview/recount/backup-before-delete only for the three fixed non-authoritative CNC diagnostic collections. Planning, release, workflow, cycle/output, anomaly, current-state, and audit records remain outside the deletion catalog. Tasks 24-30 add the immutable schema-v61 foundation (including the v59 anomaly ledger/queue, v60 controller mappings, and v61 persistent/alias enforcement), protected success/failure resolution and macro-version blocking, audited Windows recovery, development-only CNC and expanded E-Ink simulators, and one integrated 29-step development workflow test through retroactive session closure and readable diagnostics. The full audit additionally made OLC, verification-result, and cycle identities fail closed; binds SVS/SVF to the exact release token and nonce so delayed old results cannot resolve a newer challenge; rejects conflicting reused cycle identifiers; and classifies non-pending result replays without false expiry. HTTP coverage proves recovery route activation, Edit Mode rejection, replacement release preservation, and revocation. Task 32 documentation is reconciled. Task 31 has a blocking physical `FAIL`: the VF-3SS accepted a correct response after at least 130 seconds at M109, its `#3001` sequence is not monotonic across reboot/wrap, and quarantined macro versions 3-5 do not emit the result-correlation evidence now required by the Server. Those macros and packages v1-v3 remain forensic artifacts. A separate structurally tested macro-v6 no-motion candidate now uses a protected finalizer, exact result correlation, and one persistent sequence, but remains unsigned and physically uncommissioned. The fail-closed record audit reports 4 `PASS`, 1 `FAIL`, 9 `NOT_TESTED`, five incomplete Machine/controller fields, and both sign-offs missing; protected CNC execution, physical tablet behavior, and factory acceptance are not production-approved.
- **2026-08-30 commissioning update:** The bounded R2 physical attempt quarantined macro v6 after alarm 903 left the sixth M109 ASCII value in `#10500`. A distinct macro-v7 desk candidate adds per-digit cleanup and an immediate four-variable finalizer cleanup barrier; its generator/manifest tests pass, but it is unreviewed, physically untested, and not approved for loading or enablement.

Current Tasks 24-32 repository result, reproduced separately in both Debug and Release:

| Test assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `Meimad.Planner.Server.Tests` | 619 | 0 | 0 |
| `Meimad.Planner.Client.Windows.Tests` | 244 | 0 | 0 |
| **Total** | **863** | **0** | **0** |

Tasks 24-32 completion boundary:

| Task | Repository status | Remaining external gate |
|---:|---|---|
| 24 | Implemented: immutable typed anomaly ledger, idempotent detection, bounded read queue, deterministic messages, and tests. | Silence-only `offset_loader_interrupted` and `tablet_offline` require an approved authoritative signal/threshold before automatic detection; the Server does not fabricate them. |
| 25 | Implemented: editor-authorized invalidation, Offset Loader revocation/replacement, tablet reassignment/credential rotation/revocation, ordinary QC retry, and attributed audit. No bypass route exists. | Physical recovery procedure rehearsal. |
| 26 | Implemented: expected version per Machine, strict reported-version comparison, blocking anomaly/message, and tests. | Physical protected-macro report proof. |
| 27 | Implemented: Machine-scoped transport/three-program/alias/five-variable/encrypted-secret/version/digits/timeout/enablement configuration with M109/persistent-range validation; sensitive tablet projections are excluded. | Site-qualified CNC engineer approval of collision-free mappings and key provisioning. External HFO approval is not required. |
| 28 | Implemented: development-only loopback TCP/ASCII simulator with strict scenarios for failure, stale/current releases, sequencing, delay, duplicates, out-of-order evidence, cycles, and next Run. | No physical claim. |
| 29 | Implemented: existing browser simulator covers all fixed states, verification/failure/expiry, offline last-known-good, low battery, revision change, and only official scoped `SEND_TO_QC`. | Physical panel behavior remains separate. |
| 30 | Implemented: one real-service automated scenario covers all 29 requested steps and verifies quantities, anomalies, audits, closure, and readable timeline. | No physical claim. |
| 31 | Checklist, public-vector grader, protected-macro audit reproducer, matched no-motion NC pair, packaging, and fail-closed readiness audit implemented. Current decision is `NOT_READY`; generation and Machine-specific ZIP creation refuse by default, and explicit audit-only reproduction retains the physical-timeout quarantine. | Complete written internal review and collision approval of the separate v6 finalizer/persistent-counter candidate, then run one bounded no-motion retest; nine checks, five Machine/controller fields, two sign-offs, and the one-Machine pilot remain. |
| 32 | Implemented: required documents explicitly retain `REMOVED` persistent mode and `SUPPORTED` protected temporary handshake variables, with implementation/commissioning status reconciled. | Continue same-change documentation discipline. |

Focused development evidence also includes successful builds and `--validate-only`
runs of `tools/Meimad.Planner.CncSimulator/scenario.full.json` (15 strict DPRNT
events) and the matched verification-commissioning scenario, successful compilation
of the production, contract-test, and backend-free demo ESP32 environments, plus
the E-Ink simulator contract test. Macro/grader/package tests validate the public
vectors, strict output transcript, no-motion O1991 test Offset Loader, first-block
O1990 test NC, sensitive local-config exclusion, archive contents, hashes, default
generation/package refusal, and the mandatory quarantine marker. They do not
validate Haas M109/look-ahead timing.
The commissioning-record gate correctly rejects `-RequireReady`. The tablet
administration integration test now also proves that registration, replacement
assignment/credential rotation, and revocation retain editor-attributed audit rows.
The integrated CNC workflow test
uses real Server services and schema rather than an unauthenticated simulator
mutation endpoint. See [CNC commissioning checklist](cnc-commissioning-checklist.md)
for the physical evidence still required. The
[NGC engineering behavior tests](haas-ngc-engineering-machine-tests.md) map the
six open controller questions to checksummed no-motion probes using the selected
`PERSISTENT_COUNTER` contract. The
[bounded retest](haas-bounded-retest-after-internal-approval.md) is prewritten and
limited to 30 minutes. The internal CNC/design approval is recorded, but the
retest remains prohibited until the disabled Server configuration is updated
from the v5 mapping to the reviewed v6 mapping. External HFO approval is not a gate.

Installer build evidence was refreshed on 2026-08-30: `installer\build-installers.ps1`
published self-contained `win-x64` version `0.1.47` payloads and produced both MSI
packages.
The final client package is 107,583,725 bytes (SHA-256
`D1726D270E471873EA5A3718DF3E58EC4B21FCFF2CB5BB4462824E50265EC30D`) and the
Server package is 43,530,556 bytes (SHA-256
`BD0A37F6DD8429852E4F1A0977B01A372B9BC72941B45932661EA4AD4411B34F`). The final
WiX builds completed with zero warnings and zero errors. MSI table inspection proves the
advertised all-users Start Menu shortcut is owned by the executable component in the
same feature; a source-level regression test guards that relationship. A fresh
non-installing administrative extraction verified 641 client files, including the
client executable and `runtimes\win-x64\native\TKernel.dll`, plus 400 Server files.
The extracted Client assembly was also checked for the visible O9003 finalizer
and persistent event-sequence configuration fields.
The extracted Server payload also contains the updated 800×480 monochrome E-Ink
simulator with all 475 classic TFT bitmap glyph bytes, D1/D2/D4 short/hold
controls, Service/Debug screen, physical tablet status/event routes,
`WAITING_FOR_OPERATOR`, and fail-safe live-code clearing.
Windows Installer table inspection verified the automatic `Meimad Planner Server`
ServiceInstall entry, install-start/stop/remove ServiceControl entry, and compiled
`Wix4ServiceConfig` table, and matched both MSIs against the generated two-entry
`SHA256SUMS.txt`. Source and package verification enforce the bounded
policy: restart after 60 seconds for the first and second failures, no third
automatic action, reset after one day. A non-elevated quiet upgrade was correctly
refused with error 1730, leaving the installed 0.1.46 service and ProgramData state
unchanged; therefore this proves package construction and payload shape, not an
elevated upgrade, applied recovery policy, code signing, or production-host acceptance.
The non-mutating Server-upgrade preflight additionally passed against the running
Server: all configured CNC verification settings were disabled, the MSI version
and hash matched, and the result was `READY_FOR_ELEVATED_INSTALL`. The elevated
path is intentionally not self-elevating.

## 0. Schema-v25 change addendum (combined automated pass; manual checks pending)

Static documentation/code review identifies the following additions in the current working tree:

- schema v10 Machine Type catalog/linkage, Setup Calendar singleton selection, and allocated-Order lifecycle migration;
- Working Calendar read/update/guarded-delete, usage/work/break/dated-exception authoring, and dedicated Setup Calendar read/set/clear routes;
- Machine Type CRUD, linkage, inherited compatibility capabilities, and guarded deletion;
- cross-type Machine assignment warning/confirmation, mandatory reason validation, and immutable actor/type/time audit;
- linked-Order `active` / `in_production` / `complete` derivation plus quantity/status edit guards;
- Planning Board planned quantity, allocated Order references, nullable input-derived estimated time, compact cards, and player-style execution controls;
- a compact connection/Edit Mode header, dedicated Setup page, optimistic Order editing, and one canonical read-only Timeline shared by the main window and separate closable window; and
- schema v11 isolated Employee/Resource and Israeli-holiday CRUD plus optimistic report/email settings; schema v12 adds detailed employee fields, exact roles, normalized skills, restrictive role-compatible Calendar linkage, photo/notes storage, an active-only resource view, and an explicit editor-gated Setup action for editing the selected employee; schema v13 adds employee exception CRUD and deterministic exception-aware availability; and
- schema v16 extended Case/Batch Operation timing snapshots, configurable day-shift-only policy, manual initial-load/per-part-cycle and automatic initial/repeated-load/every-N-cycle cadence using unchanged fields, per-event regular-worker reservation, the pre-materialization `load_unload_occurrence_limit_exceeded` blocking guard above 10,000 non-zero-duration occurrences for either cadence, and extended compact-card estimates.
- individual employee contention, setup Machine-skill matching, calendar/break/exception-aware worker reservations, and visible resource-waiting explanations without backlog mutation.
- deterministic resource priority by earliest allocated-Order Work Finish Date and naturally smaller Order Number, with the deciding reason retained in the delayed interval and no persisted backlog mutation.
- schema v17 planned-maintenance and open-breakdown storage/API lifecycle, optimistic maintenance edit/Restore, Setup Machine Availability UI, and reason-preserving Timeline/TV downtime projection.
- API-level assignment persistence/reload into Timeline, same-Case operations split across Machines, duration derivation without planned timestamps, failed-dependency propagation without false cycle classification, precise missing-worker-role conflict text, structured Timeline-input logging, and replay of a client Timeline read invalidated while in flight.
- explicit paused-operation Reset to `not_started`, retained assignment/backlog position, closed pause event, derived Batch/Order rollback, structured `operation_reset` audit, and compact client control/refresh behavior.
- nearest-feasible fixed-backlog Timeline placement with operation-linked Machine/setup/day-shift/resource/downtime/pause/dependency waiting, assigned-but-blocked visibility, no-leapfrog propagation, common resource retry for locked groups, and overlapping-wait renderer lanes.
- schema v23 authoritative Start/Finish actual timestamps and Machine history, Reset clearing, floating not-started forecasts, fixed in-progress actual start, completed historical blocks, completed-predecessor actual-finish constraints, and forecast/actual Timeline UI metadata.
- schema v24 `forward`/`backward`/`manual` planning intent on each Machine Assignment, strict migration/database tokens, exact assignment ETag mutation, atomic actor/before/after event logging, move/reset preservation, and Planning Board assignment identity/version/mode fields.
- schema v25 `legacy_working_plan_imports` receipts and bounded cached-value OpenXML preview remain; schema v27 preserves old receipts and permits separately audited incremental Case/Order-only approvals for one workbook while retaining the changed-planning conflict. The active Windows page bypasses the former five-step compatibility wizard and exposes one fixed Case/Order preview plus one explicit Import action.
- schema v28 encrypted Server-local Kitaron connection/test configuration, schema v29 reviewed mapping storage, schema v30 one-way sync state/source links, and schema v31 Case Components/BOM counters. The four-step localhost UI can test metadata, save a Ready mapping, run synchronization, and display Case/Order/Operation/Component counts. Kitaron remains read-only; direct BOM descendants are Case Components, stopped order lines are cancelled, missing owned BOM edges deactivate, matched manual records are not overwritten, and no child Order/Batch/assignment/backlog/Timeline state is created.
- a fixed Case A/O/F/D and active Order B/L/E/N workflow that creates one missing Case per Part, links related Orders by stable source row, matches existing Case + Order Number pairs, reports invalid rows as skips, and submits empty planning/Machine selections. It does not create Batches, Operations, Machines, assignments, backlog, Timeline entries, or planning state.
- reviewable Similar/All pattern expansion that never overwrites existing decisions, never reuses a one-to-one existing Batch Operation, copies only target-offered reusable candidate IDs, recalculates the supported stock/scrap allocation from each target quantity, and applies optional unique Batch numbers only from an explicit `{part}`/`{reference}`/`{row}` template. Expanded rows are ordinary explicit commit selections and are revalidated atomically.
- importer idempotency and rollback: exact approved-payload replay remains available from the durable receipt after staging expiry/eviction/restart; a changed Case/Order-only pass for the same workbook may append a separate receipt and create remaining canonical records, while a changed approval with planning content is rejected. All-Skip/no-op cannot consume a receipt, and a later invalid row rolls back every earlier entity/assignment/event in that request.
- one canonical Timeline with assignment-owned mixed-mode calculation, reverse dependency/backlog latest-fit placement from the earliest linked Order Work Finish Date, deterministic earlier-date/shorter-duration/natural-Order contention, no global mode selector/query, one normalized operation block per assignment, duplicate-block logging, and no persisted calculated dates or backlog mutation.
- strict active-operation identity coverage proving one identified block globally even with calendar/resource waits, downtime, direct move, resume, and unassign/reassign; former-Machine occupancy remains anonymous, its exact facts are folded into the current block, pause/transfer boundaries remain correct, and the WPF capacity band cannot cover or label itself as another operation.
- assigned-but-unplaceable rows rendered as lower-band `blocked` waiting only after preceding calculated backlog work; actual/hold/history-authoritative same-Machine overlap reconciliation; fixed-point propagation through later backlog rows, Sequential chains, and locked groups; and deterministic WPF sublanes/point markers without horizontal time changes.
- phase-aware WPF operation rendering that keeps one assignment host, colors `PRODUCTION` blue, `SETUP` yellow, QA-as-`QC` green, every repeated `PART RELOAD` phase purple, and locked reservation orange; leaves internal availability gaps transparent; suppresses generic idle and ordinary anonymous waiting bars; explains `BLANK = NO OPERATION`; and retains paused `HOLD`, downtime, actual history, and assignment-owned `BLOCKED` states.
- additive per-Machine `nonWorkingWindows`, derived from the same timezone-aware Working Calendar expansion used by scheduling and painted as gray row backgrounds rather than operation/capacity blocks; coverage includes closed weekdays, breaks, overnight spill, distinct calendars, preserved downtime, and invalid-calendar fallback.
- additive factory display-time metadata plus a DST-aware two-row local hour ruler with header-only configured DAY/DARK bands, adaptive hour/date density, and a bounded drawing-backed long-horizon render plan that has no Timeline identity or scheduling effect.
- Server-`readAt`-anchored floating forecasts: `not_started` Forward/Manual work earliest-fits at/after the snapshot cursor and cascades through the stored backlog/Sequential graph; a missed Backward start falls forward transiently with `backward_start_missed`, preserves persisted mode/backlog, and returns a deadline warning when late; no-fit work is an identified blocked marker and elapsed historical horizons do not fabricate forecasts.
- one red labelled WPF `NOW` marker estimated from `readAt` plus elapsed time in the configured factory timezone; one shared 30-second throttle refreshes only while assigned `not_started` forecast or blocked work exists, so embedded/separate Timeline windows do not double-poll, calculate, or mutate planning state.

Current schema-v25 combined Release result:

| Test assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `Meimad.Planner.Server.Tests` | 370 | 0 | 0 |
| `Meimad.Planner.Client.Windows.Tests` | 192 | 0 | 0 |
| **Total** | **562** | **0** | **0** |

Current import regressions cover the exact authoritative A/B/D/E/F/L/N/O multipart mapping, Case creation by Part Number, Order creation under the matching/new Case, existing Order no-change handling, invalid-row skip reporting, an executable final Import command, and a commit body with null planning sheet plus empty Machine/planning selections. Server integration additionally proves Case/Order replay idempotency and unchanged Production Batch, Batch Operation, and Machine Assignment counts. Broader legacy endpoint compatibility tests remain green, but those capabilities are bypassed by the visible Windows tool. Automated tests do not establish production data acceptance; a backup/rehearsed commit and count reconciliation remain pending.

Current Release command:

```powershell
dotnet test .\tests\Meimad.Planner.Server.Tests\Meimad.Planner.Server.Tests.csproj -c Release --no-restore
dotnet test .\tests\Meimad.Planner.Client.Windows.Tests\Meimad.Planner.Client.Windows.Tests.csproj -c Release --no-restore
```

### 0.1 Supplied-workbook preview smoke

The operator-supplied `data/Working plane.xlsx` was exercised through the freshly built Server's actual HTTP multipart preview endpoint against an isolated temporary database; no Edit Mode or commit endpoint was used. The order-driven `גיליון1` layout produced 373 unique Case Parts, 557 grouped Part+Order rows, and 557 Part+Batch Number rows. The summed Batch planned quantity was 10,996 and exactly equaled the summed related-Order allocation quantity. Validation returned zero blockers and 239 warnings (primarily explicit aggregation/matching review notices). The original workbook was read only and was not modified.

The exact sheet names/dimensions, detected columns, proposed meanings, representative value formats, all 16 Machine-section labels, unclear-field and issue-code breakdown, current database entity mapping, transient staging design, and manual production-import checklist are in [Legacy Excel planning import report](legacy-excel-import-report.md).

This smoke establishes parser/layout compatibility for the supplied copy only. It does not approve candidates or mutate the planning database. Before the first real commit, create and verify a Server backup, rehearse on a database copy, review the 373 Case, 557 Order, and 557 Batch decisions and all warnings/Skips, reconcile results, and obtain operator sign-off.

## 1. Scope and method

Feature development was stopped for this review. The audit used four forms of evidence:

1. The project baselines in [Functional specification](functional-spec.md), [Architecture](architecture.md), [Data model](data-model.md), and [API contract](api-contract.md).
2. Static review of Server, Windows client, TV Dashboard, simulator, persistence migrations, and API route registration.
3. Existing focused unit, persistence, migration, HTTP, concurrency, and client-presentation tests.
4. A new deterministic end-to-end acceptance fixture that uses the real schema, Server services, TestServer HTTP pipeline, timeline engine, dashboard projections, E-Ink authorization/file delivery, and backup service.

The fixture is [acceptance-dataset.sql](../tests/Meimad.Planner.Server.Tests/Acceptance/acceptance-dataset.sql), embedded in the test assembly and executed only through the internal Server-owned `SqliteDatabase` test boundary. Its verification is in [EndToEndAcceptanceTests.cs](../tests/Meimad.Planner.Server.Tests/Acceptance/EndToEndAcceptanceTests.cs). It is not application functionality or production seed data.

No Customer Portal, cloud infrastructure, public-Internet integration, native mobile application, or automatic scheduling implementation was found or added. A separate ESP32 prototype now exists with a bounded status client and first production screen layout; its compile evidence is narrower than the Server/Windows test evidence in this report.

## 2. Executive result

The repository demonstrates a coherent client-server vertical slice:

- only the Server project references SQLite;
- schema migrations and foreign keys are active;
- every implemented planning mutation checks the active Edit Mode client/generation in the same immediate SQLite transaction as its write;
- Batch creation and allocation are atomic;
- Machine Assignment operations preserve stable, contiguous backlog order;
- timeline calculation is deterministic, read-only, and does not reorder the plan;
- TV is read-only; E-Ink assigned reads and the implemented, separately scoped `SEND_TO_QC` command cannot mutate planning data;
- E-Ink credentials are device-scoped and blocked from other API surfaces; and
- SQLite backups are consistent, integrity-checked, and restored to an isolated test location for verification.

This is not yet production-ready. The largest blockers are the absence of production human/TV authentication and TLS, and a backup boundary that protects SQLite metadata but not the official package-file root. Several contract surfaces also remain incomplete, particularly Case Operation reorder, Calendar combined-overnight/overtime/archive policy, downtime recurrence/cancellation, and a standalone conflict resource. A single overnight working window is implemented and covered.

## 3. End-to-end acceptance dataset

| Dataset area | Included evidence |
|---|---|
| Cases | 10 Cases, `ACC-001` through `ACC-010`, with external Working Folder paths and varied material/timing data. |
| Orders | 15 Orders across the Cases, with active and completed demand and urgent/normal Work Finish Dates. |
| Production Batches | 11 Batches: a two-Order combined Batch, two Batches splitting one Order, a stock-only Batch with scrap, a mixed Order/stock/scrap Batch, and ordinary one-Order Batches. Schema v9 normalizes their Server-owned aggregate lifecycle. |
| Allocations | 15 explicit allocation rows. Every Batch satisfies `planned quantity = Order + stock + scrap`; all Order allocations belong to the Batch Case. |
| Operations/dependencies | Two Case Operations and two scalar/dependency-snapshotted Batch Operations per Batch. The dataset contains `SEQUENTIAL`, `PARALLEL_CAPABLE`, `INDEPENDENT`, and `LOCKED_SIMULTANEOUS` semantics. |
| Machines | 15 active, display-enabled Machines spanning mill, lathe, and inspection process types and varied axis/capability tokens. |
| Calendars | Three explicit UTC calendars: day, extended, and deliberately limited availability, plus a setup calendar. |
| Assignments | 22 stable, gap-free assignment positions across Machines M-01 through M-12; M-13 through M-15 provide intentional idle-dashboard coverage. |
| Downtime | Three intervals: current inspection, future maintenance, and CMM calibration. |
| Conflicts | Deliberate `missing_timing` and `insufficient_availability` conditions, returned as explained blocking conflicts without plan mutation. |
| TV data | 15 display Machines, current/next jobs, idle rows, urgency, current/future downtime, and calculated conflicts. |
| E-Ink data | One bound E-Ink device, one immutable official package revision, tool-cart metadata, one instruction asset, SHA-256 manifest data, and actual Server-owned package bytes. |
| Backup | Online SQLite snapshot, integrity and foreign-key checks, isolated restore verification, and restored row-count checks for Cases, Machines, and E-Ink package metadata. |

The dataset deliberately does not simulate device-local checklist/comments. Focused execution tests, rather than the broad fixture, verify the authoritative Batch `waiting` / `in_production` / `complete` transitions.

## 4. Review matrix

| Area | Result | Evidence and conclusion |
|---|---|---|
| Specification compliance | **Partial pass** | Required MVP component boundaries exist and excluded products are absent. The implemented subset is accurately described as partial in the project docs. Production security, some resource APIs, operating policies, and physical-device acceptance remain unresolved. |
| Data model | **Focused automated pass / policy gap** | Schema-v25 tests cover the original legacy-import receipt; schema-v27 covers preservation of an existing v25 receipt, workbook-plus-request uniqueness, incremental Case/Order receipts, and startup/upgrade behavior. Schema-v24 coverage preserves assignment identity/position/version while adding checked planning mode. Earlier coverage includes Machine Types, Setup Calendar selection, administrative resources, employee exceptions, holiday policy, cross-type overrides, and schema-v23 actual-time history. Aggregate route revision, arbitrary dependency fan-in/out, and broader audit rules remain open. |
| API boundaries | **Automated pass / coverage gap** | Windows and web clients remain HTTP/API-only and contain no SQLite dependency. Focused tests pass for bounded legacy `.xlsx` preview and atomic commit, Working Calendar CRUD/overnight expansion/Setup selection, Machine and Machine Type CRUD/linkage, planned-maintenance/breakdown mutation, and allocation-safe Order PATCH. Route reorder, Calendar combined-overnight/overtime/archive policy, downtime recurrence/cancellation, and standalone `/api/v1/conflicts` remain absent; human/TV auth is absent. |
| Edit token concurrency | **Core pass / operational gap** | Immediate transactions, singleton token, unique pending request, generation invalidation, Release, Reject, voluntary release, and configured no-response transfer pass concurrency tests. Caller headers are not authenticated, and heartbeat/disconnect, notification, cancellation, audit, and history retention policies remain open. |
| Batch allocation / Order lifecycle | **Automated pass / policy gap** | Historical allocation tests and current aggregate Order-lifecycle tests pass for multi-Order, split/partial/full completion, atomic Batch create/delete/operation recomputation, cancelled-allocation rejection/preservation/resume, server-owned status, and quantity/status edit guards. Cross-Batch over-allocation and later reallocation/cancellation policy remain undefined. |
| Machine assignment ordering | **Focused automated pass** | Compatible assign/move/unassign, stable contiguous ordering, running-head displacement rejection, linked Machine Type capabilities, unsafe type-update/rename protection, explicitly warned/reasoned/audited cross-type overrides, and assignment-owned mode PATCH are covered. Mode tests verify exact ETag concurrency, idempotent no-op, actor/before/after audit, same assignment/Machine/position, unchanged neighboring row versions/timestamps, and mode preservation through move/reset. Inactive Machines remain non-overridable. No automatic Machine choice, route change, or scheduling is present. |
| Time calculation | **Focused automated pass / model gap** | Deterministic tests cover assignment-owned forward/manual earliest-fit and backward latest-fit nodes in one fixed backlog/dependency graph, one normalized assignment/blocked block, same-Machine non-overlap reconciliation, blocked rows after preceding work, recurring/local calendars, individual employee contention and setup skill eligibility, breaks/absence/holidays, setup/QA/manual phases, downtime, locked reservations, cycles, deadline conflicts, and unchanged persisted assignment/date/order facts. Display tests additionally cover factory-zone hour labels, DST gaps/repeated hours, and bounded DAY/DARK ruler rendering without changing calculation inputs. Persisted worker assignment, qualification expiry, and broader calculation performance targets remain open. |
| Conflicts | **Partial pass** | Timeline/TV return deterministic, explained conflicts such as missing timing, invalid Machine calendars, cycles, invalid dependencies, same-Machine simultaneous work, and insufficient availability. A missing separate setup calendar now produces an attention explanation and Machine-calendar-only scheduling rather than suppressing operations. There is no standalone conflict API, persisted conflict identity/history, due-date-risk rule, acknowledgement policy, or complete severity catalog. |
| Backups | **Database pass / system gap** | Online snapshot, timestamped unique file, count retention, integrity/FK checks, isolated restore, active-DB protection, corruption rejection, and concurrent-write tests pass. Official E-Ink package bytes and external Case folders are outside the SQLite backup and have no coordinated backup/restore policy. Scheduling, encryption, access control, RPO, and RTO remain open. |
| TV Dashboard read-only behavior | **Automated pass / visual and security gap** | Projection and UI are GET-only; POST returns 405. Static regression tests require status-only markup, no form/input/button, no Server URL/host text, hidden viewport overflow, dynamic grid fitting, connection-dot states, conditional refresh, and no job/urgent rendering. Last-known status is retained on failure. TV authentication and physical 1080p/4K kiosk/read-distance acceptance remain unverified. |
| E-Ink scoped behavior | **Implemented read side and scoped workflow command / compiled layout, revision, state, input, power, battery-metadata, and backend-free demo policies / physical and telemetry-history gaps** | Device-scoped token validation, revocation/rotation, version ETag, Machine screen, revision-qualified manifest/files, checksum enforcement, time config, simulator, and cross-surface authorization behavior pass. Schema-v55 tests cover the exact `SEND_TO_QC` body, path/token scope, Server-resolved current Run, `IN_SETUP_RUN` eligibility, transition to `IN_QC`, planning-fact preservation, status revision, sequential/concurrent retry idempotency with the original Server timestamp, and a distinct new attempt after `QC_FAIL`. The ESP32 production, demo, and focused contract-test images compile with the bounded status parser, text-only Machine/part/Operation/status layout, three-row tool pagination model, no-image rendering, revision decisions, all seven status-to-wake policies, page boundaries, wake-button mapping, single-attempt guard, `IN_SETUP_RUN`-only send eligibility, network-required/local-only action decisions, voltage-header formatting, and all ten required demo fixtures. Firmware persists completed screen revision/tablet identity, selected tool page, and displayed battery-warning state in NVS; it skips equal status updates, preserves the retained screen on failure, logs actual `update()` duration, configures timer/EXT1 sources, debounces/releases/logs button actions, follows a guarded send with status GET, and renders temporary accepted/confirmed/rejected/uncertain notices. It retains prior sleep policy in RTC memory, logs wake reason/available UTC time/battery/pre-sleep state, sends bounded metadata on ping/status/event HTTP calls, displays a provisional 3.30 V `LOW BATTERY` warning, skips Wi-Fi for local button wakes, and disables the radio after HTTP work and before deep sleep. The demo image uses only compiled local fixtures and has no Wi-Fi, HTTP, telemetry, or event-submission path. The assertions, demo controls, RTC retention/pull-up current, timer cadence, GPIO actions, Wi-Fi-off behavior, header receipt, battery accuracy/threshold, timestamp continuity, confirmation display, and persistence/refresh sequence have not been executed on hardware. Mandatory configured-workday/shift enforcement is not implemented, so automatic polling is development-only and not production-accepted. Battery observation history/retention and any percentage model are unimplemented. Official package tools are not yet bound to the layout. There is no checklist/comment write-back or USB endpoint. Physical panel readability/clipping, page actions, SD/error/package-last-known-good behavior, device secret storage, signatures, superseded-package retention, and hardware acceptance are not verified. |
| Windows QC Queue | **Automated pass / operational identity gap** | View Mode lists only Runs whose latest event is `SEND_TO_QC`, with Machine, multi-output part/Operation summaries, receipt time, and latest packaged setup worker when known. PASS/FAIL require matching active editor client, user, and generation; invalid state/payload/reason, stale or spoofed authority, E-Ink credentials, unknown Runs, and concurrent decisions are rejected. Immutable events retain user, Server UTC, and optional reason; FAIL permits a distinct resend and PASS exposes its receipt time as `production_approved_at`. Human identity headers are still development identity rather than production authentication. |

## 5. Findings and required decisions

### High priority

**VR-001 — Production identity and transport security are not implemented.**

Windows mutation authority currently begins with caller-supplied `X-Meimad-Client-Id`, `X-Meimad-User-Id`, and generation headers. These prove edit-token state but not a human identity. TV data is anonymous, and Kestrel is configured for HTTP. The default bind is loopback, but configurable wildcard/LAN binding without authenticated identity or TLS would expose planning data and allow client-ID impersonation to any reachable host. Before a production LAN deployment, define authenticated Windows/admin/TV identities, authorization policies, TLS/certificate and firewall configuration, secret handling, and audit events. Keep public Internet exposure prohibited.

**VR-002 — A database-only backup cannot restore the official package system.**

SQLite stores E-Ink package metadata and checksums while file bytes live under the configured Server package root. The backup service captures only SQLite. A successful database restore can therefore point to missing package files and make official device output unavailable. External Case folders are intentionally outside SQLite as well. Define a coordinated, access-controlled backup/restore set for SQLite plus the Server-owned package root, and separately document the responsibility for external Case folders. Verify cross-component consistency, retention, encryption, RPO, and RTO.

### Medium priority

**VR-003 — Resolved: dependency snapshots are immutable with a Production Batch.**

Schema v9 stores dependency type, predecessor source Case Operation ID, and simultaneous-group values with each Batch Operation and backfills existing Batches. Timeline mapping resolves that source ID to the corresponding operation within the same Batch and reads no mutable Case Operation dependency fields. Optimistic Case Operation edits therefore affect only future Batches. Aggregate route revision, route reorder, and arbitrary fan-in/out remain separate open work.

**VR-004 — Required resource/contract coverage is incomplete.**

The current route set adds weekly Working Calendar usage/work/break/dated-exception read/update/delete with one-window overnight support, dedicated Setup Calendar selection, Machine Type CRUD, and planned-maintenance/breakdown downtime lifecycle, but still lacks route reorder, Calendar combined-overnight/overtime/archive/automatic-holiday policy, downtime recurrence/cancellation, and standalone `/api/v1/conflicts`. The Markdown route-coverage table presents some target paths alongside implemented paths, so it must not be treated as a frozen OpenAPI contract. Verify the new routes and decide the remaining boundaries before declaring the domain API complete.

**VR-005 — Cross-Batch Order allocation policy is unresolved.**

Creation validates each Batch internally, and the new Order PATCH guard prevents reducing demand below the current aggregate allocation. Nothing prevents cumulative allocations across active Batches from exceeding an Order's demand, however. Split production is valid, so the missing rule is not simply a uniqueness constraint. Define whether cumulative good-quantity allocations may exceed Order quantity, which Batch/Order statuses participate, and how cancellation/reallocation is handled; then enforce it transactionally.

**VR-006 — Edit Mode is concurrency-safe but not yet an operational lock policy.**

The durable token survives restart and a pending requester can obtain an automatic transfer, but there is no authenticated identity, editor heartbeat, explicit abandoned-session policy, transfer notification channel, requester cancellation, unsaved-change protocol, or audit trail. Resolve those before relying on Edit Mode for production accountability.

**VR-007 — Conflict coverage is useful but incomplete.**

Current conflicts are recalculated inside timeline/TV projections and lack plan revision, standalone filtering, Work Finish Date risk, full capability/calendar policy, acknowledgement, or stable historical meaning. Approve the conflict catalog/severity/identity rules and decide whether a dedicated read endpoint is required.

**VR-008 — Display acceptance remains simulated.**

The TV UI has automated structural tests but no target-resolution, kiosk-browser, viewing-distance, or long-running refresh test. E-Ink has Server API/browser-simulator coverage plus compile-only evidence for the physical firmware's first text layout, pagination model, and debounced button-action policy. Physical execution of its assertions, panel readability/clipping/ghosting, paging/send buttons and confirmation notice, SD loss/corruption, deep sleep, configured shift wake, battery replacement, and last-known-good activation remain required.

### Low priority

**VR-009 — API durability mechanisms are not frozen.**

General create idempotency, pagination limits, OpenAPI publication, consumer compatibility, resource ETags beyond implemented slices, and explicit plan revisions remain incomplete. Freeze these only after the open domain/security decisions are resolved.

**VR-010 — Resolved documentation inconsistency.**

[Data model](data-model.md#71-machine) now consistently records that E-Ink binding, rotation, and revocation are implemented through the device-registration API.

**VR-011 — Resolved: a committed verification baseline exists.**

The repository now has an initial `HEAD` commit. Verification results can be tied to a revision and subsequent work reviewed as a diff; release verification must continue to record the tested commit or artifact hashes.

## 6. Historical baseline test execution

The following command/result applies to the 2026-08-11 schema-v9 baseline only. It must not be treated as evidence for the schema-v10/v11 working-tree additions listed in the addendum.

Historical full-suite command:

```powershell
dotnet test server/Meimad.Planner.Server.slnx -c Release --artifacts-path .qa/delete-edit-release
```

Result:

| Test assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `Meimad.Planner.Server.Tests` | 165 | 0 | 0 |
| `Meimad.Planner.Client.Windows.Tests` | 61 | 0 | 0 |
| **Total** | **226** | **0** | **0** |

The same full solution also passed in Debug with 226 passed (165 Server and 61 Windows Client), 0 failed, and 0 skipped. The acceptance test itself passed in isolation before both full-suite runs.

Coverage includes startup/health, configuration validation, ordered migrations, foreign-key/orphan rejection, Case/Order persistence and API behavior, read-only Case timing sums, transactional Case Operation creation/edit and graph/reference validation, preservation of prior Batch scalar/dependency snapshots, complete Windows Case/Operation/Order/Batch/Machine creation/edit presentation, version-checked Machine editing, confirmation-protected guarded deletion commands, deletion relationship blockers and route compaction, explicit combined/stock/scrap Batch allocation payloads, derived Batch lifecycle transitions, path-only Machine picture persistence/delivery, dependency graph semantics, Batch allocation adversarial cases, Machine assignment ordering, atomic Start/Suspend/Finish/Reset transitions and invalid/concurrent requests, Edit Mode races, deterministic time calculation and setup-calendar fallback, Timeline operation markers, TV and E-Ink scoped surfaces including idempotent `SEND_TO_QC`, job-package generation/integrity, backup/restore verification, Windows API models, and Windows presentation/view-model behavior.

## 7. Acceptance decision

The schema-v25 working tree remains a **development MVP vertical slice**, not a production factory deployment or a visually accepted Windows release. The combined importer/assignment-mode/canonical-Timeline Release suite passes. The manual/environmental checks listed in the addendum remain outstanding, including visual review of the import UI and a backed-up rehearsal before importing the supplied workbook into the current database. Production release should remain blocked until VR-001 and VR-002 are resolved and the owners explicitly accept or close VR-004 through VR-008 with tests and updated contracts; VR-003 remains resolved by schema v9.

Physical TV/E-Ink behavior, production Windows Service installation/upgrades, real factory network security, disaster recovery, performance/scale, and operator workflows were not exercised and must not be inferred from this report.
