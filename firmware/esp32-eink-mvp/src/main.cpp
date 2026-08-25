#include <Arduino.h>
#include <esp_system.h>
#include <esp_sleep.h>
#include <esp_mac.h>
#include "hardware_config.h"

#ifndef MEIMAD_FIRMWARE_VERSION
#define MEIMAD_FIRMWARE_VERSION "0.1.0-mvp"
#endif

#ifndef MEIMAD_HARDWARE_PROFILE
#define MEIMAD_HARDWARE_PROFILE "unknown"
#endif

namespace {
constexpr uint32_t kSerialWaitMs = 1500;

void printMacAddress() {
  uint8_t mac[6]{};
  esp_read_mac(mac, ESP_MAC_WIFI_STA);
  Serial.printf("MAC address: %02X:%02X:%02X:%02X:%02X:%02X\n",
                mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

const char* resetReason(esp_reset_reason_t reason) {
  switch (reason) {
    case ESP_RST_POWERON: return "power-on";
    case ESP_RST_EXT: return "external";
    case ESP_RST_SW: return "software";
    case ESP_RST_PANIC: return "panic";
    case ESP_RST_INT_WDT: return "interrupt-watchdog";
    case ESP_RST_TASK_WDT: return "task-watchdog";
    case ESP_RST_WDT: return "other-watchdog";
    case ESP_RST_DEEPSLEEP: return "deep-sleep";
    case ESP_RST_BROWNOUT: return "brownout";
    case ESP_RST_SDIO: return "sdio";
    default: return "unknown";
  }
}

void printWakeReason() {
  const auto cause = esp_sleep_get_wakeup_cause();
  const char* text = "not-from-sleep";
  switch (cause) {
    case ESP_SLEEP_WAKEUP_EXT0: text = "external-rtc-0"; break;
    case ESP_SLEEP_WAKEUP_EXT1: text = "external-rtc-1"; break;
    case ESP_SLEEP_WAKEUP_TIMER: text = "timer"; break;
    case ESP_SLEEP_WAKEUP_TOUCHPAD: text = "touchpad"; break;
    case ESP_SLEEP_WAKEUP_ULP: text = "ulp"; break;
    case ESP_SLEEP_WAKEUP_GPIO: text = "gpio"; break;
    default: break;
  }
  Serial.printf("Wake-up reason: %s (%d)\n", text, static_cast<int>(cause));
}

void printBattery() {
  pinMode(meimad::hardware::kBatteryAdcEnableGpio, OUTPUT);
  digitalWrite(meimad::hardware::kBatteryAdcEnableGpio, HIGH);
  analogReadResolution(12);
  analogSetPinAttenuation(meimad::hardware::kBatteryAdcGpio, ADC_11db);
  delay(5);
  long sum = 0;
  for (int i = 0; i < 30; ++i) {
    sum += analogRead(meimad::hardware::kBatteryAdcGpio);
    delayMicroseconds(100);
  }
  const int raw = static_cast<int>(sum / 30);
  const float voltage = (raw / 4095.0f) * 3.6f * 2.0f
      * meimad::hardware::kBatteryCalibration;
  digitalWrite(meimad::hardware::kBatteryAdcEnableGpio, LOW);
  Serial.printf("Battery voltage: %.3f V (ADC raw %d, calibrated)\n", voltage, raw);
}

void configureWakeButtons() {
  pinMode(meimad::hardware::kRefreshButtonGpio, INPUT_PULLUP);
  pinMode(meimad::hardware::kPageButtonGpio, INPUT_PULLUP);
  pinMode(meimad::hardware::kActionButtonGpio, INPUT_PULLUP);
  // The actual S3 carrier must confirm RTC-capable routing before enabling
  // deep-sleep wake. The MVP keeps this opt-in to avoid unsafe pin guesses.
  Serial.printf("Buttons: refresh GPIO %d, page GPIO %d, action GPIO %d\n",
                meimad::hardware::kRefreshButtonGpio,
                meimad::hardware::kPageButtonGpio,
                meimad::hardware::kActionButtonGpio);
}
} // namespace

void setup() {
  Serial.begin(115200);
  const uint32_t started = millis();
  while (!Serial && millis() - started < kSerialWaitMs) delay(10);
  delay(50);
  Serial.println();
  Serial.println("=== Meimad Planner E-Ink Tablet MVP ===");
  Serial.printf("Firmware version: %s\n", MEIMAD_FIRMWARE_VERSION);
  Serial.printf("Hardware profile: %s\n", MEIMAD_HARDWARE_PROFILE);
  Serial.printf("MCU: %s\n", meimad::hardware::kMcuProfile);
  Serial.printf("Display: %s (%dx%d)\n", meimad::hardware::kDisplayProfile,
                meimad::hardware::kDisplayWidth, meimad::hardware::kDisplayHeight);
  Serial.printf("Chip: model=%s revision=%d cores=%d\n",
                ESP.getChipModel(), ESP.getChipRevision(), ESP.getChipCores());
  Serial.printf("Reset reason: %s\n", resetReason(esp_reset_reason()));
  printWakeReason();
  printMacAddress();
  configureWakeButtons();
  printBattery();
  Serial.printf("Display controller board combo: %d (UC8179)\n",
                meimad::hardware::kEinkControllerBoardCombo);
  Serial.println("MVP boot diagnostics complete; communication tasks are next.");
}

void loop() {
  static uint32_t lastLog = 0;
  if (millis() - lastLog >= 10000) {
    lastLog = millis();
    Serial.printf("Heartbeat: uptime=%lu s, refresh=%d, page=%d, action=%d\n",
                  millis() / 1000UL,
                  digitalRead(meimad::hardware::kRefreshButtonGpio) == LOW,
                  digitalRead(meimad::hardware::kPageButtonGpio) == LOW,
                  digitalRead(meimad::hardware::kActionButtonGpio) == LOW);
  }
  delay(25);
}
