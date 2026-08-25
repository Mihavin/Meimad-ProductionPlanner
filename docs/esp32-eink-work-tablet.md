# ESP32 / Color E-Ink Work Tablet

- **Concept baseline:** v0.1
- **Source date:** 11 August 2026
- **Status:** Hardware and firmware concept; choices marked TBD require prototyping.

This document normalizes `Meimad_Planner_ESP32_EInk_Work_Tablet_Concept_v0.1.docx`. The source title identifies v0.1 while its footer says v0.3; this repository treats the device concept as v0.1 until the source is corrected.

## 1. Purpose and authority boundary

The Color E-Ink Work Tablet is a low-cost, low-power operational display and local setup checklist for use near CNC Machines and in the Tool Room.

It shows the Machine backlog, current operation, setup package, tool checklist, NC/text files, offsets, instructions, and local notes. It is not a planning editor and never becomes an authoritative planning data source. Its one Server write is the scoped `SEND_TO_QC` operational event.

Primary goals are:

- One inexpensive, unified device design for all Machines.
- One tablet per Machine plus one or two spares.
- Months of practical operation from replaceable AA batteries, subject to measurement.
- Official planning/package data flowing from server to device, with only the `SEND_TO_QC` operational event flowing back.
- Paper-like local checklist marks and notes that never synchronize.

## 2. MVP boundaries

| Included | Excluded |
|---|---|
| Read-only Machine and setup views plus `SEND_TO_QC` | Planning edits or any other official-data write-back |
| Wi-Fi package download | USB Mass Storage to CNC |
| SD/microSD cache | Official CNC program-carrier responsibility |
| Device-local checklist and comments | Full tool inventory management |
| Read-only NC/text viewer | Full Android-tablet behavior |
| Replaceable 3 x AA power | Rechargeable battery or charger circuit |
| Service/programming connector if needed | OTA update unless later accepted as low risk |

## 3. Target hardware

| Area | Target or constraint |
|---|---|
| MCU | XIAO ESP32-S3 Plus on the Seeed XIAO ePaper Display Dev Board. |
| Display | TRMNL BYOD 7.5-inch (OG) monochrome E-Ink, UC8179 controller, 800×480. |
| Storage | Removable SD/microSD for official packages, previews, rendered screens, NC/text, tool tables, and local annotations. |
| Power | Three replaceable AA batteries; no rechargeable cell or charger in the MVP. The kit battery is not the production power design. |
| Regulation | Low-quiescent-current design with brownout-safe Wi-Fi peaks; power-gate SD/display where practical. |
| Inputs | Three active-low user buttons: D1/GPIO2 Refresh, D2/GPIO3 Previous Tool Page, short D4/GPIO5 Next Tool Page, and 1.2-second D4/GPIO5 hold for `SEND_TO_QC`, plus reset. Physical labels/ergonomics remain to be accepted. |
| USB | Optional programming/service connector only; never expose Mass Storage to CNC in MVP. |
| Mechanical | Rugged mount near the Machine, easy setup-time removal, and practical dust/oil-mist protection. Required rating is TBD. |
| Status | Prefer on-screen indication; avoid always-on LEDs. |
| Battery | BAT_ADC on D0/GPIO1 with ADC enable GPIO6; calibration factor 0.968 in the MVP. |

Hardware and firmware must not be customized per Machine. Machine identity, network configuration, server location, and credentials are provisioned data.

## 4. Power and refresh behavior

Deep sleep is the default state.

1. Wake manually at any time when Refresh is pressed, or automatically during a configured workday/shift window.
2. Make a small version/change request before downloading content or refreshing the E-Ink panel.
3. If nothing changed and the user did not explicitly force a full refresh, return to sleep without a display refresh.
4. If the assigned revision changed, download required content into a staging area.
5. Verify the manifest and every downloaded file.
6. Activate the new package only after the complete revision passes verification.
7. Render or display the relevant screen, persist last-known-good state, and return to sleep.

The physical firmware implements the status-screen subset of this rule. It
stores the last completed status `revision` and its tablet identity in NVS. A
matching tablet/revision skips in-memory drawing and the physical E-Ink update;
a missing value, changed revision, or reassignment refreshes and then saves the
new value. NVS is not advanced before the blocking panel update returns. Every
actual update logs its revision and measured `update()` duration. An unavailable
or malformed response keeps the retained same-tablet screen and revision.
Package download/activation revisions remain separate future state and must not
be conflated with this status-screen revision.

