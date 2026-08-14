# Architectural and correctness verification report

- **Baseline verification date:** 2026-08-11
- **Current change review date:** 2026-08-14
- **Repository:** `Meimad-ProductionPlanner`
- **Baseline runtime tested:** .NET SDK 10.0.303, Release
- **Verdict:** The schema-v24 combined Release run passed 437 tests (293 Server and 144 Windows Client), with no failures or skips. Assignment-owned planning modes, strict one-identified-operation normalization, cross-Machine paused-history folding, blocked-wait separation, fixed-point same-Machine/dependency overlap reconciliation, phase-colored composite operation rendering, default ordinary-wait suppression, per-Machine calendar backgrounds, the factory-local hour/DAY/DARK ruler, and the Server-`readAt`-anchored current-time/floating-not-started behavior have API/domain/client coverage. Windows operator workflow, live SMTP-provider behavior, physical-device, production-service, network-security, and disaster-recovery acceptance remain manual/environmental gaps.

## 0. Schema-v24 change addendum (combined automated pass; manual checks pending)

Static documentation/code review identifies the following additions in the current working tree:

- schema v10 Machine Type catalog/linkage, Setup Calendar singleton selection, and allocated-Order lifecycle migration;
- Working Calendar read/update/guarded-delete, usage/work/break/dated-exception authoring, and dedicated Setup Calendar read/set/clear routes;
- Machine Type CRUD, linkage, inherited compatibility capabilities, and guarded deletion;
- cross-type Machine assignment warning/confirmation, mandatory reason validation, and immutable actor/type/time audit;
- linked-Order `active` / `in_production` / `complete` derivation plus quantity/status edit guards;
- Planning Board planned quantity, allocated Order references, nullable input-derived estimated time, compact cards, and player-style execution controls;
- a compact connection/Edit Mode header, dedicated Setup page, optimistic Order editing, and one canonical read-only Timeline shared by the main window and separate closable window; and
- schema v11 isolated Employee/Resource and Israeli-holiday CRUD plus optimistic report/email settings; schema v12 adds detailed employee fields, exact roles, normalized skills, restrictive role-compatible Calendar linkage, photo/notes storage, and an active-only resource view; schema v13 adds employee exception CRUD and deterministic exception-aware availability; and
- schema v16 extended Case/Batch Operation timing snapshots, configurable day-shift-only policy, role-constrained Timeline phases in setup/QA/load-unload/production order, and extended compact-card estimates.
- individual employee contention, setup Machine-skill matching, calendar/break/exception-aware worker reservations, and visible resource-waiting explanations without backlog mutation.
- deterministic resource priority by earliest allocated-Order Work Finish Date and naturally smaller Order Number, with the deciding reason retained in the delayed interval and no persisted backlog mutation.
- schema v17 planned-maintenance and open-breakdown storage/API lifecycle, optimistic maintenance edit/Restore, Setup Machine Availability UI, and reason-preserving Timeline/TV downtime projection.
- API-level assignment persistence/reload into Timeline, same-Case operations split across Machines, duration derivation without planned timestamps, failed-dependency propagation without false cycle classification, precise missing-worker-role conflict text, structured Timeline-input logging, and replay of a client Timeline read invalidated while in flight.
- explicit paused-operation Reset to `not_started`, retained assignment/backlog position, closed pause event, derived Batch/Order rollback, structured `operation_reset` audit, and compact client control/refresh behavior.
- nearest-feasible fixed-backlog Timeline placement with operation-linked Machine/setup/day-shift/resource/downtime/pause/dependency waiting, assigned-but-blocked visibility, no-leapfrog propagation, common resource retry for locked groups, and overlapping-wait renderer lanes.
- schema v23 authoritative Start/Finish actual timestamps and Machine history, Reset clearing, floating not-started forecasts, fixed in-progress actual start, completed historical blocks, completed-predecessor actual-finish constraints, and forecast/actual Timeline UI metadata.
- schema v24 `forward`/`backward`/`manual` planning intent on each Machine Assignment, strict migration/database tokens, exact assignment ETag mutation, atomic actor/before/after event logging, move/reset preservation, and Planning Board assignment identity/version/mode fields.
- one canonical Timeline with assignment-owned mixed-mode calculation, reverse dependency/backlog latest-fit placement from the earliest linked Order Work Finish Date, deterministic earlier-date/shorter-duration/natural-Order contention, no global mode selector/query, one normalized operation block per assignment, duplicate-block logging, and no persisted calculated dates or backlog mutation.
- strict active-operation identity coverage proving one identified block globally even with calendar/resource waits, downtime, direct move, resume, and unassign/reassign; former-Machine occupancy remains anonymous, its exact facts are folded into the current block, pause/transfer boundaries remain correct, and the WPF capacity band cannot cover or label itself as another operation.
- assigned-but-unplaceable rows rendered as lower-band `blocked` waiting only after preceding calculated backlog work; actual/hold/history-authoritative same-Machine overlap reconciliation; fixed-point propagation through later backlog rows, Sequential chains, and locked groups; and deterministic WPF sublanes/point markers without horizontal time changes.
- phase-aware WPF operation rendering that keeps one assignment host, colors `PRODUCTION` blue, `SETUP` yellow, QA-as-`QC` green, `PART RELOAD` purple, and locked reservation orange; leaves internal availability gaps transparent; suppresses generic idle and ordinary anonymous waiting bars; explains `BLANK = NO OPERATION`; and retains paused `HOLD`, downtime, actual history, and assignment-owned `BLOCKED` states.
- additive per-Machine `nonWorkingWindows`, derived from the same timezone-aware Working Calendar expansion used by scheduling and painted as gray row backgrounds rather than operation/capacity blocks; coverage includes closed weekdays, breaks, overnight spill, distinct calendars, preserved downtime, and invalid-calendar fallback.
- additive factory display-time metadata plus a DST-aware two-row local hour ruler with header-only configured DAY/DARK bands, adaptive hour/date density, and a bounded drawing-backed long-horizon render plan that has no Timeline identity or scheduling effect.
- Server-`readAt`-anchored floating forecasts: `not_started` Forward/Manual work earliest-fits at/after the snapshot cursor and cascades through the stored backlog/Sequential graph; a missed Backward start falls forward transiently with `backward_start_missed`, preserves persisted mode/backlog, and returns a deadline warning when late; no-fit work is an identified blocked marker and elapsed historical horizons do not fabricate forecasts.
- one red labelled WPF `NOW` marker estimated from `readAt` plus elapsed time in the configured factory timezone; one shared 30-second throttle refreshes only while assigned `not_started` forecast or blocked work exists, so embedded/separate Timeline windows do not double-poll, calculate, or mutate planning state.

