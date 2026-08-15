# Functional Specification

- **Product:** Meimad Production Planner
- **Baseline:** v0.3 Client-Server + E-Ink
- **Source date:** 12 August 2026
- **Status:** Normalized internal draft; implementation status is identified in the architecture and implementation plan.

This document normalizes `Meimad_Planner_Functional_Specification_v0.3_Client_Server_EInk.docx`. Device-specific details are in [ESP32 / Color E-Ink Work Tablet](esp32-eink-work-tablet.md). Open choices remain explicit rather than being silently resolved.

## 1. Product definition

Meimad Production Planner is a local client-server system for manual planning of CNC machining work inside a factory. It replaces a slow shared Excel backlog with a fast visual planning tool.

The system centralizes planning data, calculates timeline consequences, and identifies and explains conflicts. It does not optimize the schedule automatically and does not silently repair a plan. All assignment, sequencing, and corrective decisions remain with a human planner.

The central server is the only authoritative source of planning data. Windows Planning Clients provide full editing; TV Dashboard and Color E-Ink Work Tablets are read-only operational views.

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
- Read-only E-Ink display and package API.
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
| E-Ink Work Tablet | Machine backlog, active operation, setup package, checklist, and read-only file viewing. | None for official data; device-local annotations only. |
| Setupist | Carries the machine tablet to the Tool Room and back; uses it as a setup viewer and local checklist. | No server write authority through the tablet. |
| Server operator | Installs/configures service and performs controlled backup/restore. Formal application role is TBD. | Operational authority is TBD. |

Authentication, authorization roles, and audit requirements for people are not defined by the source and remain open.

## 5. Authoritative data and core rules

### 5.1 Case

- A Case is the permanent master record for a part.
- A Case is not an Order and has no production quantity by itself.
- A Case contains Part Number, Name, Revision, Customer, Customer Reference, optional Preview path, Case Working Folder path, material type/specification, raw-material form/dimensions, notes, and a route of Case Operations. Its current setup and cycle totals are read-only derived summaries of that route.
- A Case may exist without any Orders.
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

Machine master data currently records Number, Name, a reusable Machine Type link plus a compatible legacy/process-type token, optional axis type, Machine-specific capability tokens, Working Calendar reference, active state, display-enabled state, an optional external picture path, and a read-only E-Ink binding projection. A Machine Type is a Server-owned named catalog entry with reusable capability tokens. Schema v10 links existing Machines to catalog entries generated from their legacy process types. Renaming a linked Machine Type propagates its name to the compatibility process token; type-capability changes and Machine edits are rejected when they would invalidate assigned work, and a rename is blocked while a Case Operation or unfinished Batch Operation requires the old name. A Machine Type cannot be deleted while any Machine, Case Operation, or Batch Operation references it.

SQLite stores Machine picture paths only; the Server streams supported image bytes to the Windows client. Assignment without an override requires an active Machine whose process/Machine Type name matches the Operation required type case-insensitively. Axis and capability tokens remain available for structural safety checks but do not suppress a warning when the selected Machine Type differs. A different active Machine type may be selected only through a visible warning and a second explicit confirmation with mandatory reason text. The Server atomically stores the assignment and immutable audit values for confirmer user/client, confirmation time, original intended type, selected Machine type, and reason. This override never permits an inactive Machine and never changes the Operation route. Assign, unassign, and explicit moves preserve stable backlog order, and an assignment command cannot move an existing in-progress operation away from position zero. The active editor can Start the first queued operation, Suspend an in-progress operation, resume it with Start, Reset a suspended operation to `not_started`, or Finish it. Reset closes the active pause while retaining the Machine assignment and backlog position. Finish records `completed`, removes the active assignment, and compacts the backlog. Both Reset and Finish atomically update the parent Batch and linked Order statuses; neither starts or rearranges another operation. Every accepted transition is structurally logged. The MVP stores current status and pause/audit events but no complete actual-time history.

Recurring weekly Working Calendar create, list, read, optimistic update, and guarded delete are implemented. The Server generates IDs and owns timezone/workday/local-window validation, while the Windows Setup page selects calendars by name. Current authoring supports either multiple non-overlapping same-day working windows or one overnight window, contained lunch/break windows, and dated closures or special-hour exceptions with optional contained breaks. Usage tags distinguish Machine, setup-worker, regular-worker, and QA-worker calendars. A Calendar cannot be deleted while actively referenced. The Timeline subtracts breaks and replaces the recurring schedule with any matching dated exception. If `useIsraeliHolidays` is enabled and no dated exception exists, cached `non_working` closes the day, `working` preserves the recurring schedule, and `partial_working` replaces it with the holiday's local range. These calculations are offline and never invoke the provider. Combined/multiple overnight-window policy, broader overtime policy, archive, and richer Machine lifecycle remain TBD.

