# Meimad Planner ESP32 E-Ink Tablet MVP

**Persistent CNC workflow mode variable: REMOVED.** **Protected temporary setup
verification variables: SUPPORTED** only inside the CNC protected handshake;
this firmware never reads them or treats them as workflow authority.

This is the buildable firmware/development environment for the E-Ink tablet.
It includes observable boot diagnostics, bounded Wi-Fi and tablet-status API
calls, a first production screen layout, revision-gated refresh, and the first
status-driven deep-sleep state machine with wake-button actions. It also parses
and renders the Server-projected setup-verification response. Package
activation and the broader checklist/comment input state machine remain future
work.

Workflow status is Server-projected from immutable Production Run events. The
persistent CNC Setup/Production macro variable is removed. Protected temporary
verification variables belong only to the commissioned CNC challenge/response
handshake and are not tablet or firmware state; the firmware receives only the
bounded display projection, never the nonce, secret, variable mapping, or
algorithm.

## Hardware status

The development target uses the TRMNL BYOD 7.5-inch (OG) display/carrier with a
Seeed XIAO ESP32-S3 Plus and UC8179 monochrome 800x480 panel. The MVP product
power design remains three replaceable AA batteries with no rechargeable cell
or charger; the rechargeable pack bundled with some development kits is not an
accepted production power source. Seeed documentation identifies three user
buttons (D1, D2, D4), a battery ADC on D0/GPIO1, and ADC enable on GPIO6.
Display wiring is provided by the XIAO ePaper EE04 board.

| Item | Current MVP profile | Status |
|---|---|---|
| MCU | XIAO ESP32-S3 Plus | confirmed by kit documentation |
| Display | 7.5-inch monochrome E-Ink, 800x480 | confirmed |
| Controller | UC8179, Seeed board combo 502 | confirmed |
| Buttons | D1/GPIO2, D2/GPIO3, D4/GPIO5, active-low | confirmed |
| Deep-sleep wake | EXT1 active-low GPIO2/GPIO3/GPIO5 wake implemented | bench verification pending |
| Battery ADC | D0/GPIO1, ADC enable GPIO6, calibration 0.968 | confirmed |
| Battery | Three replaceable AA cells, no charger | product requirement; regulator/ADC/current bench validation pending |

## Build and serial diagnostics

Install PlatformIO, connect the board, then run:

```powershell
pio run -e xiao-esp32s3-plus
pio run -t upload
pio device monitor -b 115200
```

Boot output includes firmware version, chip model/revision/core count, MAC,
reset reason, wake reason, available UTC timestamp, RTC-retained pre-sleep
state, provisional display profile, button pins, and sampled battery voltage.
It also logs the selected state policy, effective wake sources, and Wi-Fi
shutdown immediately before entering deep sleep.

## Structured firmware logging

Firmware diagnostics pass through `include/firmware_logging.h`. Each emitted
record starts with a stable category such as `[BOOT]`, `[WAKE]`, `[WIFI]`,
`[API]`, `[DISPLAY]`, `[BUTTON]`, `[BATTERY]`, or `[SLEEP]`, followed by
key/value details. This keeps serial output filterable while avoiding direct
serial-print calls in the main API, display, network, and workflow lifecycle
paths.

The firmware enables the Seeed_GFX renderer for `BOARD_SCREEN_COMBO 502` and
`USE_XIAO_EPAPER_DISPLAY_BOARD_EE04`. On boot it renders the first production
layout and measures its full refresh over serial. E-Ink retains the completed
page without ESP32 power until the next refresh.

## First production layout

The 800x480 screen is text-only and prioritizes Machine identity, part,
Operation, a framed status band, and a fixed three-row tool table. Tool rows are
paginated rather than scrolled, and the footer reports `TOOLS n / total`.
Status tokens are converted to operator-readable text and never rely on color.
For `IN_SETUP`, the ordinary status/tool region is replaced by a prominent
setup-verification panel. `WAITING_FOR_OPERATOR` centers the fixed-width 4–6
digit response code and tells the operator to enter it at the CNC. `EXPIRED`,
`INVALIDATED`, and `UNAVAILABLE` show a text-only `SETUP BLOCKED` reason and
`PRESS REFRESH - DO NOT START`, with no response code.

The initial `/api/tablet/ping?hardwareId=<mac>` bootstrap sends the provisioned
bearer credential and accepts the Server-assigned short Tablet ID only after the
Server verifies that credential against the normalized physical MAC. When the
approved tablet-status GET succeeds, the layout displays that response.
The status response does not yet carry official tool rows, so the live tool area
explicitly says `NO TOOL DATA AVAILABLE`; it never fabricates Server data. Until
the pending Server compatibility route exists, the boot screen uses the example
layout fixture and marks it `LAYOUT DEMO`. That fixture contains seven tools to
exercise three-page pagination. The physical Previous/Next gestures select
those pages and persist the selected page in NVS. Official package-to-tool-row
binding remains pending.

