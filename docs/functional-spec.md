# Functional Specification

## Criticality, external delay, and layered calendars

Planning items are Batch Operations. The Pool is displayed by ready state, overdue Latest Start, earliest Latest Start, and linked Order Work Finish Date; this display calculation never reorders a persisted Machine backlog. A missing due date or duration produces an explicit warning. Case Operations may define an external process description, positive duration/unit, an existing Working Calendar, and whether to respect the Israel Master Calendar. Working-day delays require whole days and count configured working dates, skipping weekends, holidays, and factory closures. These facts are snapshotted into Batch Operations. Dependency calculation delays successors while freeing the predecessor Machine immediately and creates no external-delay Machine block. Setup exposes the Israel Master Calendar selection plus per-Machine and per-employee `Respect Israel Master Calendar` controls; opted-in availability is the resource calendar intersected with the master.

- **Product:** Meimad Production Planner
- **Baseline:** v0.3 Client-Server + E-Ink
- **Source date:** 12 August 2026
- **Status:** Normalized internal draft; implementation status is identified in the architecture and implementation plan.

This document normalizes `Meimad_Planner_Functional_Specification_v0.3_Client_Server_EInk.docx`. Device-specific details are in [ESP32 / Color E-Ink Work Tablet](esp32-eink-work-tablet.md). Open choices remain explicit rather than being silently resolved.

## 1. Product definition

Meimad Production Planner is a local client-server system for manual planning of CNC machining work inside a factory. It replaces a slow shared Excel backlog with a fast visual planning tool.

The system centralizes planning data, calculates timeline consequences, and identifies and explains conflicts. It does not optimize the schedule automatically and does not silently repair a plan. All assignment, sequencing, and corrective decisions remain with a human planner.

The central server is the only authoritative source of planning data. Windows Planning Clients provide full planning editing; TV Dashboard is read-only. Color E-Ink Work Tablets read assigned operational/package data and have one narrow operational command, `SEND_TO_QC`, which asks the Server to move the tablet workflow projection for its resolved active Production Run to `IN_QC` without granting planning Edit Mode.

## 2. Requirements language

- **Must** identifies a direct, non-negotiable MVP rule from the source specification.
- **Should** identifies a target that still needs implementation validation or an operational choice.
- **Proposed** identifies a design added to make the normalized documents coherent; it is not yet approved source behavior.
- **TBD** identifies a decision that must be resolved before dependent implementation begins.

## 3. MVP scope

### 3.1 Included

- Cases, Orders, Production Batches, and Operations.
- Machine catalog, working calendars, planned downtime, and machine backlogs.
- Manual assignment and backlog ordering of Batch Operations.
- Timeline calculation and visible, explained conflicts.
- Server-enforced Single Edit Mode.
- Full Windows planning client.
- Read-only fullscreen/kiosk TV Dashboard.
- Read-only E-Ink display/package API plus the scoped `SEND_TO_QC` operational event.
- Server-owned SQLite schema and migrations.
- Backup with verified restore.

### 3.2 Excluded

- Automatic scheduling, optimization, or silent plan repair.
- Public Internet exposure or remote editing.
- Customer Portal.
- ERP inventory synchronization.
- Planner-owned warehouse inventory balance.
- Native mobile applications.
- Full tool inventory management.
- E-Ink checklist/comment write-back.
- USB Mass Storage or official CNC program transfer by the tablet.

Later work requires an explicit scope decision; exclusion from MVP is not authorization to add a placeholder integration.

## 4. Users and system surfaces

| Actor or surface | MVP responsibility | Write authority |
|---|---|---|
| Planner using Windows client | Maintain master and demand data; create batches; assign and reorder operations; inspect timeline and conflicts. | Only while that client holds Single Edit Mode. |
| Current editor | Sole Windows client permitted to mutate planning data. | Yes, through the server API. |
| Other Windows clients | Continue viewing current server data and may request transfer of Edit Mode. | No until transfer succeeds. |
| TV Dashboard | Fullscreen/kiosk machine and factory status. | None. |
| E-Ink Work Tablet | Machine backlog, active operation, setup package, checklist, read-only file viewing, and `SEND_TO_QC`. | Only `SEND_TO_QC` for its Server-resolved active Production Run; no planning/package editing. |
| Setupist | Carries the machine tablet to the Tool Room and back; uses it as a setup viewer and local checklist, and may signal that setup work is ready for QC. | May invoke `SEND_TO_QC` through the assigned tablet; no planning Edit Mode or other server write authority. |
| Server operator | Installs/configures service and performs controlled backup/restore. Formal application role is TBD. | Operational authority is TBD. |

Authentication, authorization roles, and audit requirements for people are not defined by the source and remain open.

## 5. Authoritative data and core rules

### 5.1 Case

- A Case is the permanent master record for a part.
- A Case is not an Order and has no production quantity by itself.
- A Case contains Part Number, Name, Revision, Customer, Customer Reference, optional Preview path, Case Working Folder path, material type/specification, raw-material form/dimensions, notes, and a route of Case Operations. Its current setup and cycle totals are read-only derived summaries of that route.
- A Case may exist without any Orders.
- All Cases share one pool. Active component edges derive the roles standalone, parent, child, or both; role does not move a Case into another list.
- A parent owns its Case form, Components, and direct Orders. It has no direct Case Operations; the Windows workspace replaces its Operations tab with Components.
- A child shows parent-derived Orders read-only. Each projection multiplies source Order quantity across its component path and can be consumed by an explicitly allocated Production Batch owned by the child Case.
- The Case Working Folder is external. The database stores path strings only and no file bytes; an unavailable external path does not invalidate an existing Case. Original engineering files must never be modified.
- Generated previews or cache files may be placed only under `_MeimadPlanner` within the Case folder.
- Each Case Operation owns its current working setup and cycle values. The Case summary adds all operation setup values and all operation cycle-per-part values separately, treating a missing operation value as zero; an empty route therefore exposes zero for both totals. These sums are descriptive route totals, not authoritative elapsed Timeline duration. Parallel-capable and Locked-simultaneous timing continues to follow dependency semantics rather than a simple sum.
- The Windows client presents production-duration inputs and summaries as total-hours `HH:mm:ss` values, where hours may exceed 23. The Server API and SQLite storage remain locale-independent non-negative integer seconds.
- A Case is active for filtering when it has Order demand in `active` or `in_production`, or a Production Batch in `waiting` or `in_production`. A `complete` Order or Batch does not by itself make its Case active. Case activity is derived and never manually stored.

Whether one Case represents a part number across revisions or a part-number/revision pair remains open. Operation-owned timing and read-only Case totals are resolved for the MVP; existing Batch Operation timing snapshots remain unchanged when the source route is edited.

### 5.2 Order

- An Order belongs to a Case.
- It records quantity, Work Finish Date, status, and customer/order reference.
- It represents demand only and is never assigned directly to a Machine.
- Customer delivery date is outside MVP.

Order production status is Server-derived once an Order is linked through Batch Allocation. It is `active` before any allocated Batch Operation has left `not_started`, `in_production` when any allocated operation is in progress, suspended, or completed but the completion rule is not yet satisfied, and `complete` only when the Order quantity is fully allocated, every allocated Batch has at least one operation, and every operation in every allocated Batch is completed. A zero-operation Batch therefore cannot complete an Order. The Server recomputes every affected Order atomically with Batch creation/deletion and Start, Suspend, and Finish, including when one Batch serves multiple Orders or one Order is split across Batches.

Optimistic Order editing may change Order Number, quantity, Work Finish Date, Notes, and an allowed status assertion. A new or unallocated Order may be `active` or legacy/manual `cancelled`; manually submitting `in_production` or `complete` is rejected because production status is Server-owned. The Server also rejects a quantity below the already allocated quantity and rejects a submitted status that contradicts the derived status while allocations exist. New Batch creation rejects a cancelled Order. A legacy already-linked cancelled Order remains cancelled during automatic recomputation until an explicit status PATCH equal to current production facts resumes the derived lifecycle. Cross-Batch over-allocation and broader cancellation/reallocation policy remain TBD.

### 5.3 Production Batch and allocation

