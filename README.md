# Meimad Production Planner

Meimad Production Planner is a factory-local, client-server system for manually planning CNC production. It replaces a shared Excel backlog with a central source of truth, visual machine backlogs, timeline consequence calculation, and explicit conflict reporting.

The planner does not optimize or repair schedules automatically. A human planner retains control of every assignment and ordering decision.

## Repository status

This repository contains the Server foundation, SQLite schema version 9, core planning-resource APIs, server-side Single Edit Mode, verified SQLite backup, a pure domain time-calculation engine, a Windows WPF planning client, a read-only TV Dashboard, and server-generated official E-Ink job packages with device-scoped read APIs and a browser simulator. The Windows client can create complete Case and Machine master records, create and optimistically edit validated Case Operations, create Orders, and create explicitly allocated Production Batches, including external Case/Machine picture paths, while all persistence and domain validation remain Server-owned. Schema v9 preserves Batch dependency snapshots across later route edits and persists the Server-derived `waiting` / `in_production` / `complete` Batch lifecycle. `GET /api/v1/timeline` and all display projections calculate or present consequences without mutating the manual plan. Case Operation reorder, the full conflict catalog, human authentication, package approval UI, retention workflow, and ESP32 firmware remain unimplemented.

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
| `client-windows/` | Implemented WPF connection/Edit Mode, Case workspace, and manual Machine Planning Board. |
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

Implemented Case routes are `GET/POST /api/v1/cases`, `GET/PATCH /api/v1/cases/{caseId}`, `GET/POST /api/v1/cases/{caseId}/operations`, `PATCH /api/v1/cases/{caseId}/operations/{operationId}`, and `GET /api/v1/cases/{caseId}/preview`. New Case Operations append to the route; optimistic edits may change approved operation/dependency fields but never route position. Existing Production Batch scalar/dependency snapshots are not retrofitted. Case setup/cycle fields are read-only sums of operation timing (null and an empty route contribute zero); the Windows UI shows total-hours `HH:mm:ss` while API/storage remain seconds. The Case collection accepts optional `search`, `customer`, and `isActive` filters. The preview route streams supported image content from the Server and never makes the Windows client read the stored preview path. Implemented Order routes are `POST /api/v1/orders`, `GET /api/v1/orders?caseId=...`, `GET /api/v1/orders/{orderId}`, and `PATCH /api/v1/orders/{orderId}`. Planning mutations require `X-Meimad-Client-Id`, `X-Meimad-Edit-Generation`, and a matching server-owned Edit Token; PATCH also requires the resource `ETag` in `If-Match`.

Single Edit Mode routes are `GET /api/v1/edit-mode`, `POST /api/v1/edit-mode/requests`, `GET /api/v1/edit-mode/requests/{requestId}`, `POST /api/v1/edit-mode/requests/{requestId}/decision`, and `POST /api/v1/edit-mode/release`. Development callers identify themselves with `X-Meimad-Client-Id`; acquisition also requires `X-Meimad-User-Id`. Exactly one editor and one pending transfer request are allowed. The default no-response timeout is 30 seconds and can be changed with `EditMode.TransferTimeoutSeconds`, `MEIMAD_EditMode__TransferTimeoutSeconds`, or `--EditMode:TransferTimeoutSeconds=<seconds>`. Human authentication and caller-class authorization are not implemented, so these slices are not production-ready.

Production Batch creation/read routes are `GET/POST /api/v1/batches`, `GET /api/v1/batches/{batchId}`, and `GET /api/v1/batches/{batchId}/operations`. The collection GET currently requires `caseId`. Batch creation is atomic: the Batch, balanced allocations, and current Case Operation scalar/dependency snapshots either all persist or none do. Status is Server-owned: a Batch starts `waiting`, becomes `in_production` after work starts (including suspension), and becomes `complete` when every non-empty-route operation finishes; a zero-operation Batch remains waiting.

Machine routes are `POST/GET /api/v1/machines`, `GET/PATCH/DELETE /api/v1/machines/{machineId}`, `GET /api/v1/machines/{machineId}/picture`, and `GET /api/v1/machines/{machineId}/backlog`. The Windows board provides Create, Edit, and guarded Delete Machine actions; updates use the Machine ETag and deletion is blocked by backlog, downtime, device, or official-package references. `GET /api/v1/planning-board` returns the snapshot-consistent pool and Machine columns. `PUT /api/v1/batch-operations/{id}/assignment` explicitly assigns or moves an operation; `DELETE` on that resource unassigns it. Assigned operation cards expose Start, Suspend, and Finish commands. Start/resume is restricted to the first backlog item and one running operation per Machine. Finish marks the operation completed, removes its active assignment, and compacts the remaining backlog. These commands never choose, reorder, or start another operation automatically.

