#include <Arduino.h>

#include "../../src/tablet_api.cpp"

using namespace meimad::tablet_api;

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
}  // namespace

void setup() {
  Serial.begin(115200);
  delay(100);
  testParsesExampleAndNormalizesNumericIds();
  testAcceptsEveryInitialStatus();
  testRejectsMalformedJsonWithoutChangingPreviousResponse();
  testRejectsMissingFieldsAndUnsupportedStatus();
  testRejectsMismatchedTabletIdentity();
  testParsesServerTimestampedEventAcknowledgment();
  testRejectsEventAcknowledgmentWithoutServerTimestamp();
  Serial.printf("Tablet API contract tests: %s (%lu failures)\n",
                failures == 0 ? "PASS" : "FAIL",
                static_cast<unsigned long>(failures));
}

void loop() {}