- A Production Batch is an actual production launch.
- A batch may fulfill one Order, split an Order, combine multiple Orders, include stock quantity, or be stock-only.
- Batch Allocation must explicitly identify quantity assigned to each selected Order, stock, and scrap allowance.
- Batch Operations are created from the Case route and are the units assigned to Machines.
- Meimad Planner does not own warehouse inventory balance; ERP remains authoritative for stock.

The implemented creation rule limits every Order allocation to the Batch Case and requires `plannedQuantity = Order allocations + stock + scrap allowance`. Allocation rows are positive, scrap cannot be the sole purpose, and stock-only is valid. Batch Operation scalar and dependency snapshots do not follow later Case Operation edits. Cross-Batch over-allocation and aggregate route-revision behavior remain TBD.

Production Batch status is Server-owned and follows its related Batch Operations. A new Batch is `waiting`, including the valid edge case where its Case route is empty. It becomes `in_production` when any related operation is `in_progress`, `suspended`, or `completed` while at least one operation remains unfinished; suspension therefore does not return a Batch to waiting. It becomes `complete` only when it has at least one operation and every related operation is `completed`. The Server persists this derived status atomically with operation execution changes; users do not edit it directly.

Machine master data currently records Number, Name, a reusable Machine Type link plus a compatible legacy/process-type token, optional axis type, Machine-specific capability tokens, Working Calendar reference, active state, display-enabled state, an optional external picture path, a read-only E-Ink binding projection, and schema-v34 execution configuration. Execution mode is explicit: `CNC_GCODE` or `MANUAL`. CNC readiness requires a released G-code whose Postprocessor is explicitly mapped to the Machine; MANUAL disables only that G-code requirement and does not bypass applicable tool, material, or other workflow requirements. Each Machine also stores optional positive usable tool positions, optional positive rapid rate in millimeters per minute, optional non-negative tool-change seconds, and a positive Machine time factor whose neutral default is `1.0`. A Machine Type is a Server-owned named catalog entry with reusable capability tokens and is not a Postprocessor. Schema v10 links existing Machines to catalog entries generated from their legacy process types. Renaming a linked Machine Type propagates its name to the compatibility process token; type-capability changes and Machine edits are rejected when they would invalidate assigned work, and a rename is blocked while a Case Operation or unfinished Batch Operation requires the old name. A Machine Type cannot be deleted while any Machine, Case Operation, or Batch Operation references it.

Postprocessors are managed Server-owned configuration records with name, optional description, active state, optimistic version, and timestamps. Machine compatibility is the explicit many-to-many Machine/Postprocessor mapping; brand, model, controller, Machine name, axis count, and Machine Type do not imply compatibility. A mapped Postprocessor cannot be deactivated or deleted. A Postprocessor with release history may be deactivated after mappings are removed and remains visible in history, but cannot be deleted. The Windows Setup page manages this catalog and selects one or more active Postprocessors on each Machine.

Each Case Operation can own a numbered process-revision history. Exactly one revision is active; it records the approved manufacturing-method change and exact immutable physical tool-table release. Released G-code belongs to one process revision and one Postprocessor, carries a per-process/Postprocessor revision number, original filename, size, SHA-256, releasing user/time, mandatory comment, and exact tool-table ID. It has no Draft state and cannot be updated or deleted. `LOCAL_POST_REVISION` publishes a new file while retaining the active process/tool table. `NEW_PROCESS_REVISION` requires explicit confirmation and a change description, activates the next process revision, and requires a new tool-table upload or confirmed exact reuse. Other Postprocessors become visibly stale until released for the new process. All historical process, G-code, and tool-table releases remain downloadable and hash-verified.

Planning is allowed without released context. For managed work, readiness for the concrete Batch Operation and Machine Assignment combines G-code, released tool table, contextual tool offsets, schema-v39 Batch material reservations, Machine/Postprocessor compatibility, and tool capacity. Component states are `READY`, `MISSING`, `OUTDATED`, `INCOMPATIBLE`, `BLOCKED`, `NOT_REQUIRED`, or `UNVERIFIED`; overall state is `READY_FOR_PRODUCTION` only when every applicable component is `READY` or `NOT_REQUIRED`. The Planning Board and Timeline retain not-ready work and explain it without changing assignment/order. First Start re-evaluates transactionally and rejects the first blocking component. A MANUAL Machine disables only G-code/Postprocessor requirements. Legacy Operations with no managed process revision preserve pre-v35 release behavior, while material reconciliation remains applicable.

Schema v36 parses every new released structured CSV/JSON tool table or Cimatron `TP_MODEL.TOOLS.mht` export into immutable tool rows: identifier, description, required/optional state, magazine-position requirement, active state, and optional position label. Every tool listed in a Cimatron MHT export is active, required, and magazine-consuming; its Number and Name become the identifier and description. Because that export has no separate pocket field, no magazine-position label is invented. `RequiredToolCount` is the count of distinct case-insensitive identifiers that are active, required, and consume a magazine position. Duplicate rows, optional tools, inactive/history rows, external tools, and the highest numeric T value do not inflate it. Schema v37 adds explicit assignment-level release selection and append-only offset readiness for the exact Batch Operation/Machine/process/release context. Schema v39 calculates material readiness from physically verified local receipts and explicit Batch reservations: required raw-material pieces equal planned quantity, including scrap. Old Kitaron receipts are never interpreted as availability. Changes to Machine, compatibility, capacity, process/releases, selection, offsets, material reservations, or Batch quantity appear on the next projection. No automatic workaround edits programs, tools, processes, assignments, reservations, or Batch quantities.

A Production Batch may be created, assigned, and shown on the Timeline with missing/unreserved material, but Start remains blocked until its Batch has enough reserved verified pieces. This material gate also applies to legacy Operations that otherwise retain their pre-v35 no-managed-process behavior. If material is short, the planner explicitly chooses to reduce the Batch and rebalance allocations, create a second ready/waiting Batch split with explicit allocations, or leave the full Batch waiting. The Server does not select or proportionally distribute this decision. Quantity changes reuse the existing dynamic Timeline pipeline and therefore recalculate run duration, setup/remaining production occupancy, dependent positions, conflicts, and readiness without rewriting completed/in-progress actual history.

SQLite stores Machine picture paths only; the Server streams supported image bytes to the Windows client. Assignment without an override requires an active Machine whose process/Machine Type name matches the Operation required type case-insensitively. Axis and capability tokens remain available for structural safety checks but do not suppress a warning when the selected Machine Type differs. A different active Machine type may be selected only through a visible warning and a second explicit confirmation with mandatory reason text. The Server atomically stores the assignment and immutable audit values for confirmer user/client, confirmation time, original intended type, selected Machine type, and reason. This override never permits an inactive Machine and never changes the Operation route. Assign, unassign, and explicit moves preserve stable backlog order, and an assignment command cannot move an existing in-progress operation away from position zero. The active editor can Start the first queued operation, Suspend an in-progress operation, resume it with Start, Reset a suspended operation to `not_started`, or Finish it. Reset closes the active pause while retaining the Machine assignment and backlog position. Finish records `completed`, removes the active assignment, and compacts the backlog. Both Reset and Finish atomically update the parent Batch and linked Order statuses; neither starts or rearranges another operation. Every accepted transition is structurally logged. The MVP stores current status and pause/audit events but no complete actual-time history.

Recurring weekly Working Calendar create, list, read, optimistic update, and guarded delete are implemented. The Server generates IDs and owns timezone/workday/local-window validation, while the Windows Setup page selects calendars by name. Current authoring supports either multiple non-overlapping same-day working windows or one overnight window, contained lunch/break windows, and dated closures or special-hour exceptions with optional contained breaks. Usage tags distinguish Machine, setup-worker, regular-worker, and QA-worker calendars. A Calendar cannot be deleted while actively referenced. The Timeline subtracts breaks and replaces the recurring schedule with any matching dated exception. If `useIsraeliHolidays` is enabled and no dated exception exists, cached `non_working` closes the day, `working` preserves the recurring schedule, and `partial_working` replaces it with the holiday's local range. These calculations are offline and never invoke the provider. Combined/multiple overnight-window policy, broader overtime policy, archive, and richer Machine lifecycle remain TBD.

