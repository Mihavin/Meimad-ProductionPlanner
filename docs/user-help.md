# Meimad Production Planner — User Help

This guide is for planners, supervisors, setup personnel, and machine operators. The Planner is a factory-local, client/server application. The Server is the source of truth; the Windows client edits planning data, while the Timeline and TV Dashboard are read-only. E-Ink package/planning views are read-only and the approved tablet workflow adds only `SEND_TO_QC`.

## 1. Start here

1. Start the **Meimad Planner Server** service (or start the Server application during development).
2. Open the Windows client and verify that the Server connection indicator is healthy.
3. Select a language from the client language control. English, Hebrew, and Russian are supported; language changes apply to the whole client.
4. Acquire **Edit Mode** before changing cases, orders, batches, assignments, setup data, or machine data. Only one Windows client may edit at a time.
5. Use **Refresh** after another user makes a change. Never edit the Server SQLite database directly.

If the Server is unavailable, read-only screens may keep their last snapshot, but do not assume that a displayed plan is current.

### Client updates

The Server and the client are released as a pair with the same version number, and the Server carries the client installer that belongs to its version. At every start, after the first successful connection, the client compares its version with the Server's client package:

- **Newer client on the Server:** a dialog **New version available** opens with the message "New version X is available. The client will be installed now.", downloads the installer from the Server, verifies its checksum, starts the installation, and closes the client. Windows asks for administrator confirmation (UAC) because the client is installed for all users; after the installer finishes, the client starts again by itself. **Cancel** during the download keeps the current client for this session; the dialog returns at the next start.
- **Client newer than the Server:** nothing is installed. The connection indicator shows an attention notice asking to upgrade the Server (a client is never downgraded).
- **Versions differ but the Server has no installer:** the indicator shows a notice to ask the administrator; the installer is normally present after any Server upgrade from 0.1.117 on.

The downloaded installer and its log (`install-client-update.log`) are kept under `%LOCALAPPDATA%\MeimadPlanner\updates`.

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
- Per Machine: the **NC dialect** (control family of the Server-generated NC blocks) and the **NC viewer machine** (see below)

Edit Mode is required for changes. Keep machine IDs stable, because employee skills, operation requirements, and historical records use them.

### NC viewer

