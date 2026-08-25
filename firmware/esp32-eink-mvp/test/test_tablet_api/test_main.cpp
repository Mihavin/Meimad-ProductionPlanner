#include <Arduino.h>

#include "../../src/tablet_api.cpp"
#include "../../src/production_ui.cpp"
#include "../../src/screen_revision.cpp"
#include "../../src/tablet_state_machine.cpp"
#include "../../src/button_input.cpp"

using namespace meimad::tablet_api;
using namespace meimad::production_ui;
using namespace meimad::screen_revision;
using namespace meimad::tablet_state_machine;
using namespace meimad::button_input;

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
  return String("{")
      + "\"revision\":17,"
      + "\"tablet_id\":\"3041\","
      + "\"machine\":{\"id\":10,\"name\":\"DMG MORI 10\"},"
      + "\"nc_run\":{\"id\":845},"
      + "\"part\":{\"number\":\"P-12345\",\"name\":\"Housing\"},"
      + "\"operation\":{\"number\":30,\"name\":\"Finish Milling\"},"
      + "\"status\":\"" + status + "\"}";
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
      "READY_FOR_SETUP", "IN_SETUP_RUN", "IN_QC", "READY_FOR_PRODUCTION",
      "IN_PRODUCTION", "BLOCKED", "UNKNOWN"};
  for (const char* status : statuses) {
    TabletStatusResponse response;
    String error;
    CHECK(parseStatusPayload(validStatusPayload(status), "3041", response, error));
    CHECK_STRING(status, toToken(response.status));
  }
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
  CHECK_STRING("IN SETUP", statusText(TabletStatus::InSetupRun));
  CHECK_STRING("IN QUALITY CONTROL", statusText(TabletStatus::InQc));
  CHECK_STRING("READY FOR PRODUCTION", statusText(TabletStatus::ReadyForProduction));
  CHECK_STRING("IN PRODUCTION", statusText(TabletStatus::InProduction));
  CHECK_STRING("BLOCKED", statusText(TabletStatus::Blocked));
  CHECK_STRING("STATUS UNKNOWN", statusText(TabletStatus::Unknown));
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
  CHECK(readyForSetup.pollIntervalSeconds == 120);
  CHECK(!readyForSetup.fallbackPolicy);

  const StatePolicy inSetup = policyFor(TabletStatus::InSetupRun);
  CHECK(inSetup.wakeMode == WakeMode::PhysicalButton);
  CHECK(inSetup.pollIntervalSeconds == 0);

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
  CHECK(actionForWakeMask(previousMask, false) == ButtonAction::PreviousToolPage);
  CHECK(actionForWakeMask(actionMask, false) == ButtonAction::NextToolPage);
  CHECK(actionForWakeMask(actionMask, true) == ButtonAction::SendToQc);
  CHECK(actionForWakeMask(refreshMask | actionMask, true) == ButtonAction::None);
  CHECK(actionForWakeMask(0, false) == ButtonAction::None);

  EventSubmissionGuard guard;
  CHECK(guard.tryBegin());
  CHECK(guard.attempted());
  CHECK(!guard.tryBegin());
}

void testSendToQcIsAvailableOnlyDuringSetupRun() {
  CHECK(!canSendToQc(TabletStatus::ReadyForSetup));
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
}  // namespace

void setup() {
  Serial.begin(115200);
  delay(100);
  testParsesExampleAndNormalizesNumericIds();
  testParsesExplicitMachineNumber();
  testAcceptsEveryInitialStatus();
  testRejectsMalformedJsonWithoutChangingPreviousResponse();
  testRejectsMissingFieldsAndUnsupportedStatus();
  testRejectsMismatchedTabletIdentity();
  testParsesServerTimestampedEventAcknowledgment();
  testRejectsEventAcknowledgmentWithoutServerTimestamp();
  testProductionStatusLabelsAndToolPages();
  testProductionScreenUsesStatusAndExplicitMachineNumber();
  testProductionScreenDoesNotPresentOpaqueIdAsMachineNumber();
  testDevelopmentFixtureIsPagedAndClearlyIdentifiedByCaller();
  testRevisionGateSkipsOnlyMatchingTabletAndRevision();
  testTabletStateMachineUsesOnlyPollingOrButtonWake();
  testUndefinedStatePoliciesPollConservatively();
  testWakeButtonMappingUsesLongPressOnlyForSendToQc();
  testSendToQcIsAvailableOnlyDuringSetupRun();
  testOnlyNetworkActionsContactServerAfterButtonWake();
  Serial.printf("Tablet API contract tests: %s (%lu failures)\n",
                failures == 0 ? "PASS" : "FAIL",
                static_cast<unsigned long>(failures));
}

void loop() {}