The Windows client supports Machine and Production Batch editing through Server version-checked APIs. Batch editing changes Batch Number, planned quantity, and its complete balanced allocation set without recreating its instantiated route or changing operation execution/assignment state. Guarded deletion is available for Cases, Case Operations, Orders, Production Batches, and Machines. A confirmed Production Batch deletion is always allowed under active Edit Mode and atomically removes its owned assignments, pauses/execution records, allocation/Operation rows, assignment overrides, and package database records, then compacts affected Machine backlogs and recomputes linked Orders. It does not delete external Case folders, images, original engineering content, or physical package files.

### 5.4 Operations and dependencies

A Case Operation is a route-template operation. A Batch Operation is the concrete operation created for a Production Batch.

Each Operation carries unchanged API/storage timing inputs for QA-after-setup, `loadUnloadTimeSeconds` per load event, `loadUnloadRequiresWorker`, `automaticLoading`, and optional `loadUnloadEveryNParts`. With a positive per-event duration, manual loading schedules one initial load and then one cycle per part. Automatic loading with an every-N value schedules one initial load followed by up to N cycles, then repeats that load/production-group pattern for `ceil(plannedQuantity / loadUnloadEveryNParts)` load events. Automatic loading with no frequency schedules no load events and one production run; a zero per-event duration produces no materialized load event or worker reservation. Before materializing phases, Timeline returns blocking structured conflict `load_unload_occurrence_limit_exceeded` when any operation would require more than 10,000 non-zero-duration load/unload occurrences. Its message directs the planner to increase an automatic every-N cadence or split the Batch; a manual operation can instead be split or switched to an approved automatic cadence. This fixed, reversible calculation safety guard does not mutate quantity or allocation, and a broader configurable-cap policy remains TBD. Every worker-required load event independently reserves a regular worker. These values are copied into immutable Batch Operation timing snapshots; the Timeline changes only their calculated placement, never their stored fields or API contract.

The supported dependency semantics are:

| Type | Required behavior |
|---|---|
| Sequential | The dependent operation occurs after its required predecessor and may not overlap it. |
| Parallel-capable | Operations may overlap, but the planner may choose to run them sequentially. |
| Independent | No timing or order relationship is imposed. |
| Locked simultaneous | Linked operations start and finish together. Group duration is the longest member duration; shorter machines remain reserved until group end. |

The implemented domain representation uses stable dependency records between two Case Operations. `SEQUENTIAL` is directed from prerequisite to dependent. `PARALLEL_CAPABLE` and `INDEPENDENT` create no ordering constraint. `LOCKED_SIMULTANEOUS` is grouped by a stable group key and is treated as one timing-equivalent component for graph validation. Missing/cross-Case/self references, conflicting meanings for one pair, membership in multiple locked groups, sequential ordering inside a locked group, and sequential cycles after locked groups are collapsed are invalid. The pure time engine implements the four timing meanings on transient Batch Operation inputs. Schema v9 snapshots dependency type, predecessor source Case Operation ID, and simultaneous-group values into each Batch Operation; the Timeline resolves that source ID only within the same Batch. An authorized, optimistic Case Operation edit therefore affects only future Batches. Route position remains immutable through the edit endpoint; route reordering and richer cross-Machine feasibility remain TBD.

### 5.5 Machines, assignments, and downtime

- A Machine has a number, name, type/capability, working calendar, and display configuration.
- A Machine has explicit `CNC_GCODE`/`MANUAL` execution mode, supported-Postprocessor mappings, usable tool capacity, and explicitly unit-labelled estimator timing parameters. Start-time G-code compatibility is enforced for operations that have entered managed process-revision history.
- Every new released NC file is parsed once into versioned raw analysis: feed-motion seconds, rapid distance in millimetres, tool changes, recognized dwell, detected units, warnings, unsupported constructs, and confidence. G0/G1/G2/G3, modal F/coordinates, T/M6, recognized G4, G20/G21, and G90/G91 form the MVP. Unsupported macros, canned cycles, subprograms, feed modes, transformations/TCP, rotary motion, or malformed blocks warn rather than reject an otherwise valid release. Compatible Machines evaluate the same raw release separately using their rapid rate, tool-change time, and Machine time factor. Readiness and estimate availability remain independent.
- Schema v38 preserves append-only per-Release/per-Machine calculation inputs and results. An NC estimate represents one complete NC program execution cycle; migrated one-output programs with quantity per cycle 1 remain numerically equivalent to the former per-part value. The Windows Operation release history and Planning Board retain their compatibility labels until their later Production Run UI task. For not-started work, planning cycle precedence is a valid selected/current compatible NC estimate, then the unchanged manual Batch Operation cycle snapshot. There is no implemented manager-override field; that layer and actual-time calibration remain future work. Started work does not change duration source, and actual time never overwrites an estimate.
- For a not-started managed Operation, the stored setup snapshot means fixture installation. Setup occupancy adds loading already-prepared tools (`required tool count x configured seconds/tool`) and first-piece prove-out (`selected cycle x configured factor`). Normal cycle time then applies to `max(quantity - 1, 0)` parts, preventing the proved-out first part from being counted twice. Quantity zero has no setup/production occupancy. Existing setup-worker resources schedule the combined setup phase; off-Machine tool assembly is excluded. Started/history rows and legacy Operations without a process revision keep their stored timing behavior.
- The Planning Board exposes the Server readiness result as text and component explanations. An active editor can open Production Readiness from the visible summary or Operation context menu, choose a compatible current release when required, and physically confirm material and tool offsets. The dialog reloads Server state after save; only the Server may return **Ready for Production**. Release/process/tool/offset/readiness/compatibility/capacity/estimate changes are written to the existing structured event stream without credentials or file contents.
- A Machine Assignment links one Batch Operation to one Machine, a manual backlog position, and one planning-mode token: `forward`, `backward`, or `manual`. Schema v24 defaults existing and new assignments to `manual`.
- Machine downtime is either planned maintenance with a required end/planner or a reported breakdown that remains unavailable until an explicit restored time is recorded. Both carry an explained reason; restored breakdowns may also carry a repair note.
- Orders and Cases must not be put directly into machine backlogs.
- Capability mismatches, downtime overlaps, dependency violations, missing timing, and Work Finish Date risks should be detected and explained. Their exact severity rules are TBD.
- When simultaneously ready operations compete for one eligible employee, Timeline calculation grants the resource first to the Batch with the earliest allocated-Order Work Finish Date. If dates are equal, the naturally smaller Order Number wins. The losing Machine receives an explained waiting interval; this transient comparison never reorders either Machine backlog.
- Timeline is one canonical read-only projection, not a separate view or layer per mode. Each active assigned Operation ID produces exactly one identified current operation or blocked-waiting block. `Forward` calculates its earliest feasible consequence, `Backward` latest-fits that same assignment before the earliest Work Finish Date among its linked Orders, and `Manual` retains the planner-authored Machine/backlog placement while calculating its valid visible consequence. A not-started operation never receives a forecast or identified blocked marker before the effective Server calculation cursor; it sits at that instant or its next feasible Machine/calendar/resource/dependency time. A future backward slot remains latest-fit, but if its start passes without a reported Start, the same assignment transiently falls forward with `backward_start_missed`; dependencies/backlog successors shift through the fixed graph and deadline/no-fit warnings remain visible. A backward successor whose own baseline start had not passed, but whose slot becomes infeasible because an upstream fallback moved, uses `backward_fallback_required` instead; the same attention code identifies an expired/unavailable backward slot that can still be placed forward. `backward_deadline_missed` reports a recalculated finish beyond the cutoff, and `backward_schedule_cannot_fit` remains blocking only when no future placement fits. This calculation fallback does not rewrite its stored backward mode. Mixed modes share the same Machine lanes and dependency graph; equal-date backward contention uses shorter duration and then naturally smaller Order Number. No mode stores calculated start/end dates, creates another assignment/block, or changes backlog order. Ordinary waiting, downtime, and moved-history capacity annotations are anonymous in the public projection; their facts are folded into the canonical block's phases/detail. An assigned operation that cannot be placed retains identity as a lower-band `blocked` waiting block after the later of the calculation cursor and prior calculated backlog work. A final same-Machine overlap check keeps actual/hold/history authoritative, converts conflicting forecasts to blocked waiting, and reports a structured blocking conflict without changing stored priority. Blocked/unresolved state reaches a fixed point across every later Machine-backlog row, Sequential descendant, and locked-simultaneous forecast member, while recorded actual facts remain visible with conflicts. Completed or unassigned actual work retains one identified history block when no current assignment block exists. Missing, outside-horizon, elapsed-horizon, or infeasible backward deadlines return explained conflicts.
- Timeline subtracts planned maintenance and active/restored breakdown intervals from Machine availability. Work may split or move later around the unavailable window, whose typed reason and responsible/reported-by details remain visible; no assignment or stored backlog position changes.