Compilation checks geometry-independent model behavior and pagination. Normal
working-distance readability, clipping, contrast, and button navigation still
require an uploaded image on the physical panel under shop-floor lighting.

## Backend-free demo mode

Build the dedicated development image with:

```powershell
pio run -e xiao-esp32s3-plus-demo
pio run -e xiao-esp32s3-plus-demo -t upload
```

This image enables `MEIMAD_DEMO_MODE=1`. It never connects Wi-Fi, pings the
Server, requests status, sends battery metadata, or submits `SEND_TO_QC`.
Instead, it uses compiled fixtures with the same production screen model and
renderer. The persistent scenario cycle covers Ready for Setup, setup
verification with leading-zero code `0388`, expired setup verification, In
Setup Run, In QC, Ready for Production, In Production, Blocked, Wi-Fi Error,
Server Error, Unregistered Tablet, and Low Battery.

Use D1/Refresh or a long D4 press to advance to the next scenario; D2 moves to
the previous tool page and short D4 moves to the next one. The screen carries a
`DEMO - ...` notice, and the selected scenario is stored in NVS. The normal
firmware build leaves demo mode disabled and continues to use only Server state.

## Revision-based screen refresh

After a valid status response, the tablet compares its unsigned `revision` with
the `last_revision` stored in the `meimad` NVS namespace. The associated tablet
ID is stored as `last_rev_tab` so reassignment cannot accidentally reuse another
tablet's equal numeric revision.

- Missing stored state, a changed revision, or a changed tablet ID performs one
  full production-screen refresh.
- The new revision is written to NVS only after the panel update returns.
- An equal revision for the same tablet skips both rendering and `epaper.update()`.
- If the status request fails and the retained screen belongs to the same
  tablet, that screen normally remains untouched. A retained `IN_SETUP` code is
  the safety exception: it is replaced once with `CODE UNAVAILABLE` and `DO NOT
  START`, and the next valid response forces a repaint even at the same revision.

Every actual refresh logs its source, revision, and measured panel-update
duration. Skipped refreshes log the equality or last-known-screen reason without
claiming a panel update. The duration covers the blocking E-Ink `update()` call,
not in-memory text layout preparation.

## Tablet state machine

The tablet treats the status response as read-only Server authority. It does not
create a business transition; it only selects the next wake source after the
screen/revision decision:

| Server status | Tablet wake policy |
|---|---|
| `READY_FOR_SETUP` | Deep sleep with a 15-second development timer and physical-button wake so a new challenge is detected inside its minimum supported window. |
| `IN_SETUP` | Deep sleep with a 15-second development timer and physical-button wake so the time-limited verification projection is refreshed. |
| `IN_SETUP_RUN` | Deep sleep with physical-button wake only. |
| `IN_QC` | Deep sleep with a 120-second timer and physical-button wake. |
| `READY_FOR_PRODUCTION` | Deep sleep with physical-button wake only. |
| `IN_PRODUCTION` | Deep sleep with physical-button wake only; CNC/Server owns cycle events. |
| `BLOCKED`, `UNKNOWN`, or unavailable response | Conservative 120-second retry plus physical-button wake. This fallback remains a product-policy decision. |

All three active-low buttons are configured as ESP32-S3 EXT1 wake sources. RTC
peripherals remain powered so their internal pull-ups continue to work; the
resulting deep-sleep current must be measured on the real board. If button-wake
configuration fails in a button-only state, firmware enables a 120-second safety
timer rather than creating an unreachable tablet.

The 15-second setup-verification cadence corrects the physically observed
120-second poll versus 120-second challenge race. The 120-second timer remains
the development cadence for `IN_QC` and conservative fallback states.
Production automatic wakes must additionally be restricted to
the Server-configured workdays and shift windows. The firmware does not yet
consume the existing time-config/clock contract, so this scheduling gate remains
required before production acceptance; manual physical wake remains allowed at
any time.

## Deep-sleep runtime

Every boot classifies and logs the reset/wake reason, reports a UTC wake
timestamp when the system clock is valid (otherwise explicitly reports it
unavailable), samples battery voltage, and prints the status/wake-source state
retained in RTC memory before the previous deep sleep. A cold boot or lost RTC
state is reported as unavailable rather than fabricated.

The runtime follows this bounded power sequence:

