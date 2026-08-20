# G-code readiness architecture

Status: repository audit and implementation design. This note does not add runtime behavior or a database migration.

## Scope and terminology

The repository has no `ProductionBench` entity. The existing production-context model is `ProductionBatch`, whose `BatchOperation` rows are assigned and scheduled. In this note, a product requirement that says **Production Bench operation** maps to the existing **Batch Operation**. No `ProductionBench`, second `Machine`, second `Operation`, or parallel file-storage subsystem should be introduced.

A **process revision** is an immutable, numbered production-process definition for one existing `CaseOperation`. It is not, and must never be presented as, a Git branch or source-control branch. Case product revision (`cases.revision`), Batch route revision (`production_batches.route_revision`), E-Ink package revision, G-code release number, and process revision are separate concepts.

G-code accepted into Meimad is a released production artifact. There is no Draft G-code state. Unfinished programming stays outside Meimad, and every correction creates a new release.

## Current repository audit

### Machine and setup UI

- `server/Meimad.Planner.Server/Domain/Machines/Machine.cs` is the single Machine aggregate. `Machine` already owns identity, number/name, legacy process and axis types, direct capabilities, Working Calendar, active/display state, optional picture, optional `MachineTypeId`, inherited Machine Type capabilities, master-calendar participation, optimistic `Version`, and timestamps.
- `server/Meimad.Planner.Server/Domain/MachineTypes` supplies reusable Machine Type names and capabilities. Machine Type remains useful for route/planning compatibility; it is not a postprocessor.
- `MachineService` + `IMachineRepository` + `SqliteMachineRepository` are the existing service boundary. `/api/v1/machines` is mapped by `Api/Machines/MachineEndpoints.cs`.
- The authoritative Windows editor is the **Setup > Machines** tab in `client-windows/Meimad.Planner.Client.Windows/Views/SetupView.xaml`, backed by `Presentation/SetupViewModel.cs`. It edits Machine Type, legacy/process type, axis, Working Calendar, capabilities, picture, active/display flags, and master-calendar behavior.
- `MachinePlanningBoardViewModel.cs` still contains old Machine-edit form state and commands that are not exposed by `MachinePlanningBoardView.xaml`. That dead/legacy editor should be removed when the Machine API model changes, rather than expanded into a second setup screen.

Reuse decision: add supported-postprocessor editing to the existing Machine aggregate/API and Setup > Machines screen. Store the many-to-many relationship separately; do not add a G-code-specific Machine model.

### Operation, Production Batch, assignment, and scheduling

- `Domain/CaseOperations/CaseOperation.cs` is the reusable process/route anchor. It currently owns operation number/order/name, required Machine Type, manual setup and per-part cycle seconds, dependency data, QA-after-setup, load/unload cadence and worker requirement, day-shift-only, and external-delay data exposed through `Application/Cases/CaseOperationDetails`.
- `Domain/ProductionBatches/ProductionBatch.cs` is the production lot. A `BatchOperation` is instantiated from each Case Operation by `SqliteProductionBatchRepository.InstantiateOperationsAsync`. It has a stable `SourceCaseOperationId` and snapshots route, compatibility, timing, execution status, and actual timing.
- `ProductionBatch.RouteRevision` exists but new batches currently set it to null. It is not an implemented process-version mechanism and must not be repurposed as an operation process revision.
- `MachineAssignment` in `Domain/Machines/MachineAssignment.cs` links one Batch Operation to one Machine and backlog position, with manual/forward/backward planning mode. `MachineAssignmentService`, `IMachineAssignmentRepository`, and `SqliteMachineAssignmentRepository` own placement, reorder, unassignment, planning-mode changes, Start/Suspend/Finish/Reset, actual times, pauses, and assignment-override audit rows.
- Current Machine Type incompatibility may be planned only after an explicit reasoned override. This is a planning compatibility mechanism. It is not G-code applicability and cannot override a postprocessor readiness failure.
- `Application/PlanningBoard` and `Persistence/SqlitePlanningBoardRepository.cs` project the unassigned pool and Machine backlogs. `MachinePlanningBoardView.xaml` is the scheduling and execution screen.
- `Application/Timeline/TimelineProjectionService.cs`, `ITimelineSourceRepository`, `SqliteTimelineSourceRepository`, and `Domain/Timeline/TimelineCalculationEngine.cs` calculate the read-only timeline over persisted assignments, calendars, resources, dependencies, downtime, external delay, and Batch quantity. The Timeline does not persist placement results or change backlog order.

