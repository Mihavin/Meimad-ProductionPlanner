#include <Arduino.h>
#include <esp_system.h>
#include <esp_sleep.h>
#include <esp_mac.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <Preferences.h>
#include <ArduinoJson.h>
#include "hardware_config.h"
#include "device_config.h"

#if !MEIMAD_EINK_DRIVER_STUB
#include <TFT_eSPI.h>
EPaper epaper;
#endif

#ifndef MEIMAD_FIRMWARE_VERSION
#define MEIMAD_FIRMWARE_VERSION "0.1.0-mvp"
#endif

#ifndef MEIMAD_HARDWARE_PROFILE
#define MEIMAD_HARDWARE_PROFILE "unknown"
#endif

namespace {
constexpr uint32_t kSerialWaitMs = 1500;
constexpr uint32_t kWifiConnectTimeoutMs = 15000;
constexpr uint8_t kWifiMaximumAttempts = 3;
constexpr uint64_t kWifiFailureSleepUs = 60ULL * 1000ULL * 1000ULL;

struct DeviceConfiguration {
  String hardwareId;
  String tabletId;
  String wifiSsid;
  String wifiPassword;
  String serverBaseUrl;
};

String readHardwareId() {
  uint8_t mac[6]{};
  esp_read_mac(mac, ESP_MAC_WIFI_STA);
  char value[18];
  snprintf(value, sizeof(value), "%02X:%02X:%02X:%02X:%02X:%02X",
           mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
  return String(value);
}

DeviceConfiguration loadDeviceConfiguration() {
  Preferences preferences;
  preferences.begin("meimad", false);
  if (!preferences.isKey("wifi_ssid") && strlen(meimad::config::kDefaultWifiSsid) > 0)
    preferences.putString("wifi_ssid", meimad::config::kDefaultWifiSsid);
  if (!preferences.isKey("wifi_pass") && strlen(meimad::config::kDefaultWifiPassword) > 0)
    preferences.putString("wifi_pass", meimad::config::kDefaultWifiPassword);
  if (!preferences.isKey("server_url"))
    preferences.putString("server_url", meimad::config::kServerBaseUrl);
  if (!preferences.isKey("tablet_id") && strlen(meimad::config::kDefaultTabletId) > 0)
    preferences.putString("tablet_id", meimad::config::kDefaultTabletId);
  DeviceConfiguration value {
    readHardwareId(),
    preferences.getString("tablet_id", ""),
    preferences.getString("wifi_ssid", ""),
    preferences.getString("wifi_pass", ""),
    preferences.getString("server_url", meimad::config::kServerBaseUrl)
  };
  preferences.end();
  return value;
}

void cacheTabletId(const String& tabletId) {
  if (tabletId.isEmpty()) return;
  Preferences preferences;
  preferences.begin("meimad", false);
  preferences.putString("tablet_id", tabletId);
  preferences.end();
}

bool connectWifi(const DeviceConfiguration& configuration) {
  if (configuration.wifiSsid.isEmpty()) {
    Serial.println("Wi-Fi not configured: no SSID in NVS or device_config.h");
    return false;
  }
  WiFi.mode(WIFI_STA);
  for (uint8_t attempt = 1; attempt <= kWifiMaximumAttempts; ++attempt) {
    Serial.printf("Wi-Fi connect attempt %u/%u: %s\n", attempt, kWifiMaximumAttempts,
                  configuration.wifiSsid.c_str());
    WiFi.begin(configuration.wifiSsid.c_str(), configuration.wifiPassword.c_str());
    const uint32_t startedAt = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - startedAt < kWifiConnectTimeoutMs)
      delay(250);
    if (WiFi.status() == WL_CONNECTED) {
      Serial.printf("Wi-Fi connected: IP=%s gateway=%s RSSI=%d dBm\n",
                    WiFi.localIP().toString().c_str(),
                    WiFi.gatewayIP().toString().c_str(), WiFi.RSSI());
      return true;
    }
    WiFi.disconnect();
    delay(1000);
  }
  Serial.println("Wi-Fi connection failed after bounded retries.");
  return false;
}

bool testServer(const DeviceConfiguration& configuration, String& assignedTabletId) {
  if (configuration.serverBaseUrl.isEmpty()) return false;
  HTTPClient http;
  const String url = configuration.serverBaseUrl + "/api/tablet/ping?hardwareId=" + configuration.hardwareId;
  http.setConnectTimeout(5000);
  http.setTimeout(7000);
  if (!http.begin(url)) return false;
  const int status = http.GET();
  const String payload = status == HTTP_CODE_OK ? http.getString() : String();
  http.end();
  if (status != HTTP_CODE_OK) {
    Serial.printf("Server ping failed: HTTP %d\n", status);
    return false;
  }
  JsonDocument response;
  if (deserializeJson(response, payload) != DeserializationError::Ok
      || response["status"] != "ok") return false;
  assignedTabletId = response["tabletId"].as<String>();
  Serial.println("Server connectivity test: OK");
  return true;
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

void drawSmokeTestScreen(const DeviceConfiguration& configuration, bool serverConnected) {
#if !MEIMAD_EINK_DRIVER_STUB
  epaper.begin();
  epaper.fillScreen(TFT_WHITE);
  epaper.setTextColor(TFT_BLACK, TFT_WHITE);

  // Large heading, small metadata, and rules provide a repeatable panel QA
  // reference at the actual 800x480 screen geometry.
  epaper.setTextSize(4);
  epaper.drawString("MEIMAD PLANNER", 28, 24);
  epaper.setTextSize(1);
  epaper.drawString(configuration.tabletId.isEmpty()
                        ? "UNREGISTERED TABLET"
                        : "TABLET " + configuration.tabletId,
                    610, 10);
  epaper.drawFastHLine(28, 82, 744, TFT_BLACK);
  epaper.setTextSize(2);
  epaper.drawString("Machine: TEST MACHINE", 28, 108);
  epaper.drawString("Machine No: 10", 28, 140);
  if (configuration.tabletId.isEmpty()) {
    epaper.setTextSize(1);
    epaper.drawString("Hardware ID: " + configuration.hardwareId, 28, 176);
  } else {
    epaper.drawString("Hardware identity registered", 28, 172);
  }
  epaper.drawFastHLine(28, 208, 744, TFT_BLACK);

  epaper.setTextSize(3);
  epaper.drawString("STATUS", 28, 228);
  epaper.setTextSize(4);
  epaper.drawString(serverConnected ? "SERVER CONNECTED" : "SERVER NOT AVAILABLE", 28, 270);
  epaper.drawFastHLine(28, 330, 744, TFT_BLACK);

  epaper.setTextSize(2);
  epaper.drawString("Firmware: " MEIMAD_FIRMWARE_VERSION, 28, 350);
  epaper.drawString("Full refresh: measuring...", 28, 382);
  epaper.drawString("Partial refresh: testing...", 28, 414);
  epaper.drawString("Panel sleep retains this image", 28, 446);

  const uint32_t fullStartedAt = millis();
  epaper.update();
  const uint32_t fullRefreshMs = millis() - fullStartedAt;

  char fullDuration[48];
  snprintf(fullDuration, sizeof(fullDuration), "Full refresh: %lu ms", fullRefreshMs);
  epaper.fillRect(28, 380, 420, 26, TFT_WHITE);
  epaper.setTextSize(2);
  epaper.drawString(fullDuration, 28, 382);
  const uint32_t partialStartedAt = millis();
  epaper.updataPartial(28, 380, 440, 28);
  const uint32_t partialRefreshMs = millis() - partialStartedAt;

  char partialDuration[58];
  snprintf(partialDuration, sizeof(partialDuration),
           "Partial refresh: SUPPORTED (%lu ms)", partialRefreshMs);
  epaper.fillRect(28, 412, 600, 28, TFT_WHITE);
  epaper.drawString(partialDuration, 28, 414);
  epaper.updataPartial(28, 412, 640, 28);
  Serial.printf("E-Ink full refresh: %lu ms; partial refresh: %lu ms\n",
                fullRefreshMs, partialRefreshMs);
  Serial.println("E-Ink panel is sleeping; the smoke-test image remains visible through MCU sleep/reset.");
#endif
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
  const auto configuration = loadDeviceConfiguration();
  Serial.printf("Hardware ID (MAC): %s\n", configuration.hardwareId.c_str());
  Serial.printf("Tablet ID: %s\n", configuration.tabletId.isEmpty() ? "UNREGISTERED" : configuration.tabletId.c_str());
  configureWakeButtons();
  printBattery();
  Serial.printf("Display controller board combo: %d (UC8179)\n",
                meimad::hardware::kEinkControllerBoardCombo);
  auto activeConfiguration = configuration;
  const bool wifiConnected = connectWifi(activeConfiguration);
  String assignedTabletId;
  const bool serverConnected = wifiConnected && testServer(activeConfiguration, assignedTabletId);
  if (!assignedTabletId.isEmpty() && assignedTabletId != activeConfiguration.tabletId) {
    cacheTabletId(assignedTabletId);
    activeConfiguration.tabletId = assignedTabletId;
    Serial.printf("Server-assigned Tablet ID cached in NVS: %s\n", assignedTabletId.c_str());
  }
  drawSmokeTestScreen(activeConfiguration, serverConnected);
  if (!wifiConnected) {
    Serial.printf("Wi-Fi unavailable; entering deep sleep for %llu seconds.\n", kWifiFailureSleepUs / 1000000ULL);
    esp_sleep_enable_timer_wakeup(kWifiFailureSleepUs);
    delay(200);
    esp_deep_sleep_start();
  }
  Serial.println("MVP boot diagnostics complete.");
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
