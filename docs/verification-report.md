# Architectural and correctness verification report

The task-list stabilization adds focused coverage for no-route Batch rejection, migration v26, STEP bounding/custom-reference calculations, and row-based TV projection/rendering. Final aggregate results come from the Release suites below.

- **Baseline verification date:** 2026-08-11
- **Current change review date:** 2026-08-16
- **Repository:** `Meimad-ProductionPlanner`
- **Baseline runtime tested:** .NET SDK 10.0.303, Release
- **Verdict:** The current combined Release evidence is 543 tests (358 Server and 185 Windows Client), with no failures or skips. It includes the exact order-driven Hebrew Excel mappings, Case/Order deduplication, Part+Batch aggregation, and related-Order quantity reconciliation, plus the prior STEP, assignment-owned planning, periodic PART RELOAD, canonical Timeline, employee editing, calendar-background, and current-time behavior. The supplied 45,497,003-byte workbook passed a fresh read-only HTTP preview smoke as 373 unique Parts, 557 grouped Orders, and 557 Part+Batch groups with planned quantity 10,996 exactly matching related-Order allocations. Windows operator sign-off on a backup/rehearsed production commit and the remaining physical/operational checks remains pending.

Installer build evidence on 2026-08-15: `installer\build-installers.ps1` published self-contained `win-x64` payloads and produced both MSI packages with WiX 5.0.2 at zero build warnings/errors. A non-installing administrative extraction verified 641 client files, including `runtimes\win-x64\native\TKernel.dll`, and 363 Server files. Windows Installer table inspection verified the automatic `Meimad Planner Server` ServiceInstall entry and install-start/stop/remove ServiceControl entry. This proves package construction and payload shape, not elevated install/uninstall, upgrade preservation, service recovery, code signing, or production-host acceptance.

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
- schema v25 `legacy_working_plan_imports` receipts, bounded cached-value OpenXML preview, and a five-step Windows wizard whose selected outcomes filter column/Machine mappings, whose conditional editor resolves only action-relevant fields, and whose final Review table gates Commit. Explicit actions cover Case/Order creation, full-route Batch creation into the unassigned Pool, full-route Batch creation plus one selected Machine assignment, existing unassigned Operation assignment, and Skip.
- an order-driven automatic draft that maps Case A/O/F/D, active Order B/L/E/N, and Batch P/H; creates one missing Case per Part with a system working-folder path; links related Orders by stable source row; and gives each Part+Batch group explicit related-Order allocations whose sum equals its planned quantity. It still does not invent routes/Machines/timing, approve overrides, update existing records, or Commit without review.
- reviewable Similar/All pattern expansion that never overwrites existing decisions, never reuses a one-to-one existing Batch Operation, copies only target-offered reusable candidate IDs, recalculates the supported stock/scrap allocation from each target quantity, and applies optional unique Batch numbers only from an explicit `{part}`/`{reference}`/`{row}` template. Expanded rows are ordinary explicit commit selections and are revalidated atomically.
- importer idempotency and rollback: exact approved-payload replay remains available from the durable receipt after staging expiry/eviction/restart, a changed approval for the same workbook is rejected, all-Skip/no-op cannot consume a receipt, and a later invalid row rolls back every earlier entity/assignment/event in that request.
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
| `Meimad.Planner.Server.Tests` | 358 | 0 | 0 |
| `Meimad.Planner.Client.Windows.Tests` | 185 | 0 | 0 |
| **Total** | **543** | **0** | **0** |

