#include <Arduino.h>
#include <esp_system.h>
#include <esp_sleep.h>
#include <esp_mac.h>
#include <driver/rtc_io.h>
#include <WiFi.h>
#include <HTTPClient.h>
#include <Preferences.h>
#include <ArduinoJson.h>
#include <time.h>
#include "hardware_config.h"
#include "device_config.h"
#include "tablet_api.h"
#include "production_ui.h"
#include "screen_revision.h"
#include "tablet_state_machine.h"
#include "button_input.h"
#include "demo_mode.h"
#include "firmware_logging.h"
#include "service_ui.h"

#if !MEIMAD_EINK_DRIVER_STUB
#include <TFT_eSPI.h>
EPaper epaper;
#endif

#ifndef MEIMAD_FIRMWARE_VERSION
#define MEIMAD_FIRMWARE_VERSION "0.1.1-mvp"
#endif

#ifndef MEIMAD_HARDWARE_PROFILE
#define MEIMAD_HARDWARE_PROFILE "unknown"
#endif

#ifndef MEIMAD_DEMO_MODE
#define MEIMAD_DEMO_MODE 0
#endif

#ifndef MEIMAD_PROVISIONING_BUILD
#define MEIMAD_PROVISIONING_BUILD 0
#endif

namespace {
constexpr uint32_t kSerialWaitMs = 1500;
constexpr uint32_t kWifiConnectTimeoutMs = 15000;
constexpr uint8_t kWifiMaximumAttempts = 3;
constexpr char kPreferencesNamespace[] = "meimad";
constexpr char kLastRevisionKey[] = "last_revision";
constexpr char kLastRevisionTabletKey[] = "last_rev_tab";
constexpr char kLastDisplayedStatusKey[] = "last_status";
constexpr char kVerificationFailSafeKey[] = "verify_block";
constexpr char kLastContactKey[] = "last_contact";
constexpr char kLastHttpResultKey[] = "last_http";
constexpr char kLastRefreshDurationKey[] = "last_refresh";
constexpr char kLastMachineBindingKey[] = "last_machine";
constexpr char kLastVerificationResultKey[] = "last_verify";
constexpr char kLastMacroVersionKey[] = "last_macro";
constexpr char kServiceScreenActiveKey[] = "service_active";
constexpr char kToolPageKey[] = "tool_page";
constexpr char kConfirmationPendingKey[] = "confirm_clear";
constexpr char kBatteryLowKey[] = "battery_low";
constexpr char kDemoScenarioKey[] = "demo_scene";
constexpr uint32_t kRetainedSleepStateMagic = 0x4D534C50;
constexpr time_t kMinimumValidWakeTime = 1704067200;  // 2024-01-01T00:00:00Z
constexpr float kLowBatteryThresholdVolts = 3.30f;

RTC_DATA_ATTR uint32_t gRetainedSleepStateMagic;
RTC_DATA_ATTR int32_t gRetainedTabletStatus;
RTC_DATA_ATTR uint8_t gRetainedServerStateAvailable;
RTC_DATA_ATTR uint8_t gRetainedButtonWakeEnabled;
RTC_DATA_ATTR uint8_t gRetainedTimerWakeEnabled;
RTC_DATA_ATTR uint32_t gRetainedTimerSeconds;

struct DeviceConfiguration {
  String hardwareId;
  String tabletId;
  String wifiSsid;
  String wifiPassword;
  String serverBaseUrl;
};

struct PreviousSleepState {
  bool available = false;
  meimad::tablet_api::TabletStatus status =
      meimad::tablet_api::TabletStatus::Unknown;
  bool serverStateAvailable = false;
  bool buttonWakeEnabled = false;
  bool timerWakeEnabled = false;
  uint32_t timerSeconds = 0;
};

String readHardwareId() {
  uint8_t mac[6]{};
  esp_read_mac(mac, ESP_MAC_WIFI_STA);
  char value[18];
  snprintf(value, sizeof(value), "%02X:%02X:%02X:%02X:%02X:%02X",
           mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
  return String(value);
}

String safeServerAddress(const String& value) {
  const int scheme = value.indexOf("://");
  const int authorityStart = scheme < 0 ? 0 : scheme + 3;
  const int pathStart = value.indexOf('/', authorityStart);
  const int at = value.indexOf('@', authorityStart);
  if (at < 0 || (pathStart >= 0 && at > pathStart)) return value;
  return value.substring(0, authorityStart) + value.substring(at + 1);
}

DeviceConfiguration loadDeviceConfiguration() {
  Preferences preferences;
  preferences.begin(kPreferencesNamespace, false);
  if (!preferences.isKey("wifi_ssid") && strlen(meimad::config::kDefaultWifiSsid) > 0)
    preferences.putString("wifi_ssid", meimad::config::kDefaultWifiSsid);
  if (!preferences.isKey("wifi_pass") && strlen(meimad::config::kDefaultWifiPassword) > 0)
    preferences.putString("wifi_pass", meimad::config::kDefaultWifiPassword);
  if (MEIMAD_PROVISIONING_BUILD
      && preferences.getString("server_url", "") != meimad::config::kServerBaseUrl)
    preferences.putString("server_url", meimad::config::kServerBaseUrl);
  else if (!preferences.isKey("server_url"))
    preferences.putString("server_url", meimad::config::kServerBaseUrl);
  if (MEIMAD_PROVISIONING_BUILD
      && preferences.getString("tablet_id", "") != meimad::config::kDefaultTabletId)
    preferences.putString("tablet_id", meimad::config::kDefaultTabletId);
  else if (!preferences.isKey("tablet_id") && strlen(meimad::config::kDefaultTabletId) > 0)
    preferences.putString("tablet_id", meimad::config::kDefaultTabletId);
  if (preferences.isKey("device_token")) preferences.remove("device_token");
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

meimad::screen_revision::LastRevision loadLastRevision() {
  meimad::screen_revision::LastRevision result;
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) {
    Serial.println("Screen revision NVS read failed; refresh will be required.");
    return result;
  }
  result.available = preferences.isKey(kLastRevisionKey)
      && preferences.isKey(kLastRevisionTabletKey);
  if (result.available) {
    result.revision = preferences.getULong(kLastRevisionKey, 0);
    result.tabletId = preferences.getString(kLastRevisionTabletKey, "");
    result.available = !result.tabletId.isEmpty();
  }
  preferences.end();
  return result;
}

bool saveLastRevision(const String& tabletId, uint32_t revision) {
  if (tabletId.isEmpty()) return false;
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) {
    Serial.println("Screen revision NVS write failed: namespace unavailable.");
    return false;
  }
  // Revision is written first. If power fails before the tablet identity is
  // updated, an assignment mismatch forces another safe refresh on next boot.
  const bool revisionSaved =
      preferences.putULong(kLastRevisionKey, revision) == sizeof(uint32_t);
  const bool tabletSaved = revisionSaved
      && preferences.putString(kLastRevisionTabletKey, tabletId)
          == tabletId.length();
  preferences.end();
  if (!revisionSaved || !tabletSaved) {
    Serial.println("Screen revision NVS write failed; next boot will refresh safely.");
    return false;
  }
  MEIMAD_LOG(
      "DISPLAY", "revision saved revision=%lu tablet_id=%s",
      static_cast<unsigned long>(revision),
      tabletId.c_str());
  return true;
}

String loadLastDisplayedStatus() {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return String();
  const String status = preferences.getString(kLastDisplayedStatusKey, "");
  preferences.end();
  return status;
}

void saveLastDisplayedStatus(meimad::tablet_api::TabletStatus status) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) return;
  preferences.putString(
      kLastDisplayedStatusKey,
      meimad::tablet_api::toToken(status));
  preferences.end();
}

