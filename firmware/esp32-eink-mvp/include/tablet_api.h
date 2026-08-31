#pragma once

#include <Arduino.h>

namespace meimad::tablet_api {

enum class TabletStatus {
  ReadyForSetup,
  InSetup,
  InSetupRun,
  InQc,
  ReadyForProduction,
  InProduction,
  Blocked,
  Unknown
};

enum class VerificationState {
  None,
  WaitingForOperator,
  Expired,
  Invalidated,
  Unavailable
};

enum class TabletEventType {
  SendToQc
};

struct TabletMachine {
  String id;
  String number;
  String name;
};

struct TabletNcRun {
  String id;
};

struct TabletPart {
  String number;
  String name;
};

struct TabletOperation {
  int32_t number = 0;
  String name;
};

struct TabletVerification {
  bool required = false;
  VerificationState state = VerificationState::None;
  // Kept as text so fixed-width values such as "0388" retain leading zeroes.
  String responseCode;
};

struct TabletDiagnostics {
  String verificationResult = "NOT_REPORTED";
  int32_t protectedMacroVersion = -1;
};

struct TabletStatusResponse {
  uint32_t revision = 0;
  String tabletId;
  TabletMachine machine;
  TabletNcRun ncRun;
  TabletPart part;
  TabletOperation operation;
  TabletStatus status = TabletStatus::Unknown;
  TabletVerification verification;
  TabletDiagnostics diagnostics;
};

// The request deliberately contains no timestamp. Server time is authoritative.
struct TabletEventRequest {
  TabletEventType eventType = TabletEventType::SendToQc;
};

struct TabletEventResponse {
  String tabletId;
  TabletEventType eventType = TabletEventType::SendToQc;
  String timestamp;
};

// Device health is non-planning telemetry. Voltage is sent whenever the
// tablet calls the Server; percentage remains absent until the AA power path
// has a measured discharge curve.
struct BatteryTelemetry {
  bool voltageAvailable = false;
  float voltage = 0.0f;
  bool percentAvailable = false;
  uint8_t percent = 0;
};

enum class ApiResultCode {
  Success,
  NotConfigured,
  TransportError,
  HttpError,
  MalformedResponse
};

struct ApiResult {
  ApiResultCode code = ApiResultCode::TransportError;
  int httpStatus = 0;
  String detail;

  bool succeeded() const { return code == ApiResultCode::Success; }
};

class TabletApiClient {
 public:
  TabletApiClient(
      const String& serverBaseUrl,
      const BatteryTelemetry& batteryTelemetry = BatteryTelemetry());

  ApiResult getStatus(const String& tabletId, TabletStatusResponse& response) const;
  ApiResult sendEvent(
      const String& tabletId,
      const TabletEventRequest& request,
      TabletEventResponse& response) const;

 private:
  String serverBaseUrl_;
  BatteryTelemetry batteryTelemetry_;
};

bool parseStatusPayload(
    const String& payload,
    const String& requestedTabletId,
    TabletStatusResponse& response,
    String& error);
bool parseEventPayload(
    const String& payload,
    const String& requestedTabletId,
    TabletEventType requestedEventType,
    TabletEventResponse& response,
    String& error);

const char* toToken(TabletStatus status);
const char* toToken(VerificationState state);
const char* toToken(TabletEventType eventType);
const char* toText(ApiResultCode result);
bool hasValidBatteryVoltage(const BatteryTelemetry& telemetry);
String formatBatteryVoltageHeader(const BatteryTelemetry& telemetry);

}  // namespace meimad::tablet_api