Current tests add schema-v25 fresh/upgrade/startup coverage; safe OpenXML formula/date/archive handling; generic English/Hebrew header detection; unclear-column descriptors and manual remapping; authoritative corrected re-preview; outcome-scoped Orders-only/planning-only envelopes; blocking invalid dates, quantities, and required identifiers; exact reviewed Case-route ID/version checks; preview/mapping/candidate and real-domain validation; full-route Pool snapshot with no assignment and Planning Board visibility; atomic creation/assignment/backlog append; stale Pool-Machine-field rejection; compatibility override/event logging; replay, changed-payload, expired-stage, bounded-stage, all-Skip, and expanded-row rollback paths; typed multipart/edit-header client contracts; and Windows five-step/outcome-filtered mapping/selection/review gating. Client regressions cover failed-refresh stale-preview gating, action-specific stale-field clearing, conditional WPF bindings, Batch/part context, Case-filtered route choices, exact Machine compatibility evidence, mandatory override reason, source-column remapping, safe Similar/All patterns, unique Batch templates, one-to-one existing-operation protection, and invalid/incomplete source data. Automatic-draft regressions cover exact-Machine threshold/tie handling, unique compatible route assignment, Pool fallback, deterministic Batch numbers, full-quantity stock allocations, existing-Order/no-duplicate handling, visible reasoned skips, explicit skip confirmation, preservation of manual decisions/outcomes, stale-preview invalidation, and the absence of automatic Commit or compatibility override. They retain the schema-v24 assignment-mode and canonical Timeline coverage described below. Automated tests do not establish visual or operational acceptance. Manual Windows import review, a backup/rehearsed production commit, production-sized contention, live measurement-source integration, and live SMTP-relay delivery remain pending. Authenticated SMTP credential storage is not implemented. Physical TV/E-Ink, production Windows Service, LAN/TLS/authentication, backup/disaster recovery, and shop-floor display checks also remain pending.

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

No Customer Portal, cloud infrastructure, public-Internet integration, native mobile application, ESP32 firmware, or automatic scheduling implementation was found or added.

## 2. Executive result

The repository demonstrates a coherent client-server vertical slice:

- only the Server project references SQLite;
- schema migrations and foreign keys are active;
- every implemented planning mutation checks the active Edit Mode client/generation in the same immediate SQLite transaction as its write;
- Batch creation and allocation are atomic;
- Machine Assignment operations preserve stable, contiguous backlog order;
- timeline calculation is deterministic, read-only, and does not reorder the plan;
- TV and E-Ink operational surfaces are read-only with respect to planning data;
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
| Data model | **Focused automated pass / policy gap** | Schema-v25 tests cover the additive unique legacy-import receipt and startup/upgrade behavior; schema-v24 coverage preserves assignment identity/position/version while adding checked planning mode. Earlier coverage includes Machine Types, Setup Calendar selection, administrative resources, employee exceptions, holiday policy, cross-type overrides, and schema-v23 actual-time history. Aggregate route revision, arbitrary dependency fan-in/out, and broader audit rules remain open. |
| API boundaries | **Automated pass / coverage gap** | Windows and web clients remain HTTP/API-only and contain no SQLite dependency. Focused tests pass for bounded legacy `.xlsx` preview and atomic commit, Working Calendar CRUD/overnight expansion/Setup selection, Machine and Machine Type CRUD/linkage, planned-maintenance/breakdown mutation, and allocation-safe Order PATCH. Route reorder, Calendar combined-overnight/overtime/archive policy, downtime recurrence/cancellation, and standalone `/api/v1/conflicts` remain absent; human/TV auth is absent. |
| Edit token concurrency | **Core pass / operational gap** | Immediate transactions, singleton token, unique pending request, generation invalidation, Release, Reject, voluntary release, and configured no-response transfer pass concurrency tests. Caller headers are not authenticated, and heartbeat/disconnect, notification, cancellation, audit, and history retention policies remain open. |
| Batch allocation / Order lifecycle | **Automated pass / policy gap** | Historical allocation tests and current aggregate Order-lifecycle tests pass for multi-Order, split/partial/full completion, atomic Batch create/delete/operation recomputation, cancelled-allocation rejection/preservation/resume, server-owned status, and quantity/status edit guards. Cross-Batch over-allocation and later reallocation/cancellation policy remain undefined. |
| Machine assignment ordering | **Focused automated pass** | Compatible assign/move/unassign, stable contiguous ordering, running-head displacement rejection, linked Machine Type capabilities, unsafe type-update/rename protection, explicitly warned/reasoned/audited cross-type overrides, and assignment-owned mode PATCH are covered. Mode tests verify exact ETag concurrency, idempotent no-op, actor/before/after audit, same assignment/Machine/position, unchanged neighboring row versions/timestamps, and mode preservation through move/reset. Inactive Machines remain non-overridable. No automatic Machine choice, route change, or scheduling is present. |
| Time calculation | **Focused automated pass / model gap** | Deterministic tests cover assignment-owned forward/manual earliest-fit and backward latest-fit nodes in one fixed backlog/dependency graph, one normalized assignment/blocked block, same-Machine non-overlap reconciliation, blocked rows after preceding work, recurring/local calendars, individual employee contention and setup skill eligibility, breaks/absence/holidays, setup/QA/manual phases, downtime, locked reservations, cycles, deadline conflicts, and unchanged persisted assignment/date/order facts. Display tests additionally cover factory-zone hour labels, DST gaps/repeated hours, and bounded DAY/DARK ruler rendering without changing calculation inputs. Persisted worker assignment, qualification expiry, and broader calculation performance targets remain open. |
| Conflicts | **Partial pass** | Timeline/TV return deterministic, explained conflicts such as missing timing, invalid Machine calendars, cycles, invalid dependencies, same-Machine simultaneous work, and insufficient availability. A missing separate setup calendar now produces an attention explanation and Machine-calendar-only scheduling rather than suppressing operations. There is no standalone conflict API, persisted conflict identity/history, due-date-risk rule, acknowledgement policy, or complete severity catalog. |
| Backups | **Database pass / system gap** | Online snapshot, timestamped unique file, count retention, integrity/FK checks, isolated restore, active-DB protection, corruption rejection, and concurrent-write tests pass. Official E-Ink package bytes and external Case folders are outside the SQLite backup and have no coordinated backup/restore policy. Scheduling, encryption, access control, RPO, and RTO remain open. |
| TV Dashboard read-only behavior | **Automated pass / visual and security gap** | Projection and UI are GET-only; POST returns 405. Static regression tests require status-only markup, no form/input/button, no Server URL/host text, hidden viewport overflow, dynamic grid fitting, connection-dot states, conditional refresh, and no job/urgent rendering. Last-known status is retained on failure. TV authentication and physical 1080p/4K kiosk/read-distance acceptance remain unverified. |
| E-Ink read-only behavior | **Server pass / device gap** | Device-scoped token validation, revocation/rotation, version ETag, Machine screen, revision-qualified manifest/files, checksum enforcement, time config, simulator, and cross-surface 403 behavior pass. Schema-v19 tests cover Timeline-derived setup worker/time, packaged worker photo, job/expected Machine tools, and local-only checklist policy. There is no checklist/comment write-back or USB endpoint. Physical SD/deep-sleep/wake/error/last-known-good behavior, device secret storage, signatures, superseded-package retention, and hardware acceptance are not verified in this repository. |

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

