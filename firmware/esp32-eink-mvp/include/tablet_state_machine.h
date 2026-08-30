#pragma once

#include <Arduino.h>

#include "tablet_api.h"

namespace meimad::tablet_state_machine {

constexpr uint32_t kInitialPollIntervalSeconds = 120;
constexpr uint32_t kSetupVerificationPollIntervalSeconds = 15;

enum class WakeMode {
  PollServer,
  PhysicalButton
};

struct StatePolicy {
  StatePolicy(
      WakeMode wakeModeValue = WakeMode::PollServer,
      uint32_t pollIntervalSecondsValue = kInitialPollIntervalSeconds,
      const char* reasonValue = "status unavailable; retry conservatively",
      bool fallbackPolicyValue = true)
      : wakeMode(wakeModeValue),
        pollIntervalSeconds(pollIntervalSecondsValue),
        reason(reasonValue),
        fallbackPolicy(fallbackPolicyValue) {}

  WakeMode wakeMode;
  uint32_t pollIntervalSeconds;
  const char* reason;
  bool fallbackPolicy;
};

StatePolicy policyFor(tablet_api::TabletStatus status);
bool canSendToQc(tablet_api::TabletStatus status);
const char* toText(WakeMode wakeMode);

}  // namespace meimad::tablet_state_machine
