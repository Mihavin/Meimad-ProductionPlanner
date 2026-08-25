#pragma once

#include <Arduino.h>

namespace meimad::tablet_api {

enum class TabletStatus {
  ReadyForSetup,
  InSetupRun,
  InQc,
  ReadyForProduction,
  InProduction,
  Blocked,
  Unknown
};

enum class TabletEventType {
  SendToQc
};

struct TabletMachine {
  String id;
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

struct TabletStatusResponse {
  uint32_t revision = 0;
  String tabletId;
  TabletMachine machine;
  TabletNcRun ncRun;
  TabletPart part;
  TabletOperation operation;
  TabletStatus status = TabletStatus::Unknown;
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
  TabletApiClient(const String& serverBaseUrl, const String& bearerToken = String());

  ApiResult getStatus(const String& tabletId, TabletStatusResponse& response) const;
  ApiResult sendEvent(
      const String& tabletId,
      const TabletEventRequest& request,
      TabletEventResponse& response) const;

 private:
  String serverBaseUrl_;
  String bearerToken_;
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
const char* toToken(TabletEventType eventType);
const char* toText(ApiResultCode result);

}  // namespace meimad::tablet_api