The first tablet-side power state machine is also implemented. It consumes the
Server status only to choose wake behavior:

| Server status | Firmware sleep/wake behavior |
|---|---|
| `READY_FOR_SETUP` | Deep sleep; timer poll after 120 seconds or physical-button wake. |
| `IN_SETUP_RUN` | Deep sleep; physical-button wake only. |
| `IN_QC` | Deep sleep; timer poll after 120 seconds or physical-button wake. |
| `READY_FOR_PRODUCTION` | Deep sleep; physical-button wake only while the explicit status text remains visible. |
| `IN_PRODUCTION` | Deep sleep; physical-button wake only. CNC/Server generates cycle events. |
| `BLOCKED`, `UNKNOWN`, or no valid response | Conservative 120-second retry plus physical wake; final policy is TBD. |

GPIO2/GPIO3/GPIO5 are configured as active-low ESP32-S3 EXT1 wake sources.
Internal RTC pull-ups are kept powered; physical wake and its current cost still
require bench verification. A failure to configure button wake adds a
120-second safety timer for an otherwise button-only state. The state machine
never changes a Server status.

On each boot, firmware logs the reset/wake reason, a UTC wake timestamp when
the clock is valid (otherwise an explicit unavailable result), calibrated ADC
battery voltage, and the status/source/wake configuration retained before the
previous deep sleep. That pre-sleep state uses RTC memory and is never treated
as authoritative business state. It preserves the prior sleep policy across a
local-only page wake; cold boot or lost/invalid RTC memory falls back safely.

Cold boots, timer wakes, Refresh, and `SEND_TO_QC` perform bounded Wi-Fi/Server
work. Previous/Next and ignored physical wakes do not start Wi-Fi. After the
last HTTP operation, firmware disconnects Wi-Fi and selects `WIFI_OFF` before
panel refresh and deep sleep; sleep entry repeats this shutdown as a guard. If
both timer and physical wake configuration fail, it remains awake for service
with the Wi-Fi radio disabled. Actual radio-off state, deep-sleep current,
battery calibration, wake latency, RTC retention, and timestamp continuity
still require physical measurement.

The current firmware samples one voltage per wake and attaches it as
`X-Meimad-Battery-Voltage` metadata to every ping/status/event HTTP request.
It does not guess a percentage. A simple, provisional `LOW BATTERY` screen
warning appears at or below 3.30 V; threshold transitions force a screen update
even without a new Server revision. Device-health metadata is separate from
planning and `SEND_TO_QC`; the Server may currently ignore it. Server-side
tablet battery history, retention, hardware-range validation, and an eventual
measured percentage curve remain pending.

The timer mapping is implemented as the requested initial development policy,
but it does not yet enforce configured workdays/shift windows because the
firmware has no approved clock/time-config integration. Production automatic
wake remains blocked on that OD-027 work; manual button wake remains valid at
any time.

An automatic check every ten minutes is only an example. The work calendar, wake interval, retry/backoff, clock source, time zone, DST, and force-refresh gesture are TBD.

The panel retains the last visible image without continuous power. The final battery-life goal must be expressed as a measured workload and numeric acceptance threshold after a bench prototype exists.

## 5. Device modes

| Mode | Required behavior |
|---|---|
| Machine Display | Current job, next jobs, status/conflicts, last update, and battery. |
| Setup Package | Part/Case, Batch, Operation, assigned Machine, optional tool-cart ID, package revision, instructions, and setup checklist. |
| Tool Checklist | Device-local installed/not-installed marks, local status, and optional local comment. |
| NC/Text Viewer | Paginated, read-only NC text, instructions, offsets, and tool tables. |
| Wi-Fi Setup | Temporary access point for Wi-Fi, server, device/Machine identity, and token provisioning. |
| Offline | Last cached package/screen with an unambiguous stale/offline warning and last-update time. |
| Send to QC | Confirmed `SEND_TO_QC` for the Server-resolved current run; show pending/failure/success using text and icon, never color alone. |