## 6. Planning workflow

1. Maintain a Case and its route.
2. Register one or more Orders as demand under the Case.
3. Create a Production Batch.
4. Allocate batch quantity explicitly to Orders, stock, and scrap allowance.
5. Instantiate Batch Operations from the Case route.
6. Assign and reorder Batch Operations manually in Machine backlogs.
7. Recalculate projected operation times and conflicts on the server.
8. Display consequences without changing the user's plan.
9. Let the planner decide how to resolve warnings and blocking conflicts.

All authoritative validation and calculation occurs on the server. A client may provide immediate UI feedback, but its result is advisory until accepted by the server.

## 7. Timeline and conflict behavior

- The server must calculate timeline consequences from explicit planner choices.
- The result must account for Machine backlog order, operation duration, dependency type, Machine calendar, and planned downtime once their detailed rules are approved.
- A conflict must identify the affected record, explain the violated rule, and provide enough context for a planner to act.
- Calculation must not mutate assignment, sequence, dates, or dependencies to make a plan valid.
- Windows and TV clients consume the same server-derived planning projections.

Implemented pure-domain foundation: the server reads current persisted Machine assignments, planning modes, and backlog positions and uses its Timeline snapshot `readAt` as the calculation cursor. Start/end timestamps are calculated outputs, never required assignment inputs: production duration is Batch planned quantity multiplied by the immutable per-part cycle time, with setup, QA, and the applicable per-event load/unload phases added separately. Manual work contains an initial load followed by one cycle per part; automatic work with a frequency contains an initial/repeated load followed by each up-to-N-cycle group, and automatic work without a frequency contains no load phase. A `not_started` Forward or Manual operation earliest-fits at or after that cursor and then cascades through the unchanged stored Machine backlog and Sequential dependencies. Backward work searches for the latest valid slot before its transient delivery cutoff while that intended start remains future, allocates those same phase segments in reverse, and returns their phases chronologically. If the intended Backward start is missed without Start, it temporarily falls forward from the cursor to the nearest feasible time, shifts downstream consequences, keeps its stored Backward mode/backlog position, and returns `backward_start_missed` plus a deadline warning when late. Mixed modes are resolved within the same fixed graph and every assignment is normalized to one operation-linked result. Work splits across interrupted availability. Machine/setup/day-shift calendars, skilled setup/QA/regular workers, maintenance, breakdown, pause, and dependency delays are operation-linked waiting intervals with explanatory detail. Assigned operations that cannot be fully placed remain visible as an identified blocked marker at or after the cursor; a blocked earlier row prevents later work from leapfrogging. A wholly elapsed historical horizon never fabricates a `not_started` forecast. Backlog adjacency is never changed. Different operations of one Case/Batch may be assigned to different Machines. A Sequential child starts only after all calculated predecessor finishes and applicable availability. Parallel-capable and Independent add no timing edge; Locked-simultaneous retries at common Machine/resource availability, shares projected start/finish, and reserves shorter members. Invalid, unassigned-predecessor, cyclic, duplicate, or infeasible inputs return explained conflicts rather than plan mutations.

The engine is wired to a read-only HTTP Timeline projection over persisted assignments, active Machines, Working Calendars, the selected dedicated Setup Calendar, employee/resource availability, schema-v17 planned/breakdown downtime, immutable Batch timing/dependency snapshots, and Batch quantity. Each operation is calculated as setup, QA-after-setup, then its load/production cadence. Its one assignment block can therefore contain repeated `loadunload` phases between production groups. The calculation reserves one individual employee for every worker phase, so concurrent demand cannot exceed available head count; each worker-required load event gets its own regular-worker reservation. Setup workers additionally require an exact case-insensitive skill match against the Machine number, name, type, axis, or effective capability; `*` is an explicit general skill. QA and worker-required load/unload reserve QA and regular workers respectively. Calendars, breaks, full/partial employee exceptions, cached holidays, and Machine downtime constrain availability. Resource contention appears as a waiting interval and never changes stored backlog order. The calculated employee choice is projection-only, not a persisted assignment. Day-shift-only and dependency behavior remain additional constraints. Missing or invalid timing/calendar/resource inputs remain explained conflicts. Skill taxonomy/expiry, persisted worker assignment, rounding, plan revisions, the full conflict catalog/severity policy, acknowledgement, and performance targets remain TBD.

Implemented Windows Timeline display: the factory-local view presents a two-row hour ruler. Its header-only DAY/DARK bands use the Server Timeline's `displayTimeZoneId` and configured local `dayStartsAtLocal`/`dayEndsAtLocal` window. The bands mean configured shift-day context, not astronomical daylight; they are display-only and do not create intervals, blocks, Machine rows, capacity, dependencies, or scheduling consequences. The WPF `NOW` line estimates Server now as snapshot `readAt` plus elapsed local time and labels it in the configured factory timezone. A single shared 30-second refresh runs only while assigned `not_started` forecast or blocked work exists; embedded and separate Timeline windows share it, so opening the second window never doubles polling. The client has no Server/API/database forecast state and never calculates placement or mutates planning data. The descriptor is additive, so existing clients may ignore it.

## 8. Single Edit Mode

The server enforces one editor at a time. TV Dashboard and E-Ink devices never request edit rights.

1. One Windows Planning Client holds Edit Mode; every other Windows client remains in View Mode.
2. A View Mode client may request Edit Mode from the current editor.
3. The current editor receives a 30-second transfer request with **Release** and **Reject** actions.
4. **Release** transfers Edit Mode immediately.
5. **Reject** leaves Edit Mode with the current editor and the requester waits.
6. No response within 30 seconds transfers Edit Mode automatically.
7. The editor may voluntarily release Edit Mode at any time.

Implementation decision: the Server keeps 30 seconds as the default but permits an operator-configured transfer timeout from 1 through 3600 seconds. This configuration does not move timeout authority to the clients.

Every mutation must be rejected unless the caller has the active server-issued edit authority, subject to a later decision on administrative operations. Identity, lease/heartbeat, crash recovery, competing requesters, unsaved local changes, rejection waiting behavior, and audit semantics are TBD.

## 9. Windows Planning Client

Implemented client decision: the MVP desktop foundation uses WPF on .NET 10. It stores only the Server root URL, a local display name, and stable client ID under Local AppData; reads `/health` and server-owned Edit Mode through HTTP; and disables edit actions whenever authority cannot be confirmed. The main header shows only a compact connection dot—green when connected, red when disconnected, and yellow while connecting or when verification needs attention—with accessible status and detail text available through automation and hover. One compact vector lock/unlock button represents View/Edit Mode: the locked button requests the token, the unlocked button releases it, and its tooltip names the action and current holder where applicable. Server URL, local-name editing, Save, Connect, and Refresh are confined to the dedicated Setup page.

