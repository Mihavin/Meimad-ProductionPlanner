#pragma once

#include <Arduino.h>

#include "tablet_api.h"

namespace meimad::tablet_state_machine {

#ifndef MEIMAD_POST_QA_REFRESH_SECONDS
#define MEIMAD_POST_QA_REFRESH_SECONDS 60
#endif

#ifndef MEIMAD_BUTTON_WIFI_SESSION_TIMEOUT_SECONDS
#define MEIMAD_BUTTON_WIFI_SESSION_TIMEOUT_SECONDS 30
#endif

constexpr uint32_t kFallbackRefreshIntervalSeconds = 120;
constexpr uint32_t kPostQaRefreshIntervalSeconds = MEIMAD_POST_QA_REFRESH_SECONDS;
constexpr uint32_t kButtonWifiSessionTimeoutSeconds =
    MEIMAD_BUTTON_WIFI_SESSION_TIMEOUT_SECONDS;

enum class SleepMode { DeepSleep, StayAwake };
enum class WifiDefault { Off };
enum class ButtonRefreshBehavior { RefreshOnce, WaitForInSetupOrTimeout };

struct StatePolicy {
  StatePolicy(
      SleepMode sleepModeValue = SleepMode::DeepSleep,
      WifiDefault wifiDefaultValue = WifiDefault::Off,
      bool buttonWakeEnabledValue = true,
      uint32_t periodicRefreshIntervalSecondsValue = kFallbackRefreshIntervalSeconds,
      ButtonRefreshBehavior buttonRefreshBehaviorValue = ButtonRefreshBehavior::RefreshOnce,
      uint32_t wifiSessionTimeoutSecondsValue = kButtonWifiSessionTimeoutSeconds,
      const char* reasonValue = "status unavailable; retry conservatively",
      bool fallbackPolicyValue = true)
      : sleepMode(sleepModeValue),
        wifiDefault(wifiDefaultValue),
        buttonWakeEnabled(buttonWakeEnabledValue),
        periodicRefreshIntervalSeconds(periodicRefreshIntervalSecondsValue),
        buttonRefreshBehavior(buttonRefreshBehaviorValue),
        wifiSessionTimeoutSeconds(wifiSessionTimeoutSecondsValue),
        reason(reasonValue),
        fallbackPolicy(fallbackPolicyValue) {}

  SleepMode sleepMode;
  WifiDefault wifiDefault;
  bool buttonWakeEnabled;
  uint32_t periodicRefreshIntervalSeconds;
  ButtonRefreshBehavior buttonRefreshBehavior;
  uint32_t wifiSessionTimeoutSeconds;
  const char* reason;
  bool fallbackPolicy;
};

StatePolicy policyFor(tablet_api::TabletStatus status);
bool shouldContinueButtonWifiSession(
    tablet_api::TabletStatus originStatus,
    tablet_api::TabletStatus observedStatus,
    uint32_t elapsedSeconds,
    uint32_t timeoutSeconds = kButtonWifiSessionTimeoutSeconds);
bool canSendToQc(tablet_api::TabletStatus status);
const char* toText(SleepMode sleepMode);
const char* toText(WifiDefault wifiDefault);
const char* toText(ButtonRefreshBehavior behavior);

}  // namespace meimad::tablet_state_machine
