#pragma once

#include <Arduino.h>

#include "production_ui.h"

namespace meimad::demo_mode {

enum class DemoScenario : uint8_t {
  ReadyForSetup,
  SetupVerification,
  SetupVerificationExpired,
  InSetupRun,
  InQc,
  ReadyForProduction,
  InProduction,
  Blocked,
  WifiError,
  ServerError,
  UnregisteredTablet,
  LowBattery
};

constexpr uint8_t kScenarioCount = 12;

DemoScenario scenarioForIndex(uint8_t index);
uint8_t nextScenarioIndex(uint8_t index);
const char* scenarioName(DemoScenario scenario);
production_ui::ProductionScreenModel makeScreen(DemoScenario scenario);

}  // namespace meimad::demo_mode
