# Meimad Production Planner

Meimad Production Planner is a factory-local, client-server system for manually planning CNC production. It replaces a shared Excel backlog with a central source of truth, visual machine backlogs, timeline consequence calculation, and explicit conflict reporting.

The planner does not optimize or repair schedules automatically. A human planner retains control of every assignment and ordering decision.

## Repository status

This repository contains the Server foundation, SQLite schema version 24, core planning-resource and Setup APIs, server-side Single Edit Mode, verified SQLite backup, a pure domain time-calculation engine, a Windows WPF planning client, a read-only TV Dashboard, weekly material and employee-efficiency reports, an exportable structured planning-event stream, and server-generated official E-Ink job packages with device-scoped read APIs and a browser simulator. The Windows client has a compact connection/Edit Mode header and a dedicated Setup page for connection settings, Working Calendars and the dedicated Setup Calendar, Machines, reusable Machine Types, employee/resources, Israeli holidays, and report/email configuration. Its operational surfaces cover Case/Operation/Order/Batch workflows, a compact manual Machine Board with quantity/Order/time projections and player-style execution controls, and one canonical read-only Timeline shared by the embedded tab and a separate window.

Schema v9 preserves Batch dependency snapshots and the Server-derived Batch lifecycle. Schema v10 adds Machine Type linkage, stable Setup Calendar selection/timezone use, and the linked-Order `active` / `in_production` / `complete` lifecycle with allocation-safe edits. Schema v23 records authoritative operation actual start/end and Machine history separately from floating Timeline forecasts. Schema v24 stores `forward`, `backward`, or `manual` planning intent on each Machine Assignment, defaulting existing and new assignments to `manual`. `GET /api/v1/timeline` and all display projections calculate or present consequences without persisting calculated start/end dates, duplicating assignments, or reordering the manual backlog. Case Operation reorder, overnight calendar windows, the full conflict catalog, human authentication, package approval UI, retention workflow, and ESP32 firmware remain unimplemented.

Schema v11 adds isolated administrative storage for employee/resources, Israeli holiday definitions, and singleton report/email settings. Schema v12 extends employees with first/last names, one of the three worker roles, normalized Machine-ID skills, a role-compatible Working Calendar reference, photo path, notes, and active state. Schema v13 adds employee-owned vacation, sick-day, personal-day, unavailable, and custom-note exceptions. Schema v14 adds an offline Israeli-holiday cache with non-working, working, and partial-working policies, online refresh state, and preserved manual corrections. Schema v15 adds immutable cross-type Machine-assignment override records. Schema v16 extends mutable Case Operations and immutable Batch Operation snapshots with QA-after-setup time, load/unload time and worker/frequency policy, automatic-loading mode, and a day-shift-only flag. Timeline places setup, QA, load/unload, and production in order and reserves individual active employees during worker phases. Setup employees select the Machines they know how to operate by stable Machine ID in Setup; legacy number/name/type/axis/capability tokens remain readable and are converted to IDs when the employee is next saved. QA and regular-worker phases use role availability. Calendars, breaks, exceptions, and cached holidays constrain each employee. These calculated reservations are not persisted worker assignments and never reorder a Machine backlog.

## MVP system

- **Meimad Planner Server:** sole owner of business rules, timeline/conflict calculation, edit coordination, backup, and a server-local SQLite database.
- **Windows Planning Client:** full viewing and editing, including manual drag-and-drop machine backlog planning. Exactly one Windows client edits at a time.
- **TV Dashboard:** read-only fullscreen/kiosk view for machine and factory status.
- **Color E-Ink Work Tablet:** read-only machine/setup/package display. Official packages download over Wi-Fi and cache on SD/microSD; checklist marks and comments stay local to the tablet.

The MVP is LAN-only. It excludes public access, automatic scheduling, ERP synchronization, native mobile apps, full tool inventory, E-Ink write-back, USB Mass Storage to CNC, and a Customer Portal.

## Repository layout