The client implements the API-only Case workspace, including optimistic Case Operation and Order editing and a bounded local STEP solid viewer. The viewer uses OpenCascade to read `.stp` / `.step` B-rep bodies and tessellate their faces into triangles. Shaded is the default display; Visible edges adds boundary, crease, and silhouette edges over the same shaded body; Wireframe hides the faces and draws the unique tessellation edges. The bounding box is hidden by default and appears only after the operator enables it. These are presentation modes for one loaded model, not separate geometry imports. Initial load performs one camera Fit using only tessellated body geometry, so coordinate-system and origin entities cannot expand or displace the view. Rotation never refits: it preserves the orthographic camera width and any manual wheel zoom until the operator explicitly selects Fit. Solid faces, optional edges, bounding box, and selection overlays use unchanged model coordinates plus one shared center and uniform camera projection; no independent axis normalization or display-layer offset is applied. A closed consistently oriented body rotates around its signed-volume center of gravity; open geometry falls back to the centroid of its tessellated model vertices. The viewer provides drag rotation, wheel zoom, isometric/front/top/right views, an explicit Fit command, vertex-to-vertex straight distance plus X/Y/Z delta measurement in STEP model coordinate units, and PNG capture. A captured PNG is written only to the operator-selected path and assigned to the existing Case picture-path form field; the Case changes only after an editor explicitly saves it. Import is bounded to 64 MiB, 1,500,000 tessellated vertices, and 500,000 triangles. Files that cannot provide tessellated faces retain the prior explicitly labeled edge/point fallback rather than being misrepresented as solids. The client also provides a compact Machine Planning Board with manual drag/drop commands and explicit player-style Start/Pause/Finish/Reset controls, and a read-only Timeline rendering Server intervals/conflicts plus dashed dependency arrows for the selected Batch. Each identified current or sole historical operation uses the primary portion of its compact Machine row. A current operation is one composite visual object with labeled phase spans: `PRODUCTION` blue (`#1E88E5`), `SETUP` yellow (`#FBC02D`), `QC` green (`#43A047`), and each returned `PART RELOAD` phase purple (`#7B1FA2`); internal gaps are transparent and a locked reservation phase is orange. Repeated reload spans remain inside that one assignment object rather than creating another operation identity. Generic idle and ordinary anonymous `waiting` capacity bars are suppressed in the default Timeline, so empty row space communicates waiting/idle. This suppresses display only: waiting data remains Server-calculated and API-returned for the conflict panel, tooltips/diagnostics, and future explicit debug display. Each Machine's Server-expanded `nonWorkingWindows` paints gray row-background columns for configured nights, weekends/closed weekdays, breaks, exceptions, overnight boundaries, and enabled cached holidays; these backgrounds are not Timeline operation blocks. Assignment-owned infeasibility remains visibly labeled `BLOCKED`; paused hold, downtime, and actual history remain visible. The same live Timeline view can be opened in a separate read-only window; closing that window does not close the planner or change the plan. Production durations are entered and displayed as total-hours `HH:mm:ss`, while the API continues to exchange seconds. The operation Machine requirement is selected from a dynamic union of registered Machine process, axis, Machine capability, and linked Machine Type capability tokens, with a blank Any option and preservation of a selected legacy token. Rejected mutations leave the displayed authoritative state unchanged and show text feedback. The local name is not authentication. Production login and the remaining unresolved planning workflows remain later phases.

The STEP shaded and visible-edge modes also draw the same camera-projected, depth-sorted triangles through a WPF software surface. This is a presentation fallback for hardware/driver environments where `Viewport3D` produces a blank frame; it does not create another model, alter coordinates, or change measurement and snapshot semantics.

### 9.1 Board view information hierarchy

- The Case Pool keeps an explicit vertical scrollbar inside its fixed left column; scrolling a long Case list does not move the selected Case detail workspace.
- The Case Pool can order the same Server-owned result set by Part Number, Customer name, or the closest Work Finish Date among active/in-production Orders. Missing Customers and Cases without current Order demand sort last.
- The Case edit form has an independent, always-visible vertical scrollbar so all fields and Save/Cancel controls remain reachable at compact window heights.
- STEP Viewer, Operations, Orders, and Batches each have an independent always-visible vertical scrollbar; the STEP canvas consumes the mouse wheel for model zoom without also scrolling its containing tab.
- Server connectivity, current View/Edit Mode, current editor, and Edit Mode action.
- Case/Batch pool with Active, Assigned, and Not Assigned filters.
- Search by part number, customer, and batch.
- Cards showing preview, part, batch, quantity, and text/icon status.
- Compact operation cards showing part, Batch, Operation number/name, planned quantity, allocated Order references (or stock/no-Order text), text/icon status, and the total input-derived `setup + QA + aggregate load/unload + planned quantity x cycle` estimate when timing inputs exist.
- Per-Machine backlogs showing the Machine number/name on one compact header line and player-style Start/Pause/Finish/Reset actions. Invalid or unauthorized actions remain disabled; buttons do not imply automatic advancement.
- Conflict count and explanatory messages.
- Manual drag-and-drop assignment and backlog ordering while in Edit Mode.
- Navigation to Timeline and TV views.

### 9.2 Case details information hierarchy

- Part identity, description, revision, customer, preview, material, and raw-stock description.
- Preview imagery remains unobstructed when available; explanatory text is reserved for the no-preview/error state rather than drawn across a valid thumbnail.
- Working Folder path with an open-folder action.
- Read-only current setup and cycle totals derived from the Case Operations, shown as total-hours `HH:mm:ss`.
- General, Files, Operations, Orders, and Batches sections.
- Ordered operations with number, name, Machine requirement, dependency, setup, and cycle.
- Add and edit operations while authorized to edit; route position does not change during an edit. Reordering remains a separate future command.

The source prototypes define zones and information hierarchy, not final visual design.

### 9.3 Setup and Timeline window

The dedicated Setup page owns connection settings and Server-authoritative resource management. It provides:

- Server URL, local user name, Save, Connect, Refresh, and current connection explanation.
- Working Calendar create/read/update/delete with usage tags, recurring work/break windows and dated exceptions; dedicated Setup Calendar selection/clear; and clear explanation that legacy explicit-window calendars are read-only.
- Machine create/edit/deactivate/delete with named Working Calendar and reusable Machine Type selection.
- Machine Type create/edit/delete with reusable capabilities.
- Employee/Resource create/edit/delete with employee number, first/last name, role (`setup_worker`, `regular_worker`, or `qa_worker`), a checkbox list of Machines the employee knows how to operate (persisted as stable Machine IDs), required compatible Working Calendar, optional photo path/notes/email, and active state. Each employee supports vacation, sick-day, personal-day, unavailable, and custom-note exceptions as either a full local day or a same-day `HH:mm` interval. The Timeline reserves individual employees transiently while calculating worker phases; inactive or calendar-less employees provide no capacity. Persisted worker-to-Operation assignment remains out of scope.
- Israeli holiday date/name/policy management, manual add/edit/delete, and explicit Hebcal refresh into a local offline cache. Manual corrections survive refresh. Opted-in Working Calendars apply cached non-working, working, or partial-working policies to Timeline and employee availability; calculations never call the provider.
- Weekly material-order report settings for sender, configurable recipients, SMTP relay/SSL, send weekday, local time, timezone, enablement, and manual Send Now. The report contains only Case/Part Number and required material-piece quantity; quantity sums each qualifying Batch planned quantity once, so explicit scrap allowance is included.
- Weekly employee-efficiency report with separate enablement/weekday/time and manual Send Now. It groups measured work by setup, QA, and regular employee; compares planned and actual time, signed and percentage difference; and compares both with capacity derived from calendars after breaks, holidays, and employee exceptions. It excludes payroll, ranking, Machine efficiency, and maintenance. Employees without measured work in the completed week are omitted instead of receiving an artificial zero score.
- Structured planning-event logging records cross-type overrides, per-assignment planning-mode changes, manual backlog reorder, Operation start/pause/resume/finish, maintenance/breakdown changes, calculated resource waits, and Timeline conflicts. Records carry event time/user and related IDs, plus reason/comment and before/after JSON when applicable. This is exportable evidence for future analysis; it performs no AI analysis, prediction, optimization, or dashboard ranking.
- A temporary fixed-mapping Excel importer. Its Windows page asks only for the workbook and worksheet, previews Case/Order rows, and provides one `Import Cases and Orders` action. The fixed mapping is Case Part Number A, Name O, Revision F, Customer D; Order Number B, Quantity L, Work Finish Date E; and active/production-instruction filter N. Other Case/Order fields remain empty. Part Number matches one Case and Case + Order Number matches one Order; existing records are not silently overwritten. Invalid rows are explained and skipped while valid rows remain eligible for one atomic pass. Exact approved passes replay idempotently. A later changed approval for the same workbook is accepted only when it is again Case/Order-only, contains at least one explicit creation, and contains no planning sheet, Batch, Operation, Machine mapping, assignment, or planning mutation; this lets the fixed tool resume after an older partial import while retaining a separate durable receipt for every accepted pass. Changed planning approvals remain rejected. This tool is temporary until Kitaron integration.
- A Server-local Kitaron connector page at `/kitaron-setup/`. Only loopback requests may use it. Steps 1-3 manage encrypted connection settings, metadata detection, and an optimistic Draft/Ready field mapping, including a `material_orders` group. Step 4 shows one-way synchronization status and permits a manual run; Ready mappings also run periodically. SQL Server is always opened with read intent. Each run atomically creates or refreshes connector-owned Cases, canonical `TSubOrder` Orders, reusable Case Operations, and every valid direct BOM edge returned by `TTreeNodes`. Schema v41 also imports `TBuyRow` raw-material purchase lines plus the latest matching `TAppCostOfferBySupplier` delivery approval into an advisory register. Historical Kitaron received quantities and approvals never create a locally verified receipt or reservation and never satisfy material readiness. Matching manual records are linked but never overwritten; stale links are repaired, missing imported component edges are deactivated, failures roll back, and no Kitaron mutation, allocation, Machine assignment, backlog entry, or Timeline position is created.

