# Meimad Planner ESP32 E-Ink Tablet MVP

This is the first buildable firmware/development environment for the E-Ink
tablet. It intentionally starts with observable boot diagnostics and button
heartbeat logging before adding Wi-Fi, Server API calls, package activation, or
deep sleep.

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

The firmware now enables the Seeed_GFX renderer for `BOARD_SCREEN_COMBO 502`
and `USE_XIAO_EPAPER_DISPLAY_BOARD_EE04`. On boot it renders the smoke-test
reference layout, measures a full refresh, performs a small partial refresh,
and prints both durations over serial. The final page states that the panel is
asleep; E-Ink retains this image without ESP32 power until the next refresh.

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

## Proposed tablet status/event adapter

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

The firmware reads the proposed status after a successful registration ping.
It does not send `SEND_TO_QC` from a button or mutate planning state. The current
Server does not implement these two compatibility routes: its implemented,
authorized baseline remains the GET-only `/api/v1/eink/devices/{deviceId}/...`
contract. Server-side status derivation, event authorization/persistence, and
the relationship to the existing E-Ink projection require the recorded product
decision before these routes can be enabled end to end.
