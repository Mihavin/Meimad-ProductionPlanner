# Meimad Planner ESP32 E-Ink Tablet MVP

This is the buildable firmware/development environment for the E-Ink tablet.
It includes observable boot diagnostics, bounded Wi-Fi and tablet-status API
calls, a first production screen layout, and button heartbeat logging. Package
activation and the complete deep-sleep/input state machine remain future work.

## Hardware status

The target is the TRMNL BYOD 7.5-inch (OG) DIY Kit: Seeed XIAO ESP32-S3 Plus,
UC8179 monochrome 800x480 panel, and 2000 mAh rechargeable battery. The
official Seeed documentation identifies three user buttons (D1, D2, D4), a
battery ADC on D0/GPIO1, and ADC enable on GPIO6. Display wiring is provided by
the XIAO ePaper EE04 board.

| Item | Current MVP profile | Status |
|---|---|---|
| MCU | XIAO ESP32-S3 Plus | confirmed by kit documentation |
| Display | 7.5-inch monochrome E-Ink, 800x480 | confirmed |
| Controller | UC8179, Seeed board combo 502 | confirmed |
| Buttons | D1/GPIO2, D2/GPIO3, D4/GPIO5, active-low | confirmed |
| Deep-sleep wake | GPIO wake still needs bench verification | pending |
| Battery ADC | D0/GPIO1, ADC enable GPIO6, calibration 0.968 | confirmed |
| Battery | 2000 mAh rechargeable Li-ion | confirmed |

## Build and serial diagnostics

Install PlatformIO, connect the board, then run:

```powershell
pio run -e xiao-esp32s3-plus
pio run -t upload
pio device monitor -b 115200
```

Boot output includes firmware version, chip model/revision/core count, MAC,
reset reason, wake reason, provisional display profile, button pins, and battery
availability. A heartbeat is printed every ten seconds so a bench test can
confirm the firmware remains alive.

The firmware enables the Seeed_GFX renderer for `BOARD_SCREEN_COMBO 502` and
`USE_XIAO_EPAPER_DISPLAY_BOARD_EE04`. On boot it renders the first production
layout and measures its full refresh over serial. E-Ink retains the completed
page without ESP32 power until the next refresh.

## First production layout

The 800x480 screen is text-only and prioritizes Machine identity, part,
Operation, a framed status band, and a fixed three-row tool table. Tool rows are
paginated rather than scrolled, and the footer reports `TOOLS n / total`.
Status tokens are converted to operator-readable text and never rely on color.

When the approved tablet-status GET succeeds, the layout displays that response.
The status response does not yet carry official tool rows, so the live tool area
explicitly says `NO TOOL DATA AVAILABLE`; it never fabricates Server data. Until
the pending Server compatibility route exists, the boot screen uses the example
layout fixture and marks it `LAYOUT DEMO`. That fixture contains seven tools to
exercise three-page pagination. Previous/Next button behavior is intentionally
not bound yet because the complete physical-input mapping remains open.

Compilation checks geometry-independent model behavior and pagination. Normal
working-distance readability, clipping, contrast, and button navigation still
require an uploaded image on the physical panel under shop-floor lighting.

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
  tablet, that screen remains untouched.

Every actual refresh logs its source, revision, and measured panel-update
duration. Skipped refreshes log the equality or last-known-screen reason without
claiming a panel update. The duration covers the blocking E-Ink `update()` call,
not in-memory text layout preparation.

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
`READY_FOR_SETUP`, `IN_SETUP_RUN`, `IN_QC`, `READY_FOR_PRODUCTION`,
`IN_PRODUCTION`, `BLOCKED`, or `UNKNOWN`. Opaque string IDs are preferred;
integer IDs from the example payload are also accepted and normalized to
strings in memory. Missing fields, wrong types, a mismatched tablet ID,
unsupported tokens, and invalid JSON are rejected as malformed without
replacing a previously valid response.

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
no planning/package mutation authority. The current Server does not yet
implement these two compatibility routes, and the firmware does not yet bind
the command to a physical button. Those implementation steps are tracked in the
plan; the implemented Server baseline remains GET-only until they land with
authorization, persistence, idempotency, and negative tests.