### 9.4 User Terminals

The implemented **User Terminals** Windows page is a read-only monitoring surface in
View Mode and an administration surface for the active Edit Mode holder. Every tablet
shows its stable Tablet ID, name, hardware MAC, Machine or spare assignment,
enabled/revoked state, last authenticated contact, reported firmware, battery and
Wi-Fi IP/RSSI, current Production Run, Server-projected workflow state, and current
official package revision. Absence is displayed explicitly; a recent timestamp is not
silently promoted to an online guarantee.

The active editor may register a tablet, bind or reassign its Machine, mark it spare,
enable or revoke it, and rotate its credential. The plaintext credential is returned
only at registration or rotation. Monitoring needs no Edit Mode, while each mutation
is revalidated against the current Server generation. Credential hashes and existing
plaintext credentials are never returned by the monitoring API.

### Haas VF-3 NGC execution integration

- Haas polling runs on the Server and uses the explicitly selected read provider, `MDC` or read-only `MTCONNECT`; connectivity, active program, and parts counters remain observations rather than workflow authority. Existing saved configurations default to MDC until a planner explicitly selects and saves MTConnect.
- The Part identity is the configured header value parsed from the NC program present on the Haas, or a valid direct PartName emitted by that program over the configured read-only DPRNT TCP port (default `8080`). The Server retains its DPRNT subscriber while the Machine is enabled and prefers the control-authored DPRNT PartName for monitoring and matching. Original filename and O-number remain separate informational/locator fields and never identify the Part.
- The first stable, valid, unique machine-header match starts the assigned Batch Operation in SETUP. No match or multiple matches create an anomaly and never choose or invent a Batch.
- The persistent CNC Setup/Production mode variable is removed. Changing any CNC variable between `0` and `1` has no workflow effect. Server state is projected from append-only Production Run operational events.
- **Persistent CNC workflow mode variable: REMOVED.**
- **Protected temporary setup verification variables: SUPPORTED**, only inside the configured, commissioned challenge/response handshake; they are never workflow authority.
- Protected temporary variables are supported only for the setup-verification challenge/response handshake; its exact Machine mapping and physical fail-closed behavior must pass the Machine-specific commissioning checklist before verification is enabled. They are not retained as Setup/Production state and their O-number/custom-code/variable mapping is configuration, not business logic.
- Schema v49 stores Server-received workflow events with separate Machine timestamps and optional NC release, Offset Loader release, tablet, user, source event, and source sequence evidence. Stored raw events are immutable; later projection changes do not rewrite them.
- Schema v50 stores immutable Offset Loader releases independently of the approved NC and tool-table releases, plus one explicit current pointer per Production Run. Creating a later Offset Loader preserves history and makes old release tokens fail current-resolution checks; release age is not a validity rule.
- Schema v51 requires exactly one generic verification hook on every newly approved NC release. It must be the first executable block, use the accepted `G65 P9xxx Axxxxxx (MEIMAD VERIFY V1)` or configured custom-G-code form, and carry a globally unique six-digit NC identity. The Server stores that identity against the immutable release and never edits the uploaded NC. Historical releases are not silently backfilled and remain verification-ineligible until explicitly re-released.
- The challenge/response algorithm-v1 folds six-digit nonce, Offset Loader token, NC identity, and a Server-derived/protected six-digit Machine key into a configurable 4-6 digit response using only bounded decimal arithmetic. The real VF-3SS reproduced all seven public vectors on 2026-08-26, including leading-zero `0282`; operator entry, alarms, cleanup, and protected-key access remain uncommissioned. Schema v52 atomically creates an expiring one-time Server session from a valid current Offset Loader event and supersedes it when a newer Offset Loader is selected. The assigned tablet status projection derives the response in memory under the fail-closed rules below.
- CNC source sequences are monotonic evidence. Gaps and out-of-order values create immutable data-quality anomalies while the received raw event remains intact; the Server never invents missing authoritative events. `(source, sourceEventId)` remains idempotent.
- After a Server-recorded `QC_PASS`, the assigned active Production Run Program may enter production only through a valid Haas DPRINT `CYCLE_START` (`CST`). A `CYCLE_END` (`CEN`) completes one cycle only when it is the immediately consecutive event from the same Machine source and matches that open Run/program attempt. Run and program identities are optional evidence in the wire line; when supplied they must match the Server-resolved active target. The END workflow event, idempotent cycle record, all coupled-output quantity changes, parent status projection, and structured audit commit atomically. Retries do not count twice. Controller part counters remain diagnostic and cannot advance official quantity.
- A new valid START while the preceding attempt is still open records a Server-derived immutable `CYCLE_INTERRUPTED` event and then retains the new START as the open attempt. The interrupted attempt never increments output and is never subtracted later. An END with no matching START, or with a nonconsecutive sequence, remains immutable Machine evidence and creates a typed workflow anomaly; it cannot create a completed cycle. Interruption/anomaly creation and the triggering Machine event are idempotent under the Machine source-event identity.
- No tablet or routine operator action closes production. When a valid current Offset Loader event starts the next authoritative Run/setup on the same Machine, schema v57 atomically appends `PRODUCTION_SESSION_CLOSED` for the most recent unclosed prior Run and stores its immutable closure projection. If the latest valid cycle fact is a completed END, `observedEndAt` and `effectiveEndAt` equal that raw Machine timestamp when present, otherwise its Server receipt time, and `endTimeInferred=false`. If the latest valid fact is an open START and an earlier completed pair supplies a minimum validated duration, only `effectiveEndAt = last START + minimum validated cycle duration` is stored with `endTimeInferred=true`; `observedEndAt` remains null. If no validated duration exists, both end fields remain unavailable rather than inventing one.
- Schema v58 preserves each sequenced production attempt as immutable raw evidence. START and its `COMPLETED` or `INTERRUPTED` boundary retain separate Server receipt times, optional Machine timestamps, source event identities, and sequences. An outcome-less START is `OPEN`; an orphan or nonconsecutive END never becomes a completion. Calculated duration is not stored. Future analytics may derive idle as the next START minus the previous END and may choose min/max/median/distribution, load/unload, interruption, outlier, setup, QC, efficiency, or downtime methods without rewriting raw history or treating one formula as permanent.
- The Server provides a bounded, read-only human diagnostic timeline for one related Machine/Production Run pair. It translates immutable workflow and existing data-quality anomaly facts into concise messages for setup, verification, QC, cycle completion/interruption, and session closure. Items remain ordered by authoritative Server receipt/detection time while showing optional Machine time separately. Raw JSON, raw DPRINT lines, credentials, nonces, and verification secrets are not exposed; reading requires no Edit Mode and cannot mutate planning data.
- Per-Machine protected-verification configuration is Server-owned and includes DPRINT transport, protected programs/custom alias, temporary variables, encrypted Machine secret, expected macro version, 4–6 response digits, timeout, and enablement. Public planning responses expose only `secretConfigured`; tablet contracts receive none of these sensitive details. For an authenticated tablet bound to the session Machine, the existing status route derives only the fixed-width response in memory while the session is pending, current, enabled, and unexpired. It omits the code otherwise and never returns the nonce, secret/key, variable mapping, or algorithm details. Strict protected-macro success/failure DPRINT ingestion and Server workflow resolution are implemented. Physical CNC operator input, alarms, cleanup, protected access, Reset/power-cycle behavior, and the alarm-before-cutting interlock remain uncommissioned.
- A deliberate 1.2-second D1/Refresh hold opens the tablet service/debug screen after bounded Server contact. It displays and serial-logs only operational identity, network health, Server contact/HTTP result, workflow/revision, battery/wake/refresh measurements, and the Server-reported verification result/macro version. Missing evidence is explicit. Credentials, Wi-Fi password, nonce, response code, Machine secret, protected-variable mapping, and algorithm details are forbidden. The gesture and physical layout remain provisional until enclosure/readability testing.
- Machine-side header access is fail-closed and remains subject to the real-controller acceptance checklist in `haas-active-program-header.md`.
- CNC communication is owned exclusively by the Server-side connection manager. Machine model/type never selects a protocol; the Machine's primary `MachineConnection.adapterType` does. `HAAS_NGC` is implemented and may explicitly select MDC or MTConnect as its read provider. The separate vendor-neutral `MTCONNECT`, OPC UA, and Custom adapter choices remain visibly unsupported and cannot be enabled.
- Every adapter emits the same normalized snapshot and capability/runtime-health model. Loss of the selected Haas read provider is `OFFLINE`; unavailable optional program-header or counter data is `DEGRADED` and must not discard other valid observations. MTConnect and the public CNC adapter surface do not write CNC variables. Retained field values carry their read time and stale flag.
- The TV dashboard does not read a CNC mode variable. Its phase is a Server-owned projection and timing fallback, paired with text rather than color alone.
- Clients obtain initial current state over HTTP and subscribe to relevant Machine IDs over the Server WebSocket. No client receives CNC credentials, vendor commands, or direct machine/share addresses for networking. Browser reconnect and CNC reconnect are independent actions.