| Path | Purpose |
|---|---|
| `AGENTS.md` | Permanent product and engineering rules for work in this repository. |
| `docs/` | Normalized requirements, target design, contract, and delivery plan. |
| `server/` | Implemented Server host, SQLite boundary/migrations/verified backup, planning-resource slices, and pure domain time engine. |
| `client-windows/` | Implemented WPF compact connection/Edit Mode shell, dedicated Setup page, Case workspace, compact manual Machine Board, and embedded/separate-window read-only Timeline. |
| `client-tv-dashboard/` | Implemented dependency-free read-only fullscreen/kiosk dashboard served by the Server. |
| `tests/` | Server and Windows-client settings/API/presentation tests, plus migration, persistence, domain/API, concurrency, backup, allocation/assignment, graph, and foreign-key tests. |
| `scripts/` | Future development, migration, deployment, backup, and verification tooling. |

## Documentation

- [Functional specification](docs/functional-spec.md)
- [ESP32 / Color E-Ink Work Tablet](docs/esp32-eink-work-tablet.md)
- [Architecture](docs/architecture.md)
- [Data model](docs/data-model.md)
- [API contract](docs/api-contract.md)
- [Implementation plan](docs/implementation-plan.md)

The Markdown specifications normalize these source documents dated 11 August 2026:

- `Meimad_Planner_Functional_Specification_v0.3_Client_Server_EInk.docx`
- `Meimad_Planner_ESP32_EInk_Work_Tablet_Concept_v0.1.docx`

The original Word files are source inputs and are not copied into this repository.

## Build, test, and run the Server

The current implementation targets the .NET 10 SDK.

```powershell
dotnet build .\server\Meimad.Planner.Server.slnx
dotnet test .\server\Meimad.Planner.Server.slnx
dotnet run --project .\server\Meimad.Planner.Server\Meimad.Planner.Server.csproj
```

Run the WPF client on Windows after starting the Server:

```powershell
dotnet run --project .\client-windows\Meimad.Planner.Client.Windows\Meimad.Planner.Client.Windows.csproj
```

The client defaults to `http://127.0.0.1:5080/`. Its Server URL, local display name, and stable client ID are stored in `%LOCALAPPDATA%\Meimad Planner\client-settings.json`. This is a development-only identity placeholder, not authentication. The client project has no SQLite dependency and never receives a database path.

The default endpoint is `http://127.0.0.1:5080`. Change `Server.Host` and `Server.Port` in `appsettings.json`, set `MEIMAD_Server__Host` / `MEIMAD_Server__Port`, or pass command-line overrides:

```powershell
dotnet run --project .\server\Meimad.Planner.Server\Meimad.Planner.Server.csproj -- --Server:Host=0.0.0.0 --Server:Port=5080
```

The same executable uses normal console lifetime during development and switches to Windows Service lifetime when later registered and launched by the Windows Service Control Manager. Service installation is not part of this skeleton.

SQLite defaults to `data/meimad-planner.db` beneath the Server content root. Change `Database.Path` in `appsettings.json`, set `MEIMAD_Database__Path`, or pass `--Database:Path=<server-local-path>`. Startup creates the parent directory, enables foreign keys on every Server connection, and applies missing ordered migrations transactionally. UNC and detected mapped network-share paths are rejected. Clients must use the future API and must never receive or open this database path.

Backups default to the `backups` folder beneath the Server content root and retain the newest 14 managed backups. Configure `Backup.Folder` and `Backup.RetentionCount` in `appsettings.json`, use `MEIMAD_Backup__Folder` / `MEIMAD_Backup__RetentionCount`, or pass command-line overrides. The internal Server backup service uses SQLite's online backup mechanism, publishes `meimad-planner-backup-<UTC timestamp>-<unique suffix>.db`, performs SQLite integrity and foreign-key checks, then restores the published backup into an isolated local test database and verifies that restore. Restore verification refuses the active database path. Scheduling and an authenticated operator trigger are not implemented yet; no unauthenticated backup/restore HTTP route is exposed.