Reuse decision: attach process/release/tooling selections to `BatchOperation`, because that is the concrete assigned and scheduled production context. Planning remains legal when readiness is incomplete. Start/resume is the existing server transition where readiness must eventually be enforced.

### File metadata, storage, operation packages, and version history

- A Case stores a Working Folder path; it is not a managed-file catalog. The functional baseline forbids modifying engineering originals and confines generated Case-folder content to `_MeimadPlanner`.
- `Domain/JobPackages/JobPackage.cs` and migrations V6/V7/V19 implement immutable E-Ink job-package revisions and assets. Assets already carry logical path, server storage-relative path, media type, length, SHA-256, modified time, display order, and role (`preview`, `tool_table`, `nc`, `text`, `offsets`, and others).
- `Application/JobPackages/JobPackageService.cs` safely resolves source paths, allow-lists extensions, stages output, hashes bytes, atomically moves a completed package directory, and removes its own failed staging directory. `SqliteJobPackageRepository` stores the snapshot and asset metadata. SQLite triggers reject updates and deletes of published package rows.
- `Configuration/EInkOptions.cs` and `ServerStoragePathResolver.cs` resolve the configured server-local package root. Installed services resolve relative storage below the product ProgramData area. The database and E-Ink package roots reject unsafe network placement; the Case Working Folder may nevertheless point at an externally managed folder.
- The package publisher copies an NC/text input from a Case Working Folder. An E-Ink package asset is a distribution snapshot for an assigned Batch Operation, not the authoritative release from which readiness can be computed.
- There is no generic managed-file aggregate or Windows package-generation UI today. The package-generation endpoint is server/API-only.

Reuse/refactor decision: extract the staging, safe-path, hashing, immutable-read verification, and cleanup logic into one `ImmutableArtifactStore` used by both G-code releases and E-Ink packages. Keep owner-specific links and roles, but do not create a second unrelated file manager. Existing `eink_package_revisions` and `eink_package_files` remain readable and immutable. Do not reinterpret old package NC copies as released G-code because their source approval, postprocessor, and release identity are unknown.

### Tool table and offsets

- `ToolTableEntry` and `OffsetEntry` exist only in `Domain/JobPackages/JobPackage.cs`.
- `GenerateJobPackageCommand` receives job tools, expected-on-Machine tools, and offsets at publication time. `JobPackageService` validates up to 500 rows and serializes them to package-specific JSON assets. V19 stores job and expected-Machine tool arrays as JSON snapshots; offsets remain a package file.
- There is no normalized Machine tool table, process tool requirement, reusable tool-offset set, tool history, or authoritative current tooling service.

Reuse decision: retain the existing row shape and JSON package output contract, but move authoritative inputs to normalized, versioned records. A future Job Package must snapshot those records rather than accepting new authoritative tool/offset values in the publish request.

### Existing estimates and timeline duration selection

- The only production estimate inputs are nullable manual `SetupTimeSeconds` and `CycleTimePerPartSeconds` on Case Operation and their Batch Operation copies. There is no G-code-derived estimate and no manager timing override. `machine_assignment_overrides` and the holiday `IsManualOverride` flag are unrelated.
- Saving a Case Operation in `SqliteCaseRepository.UpdateOperationAsync` propagates setup, cycle, QA, load/unload, and external-delay fields to every linked `not_started` Batch Operation. Started/completed rows are preserved. V33 performs the setup/cycle backfill for existing not-started rows.
- This makes the statement in older documentation that all Batch timing snapshots are permanently immutable inaccurate for not-started work. The functional spec and data-model docs need to be corrected when process revisions are implemented.
- `SqlitePlanningBoardRepository.EstimateSeconds` calculates:

  `setup + QA + load/unload per occurrence + (cycle per part * planned quantity)`

  Manual loading uses one load/unload occurrence per part. Automatic loading uses `ceil(quantity / everyNParts)` when a cadence is present, otherwise zero load occurrences.