### Multi-output Manufacturing Programs and Production Runs (architecture gate)

The accepted terminology and behavior are defined in [Production Run and multi-output Manufacturing Program architecture](production-run-architecture.md). Schema v45 implements a Manufacturing Program as a reusable immutable recipe. Schemas v46–v47 implement a Production Run as one concrete Machine session containing independently completing program streams, explicit Batch Operation allocations, exact production pins, and idempotent cycle history.

Production Run, rather than Batch Operation, is the Machine backlog unit. Batch Operation remains the concrete route/quantity/dependency obligation. One legacy assignment remains behaviorally equivalent through a one-program, one-output run. The Server forbids rounding, unequal coupled-output cycle counts, over-allocation, overproduction, output-only cycle advancement, silent release selection, and structural edits after Start. Completed program streams are skipped while other streams continue; a run completes only when all streams complete.

Manual cycle commands and valid CNC cycle completions share one Server-owned transactional accounting component. Authorization/workflow validation remains specific to the caller, but exact target checks, atomic coupled-output increments, program/run completion, Batch Operation/Batch/Order propagation, durable cycle history, and structured completion audit cannot diverge between the two paths.

  For the supplied order-driven Hebrew sheet, the Server maps Case fields from A/O/F/D, active Orders from B/L/E/N, and Batch Number/planned remainder from P/H. It deduplicates Cases by Part Number, combines repeated rows for one Part+Order into one Order (summed ordered quantity and earliest finish date), and creates one Batch proposal per Part+Batch Number. That Batch's planned quantity and explicit Order allocations are the sum of the related positive column-H remainders; empty time/stock is never inferred. The Windows automatic draft may create one missing Case per Part with only the mapped Case fields and a system-generated working-folder path below the workbook directory, then link related Orders by source-row identity in the same atomic commit. Existing routed Cases may produce Batch proposals immediately; a newly created Case still requires a planner-defined route before a later Batch import because the importer never invents Operations. The draft remains read-only until explicit Commit, never creates Machines/routes, never approves compatibility overrides, and retains visible reasons for every Skip.

  A related Order may point either to an existing Case or to the stable source-row key of a selected `create_case` action. The Server creates selected Cases first, then resolves those related Orders inside the same transaction. “Pool” clones the complete existing Case route into ordinary unassigned Batch Operations; it does not invent or import a raw Excel route. Both Batch actions submit the complete reviewed Case Operation ID/version set, which must still match inside the atomic commit. Similar/all-row pattern expansion and the optional Batch-number template are reviewable client preview decisions, are never persisted, and expand to ordinary explicit row selections that the Server independently revalidates. Commit requires Edit Mode, at least one create/assignment action, and is all-or-nothing. A pool import snapshots the full route with no Machine assignment; an assign import snapshots the same full route and assigns only the explicitly selected Operation. Imported assignments append after existing backlog rows in source order and start in `manual`; import never invents route/timing data, moves an assigned operation, schedules dates, or rewrites the workbook.

All mutations remain gated by confirmed Edit Mode. Delete and deactivate controls are convenience commands only; the Server remains authoritative and blocks references or active assignments atomically. The Planning Board does not duplicate master-data forms. Employee Machine qualifications, roles, calendars, active state, and exceptions constrain the read-only Timeline calculation; cached holidays constrain opted-in calendars. A submitted qualification must identify an existing Machine. Report/email settings remain administrative only. Excel preview itself is not a planning mutation; only the explicit commit requires Edit Mode. A blocking source issue may be resolved through an explicit supported mapping or by choosing Skip, but it is never silently ignored for a selected row.

The Planning Board context menu changes an assigned operation's mode through an Edit-Mode-gated, ETag-checked mutation of its existing Machine Assignment. It provides `Schedule from delivery date`, `Schedule forward`, and `Set manual mode`; a successful change refreshes the same canonical Timeline and preserves assignment identity, Machine, and backlog position. The Timeline tab remains embedded in the main window and offers a separate-window action. Both surfaces use the same read-only Timeline view model and committed Server projection; the separate window is the same shared Timeline, never a backward/manual layer. Opening, refreshing, or closing it cannot assign, reorder, start, suspend, finish, or otherwise mutate planning data.

## 10. TV Dashboard

The TV Dashboard is a read-only web surface suitable for fullscreen/kiosk display. Its normal display contains only active, display-enabled Machine number, name, clear connection state, and the latest normalized machine state (for example `ACTIVE`, `IDLE`, or `STOPPED`) received from the selected telemetry provider. A small green/yellow/red dot communicates connected/refreshing/disconnected state; host names, Server URLs, debug text, summaries, job detail, configuration, and controls are absent.

Implemented baseline: the Server serves a dependency-free kiosk page and `GET /api/v1/tv-dashboard`. The browser selects a row/column grid from Machine count and viewport size, constrains every card inside the available screen, conditionally refreshes every 15 seconds by default, and retains the last rendered status snapshot when refresh fails. It contains no scrolling, edit controls, or Edit Mode calls. The Server projection remains richer and authoritative; the kiosk only renders its Machine identity/status result. Authentication, offline-display telemetry, physical target-screen/read-distance acceptance, and kiosk deployment management remain TBD.

## 11. E-Ink integration

- MVP uses one unified Color E-Ink Work Tablet, normally one per Machine plus one or two spares.
- The tablet is read-only relative to official server planning/package data except for the approved `SEND_TO_QC` operational event.
- Official package revisions download over Wi-Fi and cache on SD/microSD.
- Local checklist marks and comments remain on the device and never synchronize.
- USB Mass Storage and official CNC transfer responsibility are excluded.
- Device credentials restrict each tablet to assigned read-only Machine/package data plus `SEND_TO_QC` for the Server-resolved active Production Run.
- Server-observed last-seen, optional battery/firmware telemetry, and offline status must remain separate from planning data.

`SEND_TO_QC` is an operational workflow command, not a planning edit. The request contains only `event_type: SEND_TO_QC`; it cannot name a Machine, run, program, Operation, or timestamp. The Server authenticates the enabled tablet credential, verifies the path identity, resolves the bound Machine and eligible active Production Run, creates the timestamp, records an append-only event, advances the tablet workflow projection to `IN_QC`, and changes the status revision. It does not assign, reorder, start, suspend, complete, allocate, count cycles, publish files, or change official package content. Single Edit Mode is not required. A repeat for the same run while its tablet workflow is already `IN_QC` returns the original accepted event and timestamp without another state change or duplicate event.

