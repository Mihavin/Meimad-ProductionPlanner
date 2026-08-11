# Architectural and correctness verification report

- **Verification date:** 2026-08-11
- **Repository:** `Meimad-ProductionPlanner`
- **Runtime tested:** .NET SDK 10.0.302, Debug and Release
- **Verdict:** Core automated MVP behavior passes; production acceptance is **conditional** on the high- and medium-priority gaps below.

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

This is not yet production-ready. The largest blockers are the absence of production human/TV authentication and TLS, and a backup boundary that protects SQLite metadata but not the official package-file root. Several contract surfaces also remain incomplete, particularly Case Operation edit/reorder, calendar/downtime authoring, and a standalone conflict resource.

## 3. End-to-end acceptance dataset

| Dataset area | Included evidence |
|---|---|
| Cases | 10 Cases, `ACC-001` through `ACC-010`, with external Working Folder paths and varied material/timing data. |
| Orders | 15 Orders across the Cases, with active and completed demand and urgent/normal Work Finish Dates. |
| Production Batches | 11 Batches: a two-Order combined Batch, two Batches splitting one Order, a stock-only Batch with scrap, a mixed Order/stock/scrap Batch, and ordinary one-Order Batches. |
| Allocations | 15 explicit allocation rows. Every Batch satisfies `planned quantity = Order + stock + scrap`; all Order allocations belong to the Batch Case. |
| Operations/dependencies | Two Case Operations and two Batch Operations per Batch. The dataset contains `SEQUENTIAL`, `PARALLEL_CAPABLE`, `INDEPENDENT`, and `LOCKED_SIMULTANEOUS` semantics. |
| Machines | 15 active, display-enabled Machines spanning mill, lathe, and inspection process types and varied axis/capability tokens. |
| Calendars | Three explicit UTC calendars: day, extended, and deliberately limited availability, plus a setup calendar. |
| Assignments | 22 stable, gap-free assignment positions across Machines M-01 through M-12; M-13 through M-15 provide intentional idle-dashboard coverage. |
| Downtime | Three intervals: current inspection, future maintenance, and CMM calibration. |
| Conflicts | Deliberate `missing_timing` and `insufficient_availability` conditions, returned as explained blocking conflicts without plan mutation. |
| TV data | 15 display Machines, current/next jobs, idle rows, urgency, current/future downtime, and calculated conflicts. |
| E-Ink data | One bound E-Ink device, one immutable official package revision, tool-cart metadata, one instruction asset, SHA-256 manifest data, and actual Server-owned package bytes. |
| Backup | Online SQLite snapshot, integrity and foreign-key checks, isolated restore verification, and restored row-count checks for Cases, Machines, and E-Ink package metadata. |

The dataset deliberately does not simulate production completion signals or device-local checklist/comments because those are outside the current authoritative model.

## 4. Review matrix

| Area | Result | Evidence and conclusion |
|---|---|---|
| Specification compliance | **Partial pass** | Required MVP component boundaries exist and excluded products are absent. The implemented subset is accurately described as partial in the project docs. Production security, some resource APIs, operating policies, and physical-device acceptance remain unresolved. |
| Data model | **Partial pass** | Schema v8 has required tables, timestamps, restrictive foreign keys, checks, server-owned migrations, immutable package metadata, path-only Case/Machine images, and orphan tests. Dependencies are still read from current Case Operations rather than immutable Batch dependency snapshots; lifecycle and audit rules remain open. |
| API boundaries | **Partial pass** | Windows and web clients use HTTP and contain no SQLite dependency. Planning writes are Server commands. E-Ink file paths stay internal. Append-only Case Operation, basic weekly Working Calendar creation, version-checked Machine editing, and guarded Case/Operation/Order/Batch/Machine deletion are implemented; route edit/reorder, calendar update/exceptions, Downtime mutation, and the documented standalone `/api/v1/conflicts` route are not. Human/TV auth is absent. |
| Edit token concurrency | **Core pass / operational gap** | Immediate transactions, singleton token, unique pending request, generation invalidation, Release, Reject, voluntary release, and configured no-response transfer pass concurrency tests. Caller headers are not authenticated, and heartbeat/disconnect, notification, cancellation, audit, and history retention policies remain open. |
| Batch allocation | **Core pass / policy gap** | One/multiple/split/stock/mixed/scrap cases and adversarial invalid requests pass. Same-Case ownership, positive rows, non-scrap purpose, and total equality are enforced in the creation transaction. Cross-Batch over-allocation against an Order and later reallocation/lifecycle policy are undefined. |
| Machine assignment ordering | **Pass** | Compatible assign, unassign, same-Machine move, cross-Machine move, inactive/incompatible rejection, and stable contiguous order pass. No automatic Machine choice or scheduling is present. Plan-revision/optimistic concurrency beyond Edit Mode generation is not defined. |
| Time calculation | **Core pass / model gap** | Fixed timestamps, calendars, setup, downtime subtraction, sequential and locked-simultaneous behavior, reserved/idle intervals, cycles, and insufficient availability pass deterministic tests. The engine never writes or reorders. Recurring/local calendars, DST, shared setup capacity, in-progress work, final duration formula, and performance targets remain open. |
| Conflicts | **Partial pass** | Timeline/TV return deterministic, explained conflicts such as missing timing, missing calendar, cycles, invalid dependencies, same-Machine simultaneous work, and insufficient availability. There is no standalone conflict API, persisted conflict identity/history, due-date-risk rule, acknowledgement policy, or complete severity catalog. |
| Backups | **Database pass / system gap** | Online snapshot, timestamped unique file, count retention, integrity/FK checks, isolated restore, active-DB protection, corruption rejection, and concurrent-write tests pass. Official E-Ink package bytes and external Case folders are outside the SQLite backup and have no coordinated backup/restore policy. Scheduling, encryption, access control, RPO, and RTO remain open. |
| TV Dashboard read-only behavior | **Functional pass / security gap** | Projection and UI are GET-only; POST returns 405; HTML contains no form/input/button edit controls; JavaScript does not use Edit Mode. Conditional refresh, current/next and their execution status, urgency, downtime, conflicts, and stale-view retention are tested. TV authentication and target kiosk/display visual acceptance are not implemented. |
| E-Ink read-only behavior | **Server pass / device gap** | Device-scoped token validation, revocation/rotation, version ETag, Machine screen, revision-qualified manifest/files, checksum enforcement, time config, simulator, and cross-surface 403 behavior pass. There is no checklist/comment or USB endpoint. Physical SD/deep-sleep/wake/error/last-known-good behavior, device secret storage, signatures, superseded-package retention, and hardware acceptance are not verified in this repository. |