- `SqliteTimelineSourceRepository` reads setup/cycle directly from `batch_operations`. `TimelineProjectionService` rejects missing, zero, or overflowing inputs and maps setup, QA, load/unload, and one-part cycle durations into `TimelineOperationInput`. `TimelineCalculationEngine` repeats the cycle duration by planned quantity and applies calendars/resources/dependencies. External delay is placed after the Machine operation and is not part of its processing duration.
- The Planning Board and Timeline therefore use the Batch Operation snapshot, not live Case Operation fields. The estimate formula currently exists in more than one place.

Reuse/refactor decision: keep Batch Operation as the timeline input and historical execution snapshot, but resolve its timing from its selected process revision/release through one shared estimate pipeline. Started work remains frozen.

### Backend boundaries and update mechanisms

- `ServerApplication.cs` registers singleton application services and repository interfaces, with one SQLite repository per feature/aggregate and minimal API endpoint groups under `/api/v1`.
- Mutations require the single-editor Edit Mode authority (`X-Meimad-Client-Id` and edit generation). Versioned resources use `If-Match`; entities carry integer `Version` values and timestamps.
- There is no cross-client push bus. Windows view models use HTTP refreshes and local events. `MainWindowViewModel` connects `CaseWorkspace.PlanChanged`, `MachinePlanningBoard.PlanChanged`, and `Setup.ConfigurationChanged` to board/timeline invalidation and re-fetch.
- `structured_event_log` is the server audit/update history. Important repository mutations append events inside the same SQLite transaction; log payloads must not contain credentials, release bytes, UNC credentials, or sensitive configuration.

Reuse decision: follow the existing service/repository/endpoint/contract layering, Edit Mode, ETag, refresh events, and structured audit log. Do not add a client-owned cache database or an unrelated event system for this feature.

### Migration and test conventions

- Migrations are ordered `SchemaV<number><Feature>Migration` implementations of `IDatabaseMigration`, registered contiguously in `DatabaseMigrator`. Each runs in one transaction, records `schema_migrations`, and updates `PRAGMA user_version`. V33 is currently the latest migration.
- SQL uses snake_case table/column names, text IDs, integer boolean/check constraints, foreign keys, explicit indexes, `version`, `created_at`, and `updated_at`.
- Server tests use xUnit, `TemporaryDatabase`, ASP.NET `TestServer`, direct persistence tests, API integration tests, and pure domain tests. Client tests use xUnit ViewModel/presentation tests. Existing examples include `Persistence/MigrationTests.cs`, `Persistence/EInkPackageMigrationTests.cs`, `JobPackages/JobPackageApiTests.cs`, `Machines/MachineOperationExecutionApiTests.cs`, `Timeline/TimelineCalculationEngineTests.cs`, and client Planning Board/Timeline tests.

## Target domain model

The names below follow the repository's singular domain record / plural snake_case table convention. Exact request/response DTO names should continue the existing `...Request`, `...Response`, and `...Command` patterns.

### Postprocessor and Machine support

`Postprocessor`

- `PostprocessorId`, stable `Code`, display `Name`, optional vendor/version description, `IsActive`, `Version`, timestamps.
- This identifies the output dialect/configuration used to release G-code. It is not a physical Machine.

`MachineSupportedPostprocessor`

- `MachineId`, `PostprocessorId`, timestamps, unique pair.
- This is the only G-code-to-Machine applicability relation:

  `GCodeRelease.PostprocessorId IN Machine.SupportedPostprocessorIds`

Do not add `MachineId` to `GCodeRelease`. Existing required Machine Type and capabilities remain planning/process constraints, but they do not substitute for postprocessor membership.

### Process revisions

`ProcessRevision`

- `ProcessRevisionId`, `CaseOperationId`, monotonically increasing `RevisionNumber`, optional display label, manual setup seconds, manual cycle-per-part seconds, author/reason, `Version`, timestamps.
- The existing Case Operation remains the stable route node and dependency anchor. Add nullable `case_operations.current_process_revision_id` as its default pointer.
- Process revisions are immutable after their atomic creation. Corrections create the next process revision; historical revisions remain addressable. This lifecycle has no effect on the separate rule that G-code enters Meimad only as a release.
- A new process revision copies the current process inputs intentionally. It does not track a Git branch and has no source-control semantics.
- Keep dependency topology and route position on Case Operation. Keep QA/load cadence/day-shift/external-delay there initially unless the product explicitly requires those policies to vary by process revision. The selected revision owns the manual setup/cycle values and released process artifacts.