bool loadVerificationFailSafe() {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return false;
  const bool active = preferences.getBool(kVerificationFailSafeKey, false);
  preferences.end();
  return active;
}

void saveVerificationFailSafe(bool active) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) return;
  preferences.putBool(kVerificationFailSafeKey, active);
  preferences.end();
}

bool loadServiceScreenActive() {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return false;
  const bool active = preferences.getBool(kServiceScreenActiveKey, false);
  preferences.end();
  return active;
}

void saveServiceScreenActive(bool active) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) return;
  preferences.putBool(kServiceScreenActiveKey, active);
  preferences.end();
}

String loadDiagnosticText(const char* key) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return String();
  const String value = preferences.getString(key, "");
  preferences.end();
  return value;
}

void saveDiagnosticText(const char* key, const String& value) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) return;
  preferences.putString(key, value);
  preferences.end();
}

uint32_t loadLastRefreshDuration() {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return 0;
  const uint32_t duration = preferences.getULong(kLastRefreshDurationKey, 0);
  preferences.end();
  return duration;
}

void saveLastRefreshDuration(uint32_t duration) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) return;
  preferences.putULong(kLastRefreshDurationKey, duration);
  preferences.end();
}

uint8_t loadToolPage() {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return 0;
  const uint8_t page = preferences.getUChar(kToolPageKey, 0);
  preferences.end();
  return page;
}

void saveToolPage(uint8_t page) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) {
    Serial.println("Tool-page NVS write failed.");
    return;
  }
  if (!preferences.isKey(kToolPageKey)
      || preferences.getUChar(kToolPageKey, 0) != page) {
    preferences.putUChar(kToolPageKey, page);
  }
  preferences.end();
}

bool loadConfirmationPending() {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return false;
  const bool pending = preferences.getBool(kConfirmationPendingKey, false);
  preferences.end();
  return pending;
}

bool loadBatteryLowWarning(bool& available) {
  Preferences preferences;
  available = false;
  if (!preferences.begin(kPreferencesNamespace, true)) return false;
  available = preferences.isKey(kBatteryLowKey);
  const bool low = available && preferences.getBool(kBatteryLowKey, false);
  preferences.end();
  return low;
}

void saveBatteryLowWarning(bool low) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) {
    Serial.println("Battery-warning NVS write failed.");
    return;
  }
  if (!preferences.isKey(kBatteryLowKey)
      || preferences.getBool(kBatteryLowKey, false) != low) {
    preferences.putBool(kBatteryLowKey, low);
  }
  preferences.end();
}

void saveConfirmationPending(bool pending) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) {
    Serial.println("Confirmation-state NVS write failed.");
    return;
  }
  preferences.putBool(kConfirmationPendingKey, pending);
  preferences.end();
}

uint8_t loadDemoScenarioIndex() {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, true)) return 0;
  const uint8_t index = preferences.getUChar(kDemoScenarioKey, 0);
  preferences.end();
  return index % meimad::demo_mode::kScenarioCount;
}

void saveDemoScenarioIndex(uint8_t index) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) {
    Serial.println("Demo-scenario NVS write failed.");
    return;
  }
  preferences.putUChar(
      kDemoScenarioKey,
      index % meimad::demo_mode::kScenarioCount);
  preferences.end();
}

bool connectWifi(const DeviceConfiguration& configuration) {
  if (configuration.wifiSsid.isEmpty()) {
    MEIMAD_LOG("WIFI", "configuration_missing=true");
    return false;
  }
  WiFi.mode(WIFI_STA);
  for (uint8_t attempt = 1; attempt <= kWifiMaximumAttempts; ++attempt) {
    MEIMAD_LOG("WIFI", "connecting attempt=%u/%u ssid=%s", attempt, kWifiMaximumAttempts,
                  configuration.wifiSsid.c_str());
    WiFi.begin(configuration.wifiSsid.c_str(), configuration.wifiPassword.c_str());
    const uint32_t startedAt = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - startedAt < kWifiConnectTimeoutMs)
      delay(250);
    if (WiFi.status() == WL_CONNECTED) {
      MEIMAD_LOG("WIFI", "connected ip=%s gateway=%s rssi_dbm=%d",
                    WiFi.localIP().toString().c_str(),
                    WiFi.gatewayIP().toString().c_str(), WiFi.RSSI());
      return true;
    }
    WiFi.disconnect();
    delay(1000);
  }
  MEIMAD_LOG("WIFI", "connection_failed attempts=%u", kWifiMaximumAttempts);
  return false;
}

bool testServer(
    const DeviceConfiguration& configuration,
    const meimad::tablet_api::BatteryTelemetry& batteryTelemetry,
    String& assignedTabletId,
    String& diagnosticResult) {
  if (configuration.serverBaseUrl.isEmpty()) {
    diagnosticResult = "PING NOT CONFIGURED";
    return false;
  }
  HTTPClient http;
  const String url = configuration.serverBaseUrl + "/api/tablet/ping?hardwareId=" + configuration.hardwareId;
  http.setConnectTimeout(5000);
  http.setTimeout(7000);
  if (!http.begin(url)) {
    diagnosticResult = "PING INIT FAILED";
    return false;
  }
  const String batteryVoltage =
      meimad::tablet_api::formatBatteryVoltageHeader(batteryTelemetry);
  if (!batteryVoltage.isEmpty()) {
    http.addHeader("X-Meimad-Battery-Voltage", batteryVoltage);
  }
  if (batteryTelemetry.percentAvailable && batteryTelemetry.percent <= 100) {
    http.addHeader(
        "X-Meimad-Battery-Percent",
        String(batteryTelemetry.percent));
  }
  http.addHeader("X-Meimad-Firmware-Version", MEIMAD_FIRMWARE_VERSION);
  if (WiFi.status() == WL_CONNECTED) {
    http.addHeader("X-Meimad-Wifi-IP", WiFi.localIP().toString());
    http.addHeader("X-Meimad-Wifi-Rssi", String(WiFi.RSSI()));
  }
  const int status = http.GET();
  const String payload = status == HTTP_CODE_OK ? http.getString() : String();
  http.end();
  if (status != HTTP_CODE_OK) {
    diagnosticResult = "PING HTTP " + String(status);
    MEIMAD_LOG("API", "GET /api/tablet/ping response=%d", status);
    return false;
  }
  JsonDocument response;
  if (deserializeJson(response, payload) != DeserializationError::Ok
      || response["status"] != "ok") {
    diagnosticResult = "PING MALFORMED";
    return false;
  }
  assignedTabletId = response["tabletId"].as<String>();
  MEIMAD_LOG("API", "GET /api/tablet/ping response=200");
  diagnosticResult = "PING HTTP 200";
  return true;
}