Right-click a G-code release in the Case Operation history, a Machine-assigned operation on the Planning Board, or an item in the NC Creator, Tool Room and Setup Queue tabs and choose **View NC file** to open the program in the NC viewer: a 3D toolpath preview with playback, the program text, the tool table the program implies, and the machine data used for the simulation. A Server release opens **read-only** (the badge says so); *Save copy as* writes a local copy and never changes the release, and **Edit copy** continues on an editable local copy that you can save as a local version or release as a new revision. In the Release G-code form (Case → Operation → Released G-code file), **NC viewer…** opens the viewer in edit mode with the selected file, or with a new program that already carries the Meimad canonical block; inside, *New*/*Open* take any NC file, *Save* stores the modified program in the Case Working Folder under `Gcode\<Case name>\<Operation number>\<process revision>\<postprocessor name>\<local revision>` (folders are created when missing; the numbers are those of the release the program becomes), *Save as* opens that folder, **Use for G-code release** selects the saved file in the form, and **Release to Server…** releases it directly: the Server first checks that the Meimad canonical format is present (nothing is uploaded otherwise), the program is saved in the revision folder of the new release (the release is refused while the Case has no Working Folder), and the release dialog asks for the same postprocessor, change scope, comment, tool table and confirmations as the form (Edit Mode is required). **Apply Meimad Planner Format** adds the Meimad header, verification hook, event context, output line and cycle markers for the Machine's NC dialect and shows what changed and whether the result is a valid template.

The preview and the Server's NC cycle-time estimate use the same interpreter and the same **NC viewer machine**, chosen per Machine in Setup: Mazak Variaxis i-500, Okuma Genos L200E-M, Haas ST-25Y, Haas VF-3SS, generic FANUC 0i-MC 3-axis and 4-axis (A along X) mills, or the vendored Haas UMC-500, Doosan DVF 5000 and Chevalier FLC-200MC. *Auto-detect* lets the engine choose from the program and the NC dialect. Okuma OSP programs (LAP cycles, `CALL`, `VC` variables, named labels), Haas one-block lathe cycles and the Variaxis A tilt are translated for the interpreter; the preview reports every translation and every approximation (for example "G86 copy turning is approximated by one G73 pass") in its messages, and rows still refer to your program. Placeholder values in the Meimad machine definitions (rotary centre, reference positions, table height) are marked *(placeholder)* in the machine panel until they are measured on the Machine; toolpath geometry and cycle time do not depend on them, machine-frame positions and travel checks do.

Subprograms a lathe program calls (`M98 P9100`, `G65`, Haas `M97`) are read from the program's folder or the machine's **program memory** folder (viewer Settings, one folder per machine, lathes included; the folder is indexed by each file's `O` number, then its file name). A call that cannot be resolved is shown as a comment and listed in the messages. Viewer settings (default machine, G30 reference, initial macro variables, program-memory folders, home offsets) are per user on this PC and never reach the Server.

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
2. Confirm the postprocessor generated exactly one standalone `[[MEIMAD:VERIFICATION_HOOK]]` before the first executable block. G-code release checks only this insertion point; ordinary `G65 P...` machining calls are unrelated. The Server assigns the six-digit identity and inserts the configured verification call only in the generated Machine-specific package.
3. Choose the released G-code and the exact physical tool table supplied to the machine.
4. Enter the release comment and process-change description.
5. Confirm the physical tool table and the creation of the new manufacturing-process revision.
6. Release the G-code and review the recorded verification identity in revision history.

Releasing a new manufacturing-process revision makes other postprocessor releases non-current for that revision until they are regenerated. Meimad validates the insertion placeholder and stores the Server-assigned hook identity but never overwrites original NC files. Historical releases created before schema v51 remain downloadable, show the hook as unavailable, and cannot support protected NC verification until intentionally re-released with a valid placeholder.

## 5. CNC connection: type, telemetry, and part identity

The CNC Connection panel in Setup starts with one **Connection type** picker: **Haas MDC**, **Haas MTConnect**, **DPRNT only** (no machine telemetry, for example Mazak), or **FANUC FOCAS**. Pick the one protocol the Machine actually uses; the panel below then shows only the fields for that type, and a Machine-side identity/DPRNT section shared by every type sits underneath (see [DPRNT: shared by every connection type](#dprnt-shared-by-every-connection-type)). This replaced an earlier two-step "Adapter Type" plus "Machine telemetry source" pair that made it unclear which combination configured a given machine.

**Haas MDC** and **Haas MTConnect** are read-only Haas NGC monitoring channels; **Haas MTConnect** is preferred when the machine agent exposes `/current`. **DPRNT only** is for a controller with no MDC/MTConnect telemetry — the Machine then reports no machine state, program number, or part counter, and Part identity plus workflow events come from DPRNT alone. **FANUC FOCAS** reads a FANUC control over the FOCAS 2 library (see [FANUC FOCAS connection](#fanuc-focas-connection)). The application exposes no generic variable read, reset, or write control for any type.

Configure the Planner MachineID mapping with the controller's fixed IP and MAC, then the type-specific ports/counter source/timeout and the shared DPRNT block, save, and use the connection tests as appropriate. This recognizes the configured controller but does not prove its NC or Offset Loader valid.

The active NC program's machine-side header/DPRNT `PartName` is the authoritative part identity for monitoring. Do not infer the part from the program number when a valid PartName is present. The persistent CNC Setup/Production variable was removed and changing a CNC variable cannot change Meimad workflow state.

### DPRNT: shared by every connection type

DPRNT supplies Part identity and `MEIMAD/` workflow events for every connection type above, so it is configured once, in its own section, rather than repeated per type.

**DPRNT source = TCP or file**

**DPRNT source = TCP** (default) reads the controller's DPRNT output on the DPRNT port. This is the Haas NGC setting (Setting 261 = TCP Port, Setting 263 = port) and also works for a serial-only controller whose RS-232 port is wired to a serial-to-Ethernet bridge configured as a TCP server on that port.

**DPRNT source = FILE** reads a DPRNT text file that the controller writes itself, over a UNC share. Use it for a Mazak Matrix / Matrix 2 control: set DPR14 = 4 so DPRNT lines go to `C:\MC_sdg\print\print.txt` on the control PC, share that folder read-only for the Server service account, and enter the UNC path (for example `\\MAZAK-VARIAXIS\print\print.txt`) as the DPRNT file path. The Server reads only new lines after each poll and re-reads the file from the start if the control rewrote it. Use **Test DPRNT** to confirm the Server can read the file and to see the last PartName it contains.

No G-code deletes that file, so **Empty DPRNT file** says when the Server empties it (the share must then allow write access for the Server service account):

- **ON_OFFSET_LOADER** (recommended): the Server empties the file right after it has read a new Offset Loader completion line. The verification Offset Loader is the first program of every new setup, so the file always holds exactly the current setup session and never grows across jobs.
- **AFTER_READ**: the Server empties the file after every read that consumed it completely; any `MEIMAD/` events written while the Server was down are still picked up on restart.
- **NEVER**: the Server only reads; use it when the control keeps the file small itself. After a restart the Server recovers only the current PartName from the end of the file and does not replay old events (this also applies to ON_OFFSET_LOADER).

The file is only emptied after a read that ended on a complete line and only while nothing was appended since, so a line the control writes at the same moment is never lost. A failed emptying (for example a read-only share) is shown as the connection error without stopping monitoring.

Bench auto-start and program-mismatch events still need an active program number from MDC, MTConnect, or FOCAS; with connection type **DPRNT only** the PartName is shown in monitoring and `MEIMAD/` workflow events are ingested, but no Bench is started automatically, and **Test Connection** runs the DPRNT probe.

Selecting **FANUC FOCAS** as the connection type extends the DPRNT source list with **NONE**, which disables DPRNT entirely (no Part identity or workflow events); Haas connection types never offer NONE, since DPRNT is their only source of Part identity below the NC header.

### FANUC FOCAS connection

Select **FANUC FOCAS** as the connection type in the CNC Connection panel; the FANUC fields replace the Haas fields, and the shared DPRNT section below stays the same for every type. Enter the control's fixed IP and MAC, the FOCAS port (8193 unless the control was changed), and the part counter source (parameter 6711 parts count or 6712 total parts). FOCAS is read-only: the Server reads controller state, the executing program, the part counter, spindle/feed, and alarms, and never writes a variable, offset, tool, or program.

The FANUC FOCAS library is licensed by FANUC and is not in version control. A Server built from a repository whose `focas\` folder holds the FANUC files installs them in its own `focas` subfolder automatically; otherwise copy the 64-bit set from your FANUC FOCAS 2 kit (`Fwlib64.dll`, `fwlibe64.dll`, and the control-series `fwlib*64.dll` files) into the Server install folder's `focas` subfolder, then use **Test FOCAS connection**. A missing main library is reported as a failed `focas` check that names the folders that were searched; a missing `fwlibe64.dll` or series library is reported as `EW_NODLL (-15)`.

Part identity and workflow events on a FANUC control come from DPRNT exactly as on Haas: choose **DPRNT source = TCP** for a serial-to-Ethernet bridge wired to the control's RS-232 port (enter the bridge's IP as **DPRNT TCP host** and its listening port as **DPRNT TCP port**; leave the host blank only when the controller itself serves the port), **FILE** for a control or PC front-end that writes DPRNT to a file on a share (enter the UNC path and the **Empty DPRNT file** policy, normally ON_OFFSET_LOADER), or **NONE** when no DPRNT output exists (then only monitoring works and no verification or cycle events can be observed). Optionally tick **Read NC header through FOCAS program upload** so the Server parses the Part identity from the head of the executing program (`//CNC_MEM/USER/PATH1/O<number>` by default) when no DPRNT PartName line exists; commission this on the real control before relying on it. **Refresh monitoring** shows the last normalized snapshot and component health for a FANUC Machine.

The **Protected setup verification** expander stores commissioning configuration only. Keep it disabled until the real Machine passes the bounded V10 no-motion commissioning test. There is no Machine credential field. Offset Loader completion arms the exact binding without a timeout; the timeout begins only when that NC first starts.

Every CNC Machine also has an **NC dialect** in the Machine editor, next to Execution mode: **HAAS_NGC** (default), **FANUC_MACRO_B**, **MAZAK_MATRIX_EIA**, or **OKUMA_OSP**. It selects the syntax of everything the Server writes into a Production Package (the verification call, the event context line, the part-counting blocks, and the Offset Loader program) and the variable numbers the verification configuration accepts: `#10000`–`#10999` plus an M109 response variable on Haas, `#500`–`#999` on FANUC and Mazak, and `1`–`200` (rendered as `VC1`–`VC200`) on Okuma. The dialect cannot be changed while Server Verification is enabled for the Machine: disable it, change the dialect, re-enter the variables for the new control, then enable again. Existing Machines were migrated as HAAS_NGC, so set **MAZAK_MATRIX_EIA** on the Mazak Variaxis and **OKUMA_OSP** on the Okuma Genos before their first package is built; with the wrong dialect the control alarms on `G103`, `DPRNT`, or `#10504` lines. The Offset Loader for an Okuma Machine is `O1990.MIN` (ending in `M02`); every other dialect keeps `O01990.nc`.

The same expander contains **Audited recovery — no verification bypass** for the
active editor. Enter the Production Run and a reason to invalidate the current
verification session or revoke the current Offset Loader. To restore the process,
enter the approved NC and tool-table release IDs and generate a replacement Offset
Loader. Prior releases and workflow evidence remain immutable. Replacement tablets
and credential rotation are handled on **User Terminals**; a failed QC inspection
returns to setup and uses the normal correct/resend path.

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
registration. TabletID is an identifier, and the MVP has no tablet credential to rotate.

Use the Windows **QC Queue** to monitor Production Runs whose latest workflow
state is `IN_QC`. The queue shows the Machine, part and Operation outputs,
Production Run, time received, and packaged setup worker when available.
Monitoring works in View Mode. To record a decision, request Edit Mode, select
the queue row, optionally enter a reason/comment, and choose **PASS** or
**FAIL**. PASS records the current Server time as production approval and moves
the workflow projection to `READY_FOR_PRODUCTION`. FAIL records the same audit
details, returns it to `IN_SETUP_RUN`, and allows the setupist to correct the
setup and send it to QC again. Neither button writes a CNC variable.

After QC PASS on a connected CNC, do not press **Start** in the Machine Planning
Board. The first valid, matching Haas DPRINT `CYCLE_START` automatically and
atomically changes the assigned Production Run to `IN_PROGRESS`, activates its
Program, projects `IN_PRODUCTION`, and opens the first cycle. The following matching
`CYCLE_END` credits exactly one cycle. The manual **Start** action is retained for
Machines without a configured CNC connection and non-CNC work. The controller parts
counter remains diagnostic. For the first physical validation, run the generated
no-motion `O01992` once while
`scripts/watch-haas-cycle-count-test.ps1` is recording. The recorder passes only when
one START/END pair increments the Program by exactly one cycle and each output by its
declared quantity per cycle.

## 7. Languages and responsiveness

Use the language selector in the Windows client to switch between English, Hebrew, and Russian. Every window, tab, context menu, message box, file dialog, and the NC viewer follow the choice. Message box buttons follow the Windows display language. Names, Part Numbers, NC programs, paths, and other data stay as entered. Technical codes that help text quotes, such as NC dialects and DPRNT settings, also stay as written. If a screen appears stuck, wait for the current request to finish before switching again, then refresh. Avoid opening many Timeline windows or repeatedly refreshing a large horizon; each read-only calculation uses the Server snapshot.

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

### DPRNT file unavailable

The monitoring error names the file. Check that the control PC share is reachable from the Server, that the Server service account can read the file (and write it when **Empty DPRNT file** is not NEVER), and that DPR14 = 4 is set on the Mazak control so DPRNT actually writes the file. A DPRNT test program `POPEN` / `DPRNT[TEST-123]` / `PCLOS` should add a `TEST-123` line; **Test DPRNT** then reports it as the last PartName. If the file keeps growing with ON_OFFSET_LOADER, the Offset Loader completion line is not reaching the file: check that the package was built with verification enabled and that the control's DPRNT output is routed to the file.

### DPRNT is connected but the Part never appears

The Server only recognizes a bare part-number line: uppercase letters and digits with at least one `-` or `.` separator (for example `30P647004101-001`). A `PART=` prefix, lowercase letters, an underscore, a Part number without a separator, or a `.CNC` suffix is ignored. Ask the postprocessor writer for `DPRNT[[[MEIMAD:PART_NAME]]]` as in the [NC postprocessor specification](nc-postprocessor-and-macro-specification.md), and check what the control really sent under the Machine's CNC diagnostics.

### FOCAS library not found

The `focas` check reports `FOCAS library Fwlib64.dll was not found` and lists the searched folders. Copy the 64-bit FOCAS 2 library files from the FANUC kit into the Server install folder's `focas` subfolder, or set the `MEIMAD_FOCAS_LIBRARY_DIR` environment variable for the service, then run **Test FOCAS connection** again. `EW_NODLL (-15)` means `Fwlib64.dll` was found but `fwlibe64.dll` or the control-series `fwlib*64.dll` is missing next to it. `EW_SOCKET (-16)` with the library complete means the control did not answer on the FOCAS port: check the fixed IP, that FOCAS/Ethernet is enabled on the control, and that the Server has a route to the machine VLAN.

### Client update fails or repeats

- **The installer window closes and the client does not return:** open `%LOCALAPPDATA%\MeimadPlanner\updates\install-client-update.log`. A declined UAC prompt or an error 1603 leaves the old client installed; start it from the Start menu, and the dialog offers the update again. The same MSI can be installed by hand from that folder.
- **"The update could not be installed: … checksum did not match":** the download was corrupted or the Server's `client-installer` folder holds a modified file. Retry once; if it repeats, reinstall the Server package so the bundled installer and its manifest match.
- **The notice says the Server has no client installer:** the Server was installed from a package older than 0.1.117 or its `client-installer` folder was emptied. Upgrade the Server, or install the client MSI from the release folder by hand.
- **The dialog appears at every start although the installation succeeded:** the client started from a second, older installation folder (for example a copied `bin` directory). Use the Start menu entry of the installed client.

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

### Customer portal push (optional)

The Server can push each customer's Order status to the cloud customer portal so customers can see their own Orders online without any access to the factory network. It is off unless the administrator enables it in `appsettings.json` next to the Server:

```json
"ClientPortal": {
  "Enabled": true,
  "IngestUrl": "https://meimad-ingest-832857266466.us-central1.run.app/ingest/orders",
  "SharedSecretFile": "client-portal-secret.txt",
  "PollIntervalSeconds": 300,
  "Customers": [
    { "Customer": "<exact Customer value of the Cases>", "CustomerId": "<portal customer id>" }
  ]
}
```

`client-portal-secret.txt` (next to the Server executable, not in `appsettings.json`) holds the portal's ingest secret; `CustomerId` is the id used when the customer's portal login was created. Only Order Number, Part Number, Case name, quantity, delivery date, and status are sent; Machines, backlog, setup, and every other planning field stay inside the factory. Restart the service after changing the section. The Server log shows one line per customer per push, or the reason a push was rejected.

## 10. Current implementation notes

The Server APIs and execution model support Production Runs, including multi-output planning data. Some Windows Timeline/Planning Board and TV/E-Ink cards do not yet render every Production Run field; where a Run-specific field is absent, use the operation card and Server projection as the authoritative view. The application does not yet provide automatic scheduling, ERP inventory authority, public Internet access, or native mobile editing. `SEND_TO_QC` is implemented in the Server, Windows QC flow, browser simulator, and compiled firmware; its physical tablet gesture/display behavior remains uncommissioned. Every other E-Ink write-back remains excluded.

For deployment and engineering details, see the repository [README](../README.md), [functional specification](functional-spec.md), and [performance/stability audit](performance-stability-audit-2026-08-23.md).
