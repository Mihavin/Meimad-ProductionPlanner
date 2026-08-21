# G-code release and readiness architecture

Status: Tasks 1-9 implemented through schema v39; authoritative ERP inventory integration, a persisted manager-override authoring flow, actual-time calibration, and detailed tool verification are identified explicitly below.

## Scope and terminology

Meimad has no `ProductionBench` entity. The concrete production context is the existing `BatchOperation`, created from an existing `CaseOperation` and placed through an existing `MachineAssignment`. These are reused; there is no second Machine, Operation, Production Batch, or assignment model.

A **process revision** is an approved manufacturing-method revision for one Case Operation. It is not a Git branch and has no source-control semantics. A **G-code release** is an immutable released production artifact for one process revision and one managed Postprocessor. Meimad has no Draft G-code state; unfinished programming stays outside the application.

## Reused repository structures

- `Domain/Machines/Machine`, `MachineService`, `SqliteMachineRepository`, `/api/v1/machines`, and **Setup > Machines** remain the single Machine model and setup flow. Schema v34 adds explicit `CNC_GCODE`/`MANUAL` execution mode, supported-Postprocessor IDs, usable tool positions, rapid rate in mm/min, tool-change seconds, and Machine time factor.
- `Domain/Postprocessors`, `PostprocessorService`, `SqlitePostprocessorRepository`, `/api/v1/postprocessors`, and **Setup > Postprocessors** manage output configurations. Applicability is only `release.PostprocessorId IN machine.SupportedPostprocessorIds`; a release never owns a physical Machine ID.
- `CaseOperation` remains the stable route/dependency node. Process and release history is keyed to its existing ID.
- `ProductionBatch`, `BatchOperation`, `MachineAssignmentService`, and `SqliteMachineAssignmentRepository` remain the production, scheduling, and execution boundary. Start is the transactional point at which exact release context is pinned.
- `JobPackageService` and `EInkOptions` supplied the established safe-path, staged-directory, hashing, atomic-move, and compensation pattern. E-Ink packages remain distribution snapshots; an old NC package asset is not promoted to an approved G-code release.
- Edit Mode headers/generation, `structured_event_log`, HTTP refreshes, existing view-model events, SQLite migration conventions, and xUnit/TestServer conventions are reused.

## Implemented Task 3 model

Schema v35 creates three history tables with restrictive foreign keys and uniqueness constraints.

`tool_table_releases`

- Stable `id`, `case_operation_id`, monotonically increasing operation-local `revision_number`.
- Original filename, server storage-relative path, byte length, SHA-256 hash.
- Release timestamp, releasing user, mandatory release comment, and timestamps.
- Update/delete triggers make published metadata immutable.

`process_revisions`

- Stable `id`, `case_operation_id`, monotonically increasing revision number, and exactly one active row per operation through a partial unique index.
- Exact `tool_table_release_id`, creator/time, mandatory manufacturing-change description, optimistic version, and updated timestamp.
- Creating a new revision deactivates the previous row in the same immediate SQLite transaction. History is never deleted.
- The exact released tool-table file represents the approved tool selection/order. Schema v36 adds immutable normalized released rows and a database-verified required-magazine-tool count.

`gcode_releases`

- Stable release ID; exact operation, process revision, Postprocessor, and tool-table release IDs.
- Monotonically increasing `post_specific_revision` within a `(process revision, Postprocessor)` pair.
- Original filename, server storage-relative path, size, SHA-256 hash, release user/time, mandatory comment, and `change_scope`.
- Allowed scopes are `LOCAL_POST_REVISION` and `NEW_PROCESS_REVISION`. There is no status or Draft value.
- Update/delete triggers make every release immutable. “Current” is derived as the greatest post-specific revision for the active process/Postprocessor pair, so publishing never mutates an earlier release.

The operation catalog returns all process revisions and releases. A Postprocessor is `current` when it has a release for the active process, `stale` when it has only historical-process releases, and `missing` when it has none. Inactive Postprocessors remain visible in history but cannot receive new releases. Referenced Postprocessors can be deactivated when no Machine mapping remains, but cannot be deleted.

## Process-revision and release behavior

