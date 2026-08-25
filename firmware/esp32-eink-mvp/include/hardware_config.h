#pragma once

// Hardware profile: TRMNL BYOD 7.5-inch (OG) DIY Kit, Seeed XIAO ESP32-S3 Plus
// and 7.5-inch 800x480 monochrome panel. Display driver is UC8179 (Seeed
// BOARD_SCREEN_COMBO 502 / XIAO ePaper EE04 board).
namespace meimad::hardware {

constexpr char kMcuProfile[] = "XIAO ESP32-S3 Plus";
constexpr char kDisplayProfile[] = "TRMNL 7.5 OG monochrome E-Ink / UC8179";
constexpr int kDisplayWidth = 800;
constexpr int kDisplayHeight = 480;

// Active-low user buttons documented by Seeed: D1, D2, D4. On XIAO ESP32-S3
// these map to GPIO2, GPIO3, GPIO5 respectively.
constexpr int kRefreshButtonGpio = 2; // D1
constexpr int kPageButtonGpio = 3;    // D2
constexpr int kActionButtonGpio = 5;  // D4

// TRMNL board battery monitor: BAT_ADC on D0/GPIO1 and ADC enable GPIO6.
constexpr int kBatteryAdcGpio = 1;
constexpr int kBatteryAdcEnableGpio = 6;
constexpr float kBatteryCalibration = 0.968f;

// SPI/display pins are intentionally provisional until the panel controller
// and carrier board are confirmed.
constexpr int kEinkControllerBoardCombo = 502; // UC8179 / EE04

} // namespace meimad::hardware