Implemented Case routes are `GET/POST /api/v1/cases`, `GET/PATCH /api/v1/cases/{caseId}`, `GET/POST /api/v1/cases/{caseId}/operations`, `PATCH /api/v1/cases/{caseId}/operations/{operationId}`, and `GET /api/v1/cases/{caseId}/preview`. New Case Operations append to the route; optimistic edits may change approved operation/dependency fields but never route position. Existing Production Batch scalar/dependency snapshots are not retrofitted. Case setup/cycle fields are read-only sums of operation timing (null and an empty route contribute zero); the Windows UI shows total-hours `HH:mm:ss` while API/storage remain seconds. The Case collection accepts optional `search`, `customer`, and `isActive` filters. The preview route streams supported image content from the Server; the Windows client leaves valid thumbnails unobstructed and uses text only for missing/error state.

Implemented Order routes are `POST /api/v1/orders`, `GET /api/v1/orders?caseId=...`, `GET /api/v1/orders/{orderId}`, and `PATCH /api/v1/orders/{orderId}`. Once allocated, a non-cancelled Order is Server-derived as `active`, `in_production`, or `complete` across all related Batches; completion requires full quantity allocation, at least one operation in every allocated Batch, and all related operations completed. New/unallocated Orders reject manually submitted production tokens, and Batch creation rejects allocation to a cancelled Order. PATCH rejects quantity below existing allocation and a submitted linked status that contradicts production facts. Legacy already-linked `cancelled` rows remain preserved until an explicit PATCH matching current production facts resumes the derived lifecycle. Batch create/delete and operation transitions recompute affected Orders atomically. Planning mutations require `X-Meimad-Client-Id`, `X-Meimad-Edit-Generation`, and a matching server-owned Edit Token; PATCH also requires the resource `ETag` in `If-Match`.

Single Edit Mode routes are `GET /api/v1/edit-mode`, `POST /api/v1/edit-mode/requests`, `GET /api/v1/edit-mode/requests/{requestId}`, `POST /api/v1/edit-mode/requests/{requestId}/decision`, and `POST /api/v1/edit-mode/release`. Development callers identify themselves with `X-Meimad-Client-Id`; acquisition also requires `X-Meimad-User-Id`. Exactly one editor and one pending transfer request are allowed. The default no-response timeout is 30 seconds and can be changed with `EditMode.TransferTimeoutSeconds`, `MEIMAD_EditMode__TransferTimeoutSeconds`, or `--EditMode:TransferTimeoutSeconds=<seconds>`. Human authentication and caller-class authorization are not implemented, so these slices are not production-ready.

Production Batch creation/read routes are `GET/POST /api/v1/batches`, `GET /api/v1/batches/{batchId}`, and `GET /api/v1/batches/{batchId}/operations`. The collection GET currently requires `caseId`. Batch creation is atomic: the Batch, balanced allocations, and current Case Operation scalar/dependency snapshots either all persist or none do. Status is Server-owned: a Batch starts `waiting`, becomes `in_production` after work starts (including suspension), and becomes `complete` when every non-empty-route operation finishes; a zero-operation Batch remains waiting.

Machine routes are `POST/GET /api/v1/machines`, `GET/PATCH/DELETE /api/v1/machines/{machineId}`, `GET /api/v1/machines/{machineId}/picture`, and `GET /api/v1/machines/{machineId}/backlog`. Reusable Machine Types use `GET/POST /api/v1/machine-types` and `GET/PATCH/DELETE /api/v1/machine-types/{machineTypeId}`. A linked type contributes reusable capabilities; unsafe Machine/type changes are blocked, a rename cannot strand Case or unfinished Batch Operation requirements that use the old name, and deletion is blocked while a Machine or Operation requirement references the type. Machine management is on the Setup page and uses optimistic ETags; deactivation/deletion is blocked by unsafe planning references.