The first approved process is released with `NEW_PROCESS_REVISION`, an uploaded tool-table file, explicit process confirmation, and exact tool-table confirmation. A later new manufacturing method requires a mandatory change description and either:

- a newly uploaded physical tool-table release, or
- explicit reuse of the active process's exact tool-table release.

`LOCAL_POST_REVISION` keeps the active process and exact tool-table ID. It publishes a new immutable G-code file for one Postprocessor and increments only that pair's post-specific revision. Uploading a changed tool table under local scope is rejected.

`NEW_PROCESS_REVISION` creates and activates the next process revision, makes all other Postprocessors stale relative to it, and publishes the supplied Postprocessor's first release for that new process. Neither action overwrites or deletes prior bytes or metadata.

The implemented service boundary is `Application/GCode/GCodeService` plus `IGCodeRepository`/`SqliteGCodeRepository`; HTTP routes are mapped by `Api/GCode/GCodeEndpoints`. `CaseWorkspaceViewModel` and `CaseWorkspaceView` add the selected Operation's active revision, revision history, Postprocessor matrix, release history, and one explicit **Release G-code** action. No Draft action exists.

## Released tool table and capacity validation

Schema v36 extends each `tool_table_releases` row with nullable `required_tool_count` and creates immutable `tool_table_release_tools` rows containing tool identifier, description, required/optional state, magazine-position requirement, active state, and optional position. New releases parse structured CSV or JSON before database publication. Their count is:

```text
count(distinct case-insensitive tool identifier)
where active and required and requires a magazine position
```

This deliberately excludes duplicate rows, optional tools, inactive/history rows, and external tools. It does not use the highest T number. A consistency trigger prevents a process revision from referencing a new release whose stored aggregate differs from its immutable rows. Existing v35 release metadata is preserved with a null count because the old arbitrary file bytes cannot be reconstructed safely.

`Domain/GCode/ToolCapacityEvaluator` is the shared pure rule. `SqlitePlanningBoardRepository` evaluates it on every board read for the concrete Batch Operation, selected process/tool-table context, assigned Machine, and `UsableToolPositions`. `SqliteMachineAssignmentRepository` evaluates the same rule transactionally before first Start. A MANUAL Machine skips only G-code compatibility; applicable released tool capacity still participates. A mismatch remains planned, is shown with required and available counts, and blocks readiness/Start without modifying a program, table, assignment, or process.

## Contextual readiness engine

Schema v37 adds assignment-level `selected_gcode_release_id`, the now-legacy manual Batch Operation material-confirmation table, and append-only tool-offset readiness records keyed to Batch Operation, Machine, process revision, and selected/effective G-code release. Schema v39 supersedes the manual material input with Case-scoped locally verified material receipts and explicit Production Batch reservations. No global `Operation.IsReady` value exists. `Domain/Readiness/ProductionReadinessEvaluator` is the single pure component rule used by `SqliteProductionReadinessRepository`, Planning Board projection, Timeline annotation, and the transactional first-Start check.

The components are G-code, Tool Table, Tool Offsets, Material, Machine/Postprocessor Compatibility, and Tool Capacity. A managed context is `READY_FOR_PRODUCTION` only when every component is `READY` or `NOT_REQUIRED`. CNC G-code distinguishes missing, historical/outdated, incompatible, and multiple-compatible-selection-required states. MANUAL execution makes only G-code and Postprocessor compatibility `NOT_REQUIRED`. A unique compatible current release can be resolved safely and is persisted on the assignment within the Start transaction; multiple compatible releases require the explicit readiness-input selection API.

Material is calculated at Production Batch scope. Required pieces equal Batch `planned_quantity`, including its explicit scrap allocation. The component is `READY` only when explicit reservations from locally verified receipts cover that quantity; it is `MISSING` when verified availability cannot cover it and `UNVERIFIED` when enough verified material exists but has not been reserved. Every Batch Operation reads that shared Batch result. The engine and Kitaron connector have no path that promotes historical Kitaron receipt/approval data into these tables. Offset confirmation is exact to the current production tuple; an older Machine/process/release record is retained and reported `OUTDATED`. When required tool count is zero, offsets are `NOT_REQUIRED`; otherwise missing/unverified offsets block managed Start.

