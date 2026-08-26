#include "service_ui.h"

#include "firmware_logging.h"

namespace meimad::service_ui {
namespace {
String available(const String& value) {
  return value.isEmpty() ? "UNAVAILABLE" : value;
}

#if !MEIMAD_EINK_DRIVER_STUB
String fitText(EPaper& display, const String& value, int maximumWidth) {
  const String shown = available(value);
  if (display.textWidth(shown) <= maximumWidth) return shown;
  String fitted = shown;
  while (!fitted.isEmpty() && display.textWidth(fitted + "...") > maximumWidth) {
    fitted.remove(fitted.length() - 1);
  }
  return fitted + "...";
}

void drawField(
    EPaper& display,
    const char* label,
    const String& value,
    int x,
    int y,
    int width) {
  display.setTextSize(1);
  display.drawString(label, x, y);
  display.setTextSize(2);
  display.drawString(fitText(display, value, width), x, y + 12);
}
#endif
}  // namespace

void logServiceDiagnostics(const ServiceScreenModel& model) {
  MEIMAD_LOG(
      "SERVICE",
      "tablet_id=%s mac=%s firmware=%s machine=%s wifi_ssid=%s ip=%s rssi=%s",
      available(model.tabletId).c_str(),
      available(model.hardwareMac).c_str(),
      available(model.firmwareVersion).c_str(),
      available(model.machineBinding).c_str(),
      available(model.wifiSsid).c_str(),
      available(model.ipAddress).c_str(),
      available(model.rssi).c_str());
  MEIMAD_LOG(
      "SERVICE",
      "server=%s last_contact=%s last_http=%s workflow=%s revision=%s battery=%s wake=%s refresh=%s verification=%s macro_version=%s",
      available(model.serverAddress).c_str(),
      available(model.lastSuccessfulContact).c_str(),
      available(model.lastHttpResult).c_str(),
      available(model.workflowState).c_str(),
      available(model.revision).c_str(),
      available(model.batteryVoltage).c_str(),
      available(model.wakeReason).c_str(),
      available(model.lastRefreshDuration).c_str(),
      available(model.verificationResult).c_str(),
      available(model.protectedMacroVersion).c_str());
}

#if !MEIMAD_EINK_DRIVER_STUB
void drawServiceScreen(EPaper& display, const ServiceScreenModel& model) {
  constexpr int kLeft = 20;
  constexpr int kDivider = 398;
  constexpr int kRightColumn = 420;
  constexpr int kColumnWidth = 350;
  constexpr int kFirstRow = 60;
  constexpr int kRowHeight = 46;

  display.begin();
  display.fillScreen(TFT_WHITE);
  display.setTextColor(TFT_BLACK, TFT_WHITE);
  display.setTextSize(3);
  display.drawString("TABLET SERVICE / DEBUG", kLeft, 14);
  display.setTextSize(1);
  display.drawString("HOLD D1 / REFRESH 1.2s TO OPEN", 568, 22);
  display.drawFastHLine(kLeft, 50, 760, TFT_BLACK);
  display.drawFastVLine(kDivider, 58, 402, TFT_BLACK);

  drawField(display, "TABLET ID", model.tabletId, kLeft, kFirstRow, kColumnWidth);
  drawField(display, "HARDWARE MAC", model.hardwareMac, kLeft, kFirstRow + kRowHeight, kColumnWidth);
  drawField(display, "FIRMWARE", model.firmwareVersion, kLeft, kFirstRow + 2 * kRowHeight, kColumnWidth);
  drawField(display, "MACHINE BINDING", model.machineBinding, kLeft, kFirstRow + 3 * kRowHeight, kColumnWidth);
  drawField(display, "WI-FI SSID", model.wifiSsid, kLeft, kFirstRow + 4 * kRowHeight, kColumnWidth);
  const String network = model.ipAddress.isEmpty() && model.rssi.isEmpty()
      ? String()
      : model.ipAddress + "  " + model.rssi;
  drawField(display, "IP / RSSI", network, kLeft, kFirstRow + 5 * kRowHeight, kColumnWidth);
  drawField(display, "BATTERY", model.batteryVoltage, kLeft, kFirstRow + 6 * kRowHeight, kColumnWidth);
  drawField(display, "WAKE REASON", model.wakeReason, kLeft, kFirstRow + 7 * kRowHeight, kColumnWidth);

  drawField(display, "SERVER", model.serverAddress, kRightColumn, kFirstRow, kColumnWidth);
  drawField(display, "LAST SUCCESSFUL CONTACT", model.lastSuccessfulContact, kRightColumn, kFirstRow + kRowHeight, kColumnWidth);
  drawField(display, "LAST HTTP RESULT", model.lastHttpResult, kRightColumn, kFirstRow + 2 * kRowHeight, kColumnWidth);
  drawField(display, "WORKFLOW STATE", model.workflowState, kRightColumn, kFirstRow + 3 * kRowHeight, kColumnWidth);
  drawField(display, "CURRENT REVISION", model.revision, kRightColumn, kFirstRow + 4 * kRowHeight, kColumnWidth);
  drawField(display, "LAST PANEL REFRESH", model.lastRefreshDuration, kRightColumn, kFirstRow + 5 * kRowHeight, kColumnWidth);
  drawField(display, "LAST CNC VERIFICATION", model.verificationResult, kRightColumn, kFirstRow + 6 * kRowHeight, kColumnWidth);
  drawField(display, "PROTECTED MACRO VERSION", model.protectedMacroVersion, kRightColumn, kFirstRow + 7 * kRowHeight, kColumnWidth);
}
#endif

}  // namespace meimad::service_ui