```text
wake -> capture/debounce button -> decide whether network is required
     -> connect and perform bounded HTTP work when required
     -> apply revision/button display decision
     -> disable Wi-Fi -> configure timer/button wake -> retain sleep state
     -> deep sleep
```

Timer wakes, cold boots, Refresh, and `SEND_TO_QC` require Server contact.
Previous/Next and ignored physical-button wakes are local-only and do not start
Wi-Fi. The prior authoritative/fallback status is retained in RTC memory for
those local wakes so the next sleep policy does not silently change. Until the
official package/tool model is cached, a local page press against a retained
live screen cannot reconstruct another page and therefore preserves the current
panel; the development fixture can page locally.

After all required HTTP calls finish, Wi-Fi is disconnected and placed in
`WIFI_OFF` before any panel refresh. Sleep entry verifies shutdown again. If no
timer or button wake source can be configured, the firmware remains awake for
service with Wi-Fi still disabled. Physical current, RTC retention, timestamp
continuity, ADC calibration, and wake timing remain bench-verification items.

## Battery telemetry and warning

The tablet samples battery voltage once per boot and attaches it to every HTTP
request it makes (`/api/tablet/ping`, tablet-status GET, and tablet-event POST)
as `X-Meimad-Battery-Voltage`, formatted to three decimal places. The optional
`X-Meimad-Battery-Percent` header is deliberately omitted: percentage requires
a measured three-AA discharge curve and is not inferred from voltage alone.
The same authenticated requests report the compiled firmware version and, while
connected, the local IP address and RSSI through bounded
`X-Meimad-Firmware-Version`, `X-Meimad-Wifi-IP`, and `X-Meimad-Wifi-Rssi`
headers. This health metadata is separate from planning data and does not change
the strict `SEND_TO_QC` JSON payload.

`LOW BATTERY` is shown when a valid sample is at or below the provisional 3.30
V threshold. Its last displayed state is stored in NVS so a threshold crossing
forces a single E-Ink refresh even when the Server revision is unchanged. The
threshold and ADC calibration must be confirmed on the physical AA power path.
Schema v53 records the last valid firmware, battery, IP, and RSSI observations
for the Windows User Terminals monitor. It does not infer an online SLA or alter
planning, workflow, package, or execution state. Historical telemetry retention
and battery-percentage calibration remain separate work.

## Physical buttons

The first three-button mapping is deliberately small and reversible until the
enclosure labels and shop-floor ergonomics are physically accepted:

| Hardware input | Firmware action |
|---|---|
| Short D1 / GPIO2 | Refresh: wake, connect, and request current Server status. |
| Hold D1 / GPIO2 for 1.2 seconds | Open the service/debug screen after a bounded Server refresh. |
| D2 / GPIO3 | Previous tool page. |
| Short D4 / GPIO5 | Next tool page. |
| Hold D4 / GPIO5 for 1.2 seconds | `SEND_TO_QC`, only after a fresh `IN_SETUP_RUN` status permits it. |

Inputs are active-low EXT1 wake sources. The abstraction requires exactly one
button, debounces it for 40 ms, logs the resolved action and hold time, and
waits for release before sleeping again. Ambiguous, bounced, or unreleased
inputs are ignored. This prevents one held button from causing an immediate
second wake/action; an in-memory guard also allows at most one event POST in a
wake cycle.

## Service/debug screen

A deliberate 1.2-second D1/Refresh hold opens a flat two-column diagnostic
screen. It shows Tablet ID, hardware MAC, firmware version, Machine binding,
Wi-Fi SSID, current IP/RSSI, Server address, last successful contact, last HTTP
result, workflow state/revision, battery voltage, wake reason, prior panel
refresh duration, latest Server-known CNC verification result, and reported
protected-macro version. The same bounded values are emitted as `[SERVICE]`
serial records. Missing clock/network/Server evidence is labeled unavailable;
it is never fabricated.

The screen model has no bearer-token, Wi-Fi-password, raw nonce, Machine secret,
protected-variable mapping, response algorithm, or operator response-code
field. The long-D1 gesture is provisional pending physical enclosure and
shop-floor ergonomic acceptance. The service-screen marker is persisted so the
next valid ordinary status response restores the production screen even when
its numeric revision has not changed.

After `SEND_TO_QC`, the firmware always performs another status GET without
submitting the event again. The status band shows accepted, confirmed,
rejected, refresh-pending, or unknown-result feedback. A transport timeout is
not presented as rejection because the Server may have committed the event;
an `IN_QC` follow-up confirms it. The feedback is cleared by the next valid
status refresh, with that cleanup marker persisted in NVS. The Server remains
responsible for cross-wake idempotency and the first accepted timestamp.

