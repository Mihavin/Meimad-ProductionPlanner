# Functional Specification

- **Product:** Meimad Production Planner
- **Baseline:** v0.3 Client-Server + E-Ink
- **Source date:** 11 August 2026
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
- A Case contains Part Number, Name, Revision, Customer, Customer Reference, optional Preview path, Case Working Folder path, material type/specification, raw-material form/dimensions, current working setup time, current cycle time per part, notes, and a route of Case Operations.
- A Case may exist without any Orders.
- The Case Working Folder is external. The database stores path strings only and no file bytes; an unavailable external path does not invalidate an existing Case. Original engineering files must never be modified.
- Generated previews or cache files may be placed only under `_MeimadPlanner` within the Case folder.
- MVP stores current working setup and cycle values, not separate plan/fact history.
- A Case is active for filtering when it has active demand or active Production Batch context. The source does not define qualifying statuses. The implemented minimal lifecycles treat Order `active` and Production Batch `planned` as active context; Case activity is derived and never manually stored. Additional Batch lifecycle statuses remain TBD.

Whether one Case represents a part number across revisions or a part-number/revision pair remains open. Current Case-level timing is implemented; precedence and propagation relative to Case Operation timing must be decided before route/timeline behavior is frozen.

### 5.2 Order

- An Order belongs to a Case.
- It records quantity, Work Finish Date, status, and customer/order reference.
- It represents demand only and is never assigned directly to a Machine.
- Customer delivery date is outside MVP.

### 5.3 Production Batch and allocation

- A Production Batch is an actual production launch.
- A batch may fulfill one Order, split an Order, combine multiple Orders, include stock quantity, or be stock-only.
- Batch Allocation must explicitly identify quantity assigned to each selected Order, stock, and scrap allowance.
- Batch Operations are created from the Case route and are the units assigned to Machines.
- Meimad Planner does not own warehouse inventory balance; ERP remains authoritative for stock.

The implemented creation rule limits every Order allocation to the Batch Case and requires `plannedQuantity = Order allocations + stock + scrap allowance`. Allocation rows are positive, scrap cannot be the sole purpose, and stock-only is valid. BatchOperation field snapshots do not follow later CaseOperation edits. Cross-Batch over-allocation/lifecycle behavior, aggregate route revision, and dependency snapshots remain TBD.

Machine master data currently records Number, Name, process type, optional axis type, capability tokens, Working Calendar reference, active state, display-enabled state, an optional external picture path, and a read-only E-Ink binding projection. SQLite stores the picture path only; the Server streams supported image bytes to the Windows client. Manual assignment accepts only an active compatible Machine. Assign, unassign, and explicit moves preserve stable backlog order. The active editor can Start the first queued operation, Suspend an in-progress operation, resume it with Start, or Finish it. Finish records `completed`, removes the active assignment, and compacts the backlog; it never starts or rearranges another operation. The MVP stores current status but no actual-time history. Basic recurring weekly Working Calendar creation/listing is implemented: the Server generates IDs and owns timezone/workday/local-shift validation, while the Windows Machine form selects a calendar by name. Breaks, exceptions, holidays, overnight shifts, and Machine lifecycle beyond active/inactive remain TBD.

The Windows client supports Machine editing through the Server's version-checked API. Guarded deletion is available for Cases, Case Operations, Orders, Production Batches, and Machines. The Server rejects deletion while protected relationships exist and never deletes external Case folders, images, original engineering content, or official package files. Production Batch deletion may remove only its own unassigned Batch Operations and explicit allocations as one transaction.

### 5.4 Operations and dependencies

A Case Operation is a route-template operation. A Batch Operation is the concrete operation created for a Production Batch.

The supported dependency semantics are:

| Type | Required behavior |
|---|---|
| Sequential | The dependent operation occurs after its required predecessor and may not overlap it. |
| Parallel-capable | Operations may overlap, but the planner may choose to run them sequentially. |
| Independent | No timing or order relationship is imposed. |
| Locked simultaneous | Linked operations start and finish together. Group duration is the longest member duration; shorter machines remain reserved until group end. |

The implemented domain representation uses stable dependency records between two Case Operations. `SEQUENTIAL` is directed from prerequisite to dependent. `PARALLEL_CAPABLE` and `INDEPENDENT` create no ordering constraint. `LOCKED_SIMULTANEOUS` is grouped by a stable group key and is treated as one timing-equivalent component for graph validation. Missing/cross-Case/self references, conflicting meanings for one pair, membership in multiple locked groups, sequential ordering inside a locked group, and sequential cycles after locked groups are collapsed are invalid. The pure time engine implements the four timing meanings on transient Batch Operation inputs; persistence, edit/version behavior, dependency snapshot mapping, and richer cross-Machine feasibility remain TBD.

### 5.5 Machines, assignments, and downtime

- A Machine has a number, name, type/capability, working calendar, and display configuration.
- A Machine Assignment links one Batch Operation to one Machine and a manual backlog position.
- Downtime is a manually planned Machine-unavailable interval.
- Orders and Cases must not be put directly into machine backlogs.
- Capability mismatches, downtime overlaps, dependency violations, missing timing, and Work Finish Date risks should be detected and explained. Their exact severity rules are TBD.

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

