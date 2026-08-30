#include "tablet_state_machine.h"

namespace meimad::tablet_state_machine {

StatePolicy policyFor(tablet_api::TabletStatus status) {
  switch (status) {
    case tablet_api::TabletStatus::ReadyForSetup:
      return {
          WakeMode::PollServer,
          kSetupVerificationPollIntervalSeconds,
          "polling inside the minimum verification window for a new challenge",
          false};
    case tablet_api::TabletStatus::InSetup:
      return {
          WakeMode::PollServer,
          kSetupVerificationPollIntervalSeconds,
          "verification code is time-limited; refresh from the Server",
          false};
    case tablet_api::TabletStatus::InSetupRun:
      return {
          WakeMode::PhysicalButton,
          0,
          "setup information remains visible until physical wake",
          false};
    case tablet_api::TabletStatus::InQc:
      return {
          WakeMode::PollServer,
          kInitialPollIntervalSeconds,
          "waiting for the Server QC_PASS or QC_FAIL result state",
          false};
    case tablet_api::TabletStatus::ReadyForProduction:
      return {
          WakeMode::PhysicalButton,
          0,
          "ready-for-production screen remains visible until physical wake",
          false};
    case tablet_api::TabletStatus::InProduction:
      return {
          WakeMode::PhysicalButton,
          0,
          "CNC and Server own production-cycle events",
          false};
    case tablet_api::TabletStatus::Blocked:
      return {
          WakeMode::PollServer,
          kInitialPollIntervalSeconds,
          "BLOCKED sleep policy is open; retry conservatively",
          true};
    case tablet_api::TabletStatus::Unknown:
      return {
          WakeMode::PollServer,
          kInitialPollIntervalSeconds,
          "status unavailable or UNKNOWN; retry conservatively",
          true};
  }
  return {};
}

bool canSendToQc(tablet_api::TabletStatus status) {
  return status == tablet_api::TabletStatus::InSetupRun;
}

const char* toText(WakeMode wakeMode) {
  switch (wakeMode) {
    case WakeMode::PollServer: return "POLL_SERVER";
    case WakeMode::PhysicalButton: return "PHYSICAL_BUTTON";
  }
  return "POLL_SERVER";
}

}  // namespace meimad::tablet_state_machine
