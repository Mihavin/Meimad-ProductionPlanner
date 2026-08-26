# Meimad Production Planner — User Help

This guide is for planners, supervisors, setup personnel, and machine operators. The Planner is a factory-local, client/server application. The Server is the source of truth; the Windows client edits planning data, while the Timeline and TV Dashboard are read-only. E-Ink package/planning views are read-only and the approved tablet workflow adds only `SEND_TO_QC`.

## 1. Start here

1. Start the **Meimad Planner Server** service (or start the Server application during development).
2. Open the Windows client and verify that the Server connection indicator is healthy.
3. Select a language from the client language control. English, Hebrew, and Russian are supported; language changes apply to the whole client.
4. Acquire **Edit Mode** before changing cases, orders, batches, assignments, setup data, or machine data. Only one Windows client may edit at a time.
5. Use **Refresh** after another user makes a change. Never edit the Server SQLite database directly.

If the Server is unavailable, read-only screens may keep their last snapshot, but do not assume that a displayed plan is current.

## 2. Main screens

### Cases

Cases are part masters. Search by part number, name, customer, or active state. Open a Case to review its engineering preview, route, operations, dependencies, orders, batches, and revisions.

The Case working folder contains the source engineering files. The Planner does not modify original CAD, NC, or customer files. Generated Planner material is kept in the designated `_MeimadPlanner` area.

### Planning Board

The Planning Board has a pool of unassigned operations and one backlog per machine.

- Drag an operation from the pool or between machine backlogs to assign or move it.
- Reordering is manual. The Planner does not optimize or silently repair the plan.
- The first backlog operation is the one eligible to start. A running operation cannot be displaced.
- Use the operation commands **Start**, **Pause/Suspend**, **Finish**, and **Reset** only when the command is enabled.
- A pause requires a reason. Reset is for a paused operation and returns it to `not started`; Finish closes the operation and compacts the backlog.
- Cross-machine or cross-type compatibility warnings require explicit confirmation and a reason.

Conflicts are explanations of consequences or missing prerequisites. Resolve the underlying condition; do not treat a conflict message as an automatic schedule change.

### Timeline

Timeline is a read-only forecast calculated from the current Server snapshot. It shows setup, QA, production, reload/load events, downtime, holds, actual history, dependencies, and blocked work. Blank space may represent idle or non-working time.

Change the displayed horizon when needed. The separate Timeline window is also read-only; closing it does not change planning. The timeline is a consequence view, not a second place to schedule work.

### Setup

Setup contains the factory master data:

- Machines and reusable Machine Types
- Postprocessors and compatibility requirements
- Working Calendars, breaks, holidays, and exceptions
- Machine availability, maintenance, and breakdown/restore records
- Employees/resources, roles, machine skills, photos, and availability
- Material-order report and email settings
- CNC connection settings and monitoring diagnostics

Edit Mode is required for changes. Keep machine IDs stable, because employee skills, operation requirements, and historical records use them.

## 3. Normal planning workflow

1. Create or verify the Case and its complete operation route.
2. Create the Order with quantity and delivery information.
3. Create a Production Batch and allocate its quantity explicitly to one or more Orders. A Batch is the actual production launch; an Order is demand.
4. Confirm the Batch route and assign its operations to machines.
5. Review material receipts and explicit reservations. Only locally verified receipts create trusted availability.
6. Review G-code/postprocessor, tool-table, machine, worker, calendar, and dependency readiness.
7. Use the Timeline to inspect the calculated result and resolve blocking conflicts.
8. When the machine is ready, start the first eligible operation from the Planning Board.

The Server owns Batch and Order lifecycle status. Do not manually force a status that contradicts allocation or operation facts.

## 4. Readiness and release

An operation can be blocked by missing material, an unassigned machine, incompatible machine/postprocessor requirements, missing tools or tool table, unavailable workers, downtime, calendar capacity, or dependency rules.

For a released G-code revision:

1. Select the postprocessor and change scope.
2. Confirm the postprocessor generated exactly one generic verification hook as the first executable block: `G65 P9xxx Axxxxxx. (MEIMAD VERIFY V1)` or the approved custom-G-code equivalent. The six-digit `A` identity must be new for this exact release.
3. Choose the released G-code and the exact physical tool table supplied to the machine.
4. Enter the release comment and process-change description.
5. Confirm the physical tool table and the creation of the new manufacturing-process revision.
6. Release the G-code and review the recorded verification identity in revision history.

Releasing a new manufacturing-process revision makes other postprocessor releases non-current for that revision until they are regenerated. Meimad validates and stores the hook identity but never inserts it or overwrites original NC files. Historical releases created before schema v51 remain downloadable, show the hook as unavailable, and cannot support protected NC verification until intentionally re-released with a valid hook.

## 5. Haas NGC connection and part identity

Use the CNC connection panel in Setup for the Haas machine. Prefer **MTConnect** for read-only monitoring when the machine agent exposes `/current`. MDC remains a separate read-only monitoring/test channel; the application exposes no generic variable read, reset, or write control.

Configure the machine’s IP/hostname, MDC port, MTConnect port, DPRNT PartName port, part-counter source, polling interval, timeout, local NC share, and credential reference. Save the configuration, then use **Test Connection**, **Test MTConnect**, **Test MDC**, **Test Net Share**, **Refresh monitoring**, and **Reconnect** as appropriate.

The active NC program’s machine-side header/DPRNT `PartName` is the authoritative part identity for monitoring. Do not infer the part from the program number when a valid PartName is present. The persistent CNC Setup/Production variable was removed and changing a CNC variable cannot change Meimad workflow state.