The Windows client supports Machine editing through the Server's version-checked API. Guarded deletion is available for Cases, Case Operations, Orders, Production Batches, and Machines. The Server rejects deletion while protected relationships exist and never deletes external Case folders, images, original engineering content, or official package files. Production Batch deletion may remove only its own unassigned Batch Operations and explicit allocations as one transaction.

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

The client implements the API-only Case workspace, including optimistic Case Operation and Order editing and a simple local STEP viewer. The viewer accepts bounded `.stp` / `.step` text files, extracts explicit Cartesian vertices plus `EDGE_CURVE`/`POLYLINE` connectivity when present, and fits only Cartesian points referenced by the displayed model edges/polylines on initial display. Unreferenced coordinate-system and origin points do not affect Fit or orbit. Rotation uses the centroid of the unique referenced model vertices rather than the file origin. It provides drag rotation, wheel zoom, isometric/front/top/right views, an explicit Fit command, vertex-to-vertex straight distance plus X/Y/Z delta measurement in STEP model coordinate units, and PNG capture. A captured PNG is written only to the operator-selected path and assigned to the existing Case picture-path form field; the Case changes only after an editor explicitly saves it. It is a lightweight preview rather than a full CAD/BREP tessellator, so files without explicit edges display their available point cloud and use the available point-cloud centroid, while measurement selects available vertices rather than analytic CAD faces. The client also provides a compact Machine Planning Board with manual drag/drop commands and explicit player-style Start/Pause/Finish/Reset controls, and a read-only Timeline rendering Server intervals/conflicts plus dashed dependency arrows for the selected Batch. Each identified current or sole historical operation uses the primary portion of its compact Machine row. A current operation is one composite visual object with labeled phase spans: `PRODUCTION` blue (`#1E88E5`), `SETUP` yellow (`#FBC02D`), `QC` green (`#43A047`), and each returned `PART RELOAD` phase purple (`#7B1FA2`); internal gaps are transparent and a locked reservation phase is orange. Repeated reload spans remain inside that one assignment object rather than creating another operation identity. Generic idle and ordinary anonymous `waiting` capacity bars are suppressed in the default Timeline, so empty row space communicates waiting/idle. This suppresses display only: waiting data remains Server-calculated and API-returned for the conflict panel, tooltips/diagnostics, and future explicit debug display. Each Machine's Server-expanded `nonWorkingWindows` paints gray row-background columns for configured nights, weekends/closed weekdays, breaks, exceptions, overnight boundaries, and enabled cached holidays; these backgrounds are not Timeline operation blocks. Assignment-owned infeasibility remains visibly labeled `BLOCKED`; paused hold, downtime, and actual history remain visible. The same live Timeline view can be opened in a separate read-only window; closing that window does not close the planner or change the plan. Production durations are entered and displayed as total-hours `HH:mm:ss`, while the API continues to exchange seconds. The operation Machine requirement is selected from a dynamic union of registered Machine process, axis, Machine capability, and linked Machine Type capability tokens, with a blank Any option and preservation of a selected legacy token. Rejected mutations leave the displayed authoritative state unchanged and show text feedback. The local name is not authentication. Production login and the remaining unresolved planning workflows remain later phases.

