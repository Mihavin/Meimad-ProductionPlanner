#pragma once

#include <Arduino.h>

#if !MEIMAD_EINK_DRIVER_STUB
#include <TFT_eSPI.h>
#endif

namespace meimad::service_ui {

struct ServiceScreenModel {
  String tabletId;
  String hardwareMac;
  String firmwareVersion;
  String machineBinding;
  String wifiSsid;
  String ipAddress;
  String rssi;
  String serverAddress;
  String lastSuccessfulContact;
  String lastHttpResult;
  String workflowState;
  String revision;
  String batteryVoltage;
  String wakeReason;
  String lastRefreshDuration;
  String verificationResult;
  String protectedMacroVersion;
};

void logServiceDiagnostics(const ServiceScreenModel& model);

#if !MEIMAD_EINK_DRIVER_STUB
void drawServiceScreen(EPaper& display, const ServiceScreenModel& model);
#endif

}  // namespace meimad::service_ui
