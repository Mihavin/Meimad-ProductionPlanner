#include "tablet_state_machine.h"

namespace meimad::tablet_state_machine {

StatePolicy policyFor(tablet_api::TabletStatus status) {
  switch (status) {
    case tablet_api::TabletStatus::ReadyForSetup:
      return {
          SleepMode::StayAwake, WifiDefault::Off, true, 0,
          ButtonRefreshBehavior::WaitForInSetupOrTimeout,
          kButtonWifiSessionTimeoutSeconds,
          "setup operator may browse locally; D1 opens a bounded Wi-Fi session",
          false};
    case tablet_api::TabletStatus::InSetup:
      return {
          SleepMode::StayAwake, WifiDefault::Off, true, 0,
          ButtonRefreshBehavior::RefreshOnce, kButtonWifiSessionTimeoutSeconds,
          "setup verification remains locally interactive with Wi-Fi off",
          false};
    case tablet_api::TabletStatus::InSetupRun:
      return {
          SleepMode::StayAwake, WifiDefault::Off, true, 0,
          ButtonRefreshBehavior::RefreshOnce, kButtonWifiSessionTimeoutSeconds,
          "setup work remains locally interactive with Wi-Fi off",
          false};
    case tablet_api::TabletStatus::InQc:
      return {
          SleepMode::DeepSleep, WifiDefault::Off, true, 0,
          ButtonRefreshBehavior::RefreshOnce, kButtonWifiSessionTimeoutSeconds,
          "QA waits in deep sleep until physical refresh",
          false};
    case tablet_api::TabletStatus::ReadyForProduction:
      return {
          SleepMode::DeepSleep, WifiDefault::Off, true,
          kPostQaRefreshIntervalSeconds,
          ButtonRefreshBehavior::RefreshOnce, kButtonWifiSessionTimeoutSeconds,
          "post-QA production wait refreshes once per configured timer wake",
          false};
    case tablet_api::TabletStatus::InProduction:
      return {
          SleepMode::DeepSleep, WifiDefault::Off, true, 0,
          ButtonRefreshBehavior::RefreshOnce, kButtonWifiSessionTimeoutSeconds,
          "CNC and Server own production-cycle events; physical refresh only",
          false};
    case tablet_api::TabletStatus::Blocked:
      return {
          SleepMode::DeepSleep, WifiDefault::Off, true,
          kFallbackRefreshIntervalSeconds,
          ButtonRefreshBehavior::RefreshOnce, kButtonWifiSessionTimeoutSeconds,
          "BLOCKED sleep policy is open; retry conservatively",
          true};
    case tablet_api::TabletStatus::Unknown:
      return {
          SleepMode::DeepSleep, WifiDefault::Off, true,
          kFallbackRefreshIntervalSeconds,
          ButtonRefreshBehavior::RefreshOnce, kButtonWifiSessionTimeoutSeconds,
          "status unavailable or UNKNOWN; retry conservatively",
          true};
  }
  return {};
}

bool shouldContinueButtonWifiSession(
    tablet_api::TabletStatus originStatus,
    tablet_api::TabletStatus observedStatus,
    uint32_t elapsedSeconds,
    uint32_t timeoutSeconds) {
  if (policyFor(originStatus).buttonRefreshBehavior
      != ButtonRefreshBehavior::WaitForInSetupOrTimeout) return false;
  if (elapsedSeconds >= timeoutSeconds) return false;
  return observedStatus == tablet_api::TabletStatus::ReadyForSetup;
}

bool canSendToQc(tablet_api::TabletStatus status) {
  return status == tablet_api::TabletStatus::InSetupRun;
}

const char* toText(SleepMode sleepMode) {
  switch (sleepMode) {
    case SleepMode::DeepSleep: return "DEEP_SLEEP";
    case SleepMode::StayAwake: return "STAY_AWAKE";
  }
  return "DEEP_SLEEP";
}

const char* toText(WifiDefault wifiDefault) {
  switch (wifiDefault) {
    case WifiDefault::Off: return "OFF";
  }
  return "OFF";
}

const char* toText(ButtonRefreshBehavior behavior) {
  switch (behavior) {
    case ButtonRefreshBehavior::RefreshOnce: return "REFRESH_ONCE";
    case ButtonRefreshBehavior::WaitForInSetupOrTimeout:
      return "WAIT_FOR_IN_SETUP_OR_TIMEOUT";
  }
  return "REFRESH_ONCE";
}

}  // namespace meimad::tablet_state_machine