### 9.1 Board view information hierarchy

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
- A staged legacy Excel importer. Its primary Windows workflow is **Cases in the Case Pool → related Orders → Batches in the Pool → Machine assignment → Review/commit**. These are focused views of one read-only staged workbook, not separate imports: the final Commit validates every approved selection atomically. Preview shows workbook sheets, a bounded catalog of detected source-column headers/samples, editable target mappings, detected Machine sections, source values/formula provenance, existing-entity candidates, and scoped blocking/warning issues. In addition to the supplied Hebrew working-plan layout, the Server detects ordinary flat Order sheets through normalized English/Hebrew header aliases such as Part Number, Order Number, Quantity, and Work Finish Date. A corrected sheet/mapping submission performs a fresh read-only preview, treats a complete submitted mapping list as authoritative (so cleared optional fields stay cleared), and returns a new staging token; a failed refresh invalidates the prior commit gate. Orders-only imports do not require a planning sheet, and excluded outcome issues/rows do not block or enter its commit envelope. Invalid mapped Order dates/quantities and missing required row identifiers remain blocking. The operator can explicitly create Cases/Orders, create a Batch from an existing Case route into the unassigned Planning Board pool, assign one selected created/existing Batch Operation to a Machine, or Skip.

  The Windows wizard may prepare a non-authoritative automatic draft from the current Server preview. This is not a new API operation, does not write data, and never triggers Commit. Invoking the action explicitly applies one global policy. It accepts only clear existing candidates: an automatic Machine choice must be one exact unambiguous source-section match, and an automatic assignment additionally requires exactly one eligible compatible Case-route Operation. Each eligible new Batch receives one `stock` allocation equal to that source row's full quantity; this is an explicit import assumption for otherwise unlinked demand, not an ERP stock-balance assertion. An exact existing Order becomes an unchanged Skip proposal; a new Order is proposed only when exactly one existing Case matches and its number, positive quantity, and ISO finish date are complete. The automatic path never creates a Case or Working Folder. Duplicate/blocking rows, zero/multiple Case matches, and incomplete source values become reasoned Skip proposals. When Machine or Operation evidence is ambiguous, the draft proposes the unassigned Pool if a unique routed Case makes that valid, otherwise Skip. Each automatic Skip retains a visible per-row reason and remains a proposal until the planner explicitly confirms all automatic skips; unconfirmed automatic skips block Commit. This is required because one successful schema-v25 receipt consumes the workbook hash and a later different approval of the unchanged workbook is rejected. The draft never creates a Machine or Case route, selects an existing Batch Operation, invents an Operation, chooses or confirms a compatibility override, assumes scrap, updates an existing Case/Order/Batch/Operation, or silently resolves a blocking source value. The planner can change every proposal before Review.

  A related Order may point either to an existing Case or to the stable source-row key of a selected `create_case` action. The Server creates selected Cases first, then resolves those related Orders inside the same transaction. “Pool” clones the complete existing Case route into ordinary unassigned Batch Operations; it does not invent or import a raw Excel route. Both Batch actions submit the complete reviewed Case Operation ID/version set, which must still match inside the atomic commit. Similar/all-row pattern expansion and the optional Batch-number template are reviewable client preview decisions, are never persisted, and expand to ordinary explicit row selections that the Server independently revalidates. Commit requires Edit Mode, at least one create/assignment action, and is all-or-nothing. A pool import snapshots the full route with no Machine assignment; an assign import snapshots the same full route and assigns only the explicitly selected Operation. Imported assignments append after existing backlog rows in source order and start in `manual`; import never invents route/timing data, moves an assigned operation, schedules dates, or rewrites the workbook.

All mutations remain gated by confirmed Edit Mode. Delete and deactivate controls are convenience commands only; the Server remains authoritative and blocks references or active assignments atomically. The Planning Board does not duplicate master-data forms. Employee Machine qualifications, roles, calendars, active state, and exceptions constrain the read-only Timeline calculation; cached holidays constrain opted-in calendars. A submitted qualification must identify an existing Machine. Report/email settings remain administrative only. Excel preview itself is not a planning mutation; only the explicit commit requires Edit Mode. A blocking source issue may be resolved through an explicit supported mapping or by choosing Skip, but it is never silently ignored for a selected row.

The Planning Board context menu changes an assigned operation's mode through an Edit-Mode-gated, ETag-checked mutation of its existing Machine Assignment. It provides `Schedule from delivery date`, `Schedule forward`, and `Set manual mode`; a successful change refreshes the same canonical Timeline and preserves assignment identity, Machine, and backlog position. The Timeline tab remains embedded in the main window and offers a separate-window action. Both surfaces use the same read-only Timeline view model and committed Server projection; the separate window is the same shared Timeline, never a backward/manual layer. Opening, refreshing, or closing it cannot assign, reorder, start, suspend, finish, or otherwise mutate planning data.

## 10. TV Dashboard

The TV Dashboard is a read-only web surface suitable for fullscreen/kiosk display. Its normal display contains only active, display-enabled Machine number, name, and clear status. A small green/yellow/red dot communicates connected/refreshing/disconnected state; host names, Server URLs, debug text, summaries, job detail, configuration, and controls are absent.

