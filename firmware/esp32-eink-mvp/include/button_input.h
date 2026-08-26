#pragma once

#include <Arduino.h>

namespace meimad::button_input {

constexpr uint32_t kDebounceMilliseconds = 40;
constexpr uint32_t kSendToQcLongPressMilliseconds = 1200;
constexpr uint32_t kServiceScreenLongPressMilliseconds = 1200;
constexpr uint32_t kReleaseWaitMilliseconds = 2500;

enum class ButtonAction {
  None,
  Refresh,
  ServiceScreen,
  PreviousToolPage,
  NextToolPage,
  SendToQc
};

struct ButtonEvent {
  ButtonAction action = ButtonAction::None;
  uint64_t wakeMask = 0;
  uint32_t heldMilliseconds = 0;
  bool ambiguous = false;
  bool released = true;
};

class EventSubmissionGuard {
 public:
  bool tryBegin();
  bool attempted() const { return attempted_; }

 private:
  bool attempted_ = false;
};

ButtonAction actionForWakeMask(uint64_t wakeMask, bool longPress);
bool requiresServerContact(bool physicalButtonWake, ButtonAction action);
ButtonEvent captureWakeButtonEvent();
const char* toText(ButtonAction action);

}  // namespace meimad::button_input
