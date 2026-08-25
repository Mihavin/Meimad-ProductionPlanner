#include <Arduino.h>

#include "../../src/tablet_api.cpp"
#include "../../src/production_ui.cpp"
#include "../../src/screen_revision.cpp"

using namespace meimad::tablet_api;
using namespace meimad::production_ui;
using namespace meimad::screen_revision;

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
  Serial.printf("Tablet API contract tests: %s (%lu failures)\n",
                failures == 0 ? "PASS" : "FAIL",
                static_cast<unsigned long>(failures));
}

void loop() {}