`production_batches.route_revision` remains untouched until a separate whole-route revision design exists.

### Released G-code

`GCodeRelease`

- `GCodeReleaseId`, `ProcessRevisionId`, `PostprocessorId`, monotonically increasing release number within the process revision, release note, publisher identity, published timestamp, optional analyzed cycle seconds and analyzer version.
- There is no status column with `draft`; inserting a row means it is released. Update/delete triggers protect release metadata and file ownership links.
- A release may own one main program and optional released subprogram files through `GCodeReleaseFile` ownership links. Each link points at the common immutable artifact metadata produced by `ImmutableArtifactStore` and identifies main/subprogram role and logical name.
- Add a current-release pointer for each process-revision/postprocessor pair without mutating releases. A Batch Operation pins its selected release explicitly before production; changing a current pointer does not rewrite historical selections.

`BatchOperation` additions

- Nullable `ProcessRevisionId`.
- Nullable `SelectedGCodeReleaseId`.
- Nullable selected offset revision ID.
- Resolved estimate source/revision identifiers alongside the existing setup/cycle snapshot, so a historical duration can be explained.
- Existing setup/cycle fields remain the compatibility and timeline snapshot. Selecting a process revision/release recalculates them only while status is `not_started`; started/suspended/completed history is never rewritten.

### Tooling and offsets

The package-only row records should be extracted to shared tooling value objects rather than cloned with slightly different names.

- `ProcessRevisionTool`: the required job tool table for a process revision. Reuse tool ID, description, diameter, length, and note semantics.
- `MachineTool`: the current expected-on-Machine tool table for one existing Machine, with version/timestamps. Machine tool updates are audited.
- `ToolOffsetRevision`: immutable header tied to the concrete Batch Operation, assigned Machine, selected process revision, and selected G-code release.
- `ToolOffsetRow`: name/tool reference, value, unit, and note. Publishing a correction creates another offset revision and changes the Batch Operation's selection; old rows remain.

Readiness compares the complete required process tool table with the selected Machine table and requires the selected offset revision to match the exact Batch Operation/Machine/process/release context. It reports missing or mismatched rows. It never rewrites G-code, splits or truncates tools, reduces requirements, or moves the operation.

### Estimate overrides

There is no current manager estimate override to migrate. Add `ProcessEstimateOverride` as an audited, append-only record rather than adding anonymous mutable seconds to Case Operation:

- `ProcessEstimateOverrideId`, `ProcessRevisionId`, optional setup and cycle override seconds, mandatory reason, manager/user identity, timestamp.
- A current-override pointer may move; override records remain immutable.
- Authorization can initially reuse Edit Mode, but a distinct manager role is an explicit later security decision. The UI must not label an ordinary Edit Mode user as a manager unless such authorization exists.

## File-storage organization

Add a server-owned released-artifact configuration section, resolved through `ServerStoragePathResolver`, for example `ReleasedArtifacts:Root`. Clients send bytes or identify an approved source; only the Server publishes into the managed root. Do not publish in-place in a Case Working Folder and never modify a source file.

Recommended layout:

```text
<ReleasedArtifactsRoot>/
  gcode/
    <processRevisionId>/
      <gcodeReleaseId>/
        main/<safe-file-name>
        subprograms/<safe-file-name>
  .staging-<releaseId>/
```

Publication flow:

1. Validate Edit Mode, process revision, postprocessor, size/count, extension, and safe logical names.
2. Copy/upload to a unique sibling staging directory without overwriting.
3. Calculate SHA-256 and length from stored bytes and fsync/close all files.
4. Commit immutable metadata and atomically rename staging to the final release directory with compensation for failure, following the current Job Package pattern.
5. Never accept an existing final directory, never overwrite, and verify hash/length on read.

`ImmutableArtifactStore` should be extracted from `JobPackageService`; a G-code-specific wrapper supplies release policy. `JobPackageService` should then copy selected released files through that store. Package files remain immutable distribution copies with their own checksums; they are not the release owner.

The current E-Ink and database roots are explicitly server-local. If a shared/UNC release root is required operationally, that is a deployment decision that needs startup permission, durability, and atomic-rename tests under the Windows service account. It must not be enabled by silently weakening the existing server-local safeguards. Serving release/package bytes through authenticated Server endpoints is the default design.

## Process-revision and package behavior

