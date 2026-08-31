#include "button_input.h"

#include <esp_sleep.h>

#include "hardware_config.h"

namespace meimad::button_input {
namespace {
constexpr uint64_t pinMask(int pin) {
  return 1ULL << pin;
}

constexpr uint64_t kRefreshMask = pinMask(hardware::kRefreshButtonGpio);
constexpr uint64_t kPreviousMask = pinMask(hardware::kPageButtonGpio);
constexpr uint64_t kActionMask = pinMask(hardware::kActionButtonGpio);
constexpr uint64_t kKnownButtonMask = kRefreshMask | kPreviousMask | kActionMask;

bool isPressed(int pin) {
  return digitalRead(pin) == LOW;
}

bool waitForRelease(int pin) {
  const uint32_t startedAt = millis();
  while (isPressed(pin) && millis() - startedAt < kReleaseWaitMilliseconds) {
    delay(10);
  }
  return !isPressed(pin);
}

ButtonEvent captureButtonMask(uint64_t buttonMask) {
  ButtonEvent event;
  event.wakeMask = buttonMask & kKnownButtonMask;
  const bool exactlyOneButton = event.wakeMask != 0
      && (event.wakeMask & (event.wakeMask - 1)) == 0;
  if (!exactlyOneButton) {
    event.ambiguous = event.wakeMask != 0;
    return event;
  }

  const int pin = event.wakeMask == kRefreshMask
      ? hardware::kRefreshButtonGpio
      : event.wakeMask == kPreviousMask
          ? hardware::kPageButtonGpio
          : hardware::kActionButtonGpio;
  pinMode(pin, INPUT_PULLUP);
  delay(kDebounceMilliseconds);
  if (!isPressed(pin)) return event;

  const uint32_t holdStartedAt = millis();
  bool longPress = false;
  if (event.wakeMask == kActionMask || event.wakeMask == kRefreshMask) {
    const uint32_t threshold = event.wakeMask == kRefreshMask
        ? kServiceScreenLongPressMilliseconds
        : kSendToQcLongPressMilliseconds;
    while (isPressed(pin) && millis() - holdStartedAt < threshold) delay(10);
    longPress = isPressed(pin);
  }
  event.heldMilliseconds = millis() - holdStartedAt + kDebounceMilliseconds;
  event.action = actionForWakeMask(event.wakeMask, longPress);
  event.released = waitForRelease(pin);
  if (!event.released) event.action = ButtonAction::None;
  return event;
}
}  // namespace

ButtonAction actionForWakeMask(uint64_t wakeMask, bool longPress) {
  const uint64_t buttons = wakeMask & kKnownButtonMask;
  if (buttons == kRefreshMask) {
    return longPress ? ButtonAction::ServiceScreen : ButtonAction::Refresh;
  }
  if (buttons == kPreviousMask) return ButtonAction::PreviousToolPage;
  if (buttons == kActionMask) {
    return longPress ? ButtonAction::SendToQc : ButtonAction::NextToolPage;
  }
  return ButtonAction::None;
}

bool requiresServerContact(bool physicalButtonWake, ButtonAction action) {
  if (!physicalButtonWake) return true;
  return action == ButtonAction::Refresh
      || action == ButtonAction::ServiceScreen
      || action == ButtonAction::SendToQc;
}

ButtonEvent captureWakeButtonEvent() {
  if (esp_sleep_get_wakeup_cause() != ESP_SLEEP_WAKEUP_EXT1) return {};
  return captureButtonMask(esp_sleep_get_ext1_wakeup_status());
}

ButtonEvent captureRuntimeButtonEvent() {
  uint64_t pressedMask = 0;
  if (isPressed(hardware::kRefreshButtonGpio)) pressedMask |= kRefreshMask;
  if (isPressed(hardware::kPageButtonGpio)) pressedMask |= kPreviousMask;
  if (isPressed(hardware::kActionButtonGpio)) pressedMask |= kActionMask;
  return captureButtonMask(pressedMask);
}

bool EventSubmissionGuard::tryBegin() {
  if (attempted_) return false;
  attempted_ = true;
  return true;
}

const char* toText(ButtonAction action) {
  switch (action) {
    case ButtonAction::None: return "NONE";
    case ButtonAction::Refresh: return "REFRESH";
    case ButtonAction::ServiceScreen: return "SERVICE_SCREEN";
    case ButtonAction::PreviousToolPage: return "PREVIOUS_TOOL_PAGE";
    case ButtonAction::NextToolPage: return "NEXT_TOOL_PAGE";
    case ButtonAction::SendToQc: return "SEND_TO_QC";
  }
  return "NONE";
}

}  // namespace meimad::button_input