The TV UI has automated structural tests but no target-resolution, kiosk-browser, viewing-distance, or long-running refresh test. E-Ink is verified only through a browser simulator and Server API; physical panel color/ghosting, SD loss/corruption, deep sleep, configured shift wake, battery replacement, and last-known-good activation require the separate firmware/hardware project after API approval.

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

Coverage includes startup/health, configuration validation, migrations through schema v9, foreign-key/orphan rejection, Case/Order persistence and API behavior, read-only Case timing sums, transactional Case Operation creation/edit and graph/reference validation, preservation of prior Batch scalar/dependency snapshots, complete Windows Case/Operation/Order/Batch/Machine creation/edit presentation, version-checked Machine editing, confirmation-protected guarded deletion commands, deletion relationship blockers and route compaction, explicit combined/stock/scrap Batch allocation payloads, derived Batch lifecycle transitions, path-only Machine picture persistence/delivery, dependency graph semantics, Batch allocation adversarial cases, Machine assignment ordering, atomic Start/Suspend/Finish/Reset transitions and invalid/concurrent requests, Edit Mode races, deterministic time calculation and setup-calendar fallback, Timeline operation markers, TV/E-Ink read-only surfaces, job-package generation/integrity, backup/restore verification, Windows API models, and Windows presentation/view-model behavior.

## 7. Acceptance decision

The schema-v25 working tree remains a **development MVP vertical slice**, not a production factory deployment or a visually accepted Windows release. The combined importer/assignment-mode/canonical-Timeline Release suite passes. The manual/environmental checks listed in the addendum remain outstanding, including visual review of the import UI and a backed-up rehearsal before importing the supplied workbook into the current database. Production release should remain blocked until VR-001 and VR-002 are resolved and the owners explicitly accept or close VR-004 through VR-008 with tests and updated contracts; VR-003 remains resolved by schema v9.

Physical TV/E-Ink behavior, production Windows Service installation/upgrades, real factory network security, disaster recovery, performance/scale, and operator workflows were not exercised and must not be inferred from this report.