1. A Case Operation owns many process revisions and one current default pointer.
2. A process revision may own many immutable G-code releases, each for exactly one postprocessor.
3. The current release is only a default pointer. A Batch Operation pins `ProcessRevisionId` and `SelectedGCodeReleaseId` for a concrete production context.
4. Releasing a correction always inserts a new release and changes a pointer/selection; it never overwrites or deletes the former release.
5. Creating a new process revision does not mutate existing Batch Operations. Not-started operations may be explicitly reselected and recalculated; started work remains pinned.
6. Job Package publication reads the pinned release, process tools, Machine tools, and selected offsets. Deprecate request-time authoritative NC/tool/offset inputs. Existing package revisions stay readable, but the legacy publisher must not be usable to bypass release selection for readiness-managed work.

## Readiness calculation

Do not persist `Operation.IsReady` or any global manually toggled ready flag.

Add a pure `ProductionReadinessEvaluator` in `Domain/ProductionReadiness` and an application `ProductionReadinessService` backed by `IProductionReadinessRepository`. The repository assembles a consistent snapshot containing:

- Batch Operation and status/version;
- current Machine Assignment and assigned Machine/version;
- selected Process Revision;
- pinned/current G-code release and its files/checksums;
- Machine supported postprocessors;
- required process tools and current Machine tools;
- selected Tool Offset Revision;
- material-readiness provider result.

The response is a computed `ProductionReadinessResult` with evaluated context IDs/versions and stable reason codes, for example `machine_not_assigned`, `process_revision_not_selected`, `gcode_release_not_selected`, `release_process_mismatch`, `postprocessor_not_supported`, `tool_table_missing`, `machine_tool_missing`, `offsets_missing`, `offset_context_stale`, `material_not_evaluated`, and `material_not_ready`. A derived `IsReady` may be returned in the response; it is never stored as authority.

Planning Board and Timeline reads may show the result/reasons but must continue to include incomplete or incompatible assigned operations. Timeline calculation uses available estimates and does not move or repair an operation because of readiness.

For Start and resume, evaluation and the status transition must be race-safe. Extend the existing `MachineAssignmentService.ChangeExecutionStatusAsync` orchestration and `SqliteMachineAssignmentRepository` transaction so the same transaction reads the readiness context, invokes the shared evaluator, and updates execution state only when all enforced dimensions are ready. Display evaluation may use `ProductionReadinessService`; the transition must re-evaluate rather than trusting a client result. Return a structured conflict with all blocking reasons. Suspend, Finish, and Reset retain their existing transition rules.

Structured log events should cover process-revision creation/selection, release publication/selection, postprocessor support changes, tooling/offset publication, estimate overrides, readiness-rejected Start, and successful Start. Log IDs, reason codes, hashes, and versions, never release contents or credentials.

## Estimate calculation pipeline

Create one shared `ProductionDurationCalculator` in the domain layer and remove the private formula from `SqlitePlanningBoardRepository`. The calculator returns effective setup/cycle values, source labels, per-phase totals, and total processing seconds with checked overflow.

Effective input precedence for a selected process revision is:

1. the current explicit manager estimate override for the field, if present;
2. the selected G-code release's analyzed estimate for a supported field;
3. the process revision's manual estimate.

An absent analyzed value falls back; zero is a real value and is not treated as missing. QA, load/unload cadence, planned quantity, and external delay continue to use existing semantics. The total Machine-processing estimate remains:

`effective setup + QA + aggregate load/unload + (effective cycle * planned quantity)`

External delay remains a downstream timeline delay rather than Machine processing time.

Integration points:

- `CaseService`/`SqliteCaseRepository`: stop treating direct Case Operation setup/cycle edits as the long-term source; create/select a process revision. Keep current API fields as a compatibility projection during migration.
- `ProductionBatchService`/`SqliteProductionBatchRepository`: instantiate Batch Operations with the Case Operation's current process revision and resolved timing snapshot.
- Process/release/override selection service: refresh snapshot and increment version only for `not_started` Batch Operations.
- `PlanningBoardService`: calculate its estimate projection with `ProductionDurationCalculator`, outside the SQLite repository.
- `TimelineProjectionService`: validate and map the same resolved snapshot/phase calculation. `TimelineCalculationEngine` continues placement and resource allocation.
- Actual started/completed timing remains authoritative and unchanged.