Implemented pure-domain foundation: the server accepts explicit half-open UTC Machine availability, setup availability, downtime, fixed backlog order, resolved setup/production durations, and dependencies. It calculates earliest-feasible split work intervals, idle available time, downtime display intervals, and locked-simultaneous reservations. Backlog adjacency is never changed. Sequential constrains the dependent start; Parallel-capable and Independent do not; Locked-simultaneous shares projected start/finish and reserves shorter members. Invalid or infeasible inputs return explained conflicts rather than plan mutations.

The engine is wired to a read-only HTTP Timeline projection over persisted assignments, active Machines, explicit UTC calendar JSON, planned downtime, Batch timing snapshots/quantity, and current Case route dependencies. Production duration is provisionally quantity multiplied by cycle time. Missing or invalid inputs become explained conflicts. Recurring calendars/breaks, time-zone/DST conversion, setup-resource capacity, immutable dependency snapshots, rounding, recalculation triggers, plan revisions, the full conflict catalog/severity policy, acknowledgement, and performance targets remain TBD.

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

Implemented client decision: the MVP desktop foundation uses WPF on .NET 10. It stores only the Server root URL, a local display name, and stable client ID under Local AppData; reads `/health` and server-owned Edit Mode through HTTP; and disables edit actions whenever authority cannot be confirmed. It implements the API-only Case workspace, a Machine Planning Board with manual drag/drop commands, and a read-only Timeline rendering Server intervals/conflicts plus dependency edges for the selected Batch. Rejected assignments leave the board unchanged and show text feedback. The local name is not authentication. Record-mutation UI beyond Cases/assignments and production login remain later phases.

### 9.1 Board view information hierarchy

- Server connectivity, current View/Edit Mode, current editor, and Edit Mode action.
- Case/Batch pool with Active, Assigned, and Not Assigned filters.
- Search by part number, customer, and batch.
- Cards showing preview, part, batch, quantity, and text/icon status.
- Per-Machine backlogs showing setup, production, conflict, and downtime context.
- Conflict count and explanatory messages.
- Manual drag-and-drop assignment and backlog ordering while in Edit Mode.
- Navigation to Timeline and TV views.

### 9.2 Case details information hierarchy

- Part identity, description, revision, customer, preview, material, and raw-stock description.
- Working Folder path with an open-folder action.
- Current setup and cycle values.
- General, Files, Operations, Orders, and Batches sections.
- Ordered operations with number, name, Machine type, dependency, setup, and cycle.
- Add and reorder operations while authorized to edit.

The source prototypes define zones and information hierarchy, not final visual design.

## 10. TV Dashboard

The TV Dashboard is a read-only web surface suitable for fullscreen/kiosk display. It should show:

- Current time and server freshness.
- Per-Machine current status, Batch, part, Operation, projected finish, and next work.
- Conflicts, downtime, idle state, and setup-required state.
- Factory-level counts for critical conflicts, urgent batches, and offline displays.

Implemented baseline: the Server serves a dependency-free kiosk page and `GET /api/v1/tv-dashboard`. Only active, display-enabled Machines appear. Until a shop-floor execution lifecycle is defined, the first unfinished backlog operation is labeled Current job and the following operation is Next job. Active-Order Batches due within a configurable 48-hour UTC cutoff are urgent. The page shows text/icon labels with color, refreshes conditionally every 15 seconds by default, and retains the last rendered snapshot when refresh fails. It contains no edit controls or Edit Mode calls. Authentication, offline-display telemetry, target screen/browser acceptance, local-date urgency semantics, and kiosk deployment management remain TBD.

## 11. E-Ink integration

- MVP uses one unified Color E-Ink Work Tablet, normally one per Machine plus one or two spares.
- The tablet is read-only relative to official server planning/package data.
- Official package revisions download over Wi-Fi and cache on SD/microSD.
- Local checklist marks and comments remain on the device and never synchronize.
- USB Mass Storage and official CNC transfer responsibility are excluded.
- Device credentials restrict each tablet to assigned read-only Machine/package data.
- Server-observed last-seen, optional battery/firmware telemetry, and offline status must remain separate from planning data.

Implemented Server baseline: active Windows editors can register, bind/unbind, revoke, and rotate E-Ink devices; plaintext credentials are returned only when created or rotated. An active editor can publish an immutable official job-package revision for an assigned Batch Operation. The Server snapshots Machine/Case/Batch/Operation metadata, copies allow-listed Case-folder preview/NC/text inputs without modifying them, generates package-only tool-table/offset/instruction assets, and records SHA-256 for every file. Device-scoped GET endpoints provide a conditional version check, structured Machine screen, current and exact-revision manifests, checksum-verified package files, and time/refresh configuration. The Server rejects a device credential on planning, Edit Mode, TV, package-generation, and registration-administration routes. The dependency-free browser simulator exercises the read contract. Package approval roles/UI and retention, physical SD staging, deep sleep, panel rendering, hardware inputs, local annotation persistence, and all ESP32 firmware remain outside this implemented server slice.

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
