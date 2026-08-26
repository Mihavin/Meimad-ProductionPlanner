#include "demo_mode.h"

namespace meimad::demo_mode {

DemoScenario scenarioForIndex(uint8_t index) {
  switch (index % kScenarioCount) {
    case 0: return DemoScenario::ReadyForSetup;
    case 1: return DemoScenario::SetupVerification;
    case 2: return DemoScenario::SetupVerificationExpired;
    case 3: return DemoScenario::InSetupRun;
    case 4: return DemoScenario::InQc;
    case 5: return DemoScenario::ReadyForProduction;
    case 6: return DemoScenario::InProduction;
    case 7: return DemoScenario::Blocked;
    case 8: return DemoScenario::WifiError;
    case 9: return DemoScenario::ServerError;
    case 10: return DemoScenario::UnregisteredTablet;
    case 11: return DemoScenario::LowBattery;
  }
  return DemoScenario::ReadyForSetup;
}

uint8_t nextScenarioIndex(uint8_t index) {
  return static_cast<uint8_t>((index + 1) % kScenarioCount);
}

const char* scenarioName(DemoScenario scenario) {
  switch (scenario) {
    case DemoScenario::ReadyForSetup: return "READY FOR SETUP";
    case DemoScenario::SetupVerification: return "SETUP VERIFY CODE";
    case DemoScenario::SetupVerificationExpired: return "SETUP VERIFY EXPIRED";
    case DemoScenario::InSetupRun: return "IN SETUP RUN";
    case DemoScenario::InQc: return "IN QC";
    case DemoScenario::ReadyForProduction: return "READY FOR PRODUCTION";
    case DemoScenario::InProduction: return "IN PRODUCTION";
    case DemoScenario::Blocked: return "BLOCKED";
    case DemoScenario::WifiError: return "WI-FI ERROR";
    case DemoScenario::ServerError: return "SERVER ERROR";
    case DemoScenario::UnregisteredTablet: return "UNREGISTERED TABLET";
    case DemoScenario::LowBattery: return "LOW BATTERY";
  }
  return "READY FOR SETUP";
}

production_ui::ProductionScreenModel makeScreen(DemoScenario scenario) {
  auto model = production_ui::makeDevelopmentFixture("3041");
  model.notice = String("DEMO - ") + scenarioName(scenario);
  switch (scenario) {
    case DemoScenario::ReadyForSetup:
      model.status = tablet_api::TabletStatus::ReadyForSetup;
      break;
    case DemoScenario::SetupVerification:
      model.status = tablet_api::TabletStatus::InSetup;
      model.verificationState =
          tablet_api::VerificationState::WaitingForOperator;
      model.verificationResponseCode = "0388";
      break;
    case DemoScenario::SetupVerificationExpired:
      model.status = tablet_api::TabletStatus::InSetup;
      model.verificationState = tablet_api::VerificationState::Expired;
      model.verificationResponseCode = "";
      break;
    case DemoScenario::InSetupRun:
      model.status = tablet_api::TabletStatus::InSetupRun;
      break;
    case DemoScenario::InQc:
      model.status = tablet_api::TabletStatus::InQc;
      break;
    case DemoScenario::ReadyForProduction:
      model.status = tablet_api::TabletStatus::ReadyForProduction;
      break;
    case DemoScenario::InProduction:
      model.status = tablet_api::TabletStatus::InProduction;
      break;
    case DemoScenario::Blocked:
      model.status = tablet_api::TabletStatus::Blocked;
      break;
    case DemoScenario::WifiError:
    case DemoScenario::ServerError:
      model.status = tablet_api::TabletStatus::Unknown;
      break;
    case DemoScenario::UnregisteredTablet:
      model.status = tablet_api::TabletStatus::Unknown;
      model.tabletId = "UNREGISTERED TABLET";
      break;
    case DemoScenario::LowBattery:
      model.status = tablet_api::TabletStatus::InSetupRun;
      model.lowBattery = true;
      break;
  }
  return model;
}

}  // namespace meimad::demo_mode