The first production-screen mapping uses D1/GPIO2 for Refresh, D2/GPIO3 for
Previous Tool Page, short D4/GPIO5 for Next Tool Page, and a 1.2-second D4 hold
as the deliberate `SEND_TO_QC` confirmation gesture. The firmware accepts only
one debounced button per EXT1 wake, waits for release, logs the action, and
allows at most one event POST in that wake cycle. A send is enabled only after
fresh `IN_SETUP_RUN` status, is followed by a status GET, and displays distinct
accepted/confirmed/rejected/uncertain feedback. Physical behavior has not been
bench-verified. The three buttons still do not define checklist selection,
five-state local checklist status, free-text entry, broader Back/Next
navigation, or revision-cleanup prompts, so the complete post-wake interaction
state machine remains a blocking decision.

## 6. Setup workflow

1. The tablet normally remains mounted at its assigned Machine and displays that Machine's backlog.
2. At the start of setup, the setupist removes the tablet and takes it to the Tool Room.
3. An authorized Windows user prepares or confirms the official package for a specific Machine, Batch, and Operation.
4. The tablet downloads the package over Wi-Fi after assignment or Refresh.
5. The setupist receives the prepared tool cart and sees its cart ID on the tablet.
6. The setupist returns to the Machine with tablet and cart.
7. During setup, the tablet acts as a local checklist and read-only viewer.
8. When setup is ready for inspection, the setupist holds D4 for 1.2 seconds. The firmware first verifies fresh `IN_SETUP_RUN` status, submits at most one `SEND_TO_QC` attempt, and then refreshes status. The tablet sends no target ID or timestamp and does not present `IN_QC` unless a Server status response reports it.
9. The Server resolves the bound Machine/current run, records the first event using Server time, and the next status projection shows `IN_QC`. A bounded retry after an uncertain network result is safe and returns the first timestamp.
10. If a problem is found, the setupist takes the tablet to the programmer or QC. An authorized Windows user corrects and republishes the official package.
11. The tablet downloads the new revision. Local notes remain local and are not applied to the official package.

The implemented baseline lets the current Windows Edit Mode holder publish for an already assigned Batch Operation and create corrections as new immutable revisions. A distinct preparer/approver role, approval UI/audit, revision naming/order, reassignment confirmation, retention, and superseded-package access remain TBD.

## 7. Official job package

Each package contains or references:

- Package ID and revision.
- Case/part number and preview image.
- Batch ID and Operation ID.
- Assigned Machine and optional tool-cart ID.
- Operation instructions and setup notes.
- Tool table.
- Provided read-only offsets or related files.
- NC/text program files for viewing only.
- Color/status metadata for simple E-Ink rendering.
- Manifest entries containing filename, size, version, timestamp, and checksum.

Official files remain read-only in the device UI. The implemented Server publisher creates an opaque package ID plus caller-named immutable revision, snapshots Machine/part/Batch/Operation metadata, assigns each asset a role, and includes SHA-256/length/media metadata in the manifest. Preview, allow-listed NC/text, package-specific tool table, offsets, and instructions are supported. Approval roles/UI, revision ordering policy, signatures, compression, range/resume, retention, and physical-device activation remain TBD.

Download and activation must be last-known-good: interruption, corrupt SD data, or a checksum mismatch must never replace the previous verified package.

## 8. Local checklist and annotations

Local data is stored separately from official package content and is keyed to package ID and revision.

- Per-tool installed / not installed mark.
- Optional status: missing, replaced, question, needs programmer, or needs QC.
- Short local comment per tool or package page.
- Clear notice that the information is local and not synchronized.

When a new official revision arrives, the revision change must be obvious. The device may offer to clear earlier marks, but migration, retention, and deletion behavior are TBD. Local data must never be accepted as authoritative server planning or package data.

## 9. Proposed SD layout from the source concept

```text
/meimad/
  config.json
  devices/
    device_state.json
  packages/
    active/
      manifest.json
      preview.bmp
      tools.csv
      instructions.txt
      nc/
        OP20_MAIN.nc
      offsets/
        offsets.txt
    history/
      <package_id>_<revision>/
  local_notes/
    <package_id>_<revision>_notes.json
  cache/
    screens/
      machine_page.bmp
      tool_page_001.bmp
```

This is a conceptual layout, not a frozen on-device contract. Filesystem, SD capacity, atomic writes, corruption recovery, power-loss safety, history limit, and data-at-rest protection are TBD. Unlimited `history/` would conflict with the lost-device goal of retaining only limited current data.