bool requestTabletStatus(
    const meimad::tablet_api::TabletApiClient& tabletApi,
    const String& tabletId,
    const char* requestReason,
    meimad::tablet_api::TabletStatusResponse& tabletStatus,
    meimad::tablet_api::ApiResult* diagnosticResult = nullptr) {
  MEIMAD_LOG("API", "GET /api/tablets/%s/status reason=%s", tabletId.c_str(), requestReason);
  const auto result = tabletApi.getStatus(tabletId, tabletStatus);
  if (diagnosticResult != nullptr) *diagnosticResult = result;
  if (!result.succeeded()) {
    MEIMAD_LOG(
        "API", "GET status failed code=%s%s%s",
        meimad::tablet_api::toText(result.code),
        result.detail.isEmpty() ? "" : " - ",
        result.detail.c_str());
    return false;
  }
  MEIMAD_LOG(
      "API", "GET status response=200 revision=%lu machine=%s part=%s operation=%ld status=%s",
      static_cast<unsigned long>(tabletStatus.revision),
      tabletStatus.machine.name.c_str(),
      tabletStatus.part.number.c_str(),
      static_cast<long>(tabletStatus.operation.number),
      meimad::tablet_api::toToken(tabletStatus.status));
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

const char* wakeReasonText(esp_sleep_wakeup_cause_t cause) {
  const char* text = "not-from-sleep";
  switch (cause) {
    case ESP_SLEEP_WAKEUP_EXT0: text = "external-rtc-0"; break;
    case ESP_SLEEP_WAKEUP_EXT1: text = "physical-button-ext1"; break;
    case ESP_SLEEP_WAKEUP_TIMER: text = "timer"; break;
    case ESP_SLEEP_WAKEUP_TOUCHPAD: text = "touchpad"; break;
    case ESP_SLEEP_WAKEUP_ULP: text = "ulp"; break;
    case ESP_SLEEP_WAKEUP_GPIO: text = "gpio"; break;
    default: break;
  }
  return text;
}

void printWakeReason() {
  const auto cause = esp_sleep_get_wakeup_cause();
  const char* text = wakeReasonText(cause);
  MEIMAD_LOG("WAKE", "reason=%s code=%d", text, static_cast<int>(cause));
}

String currentUtcContactText() {
  const time_t now = time(nullptr);
  if (now < kMinimumValidWakeTime) return "SUCCESS - UTC UNAVAILABLE";
  struct tm utcTime {};
  if (gmtime_r(&now, &utcTime) == nullptr) return "SUCCESS - UTC UNAVAILABLE";
  char timestamp[25]{};
  if (strftime(timestamp, sizeof(timestamp), "%Y-%m-%d %H:%M:%SZ", &utcTime) == 0) {
    return "SUCCESS - UTC UNAVAILABLE";
  }
  return String(timestamp);
}

String apiDiagnosticText(
    const char* operation,
    const meimad::tablet_api::ApiResult& result) {
  if (result.succeeded()) return String(operation) + " HTTP " + String(result.httpStatus);
  String value = String(operation) + " " + meimad::tablet_api::toText(result.code);
  if (result.httpStatus != 0) value += " " + String(result.httpStatus);
  return value;
}

void printWakeTimestamp() {
  const time_t now = time(nullptr);
  if (now < kMinimumValidWakeTime) {
    Serial.println("Wake timestamp (UTC): unavailable; clock is not synchronized.");
    return;
  }
  struct tm utcTime {};
  if (gmtime_r(&now, &utcTime) == nullptr) {
    Serial.println("Wake timestamp (UTC): unavailable; UTC conversion failed.");
    return;
  }
  char timestamp[25]{};
  if (strftime(
          timestamp,
          sizeof(timestamp),
          "%Y-%m-%dT%H:%M:%SZ",
          &utcTime) == 0) {
    Serial.println("Wake timestamp (UTC): unavailable; formatting failed.");
    return;
  }
  Serial.printf("Wake timestamp (UTC): %s\n", timestamp);
}

PreviousSleepState loadPreviousSleepState() {
  PreviousSleepState state;
  const int32_t minimumStatus =
      static_cast<int32_t>(meimad::tablet_api::TabletStatus::ReadyForSetup);
  const int32_t maximumStatus =
      static_cast<int32_t>(meimad::tablet_api::TabletStatus::Unknown);
  if (esp_reset_reason() != ESP_RST_DEEPSLEEP
      || gRetainedSleepStateMagic != kRetainedSleepStateMagic
      || gRetainedTabletStatus < minimumStatus
      || gRetainedTabletStatus > maximumStatus) {
    return state;
  }
  state.available = true;
  state.status = static_cast<meimad::tablet_api::TabletStatus>(
      gRetainedTabletStatus);
  state.serverStateAvailable = gRetainedServerStateAvailable != 0;
  state.buttonWakeEnabled = gRetainedButtonWakeEnabled != 0;
  state.timerWakeEnabled = gRetainedTimerWakeEnabled != 0;
  state.timerSeconds = gRetainedTimerSeconds;
  return state;
}

void printPreviousSleepState(const PreviousSleepState& state) {
  if (!state.available) {
    Serial.println("State before sleep: unavailable; this is not a retained deep-sleep wake.");
    return;
  }
  Serial.printf(
      "State before sleep: status=%s source=%s button_wake=%s timer_wake=%s timer_seconds=%lu\n",
      meimad::tablet_api::toToken(state.status),
      state.serverStateAvailable ? "server" : "fallback",
      state.buttonWakeEnabled ? "enabled" : "unavailable",
      state.timerWakeEnabled ? "enabled" : "disabled",
      static_cast<unsigned long>(state.timerSeconds));
}

void retainStateBeforeSleep(
    meimad::tablet_api::TabletStatus status,
    bool serverStateAvailable,
    bool buttonWakeEnabled,
    bool timerWakeEnabled,
    uint32_t timerSeconds) {
  gRetainedSleepStateMagic = 0;
  gRetainedTabletStatus = static_cast<int32_t>(status);
  gRetainedServerStateAvailable = serverStateAvailable ? 1 : 0;
  gRetainedButtonWakeEnabled = buttonWakeEnabled ? 1 : 0;
  gRetainedTimerWakeEnabled = timerWakeEnabled ? 1 : 0;
  gRetainedTimerSeconds = timerWakeEnabled ? timerSeconds : 0;
  gRetainedSleepStateMagic = kRetainedSleepStateMagic;
}

void disableWifiForIdle(const char* reason) {
  const wifi_mode_t previousMode = WiFi.getMode();
  WiFi.disconnect(true, false);
  WiFi.mode(WIFI_OFF);
  MEIMAD_LOG(
      "WIFI", "disabled reason=%s previous_mode=%d current_mode=%d",
      reason,
      static_cast<int>(previousMode),
      static_cast<int>(WiFi.getMode()));
}

meimad::tablet_api::BatteryTelemetry sampleBatteryTelemetry() {
  meimad::tablet_api::BatteryTelemetry telemetry;
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
  telemetry.voltageAvailable = voltage > 0.0f;
  telemetry.voltage = voltage;
  MEIMAD_LOG("BATTERY", "voltage=%.3f raw=%d calibrated=true", voltage, raw);
  MEIMAD_LOG("BATTERY", "percent_available=false");
  return telemetry;
}

void configureWakeButtons() {
  pinMode(meimad::hardware::kRefreshButtonGpio, INPUT_PULLUP);
  pinMode(meimad::hardware::kPageButtonGpio, INPUT_PULLUP);
  pinMode(meimad::hardware::kActionButtonGpio, INPUT_PULLUP);
  Serial.printf("Buttons: refresh GPIO %d, page GPIO %d, action GPIO %d\n",
                meimad::hardware::kRefreshButtonGpio,
                meimad::hardware::kPageButtonGpio,
                meimad::hardware::kActionButtonGpio);
}

bool enablePhysicalButtonWake() {
  const gpio_num_t buttonPins[] = {
      static_cast<gpio_num_t>(meimad::hardware::kRefreshButtonGpio),
      static_cast<gpio_num_t>(meimad::hardware::kPageButtonGpio),
      static_cast<gpio_num_t>(meimad::hardware::kActionButtonGpio)};
  for (const gpio_num_t pin : buttonPins) {
    if (!esp_sleep_is_valid_wakeup_gpio(pin)) {
      Serial.printf("GPIO %d is not a valid deep-sleep wake pin.\n", pin);
      return false;
    }
    if (rtc_gpio_pullup_en(pin) != ESP_OK
        || rtc_gpio_pulldown_dis(pin) != ESP_OK) {
      Serial.printf("GPIO %d RTC pull-up configuration failed.\n", pin);
      return false;
    }
  }
  // Keep RTC peripherals powered so the internal pull-ups remain active for
  // the board's active-low buttons. Deep-sleep current needs bench measurement.
  const esp_err_t powerResult =
      esp_sleep_pd_config(ESP_PD_DOMAIN_RTC_PERIPH, ESP_PD_OPTION_ON);
  if (powerResult != ESP_OK) {
    Serial.printf(
        "RTC peripheral sleep-power configuration failed: error=%d\n",
        static_cast<int>(powerResult));
    return false;
  }
  const uint64_t buttonMask =
      (1ULL << meimad::hardware::kRefreshButtonGpio)
      | (1ULL << meimad::hardware::kPageButtonGpio)
      | (1ULL << meimad::hardware::kActionButtonGpio);
  const esp_err_t result = esp_sleep_enable_ext1_wakeup(
      buttonMask,
      ESP_EXT1_WAKEUP_ANY_LOW);
  if (result != ESP_OK) {
    Serial.printf(
        "Physical-button deep-sleep wake configuration failed: error=%d\n",
        static_cast<int>(result));
    return false;
  }
  Serial.printf(
      "Physical-button deep-sleep wake enabled: GPIO mask=0x%llX active-low\n",
      static_cast<unsigned long long>(buttonMask));
  return true;
}

void enterStateSleep(
    const meimad::tablet_state_machine::StatePolicy& policy,
    meimad::tablet_api::TabletStatus status,
    bool serverStateAvailable) {
  esp_sleep_disable_wakeup_source(ESP_SLEEP_WAKEUP_ALL);
  const bool buttonWakeEnabled = enablePhysicalButtonWake();
  bool timerWakeEnabled =
      policy.wakeMode == meimad::tablet_state_machine::WakeMode::PollServer;
  uint32_t timerSeconds = policy.pollIntervalSeconds;
  if (!buttonWakeEnabled && !timerWakeEnabled) {
    timerWakeEnabled = true;
    timerSeconds = meimad::tablet_state_machine::kInitialPollIntervalSeconds;
    Serial.println(
        "Button-only wake unavailable; enabling 120-second safety wake.");
  }
  if (timerWakeEnabled) {
    const esp_err_t timerResult = esp_sleep_enable_timer_wakeup(
        static_cast<uint64_t>(timerSeconds) * 1000ULL * 1000ULL);
    if (timerResult != ESP_OK) {
      timerWakeEnabled = false;
      Serial.printf(
          "Deep-sleep timer configuration failed: error=%d\n",
          static_cast<int>(timerResult));
    }
  }
  if (!buttonWakeEnabled && !timerWakeEnabled) {
    disableWifiForIdle("no-wake-source safety state");
    Serial.println(
        "No deep-sleep wake source is available; staying awake for service.");
    return;
  }

  Serial.printf(
      "Tablet state policy: status=%s source=%s wake=%s button_wake=%s poll_seconds=%lu%s reason=%s\n",
      meimad::tablet_api::toToken(status),
      serverStateAvailable ? "server" : "fallback",
      meimad::tablet_state_machine::toText(policy.wakeMode),
      buttonWakeEnabled ? "enabled" : "unavailable",
      static_cast<unsigned long>(timerWakeEnabled ? timerSeconds : 0),
      policy.fallbackPolicy ? " fallback=true" : "",
      policy.reason);
  Serial.printf(
      "State before sleep: status=%s source=%s button_wake=%s timer_wake=%s timer_seconds=%lu\n",
      meimad::tablet_api::toToken(status),
      serverStateAvailable ? "server" : "fallback",
      buttonWakeEnabled ? "enabled" : "unavailable",
      timerWakeEnabled ? "enabled" : "disabled",
      static_cast<unsigned long>(timerWakeEnabled ? timerSeconds : 0));
  retainStateBeforeSleep(
      status,
      serverStateAvailable,
      buttonWakeEnabled,
      timerWakeEnabled,
      timerSeconds);
  disableWifiForIdle("deep sleep");
  MEIMAD_LOG("SLEEP", "entering deep_sleep=true retained_content=true");
  meimad::logging::flush();
  delay(50);
  esp_deep_sleep_start();
}

uint32_t drawProductionLayout(
    const meimad::production_ui::ProductionScreenModel& screen,
    bool developmentFixture,
    const char* source,
    uint32_t revision,
    uint8_t toolPage) {
#if !MEIMAD_EINK_DRIVER_STUB
  meimad::production_ui::drawProductionScreen(
      epaper, screen, toolPage, developmentFixture);
  const uint32_t refreshStartedAt = millis();
  epaper.update();
  const uint32_t refreshDurationMs = millis() - refreshStartedAt;
  MEIMAD_LOG(
      "DISPLAY", "refresh completed source=%s revision=%lu duration_ms=%lu",
      source,
      static_cast<unsigned long>(revision),
      static_cast<unsigned long>(refreshDurationMs));
  MEIMAD_LOG("DISPLAY", "panel_sleeping=true retained_content=true");
  return refreshDurationMs;
#else
  return 0;
#endif
}

uint32_t drawServiceLayout(
    const meimad::service_ui::ServiceScreenModel& screen) {
#if !MEIMAD_EINK_DRIVER_STUB
  meimad::service_ui::drawServiceScreen(epaper, screen);
  const uint32_t refreshStartedAt = millis();
  epaper.update();
  const uint32_t refreshDurationMs = millis() - refreshStartedAt;
  MEIMAD_LOG(
      "DISPLAY",
      "refresh completed source=service-screen duration_ms=%lu",
      static_cast<unsigned long>(refreshDurationMs));
  return refreshDurationMs;
#else
  return 0;
#endif
}

#if MEIMAD_DEMO_MODE
void runCompileTimeDemo(
    meimad::button_input::ButtonAction action,
    const meimad::tablet_api::BatteryTelemetry& batteryTelemetry) {
  uint8_t scenarioIndex = loadDemoScenarioIndex();
  if (action == meimad::button_input::ButtonAction::Refresh
      || action == meimad::button_input::ButtonAction::SendToQc) {
    scenarioIndex = meimad::demo_mode::nextScenarioIndex(scenarioIndex);
    saveDemoScenarioIndex(scenarioIndex);
    Serial.printf("Demo scenario advanced: index=%u\n", scenarioIndex);
  }

  const auto scenario = meimad::demo_mode::scenarioForIndex(scenarioIndex);
  auto screen = meimad::demo_mode::makeScreen(scenario);
  screen.lowBattery = screen.lowBattery
      || (meimad::tablet_api::hasValidBatteryVoltage(batteryTelemetry)
          && batteryTelemetry.voltage <= kLowBatteryThresholdVolts);

  uint8_t toolPage = loadToolPage();
  if (action == meimad::button_input::ButtonAction::PreviousToolPage) {
    toolPage = meimad::production_ui::previousToolPage(toolPage, screen.toolCount);
  } else if (action == meimad::button_input::ButtonAction::NextToolPage) {
    toolPage = meimad::production_ui::nextToolPage(toolPage, screen.toolCount);
  }
  toolPage = meimad::production_ui::normalizedToolPage(toolPage, screen.toolCount);

  Serial.printf(
      "Compile-time demo: scenario=%s index=%u; Wi-Fi and Server calls are disabled.\n",
      meimad::demo_mode::scenarioName(scenario),
      scenarioIndex);
  Serial.println(
      "Demo controls: short-D1/long-D4 next scenario; long-D1 service; "
      "D2 previous page; short-D4 next page.");
  drawProductionLayout(screen, true, "compile-time-demo", scenarioIndex, toolPage);
  saveServiceScreenActive(false);
  saveToolPage(toolPage);
  saveBatteryLowWarning(screen.lowBattery);
  disableWifiForIdle("compile-time demo");
  const auto policy = meimad::tablet_state_machine::policyFor(screen.status);
  enterStateSleep(policy, screen.status, false);
}
#endif
} // namespace

