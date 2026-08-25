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