## 10. API needs

The source concept names these read-only routes:

```http
GET /api/eink/devices/{device_id}/version
GET /api/eink/devices/{device_id}/machine-screen
GET /api/eink/devices/{device_id}/package-manifest
GET /api/eink/devices/{device_id}/package-file/{file_id}
GET /api/eink/devices/{device_id}/time-config
```

The Server implements the versioned `/api/v1/eink/devices/{deviceId}/...` forms documented in [API contract](api-contract.md), including revision-qualified manifest/file routes. Authentication is by a per-device revocable bearer token; only its SHA-256 hash is persisted. A bound device can read only the package associated with the first unfinished Operation on its assigned Machine.

Structured JSON is the implemented v1 Server/simulator baseline. A pre-rendered panel asset may be added only through an explicit compatible contract decision because it changes firmware complexity, fonts, pagination, server rendering, payload size, and display-specific coupling. The browser simulator at `/eink-simulator/` performs the version request first, displays the structured Machine screen, reads manifests/files, verifies file SHA-256, and preserves its last rendered screen on errors. It does not claim physical SD staging, atomic activation, deep sleep, E-Ink refresh, or local annotation behavior.

The source also suggests optional battery/firmware telemetry using a `GET .../ping` request. That conflicts with the read-only boundary and safe HTTP semantics if it records state. Telemetry is excluded from the baseline contract until a narrowly scoped, authenticated design is approved. Server observation of normal reads may update operational last-seen state, but must never change planning data.

The Task 5 physical-firmware status/event contract is approved:

```http
GET /api/tablets/{tablet_id}/status
POST /api/tablets/{tablet_id}/events
```

The authenticated GET status route is implemented on the Server and returns the
exact snake-case payload consumed by `TabletApiClient`. It records narrow
last-contact/battery metadata, resolves the current Production Run on the bound
Machine, and keeps its numeric revision stable while visible content is
unchanged. The POST event route remains pending. A multi-output current Program
currently returns an explicit projection conflict because this firmware payload
can display only one part/operation; it never silently hides a coupled output.

`SEND_TO_QC` is allowed only from `IN_SETUP_RUN` and changes the same resolved
Production Run's tablet workflow projection to `IN_QC`. The request contains
only the event token. The Server owns target resolution and time, and a retry
for the same run is idempotent. No Edit Mode is required and no planning/run
lifecycle/package field changes. The firmware client and JSON/failure handling
exist; the Server routes/storage and physical input binding remain unimplemented.

## 11. Wi-Fi provisioning

- On first boot or a setup gesture, create a temporary access point; `MP-M07-SETUP` is an example name only.
- A technician connects with a phone or laptop and enters Wi-Fi SSID/password, server address, device/Machine ID, and token.
- The device stores configuration and restarts in normal mode.
- Configuration reset is available through a long press or service procedure.
- No recompilation or Machine-specific firmware image is required.

Server-side credential rotation/revocation and Machine/spare binding are implemented through active-editor administration routes. Setup-AP authentication and timeout, HTTPS trust, device-side credential storage, token lifetime, factory reset, and recovery remain firmware decisions.

## 12. Security boundary

- A device credential grants only the read-only Machine/package resources assigned to that tablet plus `SEND_TO_QC` for the Server-resolved eligible run.
- Do not expose unrelated customer, drawing, or engineering data.
- Limit cached official data and support immediate credential revocation for a lost device.
- Keep official and local data in separate storage namespaces.
- Do not provide CNC write functions or USB Mass Storage in MVP.
- Device state, `SEND_TO_QC`, and any later telemetry remain operational data, never planning data. `SEND_TO_QC` affects only the tablet workflow projection.

Transport security, certificate trust, removable-media encryption/signing, secure boot, firmware signing, token storage, and physical attack assumptions are TBD.

## 13. Color E-Ink UI rules

- White background and black primary text.
- Large, flat status blocks.
- No gradients, shadows, animation, or tiny colored text.
- Blue means current/in progress; green done/OK; yellow attention; orange risk; red blocking; grey idle/no data.
- Duplicate every color meaning with text and/or a symbol.
- Always show last update and stale/offline state where relevant.
- Make a package revision change visually prominent.