Implemented baseline: the Server serves a dependency-free kiosk page and `GET /api/v1/tv-dashboard`. The browser selects a row/column grid from Machine count and viewport size, constrains every card inside the available screen, conditionally refreshes every 15 seconds by default, and retains the last rendered status snapshot when refresh fails. It contains no scrolling, edit controls, or Edit Mode calls. The Server projection remains richer and authoritative; the kiosk only renders its Machine identity/status result. Authentication, offline-display telemetry, physical target-screen/read-distance acceptance, and kiosk deployment management remain TBD.

## 11. E-Ink integration

- MVP uses one unified Color E-Ink Work Tablet, normally one per Machine plus one or two spares.
- The tablet is read-only relative to official server planning/package data.
- Official package revisions download over Wi-Fi and cache on SD/microSD.
- Local checklist marks and comments remain on the device and never synchronize.
- USB Mass Storage and official CNC transfer responsibility are excluded.
- Device credentials restrict each tablet to assigned read-only Machine/package data.
- Server-observed last-seen, optional battery/firmware telemetry, and offline status must remain separate from planning data.

Implemented Server baseline: active Windows editors can register, bind/unbind, revoke, and rotate E-Ink devices; plaintext credentials are returned only when created or rotated. An active editor can publish an immutable official job-package revision for an assigned Batch Operation. The Server snapshots Machine/Case/Batch/Operation metadata, Timeline-calculated setup time, selected setup worker name/photo, job tools, optional expected-on-Machine tools, and local checklist seed definitions; it copies allow-listed files without modifying them and records SHA-256 for every file. Device-scoped GET endpoints provide a conditional version check, structured Machine screen, current and exact-revision manifests, checksum-verified package files, and time/refresh configuration. The manifest declares Wi-Fi transport, SD persistence, read-only Server access, no reverse synchronization, and no USB Mass Storage. The Server rejects a device credential on planning, Edit Mode, TV, package-generation, and registration-administration routes. The dependency-free browser simulator exercises the read contract. Package approval roles/UI and retention, physical SD staging, deep sleep, panel rendering, hardware inputs, local annotation persistence, and all ESP32 firmware remain outside this implemented server slice.

See [ESP32 / Color E-Ink Work Tablet](esp32-eink-work-tablet.md) and [API contract](api-contract.md).

## 12. Server and deployment requirements

- During development, the server may run as a normal executable or console application.
- In production, it should run as a Windows Service on a designated factory PC or local server.
- Clients connect to a configured hostname/IP and port over the factory LAN/Wi-Fi.
- Default deployment is LAN-only with no router port forwarding or public exposure.
- Only the server process opens SQLite and performs backup or restore.
- The server foundation includes configuration, logging, health reporting, migrations, backup, and restore verification.

Implemented backup decision: the Server uses SQLite online backup into local staging, publishes timestamped files to a configurable folder, retains a configurable number of verified backups, checks integrity and foreign keys, and restores only to an isolated test database for verification. It never restores over the active database. Schedule, authenticated operation, encryption, and disaster-recovery replacement remain open.
- Any later Customer Portal must be a separate minimal read-only system; it must not expose drawings, certificates of conformity, or customer VPN access.

Implementation stack, service identity, install/update method, TLS, authentication, firewall rules, log policy, monitoring, and availability targets are TBD.

## 13. Visual language and accessibility

Color may reinforce status but must never carry meaning alone. Every color is paired with text and/or an icon.

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
7. TV and E-Ink credentials cannot call planning mutation endpoints.
8. Status remains understandable without color.
9. Backup is restored and checked for integrity, not merely created.
10. Working Folder handling never changes original engineering files and confines generated content to `_MeimadPlanner`.
11. E-Ink downloads are scoped, checksum-verified, last-known-good, and read-only; local annotations never enter server state.
12. The production service can be installed, restarted, and recovered on the designated Windows host.

Quantitative scale, performance, reliability, backup RPO/RTO, and device thresholds must be added after the related open decisions are approved.

## 15. Future backlog

- Separate minimal Customer Portal showing order status only.
- Structured decision log for future AI-assisted planning.
- ERP inventory/status exchange.
- Native mobile application only if browser/PWA is insufficient.
- USB Mass Storage or official CNC package transfer only after a separate risk review.
- Setup-tablet write-back only after an explicit scope decision.

The consolidated unresolved-decision register is in [Implementation plan](implementation-plan.md#open-decisions).