void setup() {
  const auto wakeButton = meimad::button_input::captureWakeButtonEvent();
  const auto previousSleepState = loadPreviousSleepState();
  const auto wakeCause = esp_sleep_get_wakeup_cause();
  const bool physicalButtonWake = wakeCause == ESP_SLEEP_WAKEUP_EXT1;
  const bool serviceScreenRequested =
      wakeButton.action == meimad::button_input::ButtonAction::ServiceScreen;
  const bool serverContactRequired =
      meimad::button_input::requiresServerContact(
          physicalButtonWake, wakeButton.action);
  Serial.begin(115200);
  const uint32_t started = millis();
  while (!Serial && millis() - started < kSerialWaitMs) delay(10);
  delay(50);
  MEIMAD_LOG("BOOT", "firmware=%s", MEIMAD_FIRMWARE_VERSION);
  MEIMAD_LOG("BOOT", "hardware_profile=%s", MEIMAD_HARDWARE_PROFILE);
  MEIMAD_LOG("BOOT", "mcu=%s", meimad::hardware::kMcuProfile);
  MEIMAD_LOG("BOOT", "display=%s width=%d height=%d", meimad::hardware::kDisplayProfile,
                meimad::hardware::kDisplayWidth, meimad::hardware::kDisplayHeight);
  MEIMAD_LOG("BOOT", "chip_model=%s chip_revision=%d cores=%d",
                ESP.getChipModel(), ESP.getChipRevision(), ESP.getChipCores());
  MEIMAD_LOG("BOOT", "reset_reason=%s", resetReason(esp_reset_reason()));
  printWakeReason();
  printWakeTimestamp();
  printPreviousSleepState(previousSleepState);
  if (wakeButton.ambiguous) {
    MEIMAD_LOG(
        "BUTTON", "ignored reason=ambiguous mask=0x%llX",
        static_cast<unsigned long long>(wakeButton.wakeMask));
  } else if (!wakeButton.released) {
    MEIMAD_LOG(
        "BUTTON", "ignored reason=not_released mask=0x%llX",
        static_cast<unsigned long long>(wakeButton.wakeMask));
  } else if (wakeButton.wakeMask != 0
      && wakeButton.action == meimad::button_input::ButtonAction::None) {
    MEIMAD_LOG(
        "BUTTON", "ignored reason=debounce mask=0x%llX",
        static_cast<unsigned long long>(wakeButton.wakeMask));
  } else if (wakeButton.action != meimad::button_input::ButtonAction::None) {
    MEIMAD_LOG(
        "BUTTON", "action=%s mask=0x%llX held_ms=%lu",
        meimad::button_input::toText(wakeButton.action),
        static_cast<unsigned long long>(wakeButton.wakeMask),
        static_cast<unsigned long>(wakeButton.heldMilliseconds));
  }
  const auto configuration = loadDeviceConfiguration();
  MEIMAD_LOG("BOOT", "tablet_mac=%s", configuration.hardwareId.c_str());
  MEIMAD_LOG("BOOT", "tablet_id=%s", configuration.tabletId.isEmpty() ? "UNREGISTERED" : configuration.tabletId.c_str());
  configureWakeButtons();
  const auto batteryTelemetry = sampleBatteryTelemetry();
  const bool lowBattery =
      meimad::tablet_api::hasValidBatteryVoltage(batteryTelemetry)
      && batteryTelemetry.voltage <= kLowBatteryThresholdVolts;
  if (lowBattery) {
    MEIMAD_LOG(
        "BATTERY", "low=true voltage=%.3f threshold=%.2f",
        batteryTelemetry.voltage,
        kLowBatteryThresholdVolts);
  }
  Serial.printf("Display controller board combo: %d (UC8179)\n",
                meimad::hardware::kEinkControllerBoardCombo);
#if MEIMAD_DEMO_MODE
  if (serviceScreenRequested) {
    meimad::service_ui::ServiceScreenModel serviceScreen;
    serviceScreen.tabletId = configuration.tabletId.isEmpty()
        ? "UNREGISTERED"
        : configuration.tabletId;
    serviceScreen.hardwareMac = configuration.hardwareId;
    serviceScreen.firmwareVersion = MEIMAD_FIRMWARE_VERSION;
    serviceScreen.machineBinding = "DEMO MODE";
    serviceScreen.wifiSsid = configuration.wifiSsid;
    serviceScreen.serverAddress = safeServerAddress(configuration.serverBaseUrl);
    serviceScreen.lastSuccessfulContact = "DEMO - NO NETWORK";
    serviceScreen.lastHttpResult = "DEMO - NO HTTP";
    serviceScreen.workflowState = "DEMO";
    serviceScreen.revision = "DEMO";
    const String voltage =
        meimad::tablet_api::formatBatteryVoltageHeader(batteryTelemetry);
    serviceScreen.batteryVoltage = voltage.isEmpty()
        ? "UNAVAILABLE"
        : voltage + " V";
    serviceScreen.wakeReason = wakeReasonText(wakeCause);
    const uint32_t lastRefreshDuration = loadLastRefreshDuration();
    serviceScreen.lastRefreshDuration = lastRefreshDuration == 0
        ? "NOT RECORDED"
        : String(lastRefreshDuration) + " ms";
    serviceScreen.verificationResult = "NOT REPORTED";
    serviceScreen.protectedMacroVersion = "NOT REPORTED";
    meimad::service_ui::logServiceDiagnostics(serviceScreen);
    saveLastRefreshDuration(drawServiceLayout(serviceScreen));
    saveServiceScreenActive(true);
    enterStateSleep(
        meimad::tablet_state_machine::policyFor(
            meimad::tablet_api::TabletStatus::Unknown),
        meimad::tablet_api::TabletStatus::Unknown,
        false);
    return;
  }
  runCompileTimeDemo(wakeButton.action, batteryTelemetry);
  return;
#endif
  auto activeConfiguration = configuration;
  Serial.printf(
      "Wake network policy: server_contact=%s action=%s\n",
      serverContactRequired ? "required" : "skipped",
      meimad::button_input::toText(wakeButton.action));
  bool wifiConnected = false;
  String assignedTabletId;
  bool serverConnected = false;
  String lastHttpResult = loadDiagnosticText(kLastHttpResultKey);
  String lastSuccessfulContact = loadDiagnosticText(kLastContactKey);
  String wifiIp;
  String wifiRssi;
  if (serverContactRequired) {
    wifiConnected = connectWifi(activeConfiguration);
    if (wifiConnected) {
      wifiIp = WiFi.localIP().toString();
      wifiRssi = String(WiFi.RSSI()) + " dBm";
    } else {
      lastHttpResult = "WI-FI CONNECTION FAILED";
    }
    serverConnected = wifiConnected
        && testServer(
            activeConfiguration,
            batteryTelemetry,
            assignedTabletId,
            lastHttpResult);
    if (serverConnected) lastSuccessfulContact = currentUtcContactText();
  } else {
    disableWifiForIdle("local-only button wake");
    MEIMAD_LOG("WIFI", "skipped reason=local_button_wake");
  }
  if (!assignedTabletId.isEmpty() && assignedTabletId != activeConfiguration.tabletId) {
    Serial.printf("Tablet ID mismatch: firmware=%s server=%s. Reflash with the registered TabletID.\n",
                  activeConfiguration.tabletId.c_str(), assignedTabletId.c_str());
    serverConnected = false;
    lastHttpResult = "TABLET ID MISMATCH";
  }
  auto productionScreen = meimad::production_ui::makeDevelopmentFixture(
      activeConfiguration.tabletId);
  productionScreen.lowBattery = lowBattery;
  bool developmentFixture = true;
  bool refreshScreen = true;
  bool receivedServerRevision = false;
  uint32_t serverRevision = 0;
  uint8_t toolPage = loadToolPage();
  bool storedBatteryLowWarningAvailable = false;
  const bool storedBatteryLowWarning =
      loadBatteryLowWarning(storedBatteryLowWarningAvailable);
  const bool batteryWarningChanged = storedBatteryLowWarningAvailable
      ? storedBatteryLowWarning != lowBattery
      : lowBattery;
  const bool confirmationPending = loadConfirmationPending();
  bool confirmationDisplayed = false;
  bool verificationFailSafeDisplayed = false;
  String currentMachineBinding = loadDiagnosticText(kLastMachineBindingKey);
  String currentVerificationResult =
      loadDiagnosticText(kLastVerificationResultKey);
  if (currentVerificationResult.isEmpty()) currentVerificationResult = "NOT REPORTED";
  String currentMacroVersion = loadDiagnosticText(kLastMacroVersionKey);
  bool statePolicyFromServer = !serverContactRequired
      && previousSleepState.available
      && previousSleepState.serverStateAvailable;
  meimad::tablet_api::TabletStatus serverStatus =
      !serverContactRequired && previousSleepState.available
          ? previousSleepState.status
          : meimad::tablet_api::TabletStatus::Unknown;
  if (!serverContactRequired && previousSleepState.available) {
    Serial.printf(
        "Reusing retained sleep policy state for local wake: status=%s source=%s\n",
        meimad::tablet_api::toToken(serverStatus),
        statePolicyFromServer ? "server" : "fallback");
  }
  const auto lastRevision = loadLastRevision();
  const bool verificationFailSafeActive = loadVerificationFailSafe();
  const bool serviceScreenActive = loadServiceScreenActive();
  const bool lastDisplayedSetupVerification =
      loadLastDisplayedStatus() == "IN_SETUP";
  const bool retainedScreenMatchesTablet = lastRevision.available
      && lastRevision.tabletId == activeConfiguration.tabletId;
  refreshScreen = !retainedScreenMatchesTablet;
  if (lastRevision.available) {
    Serial.printf(
        "Loaded last_revision=%lu for tablet=%s\n",
        static_cast<unsigned long>(lastRevision.revision),
        lastRevision.tabletId.c_str());
  } else {
    Serial.println("No stored last_revision; next valid Server screen will refresh.");
  }
  if (serverConnected && !activeConfiguration.tabletId.isEmpty()) {
    meimad::tablet_api::TabletApiClient tabletApi(
        activeConfiguration.serverBaseUrl,
        batteryTelemetry);
    meimad::tablet_api::TabletStatusResponse tabletStatus;
    meimad::tablet_api::ApiResult statusApiResult;
    const char* statusReason =
        wakeButton.action == meimad::button_input::ButtonAction::Refresh
            ? "physical-refresh-button"
            : "wake-cycle";
    if (requestTabletStatus(
            tabletApi,
            activeConfiguration.tabletId,
            statusReason,
            tabletStatus,
            &statusApiResult)) {
      lastHttpResult = apiDiagnosticText("STATUS", statusApiResult);
      lastSuccessfulContact = currentUtcContactText();
      productionScreen = meimad::production_ui::makeProductionScreen(tabletStatus);
      productionScreen.lowBattery = lowBattery;
      developmentFixture = false;
      receivedServerRevision = true;
      statePolicyFromServer = true;
      serverRevision = tabletStatus.revision;
      serverStatus = tabletStatus.status;
      currentMachineBinding = tabletStatus.machine.number + " - " + tabletStatus.machine.name;
      currentVerificationResult = tabletStatus.diagnostics.verificationResult;
      if (tabletStatus.diagnostics.protectedMacroVersion >= 0) {
        currentMacroVersion = String(
            tabletStatus.diagnostics.protectedMacroVersion);
      } else {
        currentMacroVersion = "NOT REPORTED";
      }
      saveDiagnosticText(kLastMachineBindingKey, currentMachineBinding);
      saveDiagnosticText(kLastVerificationResultKey, currentVerificationResult);
      saveDiagnosticText(kLastMacroVersionKey, currentMacroVersion);
      const bool serverContentChanged = meimad::screen_revision::shouldRefresh(
          lastRevision,
          tabletStatus.tabletId,
          serverRevision);
      refreshScreen = serverContentChanged || verificationFailSafeActive
          || serviceScreenActive
          || confirmationPending || batteryWarningChanged;
      if (serverContentChanged) toolPage = 0;
      toolPage = meimad::production_ui::normalizedToolPage(
          toolPage, productionScreen.toolCount);
      if (!refreshScreen) {
        MEIMAD_LOG(
            "DISPLAY", "refresh skipped reason=unchanged server_revision=%lu",
            static_cast<unsigned long>(serverRevision));
      }

      if (wakeButton.action == meimad::button_input::ButtonAction::SendToQc) {
        if (!meimad::tablet_state_machine::canSendToQc(tabletStatus.status)) {
          Serial.printf(
              "SEND_TO_QC ignored: Server status %s does not allow it.\n",
              meimad::tablet_api::toToken(tabletStatus.status));
        } else {
          meimad::button_input::EventSubmissionGuard submissionGuard;
          if (!submissionGuard.tryBegin()) {
            MEIMAD_LOG("BUTTON", "SEND_TO_QC prevented reason=duplicate");
          } else {
            MEIMAD_LOG("API", "POST SEND_TO_QC attempt=1");
            meimad::tablet_api::TabletEventResponse eventResponse;
            const auto eventResult = tabletApi.sendEvent(
                activeConfiguration.tabletId,
                {},
                eventResponse);
            const bool eventAccepted = eventResult.succeeded();
            const bool eventExplicitlyRejected =
                eventResult.code == meimad::tablet_api::ApiResultCode::HttpError;
            String confirmationNotice = eventExplicitlyRejected
                ? "SEND TO QC REJECTED"
                : "QC SEND RESULT UNKNOWN";
            if (eventAccepted) {
              confirmationNotice = "SEND TO QC ACCEPTED";
              MEIMAD_LOG(
                  "API", "POST SEND_TO_QC response=200 server_timestamp=%s",
                  eventResponse.timestamp.c_str());
            } else {
              MEIMAD_LOG(
                  "API", "POST SEND_TO_QC failed code=%s%s%s",
                  meimad::tablet_api::toText(eventResult.code),
                  eventResult.detail.isEmpty() ? "" : " - ",
                  eventResult.detail.c_str());
            }

            meimad::tablet_api::TabletStatusResponse refreshedStatus;
            if (requestTabletStatus(
                    tabletApi,
                    activeConfiguration.tabletId,
                    "after-SEND_TO_QC",
                    refreshedStatus)) {
              if (refreshedStatus.revision != serverRevision) toolPage = 0;
              tabletStatus = refreshedStatus;
              productionScreen =
                  meimad::production_ui::makeProductionScreen(tabletStatus);
              productionScreen.lowBattery = lowBattery;
              serverRevision = tabletStatus.revision;
              serverStatus = tabletStatus.status;
              statePolicyFromServer = true;
              toolPage = meimad::production_ui::normalizedToolPage(
                  toolPage, productionScreen.toolCount);
              if (!eventAccepted
                  && tabletStatus.status ==
                      meimad::tablet_api::TabletStatus::InQc) {
                confirmationNotice = "SEND TO QC CONFIRMED";
              }
            } else if (eventAccepted) {
              serverStatus = meimad::tablet_api::TabletStatus::Unknown;
              statePolicyFromServer = false;
              confirmationNotice = "QC ACCEPTED - REFRESH PENDING";
            } else if (!eventExplicitlyRejected) {
              serverStatus = meimad::tablet_api::TabletStatus::Unknown;
              statePolicyFromServer = false;
            }
            productionScreen.notice = confirmationNotice;
            confirmationDisplayed = true;
            refreshScreen = true;
          }
        }
      }
    } else {
      lastHttpResult = apiDiagnosticText("STATUS", statusApiResult);
      const bool retainedVerificationCodeCouldBeVisible =
          retainedScreenMatchesTablet
          && (lastDisplayedSetupVerification
              || (previousSleepState.available
                  && previousSleepState.status
                      == meimad::tablet_api::TabletStatus::InSetup));
      if (retainedVerificationCodeCouldBeVisible && !verificationFailSafeActive) {
        productionScreen =
            meimad::production_ui::makeVerificationUnavailableScreen(
                activeConfiguration.tabletId);
        productionScreen.lowBattery = lowBattery;
        developmentFixture = false;
        refreshScreen = true;
        verificationFailSafeDisplayed = true;
        serverStatus = meimad::tablet_api::TabletStatus::Unknown;
        statePolicyFromServer = false;
        MEIMAD_LOG(
            "DISPLAY",
            "verification code cleared reason=status_unavailable");
      } else if (retainedScreenMatchesTablet) {
        refreshScreen = false;
        Serial.println(
            "E-Ink screen refresh skipped: retaining last-known screen without a valid Server revision.");
      }
    }
  }

  // Connection/bootstrap failures occur before requestTabletStatus(), so apply
  // the same fail-safe here as for a malformed or failed status response.
  const bool retainedVerificationCodeCouldStillBeVisible =
      serverContactRequired
      && !receivedServerRevision
      && retainedScreenMatchesTablet
      && (lastDisplayedSetupVerification
          || (previousSleepState.available
              && previousSleepState.status
                  == meimad::tablet_api::TabletStatus::InSetup));
  if (retainedVerificationCodeCouldStillBeVisible
      && !verificationFailSafeActive
      && !verificationFailSafeDisplayed) {
    productionScreen =
        meimad::production_ui::makeVerificationUnavailableScreen(
            activeConfiguration.tabletId);
    productionScreen.lowBattery = lowBattery;
    developmentFixture = false;
    refreshScreen = true;
    verificationFailSafeDisplayed = true;
    serverStatus = meimad::tablet_api::TabletStatus::Unknown;
    statePolicyFromServer = false;
    MEIMAD_LOG(
        "DISPLAY",
        "verification code cleared reason=server_unavailable");
  }

  if (serverContactRequired) {
    saveDiagnosticText(kLastHttpResultKey, lastHttpResult);
    if (!lastSuccessfulContact.isEmpty()) {
      saveDiagnosticText(kLastContactKey, lastSuccessfulContact);
    }
  }

  if (serverContactRequired) {
    disableWifiForIdle("network work complete");
  }

  if (wakeButton.action == meimad::button_input::ButtonAction::SendToQc
      && !receivedServerRevision) {
    Serial.println("SEND_TO_QC ignored: no valid Server state is available.");
  }

  if (wakeButton.action == meimad::button_input::ButtonAction::PreviousToolPage
      || wakeButton.action == meimad::button_input::ButtonAction::NextToolPage) {
    if (!receivedServerRevision && retainedScreenMatchesTablet) {
      Serial.println(
          "Tool-page press ignored: current tool model is unavailable; retained screen preserved.");
    } else {
      const uint8_t requestedPage =
          wakeButton.action == meimad::button_input::ButtonAction::PreviousToolPage
              ? meimad::production_ui::previousToolPage(
                    toolPage, productionScreen.toolCount)
              : meimad::production_ui::nextToolPage(
                    toolPage, productionScreen.toolCount);
      if (requestedPage != toolPage) {
        Serial.printf(
            "Tool page changed: %u -> %u\n",
            static_cast<unsigned>(toolPage + 1),
            static_cast<unsigned>(requestedPage + 1));
        toolPage = requestedPage;
        refreshScreen = true;
      } else {
        Serial.printf(
            "Tool page unchanged at boundary: %u / %u\n",
            static_cast<unsigned>(toolPage + 1),
            static_cast<unsigned>(
                meimad::production_ui::toolPageCount(productionScreen.toolCount)));
      }
    }
  }

  if (serviceScreenRequested) {
    meimad::service_ui::ServiceScreenModel serviceScreen;
    serviceScreen.tabletId = activeConfiguration.tabletId.isEmpty()
        ? "UNREGISTERED"
        : activeConfiguration.tabletId;
    serviceScreen.hardwareMac = activeConfiguration.hardwareId;
    serviceScreen.firmwareVersion = MEIMAD_FIRMWARE_VERSION;
    serviceScreen.machineBinding = receivedServerRevision
        ? currentMachineBinding
        : (currentMachineBinding.isEmpty()
            ? "UNAVAILABLE"
            : "LAST: " + currentMachineBinding);
    serviceScreen.wifiSsid = activeConfiguration.wifiSsid;
    serviceScreen.ipAddress = wifiIp;
    serviceScreen.rssi = wifiRssi;
    serviceScreen.serverAddress = safeServerAddress(activeConfiguration.serverBaseUrl);
    serviceScreen.lastSuccessfulContact = lastSuccessfulContact;
    serviceScreen.lastHttpResult = lastHttpResult;
    serviceScreen.workflowState = receivedServerRevision
        ? meimad::tablet_api::toToken(serverStatus)
        : "UNAVAILABLE";
    serviceScreen.revision = receivedServerRevision
        ? String(serverRevision)
        : (lastRevision.available
            ? "LAST: " + String(lastRevision.revision)
            : "UNAVAILABLE");
    const String voltage =
        meimad::tablet_api::formatBatteryVoltageHeader(batteryTelemetry);
    serviceScreen.batteryVoltage = voltage.isEmpty()
        ? "UNAVAILABLE"
        : voltage + " V";
    serviceScreen.wakeReason = wakeReasonText(wakeCause);
    const uint32_t lastRefreshDuration = loadLastRefreshDuration();
    serviceScreen.lastRefreshDuration = lastRefreshDuration == 0
        ? "NOT RECORDED"
        : String(lastRefreshDuration) + " ms";
    serviceScreen.verificationResult = currentVerificationResult;
    serviceScreen.protectedMacroVersion = currentMacroVersion;
    meimad::service_ui::logServiceDiagnostics(serviceScreen);
    const uint32_t duration = drawServiceLayout(serviceScreen);
    saveLastRefreshDuration(duration);
    saveServiceScreenActive(true);
  } else if (refreshScreen) {
    productionScreen.lowBattery = lowBattery;
    const uint32_t duration = drawProductionLayout(
        productionScreen,
        developmentFixture,
        receivedServerRevision
            ? "server"
            : (verificationFailSafeDisplayed
                ? "verification-fail-safe"
                : "layout-demo"),
        serverRevision,
        toolPage);
    saveLastRefreshDuration(duration);
    saveToolPage(toolPage);
    if (receivedServerRevision) {
      saveLastRevision(activeConfiguration.tabletId, serverRevision);
      saveLastDisplayedStatus(serverStatus);
      saveVerificationFailSafe(false);
      saveServiceScreenActive(false);
      if (confirmationDisplayed) {
        saveConfirmationPending(true);
      } else if (confirmationPending) {
        saveConfirmationPending(false);
      }
      saveBatteryLowWarning(lowBattery);
    } else if (verificationFailSafeDisplayed) {
      saveVerificationFailSafe(true);
    }
  }
  const auto statePolicy = meimad::tablet_state_machine::policyFor(serverStatus);
  enterStateSleep(statePolicy, serverStatus, statePolicyFromServer);
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