Legacy Operations with no managed process revision continue to bypass G-code/tool-table/offset compatibility and production pinning. They do not bypass material: their effective production-ready state and Start gate use the same parent-Batch reservation result.

`GET /api/v1/batch-operations/{id}/readiness` returns the overall result, all component states/messages, effective release, and compatible release choices. The active editor updates release selection and offsets through `PUT /api/v1/batch-operations/{id}/readiness-inputs`; material fields remain in that request for wire compatibility but must match the derived state and cannot override it. Material is managed through the v39 receipt/reservation API. Planning Board cards expose the summary and component explanations. Timeline forecast intervals remain scheduled but receive a red text-labelled not-ready outline/tooltip. Readiness never auto-moves, removes, splits, or edits production work.

## File storage and recovery

`GCode:ReleaseRoot` configures server-owned physical storage and is resolved through `ServerStoragePathResolver`. It may be a configured server disk or server-accessible shared path. Clients upload bytes; clients never choose the final server path. Metadata stores only a safe relative path.

Layout:

```text
<GCode.ReleaseRoot>/
  operations/<caseOperationId>/
    gcode/<gcodeReleaseId>/<sanitized-file-name>
    tool-tables/<toolTableReleaseId>/<sanitized-file-name>
```

Publication uses a sibling `.staging-<stable-id>` directory, allow-listed extensions, configured size limits, write-through streaming, SHA-256 calculation from stored bytes, a `.meimad-release-id` ownership marker, and an atomic directory move. Existing staging/final targets are rejected. A failed database transaction compensates by deleting only the publication it created.

`GCodeStorageRecoveryService` runs after database initialization. It removes recognisable `.staging-*` directories and marker-owned final directories whose stable IDs are absent from release metadata. It never deletes unknown files or unmarked directories. Downloads resolve beneath the configured root and verify both byte length and SHA-256 before serving historical content.

Backups must include both SQLite and `GCode:ReleaseRoot`; a database-only backup cannot restore released production bytes.

## Concrete production pinning and readiness boundary

Schema v35 adds nullable production pins to `batch_operations`:

- `production_process_revision_id`
- `production_gcode_release_id`
- `production_tool_table_release_id`
- `production_gcode_file_hash`
- `production_tool_table_file_hash`

Planning and Timeline placement remain legal without these values. On the first Start of a not-started operation, `SqliteMachineAssignmentRepository` resolves the active process inside the same immediate transaction. A MANUAL Machine pins the process/tool-table context but does not require G-code. A CNC Machine requires exactly one current release whose Postprocessor is explicitly supported by that assigned Machine. Missing compatibility/release or ambiguous compatible releases blocks Start with a reason; the Server never moves the operation, rewrites G-code, or chooses silently.

Start stores exact IDs and hashes. Suspend/resume/finish retain them; a reset to not-started clears them. Publishing a later local or process release does not switch an in-progress operation. Legacy operations with no managed process revision retain pre-v35 execution behavior for backward compatibility.

This is contextual validation and immutable production pinning, not a persisted global `Operation.IsReady`. Readiness is calculated for `(BatchOperation, parent ProductionBatch quantity/reservations, MachineAssignment, effective process revision, selected/current release, released tool table, contextual offset record)`. Planning readiness and permission to Start remain separate.

## NC analysis and planning estimate

Schema v38 separates parse-once release analysis from Machine evaluation. `NcProgramParser` normalizes feed-motion seconds, rapid distance in millimetres, tool-change count, recognized dwell, units, warnings, unsupported constructs, parser version, and confidence. It supports modal G0/G1/G2/G3, F, T/M6, recognized G4, G20/G21, G90/G91, and plane selection. Macros, canned cycles, subprogram calls, unsupported feed modes, transformations/TCP, rotary motion, and malformed blocks produce warnings and lower confidence without rejecting the released artifact.

`NcCycleTimeEstimator` evaluates that immutable analysis for each explicitly compatible Machine. The formula is `(feed seconds + rapid distance / Machine rapid rate + tool changes × Machine tool-change seconds + dwell seconds) × Machine time factor`; distance is millimetres and time is seconds. `gcode_machine_cycle_estimates` retains append-only calculation history, raw metrics, Machine inputs, warnings, confidence, and calculation time.