Current schema-v24 combined Release result:

| Test assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `Meimad.Planner.Server.Tests` | 293 | 0 | 0 |
| `Meimad.Planner.Client.Windows.Tests` | 144 | 0 | 0 |
| **Total** | **437** | **0** | **0** |

Current tests add schema-v24 creation/upgrade/default/CHECK coverage; conditional assignment-mode PATCH success, no-op, stale/missing ETag, unknown assignment, in-progress rejection, atomic event data, unchanged neighboring assignments, move/reset preservation, and Planning Board assignment metadata; canonical Timeline mixed-mode/dependency/no-overlap behavior; exactly one identified active Operation ID with anonymous ordinary capacity annotations; suspended actual/pause history across same-Machine, direct-move, resume, unassigned, and unassign/reassign states; completed/stale-assignment history classification; live-shaped two-work-plus-one-blocked backlog projection; actual/hold versus forecast and forecast-versus-forecast overlap reconciliation; transitive backlog/Sequential/locked-group block propagation with authoritative history retention; Server-`readAt` cursor placement for floating `not_started` work, forward/manual downstream cascade, missed-Backward fallback/deadline warning, blocked no-fit marker, and elapsed-horizon suppression; WPF partial-overlap lane placement, blocked lower-band rendering, equal-time boundary point markers, ordinary-wait/idle suppression, four distinct Setup/QC/Part-Reload/Production phase colors and tooltips, transparent internal gaps, reserved-phase coloring, and retained hold/blocked/downtime rendering; additive `nonWorkingWindows` JSON compatibility and WPF background layering; configured weekend/custom closed weekday, break/split-shift, overnight-shift, different-per-Machine-calendar, downtime-separation, and invalid-calendar coverage; configured factory-time API metadata; local hour/DAY/DARK ruler palette and placement; DST spring-skip/fall-repeat labels; unique adaptive local-date cadence; clipped contiguous bands; bounded coverage-aware long-horizon rendering without Timeline identity; and deterministic Server-`readAt` current-time estimation, shared-throttle/no-double-poll lifecycle, bounds, absolute-position geometry, factory-zone labelling, single-marker rendering, chart-edge clamping, and identity isolation. The suite also covers Timeline phase JSON compatibility, nested phase compatibility for job packages, Windows API/context-action/shared-view behavior, managed Setup Calendar behavior, recurring Working Calendar rules, Machine and Machine Type management, strict cross-type assignment confirmation/audit, planned-maintenance create/edit, open breakdown blocking and recovery, reason-preserving Timeline projection, detailed Employee/Resource CRUD, holiday cache behavior, extended Operation timing snapshots, resource contention, locked-group start/reservation semantics, no-leapfrog failure propagation, and the planning/dependency/status/UI coverage described below. Automated tests do not establish visual or operational acceptance. Manual Windows checks, production-sized contention, live measurement-source integration, and live SMTP-relay delivery remain pending. Authenticated SMTP credential storage is not implemented. Physical TV/E-Ink, production Windows Service, LAN/TLS/authentication, backup/disaster recovery, and shop-floor display checks also remain pending.

Current Release command:

```powershell
dotnet test .\server\Meimad.Planner.Server.slnx -c Release --no-restore
```

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
| Data model | **Focused automated pass / policy gap** | Schema-v24 focused tests cover the additive non-null/default/CHECK `machine_assignments.planning_mode` migration while preserving existing assignment identity, position, version, and timestamps. Earlier coverage includes Machine Types, Setup Calendar selection, administrative resources, employee exceptions, holiday policy, cross-type overrides, and schema-v23 actual-time history. Aggregate route revision, arbitrary dependency fan-in/out, and broader audit rules remain open. |
| API boundaries | **Automated pass / coverage gap** | Windows and web clients remain HTTP/API-only and contain no SQLite dependency. Focused tests pass for Working Calendar usage/work/break/dated-exception CRUD, one-window overnight expansion, and Setup selection, Machine and Machine Type CRUD/linkage, planned-maintenance/breakdown mutation, and allocation-safe Order PATCH. Route reorder, Calendar combined-overnight/overtime/archive policy, downtime recurrence/cancellation, and standalone `/api/v1/conflicts` remain absent; human/TV auth is absent. |
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

The schema-v24 working tree remains a **development MVP vertical slice**, not a production factory deployment or a visually accepted Windows release. The combined assignment-mode/canonical-Timeline Release suite passes. The manual/environmental checks listed in the addendum remain outstanding. Production release should remain blocked until VR-001 and VR-002 are resolved and the owners explicitly accept or close VR-004 through VR-008 with tests and updated contracts; VR-003 remains resolved by schema v9.

Physical TV/E-Ink behavior, production Windows Service installation/upgrades, real factory network security, disaster recovery, performance/scale, and operator workflows were not exercised and must not be inferred from this report.