Active editors can also request guarded deletion of a Case, Case Operation, Order, or Production Batch. The Server never cascades through unrelated planning history silently: a Case must be empty; an Order must not be allocated; an Operation must not be depended on or instantiated; and a Batch must have no assignments or official package. A safe Batch delete removes only that Batch's allocations and unassigned Batch Operations. External Case folders, pictures, original engineering files, and generated official package files are never deleted by these commands.

Working Calendars are created with `POST /api/v1/working-calendars` and listed with `GET /api/v1/working-calendars`. Creation is an Edit Mode mutation; IDs are generated by the Server and Machines select them through the Windows-client dropdown. The current authoring surface supports a named timezone, a fixed workweek choice, and one continuous non-overnight local shift. `GET /api/v1/timeline?from=<RFC3339>&to=<RFC3339>` expands those weekly shifts over the requested horizon while continuing to read legacy explicit UTC `availability` windows. An optional `application_settings['timeline.setup_calendar_json']` further constrains setup; when it is missing, setup uses Machine availability and the response explains the fallback instead of hiding operation intervals. Timeline intervals carry operation names, and the Windows view renders a visible operation marker.

The Server serves the fullscreen TV UI at `/tv-dashboard/` and its GET-only projection at `/api/v1/tv-dashboard`. It shows display-enabled Machines, top-backlog current/next jobs, calculated conflicts, urgent Batches, and current/upcoming downtime. It refreshes conditionally every 15 seconds by default and retains the last rendered snapshot during an outage. Configure `TvDashboard.RefreshAfterSeconds`, `TvDashboard.UrgentWithinHours`, and `TvDashboard.CalculationHorizonDays`. The dashboard contains no Edit Mode or mutation controls. Authentication is still pending, so keep the Server restricted to the factory LAN.

Active editors publish an immutable official revision with `POST /api/v1/job-packages`. A Batch Operation must already be assigned to a Machine. The Server snapshots Machine, Case/part, Batch, quantity, and Operation metadata; optionally copies the Case preview and allow-listed NC/text files; generates package-only tool-table, offset, and instruction assets; computes SHA-256 for every asset; stages files under `EInk.PackageRoot`; and commits metadata only after staging succeeds and Edit Mode is revalidated. Original Case files are never modified. A correction is a new caller-named revision; no package update/delete route exists.

E-Ink device reads are under `/api/v1/eink/devices/{deviceId}`: `version`, `machine-screen`, `package-manifest`, exact revision `manifest`/`files/{fileId}`, and `time-config`. Send the one-time device secret as `Authorization: Bearer <token>`. Active editors administer devices with `GET/POST /api/v1/eink/device-registrations` and `PATCH /api/v1/eink/device-registrations/{deviceId}`; create or rotate returns the plaintext token once, while SQLite stores only its hash. Configure the Server-local file root, package limits, and wake policy with the `EInk` section in `appsettings.json`. Package bytes are not stored in SQLite.

The Server serves the development simulator at `/eink-simulator/`. Enter a registered device ID and token to exercise the same GET-only API. The simulator performs the version check first, displays assigned Machine/current/next work, loads manifests/files, verifies downloaded SHA-256 values in the browser, and retains the last rendered view when refresh fails. It has no edit, checklist upload, comment upload, telemetry, USB, or ESP32 firmware behavior.

## Before further domain implementation

Review the unresolved decisions in [the implementation plan](docs/implementation-plan.md), especially authentication, Single Edit Mode failure semantics, calendar exceptions/breaks/overnight shifts, final duration derivation, plan revisions, broader conflict policy, aggregate route revision/reordering, package approval/retention, E-Ink interaction hardware, telemetry, and backup schedule/encryption/recovery targets.

Schema v9 and the current slices deliberately establish a narrow vertical boundary. Operation-owned timing summaries, optimistic one-link route editing, immutable Batch dependency snapshots, aggregate Batch lifecycle, E-Ink device lifecycle, official package generation, immutable metadata, verified downloads, the simulator, and basic weekly Working Calendar authoring are implemented. Resolve calendar exceptions, arbitrary multi-link dependencies/route reorder, identity, edit-token crash/restart recovery and audit, package approval/retention, and remaining backup operations policy before adding affected capabilities.
