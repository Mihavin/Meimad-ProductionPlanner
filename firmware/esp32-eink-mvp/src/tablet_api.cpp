#include "tablet_api.h"

#include <ArduinoJson.h>
#include <HTTPClient.h>

#include <limits.h>
#include <math.h>

namespace meimad::tablet_api {
namespace {
constexpr uint16_t kHttpConnectTimeoutMs = 5000;
constexpr uint16_t kHttpRequestTimeoutMs = 7000;

ApiResult result(ApiResultCode code, int httpStatus, const String& detail) {
  ApiResult value;
  value.code = code;
  value.httpStatus = httpStatus;
  value.detail = detail;
  return value;
}

String encodePathSegment(const String& value) {
  const char* digits = "0123456789ABCDEF";
  String encoded;
  encoded.reserve(value.length() * 3);
  for (size_t index = 0; index < value.length(); ++index) {
    const uint8_t character = static_cast<uint8_t>(value[index]);
    const bool unreserved =
        (character >= 'A' && character <= 'Z') ||
        (character >= 'a' && character <= 'z') ||
        (character >= '0' && character <= '9') ||
        character == '-' || character == '.' || character == '_' || character == '~';
    if (unreserved) {
      encoded += static_cast<char>(character);
    } else {
      encoded += '%';
      encoded += digits[character >> 4];
      encoded += digits[character & 0x0F];
    }
  }
  return encoded;
}

void addAuthorization(HTTPClient& http, const String& bearerToken) {
  if (!bearerToken.isEmpty()) http.addHeader("Authorization", "Bearer " + bearerToken);
}

void addBatteryTelemetry(HTTPClient& http, const BatteryTelemetry& telemetry) {
  const String voltage = formatBatteryVoltageHeader(telemetry);
  if (!voltage.isEmpty()) {
    http.addHeader("X-Meimad-Battery-Voltage", voltage);
  }
  if (telemetry.percentAvailable && telemetry.percent <= 100) {
    http.addHeader("X-Meimad-Battery-Percent", String(telemetry.percent));
  }
}

bool readRequiredString(
    JsonVariantConst value,
    const char* field,
    String& target,
    String& error) {
  if (!value.is<const char*>()) {
    error = String(field) + " must be a string";
    return false;
  }
  target = value.as<String>();
  if (target.isEmpty()) {
    error = String(field) + " must not be empty";
    return false;
  }
  return true;
}

bool readRequiredId(
    JsonVariantConst value,
    const char* field,
    String& target,
    String& error) {
  if (value.is<const char*>()) return readRequiredString(value, field, target, error);
  if (value.is<int64_t>()) {
    target = String(value.as<int64_t>());
    return true;
  }
  if (value.is<uint64_t>()) {
    target = String(value.as<uint64_t>());
    return true;
  }
  error = String(field) + " must be a string or integer";
  return false;
}

bool parseStatusToken(const String& value, TabletStatus& status) {
  if (value == "READY_FOR_SETUP") status = TabletStatus::ReadyForSetup;
  else if (value == "IN_SETUP_RUN") status = TabletStatus::InSetupRun;
  else if (value == "IN_QC") status = TabletStatus::InQc;
  else if (value == "READY_FOR_PRODUCTION") status = TabletStatus::ReadyForProduction;
  else if (value == "IN_PRODUCTION") status = TabletStatus::InProduction;
  else if (value == "BLOCKED") status = TabletStatus::Blocked;
  else if (value == "UNKNOWN") status = TabletStatus::Unknown;
  else return false;
  return true;
}

bool parseEventToken(const String& value, TabletEventType& eventType) {
  if (value != "SEND_TO_QC") return false;
  eventType = TabletEventType::SendToQc;
  return true;
}

bool isDigitAt(const String& value, size_t index) {
  return index < value.length() && value[index] >= '0' && value[index] <= '9';
}

int twoDigitsAt(const String& value, size_t index) {
  return (value[index] - '0') * 10 + value[index + 1] - '0';
}

bool isUtcTimestamp(const String& value) {
  if (value.length() < 20 || value[value.length() - 1] != 'Z'
      || value[4] != '-' || value[7] != '-' || value[10] != 'T'
      || value[13] != ':' || value[16] != ':') {
    return false;
  }
  const size_t requiredDigits[] = {0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18};
  for (size_t index : requiredDigits) {
    if (!isDigitAt(value, index)) return false;
  }
  if (value.length() > 20) {
    if (value.length() < 22 || value[19] != '.') return false;
    for (size_t index = 20; index + 1 < value.length(); ++index) {
      if (!isDigitAt(value, index)) return false;
    }
  }
  return twoDigitsAt(value, 5) >= 1 && twoDigitsAt(value, 5) <= 12
      && twoDigitsAt(value, 8) >= 1 && twoDigitsAt(value, 8) <= 31
      && twoDigitsAt(value, 11) <= 23
      && twoDigitsAt(value, 14) <= 59
      && twoDigitsAt(value, 17) <= 59;
}

bool parseStatusResponse(
    const String& payload,
    const String& requestedTabletId,
    TabletStatusResponse& response,
    String& error) {
  JsonDocument document;
  const DeserializationError jsonError = deserializeJson(document, payload);
  if (jsonError) {
    error = String("invalid JSON: ") + jsonError.c_str();
    return false;
  }
  if (!document.is<JsonObject>()) {
    error = "response root must be an object";
    return false;
  }

  JsonObjectConst root = document.as<JsonObjectConst>();
  if (!root["revision"].is<uint32_t>()) {
    error = "revision must be an unsigned 32-bit integer";
    return false;
  }

  TabletStatusResponse parsed;
  parsed.revision = root["revision"].as<uint32_t>();
  if (!readRequiredString(root["tablet_id"], "tablet_id", parsed.tabletId, error)) return false;
  if (parsed.tabletId != requestedTabletId) {
    error = "tablet_id does not match the requested tablet";
    return false;
  }

  JsonObjectConst machine = root["machine"].as<JsonObjectConst>();
  JsonObjectConst ncRun = root["nc_run"].as<JsonObjectConst>();
  JsonObjectConst part = root["part"].as<JsonObjectConst>();
  JsonObjectConst operation = root["operation"].as<JsonObjectConst>();
  if (machine.isNull() || ncRun.isNull() || part.isNull() || operation.isNull()) {
    error = "machine, nc_run, part, and operation must be objects";
    return false;
  }

  if (!readRequiredId(machine["id"], "machine.id", parsed.machine.id, error)
      || !readRequiredString(machine["name"], "machine.name", parsed.machine.name, error)
      || !readRequiredId(ncRun["id"], "nc_run.id", parsed.ncRun.id, error)
      || !readRequiredString(part["number"], "part.number", parsed.part.number, error)
      || !readRequiredString(part["name"], "part.name", parsed.part.name, error)) {
    return false;
  }
  if (!machine["number"].isNull()
      && !readRequiredString(
          machine["number"], "machine.number", parsed.machine.number, error)) {
    return false;
  }

  if (!operation["number"].is<int32_t>()) {
    error = "operation.number must be a 32-bit integer";
    return false;
  }
  parsed.operation.number = operation["number"].as<int32_t>();
  if (parsed.operation.number < 0) {
    error = "operation.number must not be negative";
    return false;
  }
  if (!readRequiredString(operation["name"], "operation.name", parsed.operation.name, error)) {
    return false;
  }

  String status;
  if (!readRequiredString(root["status"], "status", status, error)
      || !parseStatusToken(status, parsed.status)) {
    error = "status is not a supported tablet status";
    return false;
  }

  response = parsed;
  return true;
}

bool parseEventResponse(
    const String& payload,
    const String& requestedTabletId,
    TabletEventType requestedEventType,
    TabletEventResponse& response,
    String& error) {
  JsonDocument document;
  const DeserializationError jsonError = deserializeJson(document, payload);
  if (jsonError) {
    error = String("invalid JSON: ") + jsonError.c_str();
    return false;
  }
  if (!document.is<JsonObject>()) {
    error = "response root must be an object";
    return false;
  }

  JsonObjectConst root = document.as<JsonObjectConst>();
  TabletEventResponse parsed;
  if (!readRequiredString(root["tablet_id"], "tablet_id", parsed.tabletId, error)
      || !readRequiredString(root["timestamp"], "timestamp", parsed.timestamp, error)) {
    return false;
  }
  if (parsed.tabletId != requestedTabletId) {
    error = "tablet_id does not match the requested tablet";
    return false;
  }
  if (!isUtcTimestamp(parsed.timestamp)) {
    error = "timestamp must be a UTC ISO-8601 instant";
    return false;
  }

  String eventType;
  if (!readRequiredString(root["event_type"], "event_type", eventType, error)
      || !parseEventToken(eventType, parsed.eventType)
      || parsed.eventType != requestedEventType) {
    error = "event_type does not match the requested event";
    return false;
  }

  response = parsed;
  return true;
}

ApiResult finishRequest(
    HTTPClient& http,
    int httpStatus,
    const String& operation,
    String& responsePayload) {
  Serial.printf("Tablet API %s: HTTP %d\n", operation.c_str(), httpStatus);
  if (httpStatus < 0) {
    const String transportError = HTTPClient::errorToString(httpStatus);
    http.end();
    return result(ApiResultCode::TransportError, httpStatus, transportError);
  }

  responsePayload = http.getString();
  http.end();
  if (httpStatus < 200 || httpStatus >= 300) {
    return result(ApiResultCode::HttpError, httpStatus, "server returned a non-success status");
  }
  return result(ApiResultCode::Success, httpStatus, String());
}
}  // namespace

TabletApiClient::TabletApiClient(
    const String& serverBaseUrl,
    const String& bearerToken,
    const BatteryTelemetry& batteryTelemetry)
    : serverBaseUrl_(serverBaseUrl),
      bearerToken_(bearerToken),
      batteryTelemetry_(batteryTelemetry) {
  while (serverBaseUrl_.endsWith("/")) serverBaseUrl_.remove(serverBaseUrl_.length() - 1);
}

bool parseStatusPayload(
    const String& payload,
    const String& requestedTabletId,
    TabletStatusResponse& response,
    String& error) {
  return parseStatusResponse(payload, requestedTabletId, response, error);
}

bool parseEventPayload(
    const String& payload,
    const String& requestedTabletId,
    TabletEventType requestedEventType,
    TabletEventResponse& response,
    String& error) {
  return parseEventResponse(
      payload, requestedTabletId, requestedEventType, response, error);
}

String formatBatteryVoltageHeader(const BatteryTelemetry& telemetry) {
  if (!telemetry.voltageAvailable || !isfinite(telemetry.voltage)
      || telemetry.voltage <= 0.0f || telemetry.voltage > 12.0f) {
    return String();
  }
  return String(telemetry.voltage, 3);
}

ApiResult TabletApiClient::getStatus(
    const String& tabletId,
    TabletStatusResponse& response) const {
  if (serverBaseUrl_.isEmpty() || tabletId.isEmpty()) {
    return result(ApiResultCode::NotConfigured, 0, "server URL and tablet ID are required");
  }

  HTTPClient http;
  const String url = serverBaseUrl_ + "/api/tablets/" + encodePathSegment(tabletId) + "/status";
  http.setConnectTimeout(kHttpConnectTimeoutMs);
  http.setTimeout(kHttpRequestTimeoutMs);
  if (!http.begin(url)) {
    return result(ApiResultCode::TransportError, 0, "could not initialize HTTP request");
  }
  addAuthorization(http, bearerToken_);
  addBatteryTelemetry(http, batteryTelemetry_);

  const int httpStatus = http.GET();
  String payload;
  ApiResult requestResult = finishRequest(http, httpStatus, "GET status", payload);
  if (!requestResult.succeeded()) return requestResult;

  String parseError;
  if (!parseStatusPayload(payload, tabletId, response, parseError)) {
    return result(ApiResultCode::MalformedResponse, httpStatus, parseError);
  }
  return requestResult;
}

ApiResult TabletApiClient::sendEvent(
    const String& tabletId,
    const TabletEventRequest& request,
    TabletEventResponse& response) const {
  if (serverBaseUrl_.isEmpty() || tabletId.isEmpty()) {
    return result(ApiResultCode::NotConfigured, 0, "server URL and tablet ID are required");
  }

  JsonDocument document;
  document["event_type"] = toToken(request.eventType);
  String requestPayload;
  serializeJson(document, requestPayload);

  HTTPClient http;
  const String url = serverBaseUrl_ + "/api/tablets/" + encodePathSegment(tabletId) + "/events";
  http.setConnectTimeout(kHttpConnectTimeoutMs);
  http.setTimeout(kHttpRequestTimeoutMs);
  if (!http.begin(url)) {
    return result(ApiResultCode::TransportError, 0, "could not initialize HTTP request");
  }
  addAuthorization(http, bearerToken_);
  addBatteryTelemetry(http, batteryTelemetry_);
  http.addHeader("Content-Type", "application/json");

  const int httpStatus = http.POST(requestPayload);
  String payload;
  ApiResult requestResult = finishRequest(http, httpStatus, "POST event", payload);
  if (!requestResult.succeeded()) return requestResult;

  String parseError;
  if (!parseEventPayload(payload, tabletId, request.eventType, response, parseError)) {
    return result(ApiResultCode::MalformedResponse, httpStatus, parseError);
  }
  return requestResult;
}

const char* toToken(TabletStatus status) {
  switch (status) {
    case TabletStatus::ReadyForSetup: return "READY_FOR_SETUP";
    case TabletStatus::InSetupRun: return "IN_SETUP_RUN";
    case TabletStatus::InQc: return "IN_QC";
    case TabletStatus::ReadyForProduction: return "READY_FOR_PRODUCTION";
    case TabletStatus::InProduction: return "IN_PRODUCTION";
    case TabletStatus::Blocked: return "BLOCKED";
    case TabletStatus::Unknown: return "UNKNOWN";
  }
  return "UNKNOWN";
}

const char* toToken(TabletEventType eventType) {
  switch (eventType) {
    case TabletEventType::SendToQc: return "SEND_TO_QC";
  }
  return "SEND_TO_QC";
}

const char* toText(ApiResultCode resultCode) {
  switch (resultCode) {
    case ApiResultCode::Success: return "success";
    case ApiResultCode::NotConfigured: return "not configured";
    case ApiResultCode::TransportError: return "transport error";
    case ApiResultCode::HttpError: return "HTTP error";
    case ApiResultCode::MalformedResponse: return "malformed response";
  }
  return "unknown error";
}

}  // namespace meimad::tablet_api