`GET /api/v1/planning-board` returns the snapshot-consistent pool and compact Machine columns. Each unfinished operation includes planned quantity, allocated Order Numbers, nullable `setup + quantity x cycle` estimated seconds, and—when assigned—the Machine Assignment ID, assignment version, and planning mode. `PUT /api/v1/batch-operations/{id}/assignment` explicitly assigns or moves an operation; `DELETE` on that resource unassigns it. `PATCH /api/v1/machine-assignments/{assignmentId}` changes only that existing assignment's planning mode under Edit Mode and an exact assignment ETag; it never creates a second assignment or changes backlog position. A cross-type assignment first returns a warning and requires a second explicit confirmation with a nonblank reason; the Server atomically records the confirming user/client, time, intended type, selected type, and reason. Assigned operation cards expose player-style Start, Pause/Suspend, Finish, and Reset commands that are disabled when invalid or unauthorized. Pause requires a reason-specific dialog; schema v18 stores its structured fields, optional comment, Edit Mode user, start time, active status, and Resume/Reset end time. Board tooltips and Timeline waiting intervals expose active pauses. Start/resume is restricted to the first backlog item and one running operation per Machine; while it is running, assignment commands cannot displace it from position zero or change its planning mode. Reset is accepted only for a paused operation, returns it to `not_started`, keeps its Machine assignment, backlog position, and planning mode, closes its pause event, and recomputes parent Batch and linked Order statuses. Finish marks the operation completed, removes its active assignment, compacts the backlog, and performs the same recomputation. These commands never choose, reorder, or start another operation automatically.

Active editors can also request guarded deletion of a Case, Case Operation, Order, or Production Batch. The Server never cascades through unrelated planning history silently: a Case must be empty; an Order must not be allocated; an Operation must not be depended on or instantiated; and a Batch must have no assignments or official package. A safe Batch delete removes only that Batch's allocations and unassigned Batch Operations. External Case folders, pictures, original engineering files, and generated official package files are never deleted by these commands.

Working Calendars use `GET/POST /api/v1/working-calendars` and `GET/PATCH/DELETE /api/v1/working-calendars/{workingCalendarId}`. Setup Calendar selection uses `GET/PUT/DELETE /api/v1/setup-calendar`. Mutations require Edit Mode; IDs are generated by the Server and Machines/Setup select named records. The authoring surface supports a named timezone, machine/setup-worker/regular-worker/QA-worker usage tags, workdays, multiple non-overlapping same-day local working windows, contained lunch/break windows, and dated closures or special-hour exceptions. Timeline availability subtracts configured breaks and applies a dated exception in place of that date's recurring schedule. Existing single-shift records remain compatible. Deletion is blocked while a Machine or the Setup selection references a Calendar; active machine/setup usages cannot be removed while referenced.

Machine Availability is managed from Setup through `GET/POST /api/v1/downtimes`, optimistic `PATCH /api/v1/downtimes/{downtimeId}` for planned maintenance, and `POST /api/v1/downtimes/{downtimeId}/restore` for an active breakdown. Planned maintenance records Machine/start/end/reason/planner. A breakdown records Machine/start/reason/reporter and remains open-ended until Restore supplies its end and repair note. Timeline and TV projections show the typed reason, subtract the unavailable interval, split/delay work around it, and never change Machine backlog order.

`GET /api/v1/timeline?from=<RFC3339>&to=<RFC3339>` reloads every persisted Machine Assignment and calculates one canonical Timeline. Each assignment contributes exactly one operation-linked Timeline block, labeled with its assignment-owned `planningMode`; a global `mode` query is rejected because modes are not separate Timeline views. `forward` places the assignment at its earliest feasible calculated time, `backward` latest-fits the same assignment before the earliest linked Order Work Finish Date, and `manual` retains the planner's stored Machine/backlog placement while the Timeline calculates its valid visible consequence. Mixed modes share the same Machine lanes and cannot overlap. No mode writes planned dates, changes the assigned Machine, duplicates an assignment, or reorders a backlog. Calculation applies Machine, Setup, day-shift, and employee calendars; breaks, exceptions, cached holidays, maintenance/breakdown; setup/QA/regular-worker availability and skills; pause state; and dependencies. Work may split across availability windows. Every delay is operation-linked waiting, and assigned work that cannot fit stays visible with a structured conflict; an earlier blocked row prevents later backlog work from leapfrogging. Backward contention uses earlier Work Finish Date, then shorter duration, then naturally smaller Order Number. Missing/out-of-range delivery dates and work that cannot fit return structured warnings/conflicts; empty capacity before a future backward block is normal, not a hold layer or conflict. Legacy setup JSON remains an upgrade fallback until the first managed selection or explicit clear. The compact Windows view separates overlapping blocked intervals so labels and tooltips remain accessible. The same live read-only view can open in a separate window whose close action does not affect the main client or planning state.