## Exact API, service, and UI modification map

### Server

- Extend `Domain/Machines/Machine.cs`, `Application/Machines`, `Persistence/SqliteMachineRepository.cs`, and `Api/Machines` for supported postprocessor IDs; add `Domain/Postprocessors`, `Application/Postprocessors`, a SQLite repository, and `/api/v1/postprocessors` setup endpoints.
- Add `Domain/ProcessRevisions`, `Application/ProcessRevisions`, repository/contracts/endpoints under existing Case Operations, for example `/api/v1/cases/{caseId}/operations/{operationId}/process-revisions` and release endpoints below a revision.
- Add shared immutable artifact storage and G-code release services; refactor `JobPackageService` to consume them.
- Add normalized tooling/offset repositories and selection endpoints scoped to a Process Revision, Machine, and Batch Operation as described above.
- Add `Domain/ProductionReadiness`, `Application/ProductionReadiness`, and a read endpoint such as `/api/v1/batch-operations/{id}/readiness`.
- Modify `MachineAssignmentService` and `SqliteMachineAssignmentRepository.ChangeExecutionStatusAsync` for transactional Start/resume enforcement.
- Modify Planning Board contracts/repository/service and Timeline source/projection to expose readiness and use the common estimate pipeline while retaining unready operations.
- Register all services/endpoints in `ServerApplication.cs` using the existing singleton/interface conventions.

### Windows client

- **Setup > Machines** (`SetupView.xaml`, `SetupViewModel.cs`): edit Machine supported postprocessors; add a Postprocessors setup subsection using existing master-data patterns.
- **Cases > Operations** (`CaseWorkspaceView.xaml`, `CaseWorkspaceViewModel.cs`): keep the existing Operation route UI and add a selected-operation Process Revisions panel for current revision, manual estimates, released G-code history/current selection, required tools, and release action. Never show a G-code Draft action.
- **Planning Board** (`MachinePlanningBoardView.xaml`, `MachinePlanningBoardViewModel.cs`): show computed readiness summary/reasons and selected revision/release/tooling context. Assignment and timeline controls remain available. Start remains clickable only when a fresh response says ready, but the Server remains authoritative and returns reasons if state changed.
- **Timeline** (`TimelineView.xaml`, `TimelineViewModel.cs`): retain blocks for planned-but-unready operations and show readiness conflicts/details without changing placement automatically.
- Extend `PlannerApiModels.cs` and `PlannerApiClient.cs`; raise the existing `PlanChanged`/`ConfigurationChanged` invalidation events after context-affecting mutations. Do not add a local database or a second refresh mechanism.
- Remove the unused Machine editor state from `MachinePlanningBoardViewModel`; Machine setup remains in Setup.

There is no current Windows Job Package screen to modify. If one is added later, it must select authoritative released data rather than collect ad hoc NC/tool/offset rows.

## Migration strategy

Use contiguous migrations after current schema V33. Splitting the feature reduces backfill and rollback risk while `DatabaseMigrator` still applies the complete set transactionally one migration at a time:

1. `SchemaV34PostprocessorsAndProcessRevisionsMigration`
   - Create `postprocessors`, `machine_supported_postprocessors`, and `process_revisions` with checks/indexes.
   - Add nullable current/selected process-revision pointers.
   - Backfill one current legacy process revision per Case Operation from current manual setup/cycle values.
   - For a Batch Operation whose snapshot differs from its source Case Operation, create/pin a separate legacy snapshot revision rather than falsifying history.
2. `SchemaV35GCodeReleasesMigration`
   - Create immutable release/file ownership metadata, current-release pointers, unique revision/release numbering, SHA/length checks, indexes, and update/delete protection triggers.
   - Add nullable selected release and estimate-provenance fields to Batch Operation. Do not synthesize releases from E-Ink package files.
3. `SchemaV36ToolingOffsetsAndEstimateOverridesMigration`
   - Create process tools, Machine tools, immutable offset revisions/rows, and estimate overrides; add nullable Batch Operation selections and indexes.

All new foreign keys are restrictive for released/history rows. Defaults are nullable/empty so old rows can be read immediately, but missing production context is reported honestly. No credentials, file bytes, or absolute source paths are migrated into logs.