The **Protected setup verification** expander stores commissioning configuration only. Keep it disabled until the real Machine passes [the protected-verification technical spike](haas-protected-verification-spike.md). The Machine secret is write-only: leaving the field blank preserves an existing secret, and neither the client nor tablet can read it back.

For a local NC share, the path must be reachable by the **Server service account**, not only by your interactive Windows user. A mapped drive is not sufficient for a Windows service; use a UNC path and grant the service account read permission.

## 6. TV Dashboard and E-Ink

The TV Dashboard is a read-only kiosk view served by the Server at:

`http://<planner-server>:5080/tv-dashboard/`

It shows display-enabled machines, the current operation, part picture, operation identity, machine state, and calculated setup/current-part/Batch progress. Conflicts, next jobs, planning controls, and editing forms are intentionally hidden. The small connection indicator changes color with the Server connection state. During a short outage, the last valid snapshot remains on screen.

E-Ink devices display the official package and machine/setup instructions downloaded from the Server. Package content, checklist marks, comments, assignments, and planning facts remain read-only/local as applicable. While the current Server-resolved run is `IN_SETUP_RUN`, holding D4 for 1.2 seconds submits **Send to QC**. The command supplies no run or timestamp, does not require Edit Mode, and changes only the tablet workflow status to `IN_QC`. The Server endpoint is implemented and idempotent; the physical button/display flow is compiled but still requires hardware verification before shop-floor acceptance.

Use the Windows **User Terminals** page to monitor tablet identity, Machine binding,
last contact, reported firmware/battery/Wi-Fi, current Production Run, workflow state,
and package revision. Monitoring works in View Mode. Request Edit Mode before creating
a tablet, changing its Machine, marking it spare, enabling/revoking it, or rotating its
credential. Copy a new or rotated credential immediately; it cannot be retrieved later.

Use the Windows **QC Queue** to monitor Production Runs whose latest workflow
state is `IN_QC`. The queue shows the Machine, part and Operation outputs,
Production Run, time received, and packaged setup worker when available.
Monitoring works in View Mode. To record a decision, request Edit Mode, select
the queue row, optionally enter a reason/comment, and choose **PASS** or
**FAIL**. PASS records the current Server time as production approval and moves
the workflow projection to `READY_FOR_PRODUCTION`. FAIL records the same audit
details, returns it to `IN_SETUP_RUN`, and allows the setupist to correct the
setup and send it to QC again. Neither button writes a CNC variable.

## 7. Languages and responsiveness

Use the language selector in the Windows client to switch between English, Hebrew, and Russian. If a screen appears stuck, wait for the current request to finish before switching again, then refresh. Avoid opening many Timeline windows or repeatedly refreshing a large horizon; each read-only calculation uses the Server snapshot.

## 8. Troubleshooting

### Server connection or HTTP 502

- Confirm the **Meimad Planner Server** service is running.
- Confirm the client Server URL and port.
- Test the Server health endpoint from the Planner host.
- Check the Server log for a database, migration, or projection error.
- If only a machine monitor fails, test MTConnect and MDC separately; a healthy MTConnect response does not prove MDC is configured correctly.

### Machine appears offline

- Test the configured MTConnect URL directly.
- Confirm the MTConnect port and machine IP.
- Use **Refresh monitoring** or **Reconnect**.
- Check that the Server service account can reach the machine network.
- Confirm that the PartName/DPRNT and parts-counter settings match the Machine configuration. CNC variables do not determine workflow state.

### Net Share unavailable

Use a UNC path such as `\\server\share\NC`, grant the Server service account read access, and test from the service context. Do not rely on a drive letter mapped only in your user session.

### Operation cannot start

Check that it is first in the machine backlog, the machine is available, material is verified/reserved, required tools and tool table are ready, the G-code revision is released, workers/calendars are available, and no dependency or compatibility conflict blocks it.

### Dragging or editing behaves unexpectedly

Refresh the Planning Board, verify that you still hold Edit Mode, and retry once. If the issue persists, record the machine, operation, time, client version, and Server log entry. Do not repair the database manually.

### Picture is missing

Verify that the Case preview or machine picture path is inside an allowed Server-managed location and that the Server account can read it. Refresh the Case or TV Dashboard after correcting the file.

## 9. Safe operating rules

- Keep the Server database on a local Server disk; never place it on a UNC/network share.
- Do not open, edit, or copy the live SQLite database as a substitute for the API.
- Keep the Server restricted to the factory LAN unless a reviewed deployment explicitly adds authentication, TLS, and firewall controls.
- Use verified Server backup/restore tooling and test restore copies locally.
- Never overwrite original engineering or NC files. A correction is a new revision.
- Treat Timeline and TV as read-only projections. Treat E-Ink package/planning content as read-only; `SEND_TO_QC` is the only approved tablet command.
- Record the exact machine, operation, Batch, and time when reporting a problem.

## 10. Current implementation notes

The Server APIs and execution model support Production Runs, including multi-output planning data. Some Windows Timeline/Planning Board and TV/E-Ink cards do not yet render every Production Run field; where a Run-specific field is absent, use the operation card and Server projection as the authoritative view. The application does not yet provide automatic scheduling, ERP inventory authority, public Internet access, or native mobile editing. `SEND_TO_QC` is approved but not yet implemented end to end; every other E-Ink write-back remains excluded.

For deployment and engineering details, see the repository [README](../README.md), [functional specification](functional-spec.md), and [performance/stability audit](performance-stability-audit-2026-08-23.md).