The Setup Reports/Email tab configures sender, recipients, SMTP relay/SSL, timezone, weekly send weekday/time, and manual Send Now for the material-order report. Its GET/send APIs and email body contain only Case/Part Number and required material-piece quantity. The Server counts each qualifying upcoming-week Batch once and uses planned quantity, which includes explicit scrap allowance. Successful automatic sends are recorded per target week to prevent repeat scheduled mail.

The Server serves the fullscreen TV UI at `/tv-dashboard/` and its GET-only projection at `/api/v1/tv-dashboard`. The kiosk UI is a viewport-fitted grid containing only display-enabled Machine number, name, and status; the only Server chrome is a small green/yellow/red connection dot. It refreshes conditionally every 15 seconds by default and retains the last rendered snapshot during an outage. Configure `TvDashboard.RefreshAfterSeconds`, `TvDashboard.UrgentWithinHours`, and `TvDashboard.CalculationHorizonDays`. The richer projection remains Server-owned, but the TV exposes no host text, configuration, Edit Mode, or mutation controls. Authentication is still pending, so keep the Server restricted to the factory LAN.

Active editors publish an immutable official revision with `POST /api/v1/job-packages`. A Batch Operation must already be assigned to a Machine. The Server snapshots Machine, Case/part, Batch, quantity, Operation, Timeline-derived setup worker/time, official job tools, optional expected-on-Machine tools, and local checklist definitions; optionally copies the Case preview, setup-worker photo, and allow-listed NC/text files; generates package-only tool-table, offset, and instruction assets; computes SHA-256 for every asset; stages files under `EInk.PackageRoot`; and commits metadata only after staging succeeds and Edit Mode is revalidated. The manifest declares Wi-Fi/SD/read-only operation with no reverse sync or USB Mass Storage. Original files are never modified. A correction is a new caller-named revision; no package update/delete route exists.

E-Ink device reads are under `/api/v1/eink/devices/{deviceId}`: `version`, `machine-screen`, `package-manifest`, exact revision `manifest`/`files/{fileId}`, and `time-config`. Send the one-time device secret as `Authorization: Bearer <token>`. Active editors administer devices with `GET/POST /api/v1/eink/device-registrations` and `PATCH /api/v1/eink/device-registrations/{deviceId}`; create or rotate returns the plaintext token once, while SQLite stores only its hash. Configure the Server-local file root, package limits, and wake policy with the `EInk` section in `appsettings.json`. Package bytes are not stored in SQLite.

The Server serves the development simulator at `/eink-simulator/`. Enter a registered device ID and token to exercise the same GET-only API. The simulator performs the version check first, displays assigned Machine/current/next work, loads manifests/files, verifies downloaded SHA-256 values in the browser, and retains the last rendered view when refresh fails. It has no edit, checklist upload, comment upload, telemetry, USB, or ESP32 firmware behavior.

## Before further domain implementation

Review the unresolved decisions in [the implementation plan](docs/implementation-plan.md), especially authentication, Single Edit Mode failure semantics, overnight calendar windows/archive policy, final duration derivation, plan revisions, broader conflict policy, aggregate route revision/reordering, package approval/retention, E-Ink interaction hardware, telemetry, and backup schedule/encryption/recovery targets.

Schema v24 and the current slices deliberately establish a narrow vertical boundary. Operation-owned timing phases, immutable Batch dependency/timing snapshots, assignment-owned planning intent, individual Timeline resource contention, exception-aware availability, and planned/breakdown Machine downtime lifecycle are implemented without changing manual Machine priorities or persisting calculated dates. Resolve cross-Batch over-allocation/reallocation, persisted named worker assignment, skill taxonomy/qualification expiry, overnight/calendar archive policy, arbitrary multi-link dependencies/route reorder, authenticated identity, broader audit, package approval/retention, and remaining backup operations policy before adding affected capabilities.