Rollout must be staged because neither postprocessor support nor released G-code can be inferred safely. After migration, administrators configure postprocessors/Machine support and release/select artifacts. Existing in-progress/completed operations remain executable/history-readable and are never rewritten. Start enforcement is enabled only with the complete readiness policy described below; do not create a permissive compatibility release or infer Machine support.

Update `docs/functional-spec.md`, `docs/data-model.md`, `docs/api-contract.md`, and `docs/architecture.md` with the implemented behavior in the same implementation change, including the new not-started revision-selection semantics and correction of the older immutable-Batch-snapshot wording.

## Deferred material readiness

The repository stores Case material description and produces a weekly required-pieces report, but it does not own inventory. Legacy import can preview a `MaterialStatus` cell, but it does not persist an authoritative current material status. The functional baseline explicitly leaves ERP inventory exchange out of scope.

Do not reconstruct inventory from old Kitaron receipts or historic import rows. Define an `IMaterialReadinessProvider` boundary keyed by the concrete Batch/Case/material requirement. Until an authoritative current Kitaron/ERP response and freshness policy exist, it returns `NotEvaluated`; the overall readiness result must not claim `Ready`.

This creates an intentional rollout gate: do not activate final Start enforcement in production while every operation would be blocked only because material integration is absent. Foundation/UI work may expose an incomplete readiness result, but the feature is not production-complete until the authoritative material provider (or an explicitly approved requirement change to the enforced policy) ships. Once activated, Start/resume requires material readiness together with all other dimensions. A report, Case material text, stock allocation row, or historic receipt is not evidence of material readiness.

## Test plan

Add tests in the existing projects and style:

- Migration: fresh schema version/table/index/trigger checks; V33-to-latest upgrade; current and divergent legacy revision backfill; nullable defaults; foreign-key check; old E-Ink package readability; immutable release/offset update and delete rejection.
- Artifact storage: path traversal, disallowed type/size/count, duplicate final release, staging cleanup, atomic publish, hash/length verification, and no overwrite of previous releases.
- Domain: process-revision numbering/history; no Git-branch fields/terms in contracts; estimate precedence/fallback/zero/overflow and current cadence formula; stable readiness reason codes.
- Postprocessor: one release can apply to every Machine supporting its postprocessor; a physical Machine ID is absent from release persistence/contracts; unsupported postprocessor blocks production without moving or rewriting work.
- Readiness combinations: unassigned, missing selection, wrong process/release, missing files, missing/mismatched tools, stale offsets, material not evaluated/not ready, and fully ready. Verify no persisted `IsReady` authority.
- Execution API: unready operations may be assigned and appear on Planning Board/Timeline; Start and resume are transactionally rejected with reasons; ready Start succeeds; Suspend/Finish/Reset retain behavior; a stale client result cannot bypass re-evaluation.
- Estimate integration: selecting a release/override updates only not-started Batch snapshots; Planning Board and Timeline show the same new duration; started/completed snapshots remain unchanged.
- Job Packages: authoritative release/tool/offset selections generate the same immutable manifest assets; legacy revisions stay readable; ad hoc inputs cannot bypass a managed production context.
- Windows presentation: Setup postprocessor selection, Case Operations revision/release history, readiness reasons, Start state, and existing invalidation/refresh behavior.

Run both test projects plus focused migration/storage/timeline tests. Preserve existing Machine, Case Operation, Production Batch, assignment, E-Ink, Edit Mode, and Timeline suites as regression coverage.

## Reuse and refactor summary

Reused unchanged in identity: `Machine`, `MachineType`, `CaseOperation`, `ProductionBatch`, `BatchOperation`, `MachineAssignment`, Planning Board, Timeline engine, Case Working Folder, Edit Mode, optimistic versions, structured event log, and immutable E-Ink package history.

Extended: Machine supported postprocessors; Case Operation current process revision; Batch Operation selected process/release/offset context and estimate provenance; API/read projections and existing Windows screens.

Extracted/refactored: common immutable artifact storage from Job Package code; common duration calculation from Planning Board/Timeline paths; direct Case Operation timing edits into process-revision creation/selection; Job Package publication from ad hoc inputs to authoritative selections; unused Planning Board Machine editor code.

Not migrated as authority: old E-Ink NC copies, package-specific tool/offset JSON, `production_batches.route_revision`, legacy-import material preview, Kitaron receipt history, or Machine Type assignment overrides.
