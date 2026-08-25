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
constexpr char kPreferencesNamespace[] = "meimad";
constexpr char kLastRevisionKey[] = "last_revision";
constexpr char kLastRevisionTabletKey[] = "last_rev_tab";
constexpr char kToolPageKey[] = "tool_page";
constexpr char kConfirmationPendingKey[] = "confirm_clear";
constexpr uint32_t kRetainedSleepStateMagic = 0x4D534C50;
constexpr time_t kMinimumValidWakeTime = 1704067200;  // 2024-01-01T00:00:00Z

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
  String deviceToken;
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

DeviceConfiguration loadDeviceConfiguration() {
  Preferences preferences;
  preferences.begin(kPreferencesNamespace, false);
  if (!preferences.isKey("wifi_ssid") && strlen(meimad::config::kDefaultWifiSsid) > 0)
    preferences.putString("wifi_ssid", meimad::config::kDefaultWifiSsid);
  if (!preferences.isKey("wifi_pass") && strlen(meimad::config::kDefaultWifiPassword) > 0)
    preferences.putString("wifi_pass", meimad::config::kDefaultWifiPassword);
  if (!preferences.isKey("server_url"))
    preferences.putString("server_url", meimad::config::kServerBaseUrl);
  if (!preferences.isKey("tablet_id") && strlen(meimad::config::kDefaultTabletId) > 0)
    preferences.putString("tablet_id", meimad::config::kDefaultTabletId);
  if (!preferences.isKey("device_token") && strlen(meimad::config::kDefaultDeviceToken) > 0)
    preferences.putString("device_token", meimad::config::kDefaultDeviceToken);
  DeviceConfiguration value {
    readHardwareId(),
    preferences.getString("tablet_id", ""),
    preferences.getString("wifi_ssid", ""),
    preferences.getString("wifi_pass", ""),
    preferences.getString("server_url", meimad::config::kServerBaseUrl),
    preferences.getString("device_token", "")
  };
  preferences.end();
  return value;
}