For a not-started Batch Operation, planning precedence is `manager override > valid selected/current compatible NC estimate > manual batch_operations.cycle_seconds`. The pure estimator accepts the manager-override tier, but no persisted override authoring field exists yet, so current repository projection supplies null for that tier. Planning Board and Timeline use the selected source while preserving the manual snapshot. Started work does not switch duration source. Parser or Machine-input failure returns an unavailable estimate and falls back to manual timing; it never changes G-code readiness.

## Setup occupancy and Timeline duration

Task 7 reuses `batch_operations.setup_seconds` (snapshotted from the Case Operation) as the operation-specific **Fixture Setup Time**. It does not introduce a duplicate setup or worker model. For a not-started Operation with a managed active process revision, `SetupOccupancyEstimator` calculates in seconds:

```text
ToolLoading = RequiredToolCount * DefaultToolLoadTimePerTool
FirstPieceProveOut = SelectedCycleEstimate * DefaultFirstPieceFactor
TotalSetup = ToolLoading + FixtureSetup + FirstPieceProveOut
RemainingProduction = max(PlannedQuantity - 1, 0) * SelectedCycleEstimate
TotalPlannedMachineTime = TotalSetup + RemainingProduction
```

Tool loading means loading already assembled, prepared, and delivered tools into the Machine magazine. It explicitly excludes tool-room assembly and off-Machine measurement/preparation. `SetupEstimation:DefaultToolLoadTimePerToolSeconds` defaults to 60 seconds and `SetupEstimation:DefaultFirstPieceFactor` defaults to 1.5. Both are configuration-owned process defaults rather than Machine physics; they are deliberately isolated so a future setup-worker profile can supply tool-load time, first-piece factor, efficiency factor, and availability-calendar context without changing the formula.

The existing Timeline setup phase, setup calendar, setup-worker resources, skill matching, QA, load/unload cadence, Machine calendars, dependencies, downtime, and external delay remain authoritative. `ProductionCycleQuantity` separates full planned quantity (still used for load/unload occurrence rules) from normal-cycle quantity. The first part is included only in prove-out, so the Timeline never also adds `quantity * cycle`. Quantity zero contributes no setup or production occupancy; quantity one contributes setup/prove-out and zero remaining normal cycles.

Planning Board cards expose NC/manual source plus prepared-tool loading, fixture setup, first-piece prove-out, total setup, remaining runtime, and total planned Machine time. Existing QA and load/unload phases are additional established Timeline occupancy and are not folded into the Task 7 `TotalPlannedMachineTime` component value. Missing fixture or all cycle sources keeps duration unavailable for positive quantity. A missing required-tool count is visible as a warning and contributes zero tool-loading time until structured tool data exists.

Only not-started managed Operations are recalculated. In-progress, suspended, and completed work stays on its stored Batch Operation timing and production pins, so new NC estimates or configuration changes do not silently rewrite historical execution duration. Legacy Operations without a process revision retain their pre-v35 setup-plus-quantity-cycle behavior.

## Migration and compatibility strategy

- V34 safely defaulted existing Machines to `MANUAL`, left unknown capacity/rates null, set factor `1.0`, and inferred no Postprocessor mappings.
- V35 creates empty release/history tables and nullable Batch Operation pins. It does not invent process revisions, G-code, tool tables, hashes, or compatibility from old Case fields or E-Ink assets.
- V36 creates immutable released-tool rows and adds nullable `required_tool_count`. Existing v35 releases retain null instead of receiving an invented count; all new releases must have parsed rows and a verified aggregate.
- V37 adds nullable assignment selection and empty readiness-input/history tables. It invents no offset confirmation or material availability; missing rows evaluate safely as missing/unverified. Existing Operations without a managed process retain the documented legacy Start compatibility path.
- Task 7 is migration-free: it reuses fixture setup, released required-tool count, planning-cycle selection, quantity, and existing setup-resource scheduling. Its two backward-compatible defaults are application configuration, not persisted historical facts.
- Task 8 is migration-free and uses the schema-v22 append-only structured event stream. Existing v34-v38 migrations remain repeatable; no legacy file is silently promoted to a production release. Pre-release files remain represented by the existing package/file workflow until a programmer explicitly publishes an immutable release.
- Existing Machines, Cases, Operations, Batches, packages, and execution records remain readable. Operations gain managed context only when an approved first process is released.
- Release numbering and active-process uniqueness are serialized with `BEGIN IMMEDIATE` and database uniqueness constraints. Publication failures clean up staged/final artifacts; startup recovery handles a process crash between atomic file publication and database commit.
- Operation deletion is blocked once immutable process/release history exists. Released physical files have no retention deletion workflow in this task.

