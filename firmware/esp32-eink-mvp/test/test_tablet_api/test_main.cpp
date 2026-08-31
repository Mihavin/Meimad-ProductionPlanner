#include <Arduino.h>

#include "../../src/tablet_api.cpp"
#include "../../src/production_ui.cpp"
#include "../../src/screen_revision.cpp"
#include "../../src/tablet_state_machine.cpp"
#include "../../src/button_input.cpp"
#include "../../src/demo_mode.cpp"
#include "../../src/service_ui.cpp"

using namespace meimad::tablet_api;
using namespace meimad::production_ui;
using namespace meimad::screen_revision;
using namespace meimad::tablet_state_machine;
using namespace meimad::button_input;
using namespace meimad::demo_mode;

namespace {
uint32_t failures = 0;

void check(bool condition, const char* expression, int line) {
  if (condition) return;
  ++failures;
  Serial.printf("FAIL line %d: %s\n", line, expression);
}

#define CHECK(expression) check((expression), #expression, __LINE__)
#define CHECK_STRING(expected, actual) \
  check(String(expected) == String(actual), #actual " equals " #expected, __LINE__)

String validStatusPayload(const char* status = "IN_SETUP_RUN") {
  const String verification = String(status) == "IN_SETUP"
      ? ",\"verification\":{\"required\":true,"
        "\"state\":\"WAITING_FOR_OPERATOR\",\"response_code\":\"0388\"}"
      : "";
  return String("{")
      + "\"revision\":17,"
      + "\"tablet_id\":\"3041\","
      + "\"machine\":{\"id\":10,\"name\":\"DMG MORI 10\"},"
      + "\"nc_run\":{\"id\":845},"
      + "\"part\":{\"number\":\"P-12345\",\"name\":\"Housing\"},"
      + "\"operation\":{\"number\":30,\"name\":\"Finish Milling\"},"
      + "\"status\":\"" + status + "\"" + verification
      + ",\"diagnostics\":{\"verification_result\":\"NOT_REPORTED\","
        "\"protected_macro_version\":3}}";
}

void testParsesSafeServiceDiagnostics() {
  TabletStatusResponse response;
  String error;
  CHECK(parseStatusPayload(validStatusPayload(), "3041", response, error));
  CHECK_STRING("NOT_REPORTED", response.diagnostics.verificationResult);
  CHECK(response.diagnostics.protectedMacroVersion == 3);

  String invalid = validStatusPayload();
  invalid.replace("\"protected_macro_version\":3", "\"protected_macro_version\":-1");
  error = "";
  CHECK(!parseStatusPayload(invalid, "3041", response, error));
  CHECK_STRING("diagnostics.protected_macro_version must not be negative", error);

  invalid = validStatusPayload();
  invalid.replace("NOT_REPORTED", "SECRET_TEXT");
  error = "";
  CHECK(!parseStatusPayload(invalid, "3041", response, error));
  CHECK_STRING("diagnostics.verification_result is not supported", error);
}

void testParsesExampleAndNormalizesNumericIds() {
  TabletStatusResponse response;
  String error;
  CHECK(parseStatusPayload(validStatusPayload(), "3041", response, error));
  CHECK(response.revision == 17);
  CHECK_STRING("3041", response.tabletId);
  CHECK_STRING("10", response.machine.id);
  CHECK_STRING("845", response.ncRun.id);
  CHECK(response.operation.number == 30);
  CHECK_STRING("IN_SETUP_RUN", toToken(response.status));
}

void testParsesExplicitMachineNumber() {
  String payload = validStatusPayload();
  payload.replace(
      "\"name\":\"DMG MORI 10\"",
      "\"number\":\"M10\",\"name\":\"DMG MORI\"");
  TabletStatusResponse response;
  String error;
  CHECK(parseStatusPayload(payload, "3041", response, error));
  CHECK_STRING("M10", response.machine.number);
  CHECK_STRING("DMG MORI", response.machine.name);
}

void testAcceptsEveryInitialStatus() {
  const char* statuses[] = {
      "READY_FOR_SETUP", "IN_SETUP", "IN_SETUP_RUN", "IN_QC", "READY_FOR_PRODUCTION",
      "IN_PRODUCTION", "BLOCKED", "UNKNOWN"};
  for (const char* status : statuses) {
    TabletStatusResponse response;
    String error;
    CHECK(parseStatusPayload(validStatusPayload(status), "3041", response, error));
    CHECK_STRING(status, toToken(response.status));
  }
}

void testParsesSetupVerificationWithoutLosingLeadingZeroes() {
  TabletStatusResponse response;
  String error;
  CHECK(parseStatusPayload(validStatusPayload("IN_SETUP"), "3041", response, error));
  CHECK(response.status == TabletStatus::InSetup);
  CHECK(response.verification.required);
  CHECK(response.verification.state == VerificationState::WaitingForOperator);
  CHECK_STRING("0388", response.verification.responseCode);
  CHECK_STRING("WAITING_FOR_OPERATOR", toToken(response.verification.state));
}

void testParsesArmedVerificationAndDisplaysItsCode() {
  String payload = validStatusPayload("IN_SETUP");
  payload.replace("WAITING_FOR_OPERATOR", "ARMED");
  payload.replace("NOT_REPORTED", "ARMED");
  TabletStatusResponse response;
  String error;
  CHECK(parseStatusPayload(payload, "3041", response, error));
  CHECK(response.verification.state == VerificationState::Armed);
  CHECK_STRING("0388", response.verification.responseCode);
  CHECK_STRING("ARMED", toToken(response.verification.state));
  CHECK_STRING("ARMED", response.diagnostics.verificationResult);
  CHECK_STRING("ENTER RESPONSE CODE", verificationStateText(response.verification.state));
  CHECK_STRING("TYPE THIS CODE AT THE CNC", verificationInstructionText(response.verification.state));
}

void testAcceptsBlockingVerificationStatesWithoutAResponseCode() {
  const char* states[] = {"EXPIRED", "INVALIDATED", "UNAVAILABLE"};
  const VerificationState expected[] = {
      VerificationState::Expired,
      VerificationState::Invalidated,
      VerificationState::Unavailable};
  for (uint8_t index = 0; index < 3; ++index) {
    String payload = validStatusPayload("IN_SETUP");
    payload.replace(
        "\"state\":\"WAITING_FOR_OPERATOR\",\"response_code\":\"0388\"",
        String("\"state\":\"") + states[index] + "\"");
    TabletStatusResponse response;
    String error;
    CHECK(parseStatusPayload(payload, "3041", response, error));
    CHECK(response.verification.state == expected[index]);
    CHECK(response.verification.responseCode.isEmpty());
  }
}

void testRejectsUnsafeVerificationPayloads() {
  TabletStatusResponse response;
  String error;

  String missingVerification = validStatusPayload("IN_SETUP");
  missingVerification.replace(
      ",\"verification\":{\"required\":true,"
      "\"state\":\"WAITING_FOR_OPERATOR\",\"response_code\":\"0388\"}",
      "");
  CHECK(!parseStatusPayload(missingVerification, "3041", response, error));
  CHECK_STRING("verification must be an object for IN_SETUP", error);

  String numericCode = validStatusPayload("IN_SETUP");
  numericCode.replace("\"response_code\":\"0388\"", "\"response_code\":388");
  error = "";
  CHECK(!parseStatusPayload(numericCode, "3041", response, error));
  CHECK_STRING("verification.response_code must be a string while waiting", error);

  String malformedCode = validStatusPayload("IN_SETUP");
  malformedCode.replace("\"response_code\":\"0388\"", "\"response_code\":\"03A8\"");
  error = "";
  CHECK(!parseStatusPayload(malformedCode, "3041", response, error));
  CHECK_STRING("verification.response_code must contain 4 to 6 digits", error);

  String staleCode = validStatusPayload("IN_SETUP");
  staleCode.replace("WAITING_FOR_OPERATOR", "EXPIRED");
  error = "";
  CHECK(!parseStatusPayload(staleCode, "3041", response, error));
  CHECK_STRING("verification.response_code is forbidden for this state", error);

  String outsideSetup = validStatusPayload("READY_FOR_SETUP");
  outsideSetup.remove(outsideSetup.length() - 1);
  outsideSetup += ",\"verification\":{\"required\":true,"
      "\"state\":\"WAITING_FOR_OPERATOR\",\"response_code\":\"0388\"}}";
  error = "";
  CHECK(!parseStatusPayload(outsideSetup, "3041", response, error));
  CHECK_STRING("verification is allowed only for IN_SETUP", error);
}

void testRejectsMalformedJsonWithoutChangingPreviousResponse() {
  TabletStatusResponse response;
  response.revision = 99;
  response.tabletId = "last-known-good";
  String error;
  CHECK(!parseStatusPayload("{broken", "3041", response, error));
  CHECK(response.revision == 99);
  CHECK_STRING("last-known-good", response.tabletId);
  CHECK(error.startsWith("invalid JSON:"));
}

void testRejectsMissingFieldsAndUnsupportedStatus() {
  TabletStatusResponse response;
  String error;
  CHECK(!parseStatusPayload(
      "{\"revision\":17,\"tablet_id\":\"3041\"}", "3041", response, error));
  CHECK_STRING("machine, nc_run, part, and operation must be objects", error);

  error = "";
  CHECK(!parseStatusPayload(
      validStatusPayload("AUTOMATICALLY_FIXED"), "3041", response, error));
  CHECK_STRING("status is not a supported tablet status", error);
}

void testRejectsMismatchedTabletIdentity() {
  TabletStatusResponse response;
  String error;
  CHECK(!parseStatusPayload(validStatusPayload(), "other", response, error));
  CHECK_STRING("tablet_id does not match the requested tablet", error);
}

void testParsesServerTimestampedEventAcknowledgment() {
  TabletEventResponse response;
  String error;
  CHECK(parseEventPayload(
      "{\"tablet_id\":\"3041\",\"event_type\":\"SEND_TO_QC\","
      "\"timestamp\":\"2026-08-25T10:15:30Z\"}",
      "3041", TabletEventType::SendToQc, response, error));
  CHECK_STRING("3041", response.tabletId);
  CHECK_STRING("SEND_TO_QC", toToken(response.eventType));
  CHECK_STRING("2026-08-25T10:15:30Z", response.timestamp);
}

void testRejectsEventAcknowledgmentWithoutServerTimestamp() {
  TabletEventResponse response;
  String error;
  CHECK(!parseEventPayload(
      "{\"tablet_id\":\"3041\",\"event_type\":\"SEND_TO_QC\"}",
      "3041", TabletEventType::SendToQc, response, error));
  CHECK_STRING("timestamp must be a string", error);

  error = "";
  CHECK(!parseEventPayload(
      "{\"tablet_id\":\"3041\",\"event_type\":\"SEND_TO_QC\","
      "\"timestamp\":\"local-clock\"}",
      "3041", TabletEventType::SendToQc, response, error));
  CHECK_STRING("timestamp must be a UTC ISO-8601 instant", error);
}

void testProductionStatusLabelsAndToolPages() {
  CHECK_STRING("READY FOR SETUP", statusText(TabletStatus::ReadyForSetup));
  CHECK_STRING("IN SETUP", statusText(TabletStatus::InSetup));
  CHECK_STRING("IN SETUP RUN", statusText(TabletStatus::InSetupRun));
  CHECK_STRING("IN QUALITY CONTROL", statusText(TabletStatus::InQc));
  CHECK_STRING("READY FOR PRODUCTION", statusText(TabletStatus::ReadyForProduction));
  CHECK_STRING("IN PRODUCTION", statusText(TabletStatus::InProduction));
  CHECK_STRING("BLOCKED", statusText(TabletStatus::Blocked));
  CHECK_STRING("STATUS UNKNOWN", statusText(TabletStatus::Unknown));
  CHECK_STRING(
      "ENTER RESPONSE CODE",
      verificationStateText(VerificationState::WaitingForOperator));
  CHECK_STRING("CODE EXPIRED", verificationStateText(VerificationState::Expired));
  CHECK_STRING(
      "PRESS REFRESH - DO NOT START",
      verificationInstructionText(VerificationState::Invalidated));
  CHECK(toolPageCount(0) == 1);
  CHECK(toolPageCount(3) == 1);
  CHECK(toolPageCount(4) == 2);
  CHECK(toolPageCount(7) == 3);
  CHECK(normalizedToolPage(9, 7) == 2);
  CHECK(previousToolPage(0, 7) == 0);
  CHECK(previousToolPage(2, 7) == 1);
  CHECK(nextToolPage(0, 7) == 1);
  CHECK(nextToolPage(2, 7) == 2);
}

void testProductionScreenUsesStatusAndExplicitMachineNumber() {
  TabletStatusResponse status;
  status.tabletId = "3041";
  status.machine.id = "machine-opaque";
  status.machine.number = "M10";
  status.machine.name = "DMG MORI";
  status.part.number = "P-12345";
  status.part.name = "Housing";
  status.operation.number = 30;
  status.operation.name = "Finish Milling";
  status.status = TabletStatus::InQc;
  const ProductionScreenModel screen = makeProductionScreen(status);
  CHECK_STRING("DMG MORI", screen.machineName);
  CHECK_STRING("M10", screen.machineNumber);
  CHECK_STRING("T3041", screen.tabletId);
  CHECK_STRING("P-12345", screen.partNumber);
  CHECK(screen.operationNumber == 30);
  CHECK(screen.status == TabletStatus::InQc);
  CHECK(screen.toolCount == 0);
}

void testProductionScreenCarriesOnlyProjectedVerificationDisplayData() {
  TabletStatusResponse status;
  status.status = TabletStatus::InSetup;
  status.verification.required = true;
  status.verification.state = VerificationState::WaitingForOperator;
  status.verification.responseCode = "0388";
  const ProductionScreenModel screen = makeProductionScreen(status);
  CHECK(screen.status == TabletStatus::InSetup);
  CHECK(screen.verificationState == VerificationState::WaitingForOperator);
  CHECK_STRING("0388", screen.verificationResponseCode);
}

void testVerificationUnavailableScreenClearsThePreviousCode() {
  const ProductionScreenModel screen = makeVerificationUnavailableScreen("3041");
  CHECK(screen.status == TabletStatus::InSetup);
  CHECK(screen.verificationState == VerificationState::Unavailable);
  CHECK(screen.verificationResponseCode.isEmpty());
  CHECK_STRING("LAST CODE CLEARED", screen.partNumber);
  CHECK_STRING("T3041", screen.tabletId);
}

void testProductionScreenDoesNotPresentOpaqueIdAsMachineNumber() {
  TabletStatusResponse status;
  status.tabletId = "3041";
  status.machine.id = "machine-opaque";
  status.machine.name = "DMG MORI";
  const ProductionScreenModel screen = makeProductionScreen(status);
  CHECK_STRING("NUMBER UNKNOWN", screen.machineNumber);
}

void testDevelopmentFixtureIsPagedAndClearlyIdentifiedByCaller() {
  const ProductionScreenModel screen = makeDevelopmentFixture("3041");
  CHECK_STRING("DMG MORI", screen.machineName);
  CHECK_STRING("M10", screen.machineNumber);
  CHECK_STRING("T3041", screen.tabletId);
  CHECK(screen.toolCount == 7);
  CHECK(toolPageCount(screen.toolCount) == 3);
  CHECK_STRING("T01", screen.tools[0].tool);
  CHECK_STRING("H99", screen.tools[2].offset);
}

void testRevisionGateSkipsOnlyMatchingTabletAndRevision() {
  LastRevision missing;
  CHECK(shouldRefresh(missing, "3041", 17));

  LastRevision stored;
  stored.available = true;
  stored.tabletId = "3041";
  stored.revision = 17;
  CHECK(!shouldRefresh(stored, "3041", 17));
  CHECK(shouldRefresh(stored, "3041", 18));
  CHECK(shouldRefresh(stored, "other-tablet", 17));
  CHECK(shouldRefresh(stored, "3041", 0));
}

void testTabletStateMachineUsesOnlyPollingOrButtonWake() {
  const StatePolicy readyForSetup = policyFor(TabletStatus::ReadyForSetup);
  CHECK(readyForSetup.wakeMode == WakeMode::PollServer);
  CHECK(readyForSetup.pollIntervalSeconds == 15);
  CHECK(!readyForSetup.fallbackPolicy);

  const StatePolicy inSetup = policyFor(TabletStatus::InSetupRun);
  CHECK(inSetup.wakeMode == WakeMode::PhysicalButton);
  CHECK(inSetup.pollIntervalSeconds == 0);

  const StatePolicy awaitingVerification = policyFor(TabletStatus::InSetup);
  CHECK(awaitingVerification.wakeMode == WakeMode::PollServer);
  CHECK(awaitingVerification.pollIntervalSeconds == 15);
  CHECK(!awaitingVerification.fallbackPolicy);

  const StatePolicy inQc = policyFor(TabletStatus::InQc);
  CHECK(inQc.wakeMode == WakeMode::PollServer);
  CHECK(inQc.pollIntervalSeconds == 120);

  const StatePolicy readyForProduction =
      policyFor(TabletStatus::ReadyForProduction);
  CHECK(readyForProduction.wakeMode == WakeMode::PhysicalButton);
  CHECK(readyForProduction.pollIntervalSeconds == 0);

  const StatePolicy inProduction = policyFor(TabletStatus::InProduction);
  CHECK(inProduction.wakeMode == WakeMode::PhysicalButton);
  CHECK(inProduction.pollIntervalSeconds == 0);
}

void testUndefinedStatePoliciesPollConservatively() {
  const StatePolicy blocked = policyFor(TabletStatus::Blocked);
  CHECK(blocked.wakeMode == WakeMode::PollServer);
  CHECK(blocked.pollIntervalSeconds == 120);
  CHECK(blocked.fallbackPolicy);

  const StatePolicy unknown = policyFor(TabletStatus::Unknown);
  CHECK(unknown.wakeMode == WakeMode::PollServer);
  CHECK(unknown.pollIntervalSeconds == 120);
  CHECK(unknown.fallbackPolicy);
}

void testWakeButtonMappingUsesLongPressOnlyForSendToQc() {
  const uint64_t refreshMask = 1ULL << meimad::hardware::kRefreshButtonGpio;
  const uint64_t previousMask = 1ULL << meimad::hardware::kPageButtonGpio;
  const uint64_t actionMask = 1ULL << meimad::hardware::kActionButtonGpio;
  CHECK(actionForWakeMask(refreshMask, false) == ButtonAction::Refresh);
  CHECK(actionForWakeMask(refreshMask, true) == ButtonAction::ServiceScreen);
  CHECK(actionForWakeMask(previousMask, false) == ButtonAction::PreviousToolPage);
  CHECK(actionForWakeMask(actionMask, false) == ButtonAction::NextToolPage);
  CHECK(actionForWakeMask(actionMask, true) == ButtonAction::SendToQc);
  CHECK(actionForWakeMask(refreshMask | actionMask, true) == ButtonAction::None);
  CHECK(actionForWakeMask(0, false) == ButtonAction::None);
  CHECK(requiresServerContact(true, ButtonAction::ServiceScreen));

  EventSubmissionGuard guard;
  CHECK(guard.tryBegin());
  CHECK(guard.attempted());
  CHECK(!guard.tryBegin());
}

void testSendToQcIsAvailableOnlyDuringSetupRun() {
  CHECK(!canSendToQc(TabletStatus::ReadyForSetup));
  CHECK(!canSendToQc(TabletStatus::InSetup));
  CHECK(canSendToQc(TabletStatus::InSetupRun));
  CHECK(!canSendToQc(TabletStatus::InQc));
  CHECK(!canSendToQc(TabletStatus::ReadyForProduction));
  CHECK(!canSendToQc(TabletStatus::InProduction));
  CHECK(!canSendToQc(TabletStatus::Blocked));
  CHECK(!canSendToQc(TabletStatus::Unknown));
}

void testOnlyNetworkActionsContactServerAfterButtonWake() {
  CHECK(requiresServerContact(false, ButtonAction::None));
  CHECK(requiresServerContact(false, ButtonAction::Refresh));
  CHECK(requiresServerContact(true, ButtonAction::Refresh));
  CHECK(requiresServerContact(true, ButtonAction::SendToQc));
  CHECK(!requiresServerContact(true, ButtonAction::PreviousToolPage));
  CHECK(!requiresServerContact(true, ButtonAction::NextToolPage));
  CHECK(!requiresServerContact(true, ButtonAction::None));
}

void testBatteryTelemetryUsesVoltageUntilPercentageIsCalibrated() {
  BatteryTelemetry telemetry;
  CHECK(!hasValidBatteryVoltage(telemetry));
  CHECK_STRING("", formatBatteryVoltageHeader(telemetry));

  telemetry.voltageAvailable = true;
  telemetry.voltage = 3.91f;
  CHECK(hasValidBatteryVoltage(telemetry));
  CHECK_STRING("3.910", formatBatteryVoltageHeader(telemetry));
  CHECK(!telemetry.percentAvailable);

  telemetry.voltage = 12.1f;
  CHECK(!hasValidBatteryVoltage(telemetry));
  CHECK_STRING("", formatBatteryVoltageHeader(telemetry));
}

void testCompileTimeDemoCoversEveryRequiredScenario() {
  const DemoScenario expected[] = {
      DemoScenario::ReadyForSetup,
      DemoScenario::SetupVerification,
      DemoScenario::SetupVerificationExpired,
      DemoScenario::InSetupRun,
      DemoScenario::InQc,
      DemoScenario::ReadyForProduction,
      DemoScenario::InProduction,
      DemoScenario::Blocked,
      DemoScenario::WifiError,
      DemoScenario::ServerError,
      DemoScenario::UnregisteredTablet,
      DemoScenario::LowBattery};
  for (uint8_t index = 0; index < kScenarioCount; ++index) {
    const DemoScenario scenario = scenarioForIndex(index);
    CHECK(scenario == expected[index]);
    CHECK(nextScenarioIndex(index) == (index + 1) % kScenarioCount);
    const ProductionScreenModel screen = makeScreen(scenario);
    CHECK(screen.notice.startsWith("DEMO - "));
  }

  CHECK(makeScreen(DemoScenario::ReadyForSetup).status == TabletStatus::ReadyForSetup);
  CHECK(makeScreen(DemoScenario::SetupVerification).status == TabletStatus::InSetup);
  CHECK_STRING(
      "0388",
      makeScreen(DemoScenario::SetupVerification).verificationResponseCode);
  CHECK(
      makeScreen(DemoScenario::SetupVerificationExpired).verificationState
      == VerificationState::Expired);
  CHECK(makeScreen(DemoScenario::InSetupRun).status == TabletStatus::InSetupRun);
  CHECK(makeScreen(DemoScenario::InQc).status == TabletStatus::InQc);
  CHECK(makeScreen(DemoScenario::ReadyForProduction).status == TabletStatus::ReadyForProduction);
  CHECK(makeScreen(DemoScenario::InProduction).status == TabletStatus::InProduction);
  CHECK(makeScreen(DemoScenario::Blocked).status == TabletStatus::Blocked);
  CHECK(makeScreen(DemoScenario::WifiError).status == TabletStatus::Unknown);
  CHECK(makeScreen(DemoScenario::ServerError).status == TabletStatus::Unknown);
  CHECK_STRING("UNREGISTERED TABLET", makeScreen(DemoScenario::UnregisteredTablet).tabletId);
  CHECK(makeScreen(DemoScenario::LowBattery).lowBattery);
}
}  // namespace

void setup() {
  Serial.begin(115200);
  delay(100);
  testParsesExampleAndNormalizesNumericIds();
  testParsesExplicitMachineNumber();
  testAcceptsEveryInitialStatus();
  testParsesSafeServiceDiagnostics();
  testParsesSetupVerificationWithoutLosingLeadingZeroes();
  testParsesArmedVerificationAndDisplaysItsCode();
  testAcceptsBlockingVerificationStatesWithoutAResponseCode();
  testRejectsUnsafeVerificationPayloads();
  testRejectsMalformedJsonWithoutChangingPreviousResponse();
  testRejectsMissingFieldsAndUnsupportedStatus();
  testRejectsMismatchedTabletIdentity();
  testParsesServerTimestampedEventAcknowledgment();
  testRejectsEventAcknowledgmentWithoutServerTimestamp();
  testProductionStatusLabelsAndToolPages();
  testProductionScreenUsesStatusAndExplicitMachineNumber();
  testProductionScreenCarriesOnlyProjectedVerificationDisplayData();
  testVerificationUnavailableScreenClearsThePreviousCode();
  testProductionScreenDoesNotPresentOpaqueIdAsMachineNumber();
  testDevelopmentFixtureIsPagedAndClearlyIdentifiedByCaller();
  testRevisionGateSkipsOnlyMatchingTabletAndRevision();
  testTabletStateMachineUsesOnlyPollingOrButtonWake();
  testUndefinedStatePoliciesPollConservatively();
  testWakeButtonMappingUsesLongPressOnlyForSendToQc();
  testSendToQcIsAvailableOnlyDuringSetupRun();
  testOnlyNetworkActionsContactServerAfterButtonWake();
  testBatteryTelemetryUsesVoltageUntilPercentageIsCalibrated();
  testCompileTimeDemoCoversEveryRequiredScenario();
  Serial.printf("Tablet API contract tests: %s (%lu failures)\n",
                failures == 0 ? "PASS" : "FAIL",
                static_cast<unsigned long>(failures));
}

void loop() {}