Implemented Server baseline: active Windows editors can register, bind/unbind, revoke, and rotate E-Ink devices; plaintext credentials are returned only when created or rotated. An active editor can publish immutable official job-package revisions, and device-scoped GET endpoints provide conditional version/status, structured Machine/package data, checksum-verified files, and time configuration. Schema v49 stores shared append-only workflow events and derives physical-tablet status from the latest run event. Schema v54 and the authenticated POST route resolve the tablet's current Run, enforce `IN_SETUP_RUN`, persist one `SEND_TO_QC` per inspection attempt, and return the original Server timestamp on retries without Edit Mode or planning/package mutation. Schema v55 permits a fresh send only after an immutable `QC_FAIL` returns the same Run to `IN_SETUP_RUN`. The separate ESP32 prototype compiles its bounded status client, retained revision behavior, deep-sleep policy, and guarded long-D4 action. Official tool binding, automatic work-window enforcement, physical wake/current/input behavior, and shop-floor readability remain unverified. Package approval roles/UI and retention, physical SD staging, local annotation persistence, and the remaining ESP32 acceptance work are incomplete.

The Windows **QC Queue** is a read-only list of Production Runs whose latest
workflow event is `SEND_TO_QC`. It shows Machine, all atomic output parts and
operations, Production Run, Server receipt time, and the setup worker from the
latest official package when known. Monitoring does not require Edit Mode.
Only the active Windows editor may record `PASS` or `FAIL`, with the local user
identity, Server timestamp, and an optional bounded reason/comment. `QC_FAIL`
returns only the event-derived workflow projection to `IN_SETUP_RUN`; `QC_PASS`
projects `READY_FOR_PRODUCTION`, and its immutable event timestamp is
`production_approved_at`. Neither action writes a CNC variable or changes
planning, package, quantity, backlog, or Production Run lifecycle facts.

The compiled firmware wake cycle logs wake reason, available UTC time, battery
voltage, and RTC-retained pre-sleep status/wake state. Timer, cold, Refresh, and
send wakes use bounded Wi-Fi only when required; page-only/invalid physical
wakes keep it off. The radio is disabled after HTTP work and before panel
refresh/deep sleep. RTC state preserves only the next sleep policy on local
wakes and never replaces Server state. Clock synchronization, configured
work-window gating, and measured current/runtime behavior remain unverified.

The first firmware also sends bounded device-health metadata with each existing
HTTP call and displays `LOW BATTERY` at its provisional 3.30 V
threshold. It does not infer a battery percentage. This is non-planning
telemetry and does not alter the scoped `SEND_TO_QC` body or authority; telemetry
history/retention and battery-percentage calibration remain future work.

For UI development before the workflow backend is complete, a separate
compile-time demo image renders persistent local fixtures for all initial
tablet states, Wi-Fi and Server errors, an unregistered tablet, and low
battery. It uses the production screen model but has no Wi-Fi/HTTP behavior
and cannot submit `SEND_TO_QC`; normal firmware remains Server-driven.

See [ESP32 / Color E-Ink Work Tablet](esp32-eink-work-tablet.md) and [API contract](api-contract.md).

## 12. Server and deployment requirements

- During development, the server may run as a normal executable or console application.
- In production, it should run as a Windows Service on a designated factory PC or local server.
- Clients connect to a configured hostname/IP and port over the factory LAN/Wi-Fi.
- Default deployment is LAN-only with no router port forwarding or public exposure.
- Only the server process opens SQLite and performs backup or restore.
- The server foundation includes configuration, logging, health reporting, migrations, backup, and restore verification.
- Separate self-contained x64 MSI packages install the Windows client and Server. The client package creates an all-users Start Menu shortcut. The Server package registers an automatic Windows Service, installs immutable binaries under Program Files, and directs SQLite, backups, and E-Ink packages to a preserved `%ProgramData%\MeimadPlanner\Server` tree.

Implemented backup decision: the Server uses SQLite online backup into local staging, publishes timestamped files to a configurable folder, retains a configurable number of verified backups, checks integrity and foreign keys, and restores only to an isolated test database for verification. It never restores over the active database. Schedule, authenticated operation, encryption, and disaster-recovery replacement remain open.
- Any later Customer Portal must be a separate minimal read-only system; it must not expose drawings, certificates of conformity, or customer VPN access.

The baseline install/update method is implemented with WiX MSI packages. Final service identity, live install/upgrade/recovery acceptance, code signing, TLS, authentication, firewall rules, log policy, monitoring, and availability targets remain TBD.

## 13. Visual language and accessibility

Color may reinforce status but must never carry meaning alone. Every color is paired with text and/or an icon.

The Windows client and TV dashboard support English, Hebrew, and Russian. They select the browser/operating-system language on first use and provide an explicit language selector. The choice persists locally. Hebrew uses right-to-left window flow while identifiers, Part Numbers, Machine Numbers, dates, durations, and other technical values retain their required representation. Server API values, SQLite values, paths, imported source data, and user-entered master data are never translated.

Windows localization is event-driven. Loading a new control localizes that control, selecting a tab coalesces one pass over the newly realized tab tree, and rapid language changes coalesce to one pass over each open window. There is no recurring visual-tree localization scan while the client is idle, and persisting the language choice does not block the UI thread. Expensive Timeline and STEP drawings similarly coalesce layout/data changes, defer redraw while hidden, and redraw at most once when a dirty view becomes visible. The five-second Server refresh runs at background dispatcher priority, cannot overlap itself, and unchanged edit-session snapshots do not rebroadcast presentation state.

| Color | Hex | Meaning |
|---|---|---|
| Blue | `#1E88E5` | Current / in progress |
| Green | `#2E7D32` | Done / OK |
| Yellow | `#FBC02D` | Attention / check needed |
| Orange | `#F57C00` | Risk / urgent soon |
| Red | `#C62828` | Blocking conflict / critical |
| Grey | `#9E9E9E` | Idle / unavailable / no data |
| Black | `#111111` | Primary shop-floor and E-Ink text |
| White | `#FFFFFF` | Primary E-Ink background |

Icon concepts are: folder/part card for Case, clipboard for Order, stacked blocks/package for Batch, CNC for Machine, clock/ruler for Timeline, warning triangle for Conflict, wrench for Downtime, lock/unlock for Edit Mode, circular arrow for Refresh, and page/next for Page.

## 14. Acceptance baseline

Before the MVP can be called complete, verification must demonstrate that:

1. Only the server opens SQLite; all clients use the API.
2. Timeline/conflict calculation reports issues without reordering or repairing the plan.
3. Orders cannot be assigned directly to Machines; Batch Operations can.
4. One-order, split-order, multi-order, stock-inclusive, and stock-only batches follow the approved allocation equation.
5. All four dependency types behave exactly as specified.
6. Single Edit Mode remains atomic under simultaneous requests and covers Release, Reject, 30-second automatic transfer, voluntary release, disconnect, and restart behavior once defined.
7. TV credentials cannot mutate; E-Ink credentials can call only their assigned reads and `SEND_TO_QC`, never planning mutation endpoints or another device's/run's resources.
8. Status remains understandable without color.
9. Backup is restored and checked for integrity, not merely created.
10. Working Folder handling never changes original engineering files and confines generated content to `_MeimadPlanner`.
11. E-Ink downloads are scoped, checksum-verified, last-known-good, and read-only; `SEND_TO_QC` is scoped, Server-targeted, Server-timestamped, and idempotent; local annotations never enter server state.
12. The production service can be installed, restarted, and recovered on the designated Windows host.

Quantitative scale, performance, reliability, backup RPO/RTO, and device thresholds must be added after the related open decisions are approved.

## 15. Future backlog

- Separate minimal Customer Portal showing order status only.
- Structured decision log for future AI-assisted planning.
- ERP inventory/status exchange.
- Native mobile application only if browser/PWA is insufficient.
- USB Mass Storage or official CNC package transfer only after a separate risk review.
- Any setup-tablet write-back beyond the approved `SEND_TO_QC` event requires a separate explicit scope decision.

The consolidated unresolved-decision register is in [Implementation plan](implementation-plan.md#open-decisions).