## 5. Findings and required decisions

### High priority

**VR-001 — Production identity and transport security are not implemented.**

Windows mutation authority currently begins with caller-supplied `X-Meimad-Client-Id`, `X-Meimad-User-Id`, and generation headers. These prove edit-token state but not a human identity. TV data is anonymous, and Kestrel is configured for HTTP. The default bind is loopback, but configurable wildcard/LAN binding without authenticated identity or TLS would expose planning data and allow client-ID impersonation to any reachable host. Before a production LAN deployment, define authenticated Windows/admin/TV identities, authorization policies, TLS/certificate and firewall configuration, secret handling, and audit events. Keep public Internet exposure prohibited.

**VR-002 — A database-only backup cannot restore the official package system.**

SQLite stores E-Ink package metadata and checksums while file bytes live under the configured Server package root. The backup service captures only SQLite. A successful database restore can therefore point to missing package files and make official device output unavailable. External Case folders are intentionally outside SQLite as well. Define a coordinated, access-controlled backup/restore set for SQLite plus the Server-owned package root, and separately document the responsibility for external Case folders. Verify cross-component consistency, retention, encryption, RPO, and RTO.

### Medium priority

**VR-003 — Dependency snapshots are not immutable with a Production Batch.**

Batch Operation names/timings are copied at Batch creation, but the timeline mapper joins dependency type/predecessor/group from the current source Case Operations. Append-only creation is safe for existing Batches because no prior Batch references the new source operation and existing rows remain unchanged. A later edit/reorder could alter an existing Batch’s calculated dependencies. Add a versioned Batch dependency snapshot or explicitly approve live-route semantics before edit/reorder APIs are exposed.

**VR-004 — Required resource/contract coverage is incomplete.**

The implemented route set includes Case Operation create and basic weekly Working Calendar create/list, but still lacks route edit/reorder, calendar update/exceptions, Downtime mutation, and standalone `/api/v1/conflicts`. The Markdown route-coverage table presents some target paths alongside implemented paths, so it must not be treated as a frozen OpenAPI contract. Decide and implement these boundaries before declaring the domain API complete.

**VR-005 — Cross-Batch Order allocation policy is unresolved.**

Creation validates each Batch internally, but nothing prevents cumulative allocations across active Batches from exceeding an Order’s demand. Split production is valid, so the missing rule is not simply a uniqueness constraint. Define whether cumulative good-quantity allocations may exceed Order quantity, which Batch/Order statuses participate, and how cancellation/reallocation is handled; then enforce it transactionally.

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

**VR-011 — There is no committed verification baseline.**

The repository is on an unborn `main` branch with no `HEAD` commit, and all files are currently untracked. Tests prove the current filesystem state but cannot be tied to an immutable revision or reviewed diff. Create an intentional initial commit after the audit findings and dataset are reviewed.

## 6. Test execution

Final full-suite command:

```powershell
dotnet test server/Meimad.Planner.Server.slnx -c Release --artifacts-path .qa/delete-edit-release
```

Result:

| Test assembly | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `Meimad.Planner.Server.Tests` | 162 | 0 | 0 |
| `Meimad.Planner.Client.Windows.Tests` | 42 | 0 | 0 |
| **Total** | **204** | **0** | **0** |

The same full solution also passed in Debug with 204 passed, 0 failed, and 0 skipped. The acceptance test itself passed in isolation before both full-suite runs.

Coverage includes startup/health, configuration validation, migrations through schema v8, foreign-key/orphan rejection, Case/Order persistence and API behavior, transactional Case Operation creation and graph/reference validation, preservation of prior Batch snapshots, complete Windows Case/Operation/Order/Batch/Machine creation, version-checked Machine editing, confirmation-protected guarded deletion commands, deletion relationship blockers and route compaction, explicit combined/stock/scrap Batch allocation payloads, path-only Machine picture persistence/delivery, dependency graph semantics, Batch allocation adversarial cases, Machine assignment ordering, atomic Start/Suspend/Finish transitions and invalid/concurrent requests, Edit Mode races, deterministic time calculation, TV/E-Ink read-only surfaces, job-package generation/integrity, backup/restore verification, Windows API models, and Windows presentation/view-model behavior.

## 7. Acceptance decision

The current code is accepted as a tested **development MVP vertical slice**, not as a production factory deployment. No automated correctness failure remains in the reviewed suite. Production release should be blocked until VR-001 and VR-002 are resolved and the owners explicitly accept or close VR-003 through VR-008 with tests and updated contracts.

Physical TV/E-Ink behavior, production Windows Service installation/upgrades, real factory network security, disaster recovery, performance/scale, and operator workflows were not exercised and must not be inferred from this report.