## Deferred work

- Detailed Machine tool identity/presence/life validation, actual offset values, manager override, and actual-time calibration are later tasks. Task 5 stores only contextual offset readiness confirmation, not an offset-value inventory.
- Implemented in schema v39: material reconciliation is local and intentionally narrower than warehouse inventory. A user records a physically verified Case receipt, then explicitly reserves pieces to a Production Batch. Receipt freshness, ERP stock authority, automatic reservation, and historical Kitaron stock reconstruction remain out of scope.

## Material shortage decisions and quantity consequences

The server never chooses a shortage remedy. The planner may keep the Batch unchanged and wait, edit its balanced allocation set to reduce quantity, or use the existing create/edit Batch workflow to partition demand into explicitly allocated ready and waiting Batches. Reservations must be released before quantity is reduced below the reserved count, so no stock is silently discarded. A Batch quantity edit retains its instantiated route and assignments; Timeline and readiness read current `planned_quantity` on every projection, so cycle runtime, setup/remaining-run occupancy, dependent positions, conflicts, and material requirement update on refresh. Completed/in-progress historical actuals are not rewritten.
- Job Package generation still accepts its existing package inputs. A later change should snapshot selected authoritative process/release/tool/offset context without rewriting old package history.

## End-to-end Windows workflow and production audit

The normal Windows workflow now covers Machine execution configuration in Setup, immutable Operation release/history and Batch material reconciliation in the Case workspace, assignment and readiness on the Planning Board, and Timeline consequences. The textual readiness summary is clickable and also available from the Operation context menu. Its dialog lists all six component states/messages and current compatible release choices; material is read-only there, while tool offsets remain an exact-context confirmation. Saving requires Edit Mode, reloads the authoritative board, and presents **Ready for Production** only from the Server result.

Release and readiness transactions append distinct structured event types without file contents or credentials: `gcode_release_published`, `tool_table_release_published`, `process_revision_created`, `process_revision_activated`, `local_post_revision_published`, `tool_offsets_confirmation_recorded`, `material_readiness_changed`, `production_readiness_transition`, `machine_compatibility_failure`, `tool_capacity_mismatch`, and `nc_estimate_recalculated`. Compatibility/capacity events use stable context keys to avoid duplicate audit spam for the same Machine/process/count state. Event writes share the production-data transaction.

## Verification

Migration tests cover fresh and prior-version upgrades, repeat application, rollback fixtures, schema identity, v35 unknown-count preservation, and entity timestamps. Readiness domain/API tests cover manual G-code exemption, current/stale/incompatible/multiple releases, missing table/offsets/material, capacity mismatch, explicit selection, immediate reassignment effects, planned-state retention, and Start rejection/acceptance. NC tests cover metric/inch units, absolute/incremental/modal feed, rapid, linear/arc/helical motion, tool changes, dwell, comments/sequence numbers, macros, canned cycles, malformed blocks, per-Machine results, recalculation history, manual fallback/preservation, and Timeline-source precedence. Setup-estimate tests cover zero/one/many quantities, prepared-tool loading, fixture setup, prove-out factor, first-part de-duplication, manager/NC/manual precedence, Machine-estimate changes, missing inputs, configuration validation, Timeline duration, API projection, and Windows presentation. Task 8 acceptance runs the complete 15/20-ready, local-revision, stale-post, incompatible-Machine, 25/20-mismatch, immutable-history, and audit sequence; WPF startup coverage opens the readiness dialog. Existing Machine, execution, Timeline, migration, server-startup, and Windows suites remain regression coverage.