Machine view shows Machine ID/type, last update, battery, current part/Batch/Operation/quantity/status, and the next three jobs. Setup view shows Machine, cart, part, Batch, Operation, revision, and paginated checklist. NC/text view shows filename, revision, page position, and a read-only notice.

The physical firmware now contains the first 800x480 production layout. It is
text-only and orders the page as Machine Name/Number with a small tablet ID,
part, Operation, a prominent framed status, then a fixed three-row tool table.
Tool rows use numbered pages and never scroll. All seven approved status tokens
have operator-readable labels. A successful status response populates the
official identity/part/Operation/status fields; because that compatibility
response does not contain tools, the live layout shows `NO TOOL DATA AVAILABLE`
instead of example rows. The development-only seven-tool fixture is visibly
marked `LAYOUT DEMO`.

The layout model, pagination, same/different/missing/reassigned revision
decisions, button mapping, page-boundary behavior, single-attempt guard, and
`IN_SETUP_RUN` send eligibility compile in the focused firmware contract-test
image. The official package-to-tool-row binding, on-device execution of those
assertions, and physical button/readability/clipping/contrast validation remain
pending; the implementation therefore does not yet satisfy the prototype
acceptance gate below.

A separate compile-time demo image (`xiao-esp32s3-plus-demo`) makes UI work
independent from unfinished Server status/event routes. It disables Wi-Fi and
all Server calls, renders production-model fixtures marked `DEMO`, and persists
a ten-step state cycle: Ready for Setup, In Setup Run, In QC, Ready for
Production, In Production, Blocked, Wi-Fi Error, Server Error, Unregistered
Tablet, and Low Battery. D1/long-D4 advances the scenario while D2/short-D4
exercise tool pages. It never submits a test event or presents fixture state as
Server authority; the normal production build has demo mode disabled.

Display resolution, minimum font size, viewing distance, lighting range, localization, Unicode/RTL, bitmap format, page geometry, and ghosting/full-refresh policy are TBD.

## 14. Failure behavior

| Failure | Required response |
|---|---|
| No Wi-Fi | Show cached content with stale/offline warning. |
| Server unreachable | Keep the last package and show last-update time. |
| Low battery | Show a persistent warning until batteries are replaced. |
| Missing/corrupt SD | Show an error and do not attempt package download. |
| Checksum mismatch | Reject the new file/revision and retain the previous verified package. |
| New revision | Make the revision change explicit. |
| Display refresh failure | Retry once; persist an error indication for a later successful refresh. Exact presentation is TBD. |
| `SEND_TO_QC` timeout/connection loss | Do not optimistically show `IN_QC`; show send-pending/unknown and perform a bounded idempotent retry. |
| `SEND_TO_QC` rejected | Keep the last confirmed status and show the Server error in operator-readable text. |

Retry limits, backoff/jitter, corrupted configuration, invalid token, clock loss, oversized files, mid-download battery loss, and rollback details must be added to the firmware acceptance suite.

## 15. Prototype verification plan

1. Build one bench prototype with the selected MCU, panel, SD interface, buttons, and 3-AA power input.
2. Measure deep-sleep current.
3. Measure one wake and version-check cycle.
4. Measure one full package download and E-Ink refresh cycle.
5. Test temporary-AP provisioning and reset.
6. Test checklist persistence across sleep and, if possible, across battery replacement.
7. Test readability at Machine distance and under representative shop-floor lighting.
8. Fault-inject network loss, invalid credentials, SD removal/corruption, malformed manifests, checksum failure, and power loss during activation.
9. Verify `SEND_TO_QC` confirmation, cross-device/run rejection, revoked credentials, invalid-state rejection, lost-response retry idempotency, first-timestamp retention, and no planning/package mutation.
10. Run a one-week pilot on one Machine before ordering multiple devices.

Numeric pass/fail thresholds for battery life, timing, readability, storage, recovery, and pilot success must be approved before the prototype can pass.

## 16. Deferred work

- USB Mass Storage to CNC.
- Server write-back of tool notes or checklist state.
- Any Server write-back beyond the approved `SEND_TO_QC` event.
- Official CNC program transfer responsibility.
- Full Android-tablet behavior.
- Full tool inventory tracking.
- Rechargeable battery and charger circuit.
- OTA update unless explicitly approved as low risk.

The consolidated unresolved-decision register is in [Implementation plan](implementation-plan.md#open-decisions).