## Windows-hotspot connectivity test

Set the hotspot SSID, password, and IPv4 address of the Windows PC running the
Server in `include/device_config.h`, then upload. Use the hotspot adapter IPv4
address shown by `ipconfig`, for example:

```cpp
constexpr char kDefaultWifiSsid[] = "Meimad-Dev";
constexpr char kDefaultWifiPassword[] = "your-hotspot-password";
constexpr char kServerBaseUrl[] = "http://192.168.137.1:5080";
```

The first non-empty values are copied to ESP32 NVS. Later reboots and firmware
updates use the NVS copy. On each boot the tablet makes three bounded Wi-Fi
attempts, then requests `GET /api/tablet/ping?hardwareId=<mac>`. The display
reports `SERVER CONNECTED` or `SERVER NOT AVAILABLE`. A Wi-Fi failure sleeps
the MCU after the retries, leaving the E-Ink page visible.

The permanent identity is the station MAC address. `tablet_id` is stored in NVS
and shown once a Server response supplies it. Before assignment, the screen
shows `UNREGISTERED TABLET` and the MAC address.

## Approved tablet status/event adapter

The firmware implements a bounded client for the Task 5 compatibility shape:

```http
GET /api/tablets/{tablet_id}/status
POST /api/tablets/{tablet_id}/events
```

The status response requires `revision`, matching `tablet_id`, `machine`,
`nc_run`, `part`, `operation`, and one of these exact tokens:
`READY_FOR_SETUP`, `IN_SETUP`, `IN_SETUP_RUN`, `IN_QC`, `READY_FOR_PRODUCTION`,
`IN_PRODUCTION`, `BLOCKED`, or `UNKNOWN`. Opaque string IDs are preferred;
integer IDs from the example payload are also accepted and normalized to
strings in memory. Missing fields, wrong types, a mismatched tablet ID,
unsupported tokens, and invalid JSON are rejected as malformed without
replacing a previously valid response.

`IN_SETUP` additionally requires a `verification` object with `required: true`
and one of `WAITING_FOR_OPERATOR`, `EXPIRED`, `INVALIDATED`, or `UNAVAILABLE`.
Only the waiting state may carry `response_code`; it must be a JSON string of
4–6 decimal digits so leading zeroes survive. A missing verification object,
a numeric/malformed code, a code attached to a blocking state, or verification
data outside `IN_SETUP` is rejected rather than rendered.

An optional `diagnostics` object supplies only `verification_result` and
`protected_macro_version` for the service screen. Invalid types or negative
versions are rejected. No sensitive verification input is accepted or logged.

The production header uses `machine.number` when the response supplies it. For
compatibility with the original numeric-ID example, a missing number is rendered
as `M{id}` only when `machine.id` is numeric; new Server responses should supply
the actual display number explicitly.

The initial event request is exactly:

```json
{ "event_type": "SEND_TO_QC" }
```

It contains no device timestamp. A successful Server acknowledgment is defined
as Server-generated UTC time plus the echoed identity and event:

```json
{
  "tablet_id": "3041",
  "event_type": "SEND_TO_QC",
  "timestamp": "2026-08-25T10:15:30Z"
}
```

Both calls use a 5-second connection timeout and 7-second overall HTTP timeout,
log the returned HTTP status, and distinguish transport, HTTP, and malformed
response failures. A device bearer token can be provisioned in the `meimad`
NVS namespace as `device_token`; `kDefaultDeviceToken` exists only as a local
development bootstrap and must never contain a committed live credential.

Build the focused on-device contract test image without changing the normal
firmware artifact with:

```powershell
pio run -e xiao-esp32s3-plus-contract-tests
```

If that image is explicitly uploaded for bench testing, its 115200-baud serial
output ends with `Tablet API contract tests: PASS (0 failures)` when every JSON
fixture assertion succeeds.

The firmware reads the approved status shape after a successful registration
ping and exposes the bounded `SEND_TO_QC` request method. The approved command
transitions the Server-resolved current run from tablet workflow
`IN_SETUP_RUN` to `IN_QC`; it supplies no target or device timestamp and grants
no planning/package mutation authority. The firmware binds it to the guarded
long-D4 gesture described above and follows the POST with a status GET. The
Server now has shared append-only workflow-event persistence, derives tablet
status from it, and implements the schema-v54 path/credential authorization,
`IN_SETUP_RUN` eligibility, per-inspection-attempt idempotency, and
first-timestamp response. Schema v55 permits a later distinct send after
`QC_FAIL` returns the same Run to setup.
Negative and concurrent Server tests pass. The firmware interaction has not yet
been uploaded and exercised against that endpoint on physical hardware.