void cacheTabletId(const String& tabletId) {
  if (tabletId.isEmpty()) return;
  Preferences preferences;
  preferences.begin(kPreferencesNamespace, false);
  preferences.putString("tablet_id", tabletId);
  preferences.end();
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
  Serial.printf(
      "Stored last_revision=%lu for tablet=%s\n",
      static_cast<unsigned long>(revision),
      tabletId.c_str());
  return true;
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

void saveConfirmationPending(bool pending) {
  Preferences preferences;
  if (!preferences.begin(kPreferencesNamespace, false)) {
    Serial.println("Confirmation-state NVS write failed.");
    return;
  }
  preferences.putBool(kConfirmationPendingKey, pending);
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

bool testServer(
    const DeviceConfiguration& configuration,
    const meimad::tablet_api::BatteryTelemetry& batteryTelemetry,
    String& assignedTabletId) {
  if (configuration.serverBaseUrl.isEmpty()) return false;
  HTTPClient http;
  const String url = configuration.serverBaseUrl + "/api/tablet/ping?hardwareId=" + configuration.hardwareId;
  http.setConnectTimeout(5000);
  http.setTimeout(7000);
  if (!http.begin(url)) return false;
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

bool requestTabletStatus(
    const meimad::tablet_api::TabletApiClient& tabletApi,
    const String& tabletId,
    const char* requestReason,
    meimad::tablet_api::TabletStatusResponse& tabletStatus) {
  Serial.printf("Requesting latest tablet state: reason=%s\n", requestReason);
  const auto result = tabletApi.getStatus(tabletId, tabletStatus);
  if (!result.succeeded()) {
    Serial.printf(
        "Tablet status unavailable: %s%s%s\n",
        meimad::tablet_api::toText(result.code),
        result.detail.isEmpty() ? "" : " - ",
        result.detail.c_str());
    return false;
  }
  Serial.printf(
      "Tablet status: revision=%lu machine=%s part=%s operation=%ld status=%s\n",
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

void printWakeReason() {
  const auto cause = esp_sleep_get_wakeup_cause();
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
  Serial.printf("Wake-up reason: %s (%d)\n", text, static_cast<int>(cause));
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
  Serial.printf(
      "Wi-Fi disabled: reason=%s previous_mode=%d current_mode=%d\n",
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
  Serial.printf("Battery voltage: %.3f V (ADC raw %d, calibrated)\n", voltage, raw);
  Serial.println(
      "Battery telemetry: voltage only; percentage is unavailable until AA calibration.");
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
  Serial.println(
      "Entering deep sleep; the E-Ink display retains the current screen.");
  Serial.flush();
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
  Serial.printf(
      "E-Ink screen refresh: source=%s revision=%lu duration=%lu ms\n",
      source,
      static_cast<unsigned long>(revision),
      static_cast<unsigned long>(refreshDurationMs));
  Serial.println("E-Ink panel is sleeping; the production layout remains visible.");
  return refreshDurationMs;
#else
  return 0;
#endif
}
} // namespace

void setup() {
  const auto wakeButton = meimad::button_input::captureWakeButtonEvent();
  const auto previousSleepState = loadPreviousSleepState();
  const auto wakeCause = esp_sleep_get_wakeup_cause();
  const bool physicalButtonWake = wakeCause == ESP_SLEEP_WAKEUP_EXT1;
  const bool serverContactRequired =
      meimad::button_input::requiresServerContact(
          physicalButtonWake, wakeButton.action);
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
  printWakeTimestamp();
  printPreviousSleepState(previousSleepState);
  if (wakeButton.ambiguous) {
    Serial.printf(
        "Button wake ignored: ambiguous mask=0x%llX\n",
        static_cast<unsigned long long>(wakeButton.wakeMask));
  } else if (!wakeButton.released) {
    Serial.printf(
        "Button wake ignored: button did not release, mask=0x%llX\n",
        static_cast<unsigned long long>(wakeButton.wakeMask));
  } else if (wakeButton.wakeMask != 0
      && wakeButton.action == meimad::button_input::ButtonAction::None) {
    Serial.printf(
        "Button wake ignored after debounce: mask=0x%llX\n",
        static_cast<unsigned long long>(wakeButton.wakeMask));
  } else if (wakeButton.action != meimad::button_input::ButtonAction::None) {
    Serial.printf(
        "Button press: action=%s mask=0x%llX held_ms=%lu\n",
        meimad::button_input::toText(wakeButton.action),
        static_cast<unsigned long long>(wakeButton.wakeMask),
        static_cast<unsigned long>(wakeButton.heldMilliseconds));
  }
  const auto configuration = loadDeviceConfiguration();
  Serial.printf("Hardware ID (MAC): %s\n", configuration.hardwareId.c_str());
  Serial.printf("Tablet ID: %s\n", configuration.tabletId.isEmpty() ? "UNREGISTERED" : configuration.tabletId.c_str());
  configureWakeButtons();
  const auto batteryTelemetry = sampleBatteryTelemetry();
  Serial.printf("Display controller board combo: %d (UC8179)\n",
                meimad::hardware::kEinkControllerBoardCombo);
  auto activeConfiguration = configuration;
  Serial.printf(
      "Wake network policy: server_contact=%s action=%s\n",
      serverContactRequired ? "required" : "skipped",
      meimad::button_input::toText(wakeButton.action));
  bool wifiConnected = false;
  String assignedTabletId;
  bool serverConnected = false;
  if (serverContactRequired) {
    wifiConnected = connectWifi(activeConfiguration);
    serverConnected = wifiConnected
        && testServer(activeConfiguration, batteryTelemetry, assignedTabletId);
  } else {
    disableWifiForIdle("local-only button wake");
    Serial.println("Wi-Fi connection skipped for local-only button wake.");
  }
  if (!assignedTabletId.isEmpty() && assignedTabletId != activeConfiguration.tabletId) {
    cacheTabletId(assignedTabletId);
    activeConfiguration.tabletId = assignedTabletId;
    Serial.printf("Server-assigned Tablet ID cached in NVS: %s\n", assignedTabletId.c_str());
  }
  auto productionScreen = meimad::production_ui::makeDevelopmentFixture(
      activeConfiguration.tabletId);
  bool developmentFixture = true;
  bool refreshScreen = true;
  bool receivedServerRevision = false;
  uint32_t serverRevision = 0;
  uint8_t toolPage = loadToolPage();
  const bool confirmationPending = loadConfirmationPending();
  bool confirmationDisplayed = false;
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
        activeConfiguration.deviceToken,
        batteryTelemetry);
    meimad::tablet_api::TabletStatusResponse tabletStatus;
    const char* statusReason =
        wakeButton.action == meimad::button_input::ButtonAction::Refresh
            ? "physical-refresh-button"
            : "wake-cycle";
    if (requestTabletStatus(
            tabletApi,
            activeConfiguration.tabletId,
            statusReason,
            tabletStatus)) {
      productionScreen = meimad::production_ui::makeProductionScreen(tabletStatus);
      developmentFixture = false;
      receivedServerRevision = true;
      statePolicyFromServer = true;
      serverRevision = tabletStatus.revision;
      serverStatus = tabletStatus.status;
      const bool serverContentChanged = meimad::screen_revision::shouldRefresh(
          lastRevision,
          tabletStatus.tabletId,
          serverRevision);
      refreshScreen = serverContentChanged || confirmationPending;
      if (serverContentChanged) toolPage = 0;
      toolPage = meimad::production_ui::normalizedToolPage(
          toolPage, productionScreen.toolCount);
      if (!refreshScreen) {
        Serial.printf(
            "E-Ink screen refresh skipped: server_revision=%lu equals last_revision.\n",
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
            Serial.println("Duplicate SEND_TO_QC submission prevented.");
          } else {
            Serial.println("Submitting SEND_TO_QC once for this wake cycle.");
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
              Serial.printf(
                  "SEND_TO_QC accepted: server_timestamp=%s\n",
                  eventResponse.timestamp.c_str());
            } else {
              Serial.printf(
                  "SEND_TO_QC failed: %s%s%s\n",
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
      if (retainedScreenMatchesTablet) {
        refreshScreen = false;
        Serial.println(
            "E-Ink screen refresh skipped: retaining last-known screen without a valid Server revision.");
      }
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

  if (refreshScreen) {
    drawProductionLayout(
        productionScreen,
        developmentFixture,
        receivedServerRevision ? "server" : "layout-demo",
        serverRevision,
        toolPage);
    saveToolPage(toolPage);
    if (receivedServerRevision) {
      saveLastRevision(activeConfiguration.tabletId, serverRevision);
      if (confirmationDisplayed) {
        saveConfirmationPending(true);
      } else if (confirmationPending) {
        saveConfirmationPending(false);
      }
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
