#include "production_ui.h"

namespace meimad::production_ui {
namespace {
bool isNumeric(const String& value) {
  if (value.isEmpty()) return false;
  for (size_t index = 0; index < value.length(); ++index) {
    if (value[index] < '0' || value[index] > '9') return false;
  }
  return true;
}

String tabletLabel(const String& tabletId) {
  if (tabletId.isEmpty()) return "UNREGISTERED";
  return tabletId.startsWith("T") ? tabletId : "T" + tabletId;
}

#if !MEIMAD_EINK_DRIVER_STUB
String fitText(EPaper& display, const String& value, int maximumWidth) {
  if (display.textWidth(value) <= maximumWidth) return value;
  String fitted = value;
  while (!fitted.isEmpty() && display.textWidth(fitted + "...") > maximumWidth) {
    fitted.remove(fitted.length() - 1);
  }
  return fitted + "...";
}

void drawRightAligned(EPaper& display, const String& value, int right, int y) {
  display.drawString(value, right - display.textWidth(value), y);
}
#endif
}  // namespace

ProductionScreenModel makeProductionScreen(
    const tablet_api::TabletStatusResponse& status) {
  ProductionScreenModel model;
  model.machineName = status.machine.name;
  model.machineNumber = status.machine.number;
  if (model.machineNumber.isEmpty()) {
    // Compatibility with the Task 5 numeric-ID example. A future official
    // response should provide the Machine Number explicitly.
    model.machineNumber = isNumeric(status.machine.id)
        ? "M" + status.machine.id
        : "NUMBER UNKNOWN";
  }
  model.tabletId = tabletLabel(status.tabletId);
  model.partNumber = status.part.number;
  model.partName = status.part.name;
  model.operationNumber = status.operation.number;
  model.operationName = status.operation.name;
  model.status = status.status;
  return model;
}

ProductionScreenModel makeDevelopmentFixture(const String& tabletId) {
  ProductionScreenModel model;
  model.machineName = "DMG MORI";
  model.machineNumber = "M10";
  model.tabletId = tabletLabel(tabletId.isEmpty() ? "3041" : tabletId);
  model.partNumber = "P-12345";
  model.partName = "Housing";
  model.operationNumber = 30;
  model.operationName = "Finish Milling";
  model.status = tablet_api::TabletStatus::InSetupRun;
  model.tools[0] = {"T01", "D10 End Mill", "H01"};
  model.tools[1] = {"T02", "D6 Ball Mill", "H02"};
  model.tools[2] = {"T03", "Probe", "H99"};
  model.tools[3] = {"T04", "D20 Face Mill", "H04"};
  model.tools[4] = {"T05", "D4 Drill", "H05"};
  model.tools[5] = {"T06", "D8 Reamer", "H06"};
  model.tools[6] = {"T07", "Chamfer Mill", "H07"};
  model.toolCount = 7;
  return model;
}

const char* statusText(tablet_api::TabletStatus status) {
  switch (status) {
    case tablet_api::TabletStatus::ReadyForSetup: return "READY FOR SETUP";
    case tablet_api::TabletStatus::InSetupRun: return "IN SETUP";
    case tablet_api::TabletStatus::InQc: return "IN QUALITY CONTROL";
    case tablet_api::TabletStatus::ReadyForProduction: return "READY FOR PRODUCTION";
    case tablet_api::TabletStatus::InProduction: return "IN PRODUCTION";
    case tablet_api::TabletStatus::Blocked: return "BLOCKED";
    case tablet_api::TabletStatus::Unknown: return "STATUS UNKNOWN";
  }
  return "STATUS UNKNOWN";
}

uint8_t toolPageCount(uint8_t toolCount) {
  if (toolCount == 0) return 1;
  return static_cast<uint8_t>(
      (toolCount + kToolRowsPerPage - 1) / kToolRowsPerPage);
}

uint8_t normalizedToolPage(uint8_t requestedPage, uint8_t toolCount) {
  const uint8_t pages = toolPageCount(toolCount);
  return requestedPage < pages ? requestedPage : static_cast<uint8_t>(pages - 1);
}

uint8_t previousToolPage(uint8_t currentPage, uint8_t toolCount) {
  const uint8_t page = normalizedToolPage(currentPage, toolCount);
  return page == 0 ? 0 : static_cast<uint8_t>(page - 1);
}

uint8_t nextToolPage(uint8_t currentPage, uint8_t toolCount) {
  const uint8_t page = normalizedToolPage(currentPage, toolCount);
  const uint8_t lastPage = static_cast<uint8_t>(toolPageCount(toolCount) - 1);
  return page < lastPage ? static_cast<uint8_t>(page + 1) : lastPage;
}

#if !MEIMAD_EINK_DRIVER_STUB
void drawProductionScreen(
    EPaper& display,
    const ProductionScreenModel& model,
    uint8_t requestedToolPage,
    bool developmentFixture) {
  constexpr int kLeft = 24;
  constexpr int kRight = 776;
  constexpr int kContentWidth = kRight - kLeft;
  constexpr int kInfoDividerX = 400;
  constexpr int kToolColumn1X = 112;
  constexpr int kToolColumn2X = 650;

  const uint8_t page = normalizedToolPage(requestedToolPage, model.toolCount);
  const uint8_t pages = toolPageCount(model.toolCount);

  display.begin();
  display.fillScreen(TFT_WHITE);
  display.setTextColor(TFT_BLACK, TFT_WHITE);

  // Permanent header: Machine is the strongest identity; tablet identity is
  // deliberately small and isolated in the top-right corner.
  display.setTextSize(4);
  const String machine = model.machineName + "  -  " + model.machineNumber;
  display.drawString(fitText(display, machine, 610), kLeft, 18);
  display.setTextSize(2);
  drawRightAligned(display, model.tabletId, kRight, 14);
  if (model.lowBattery) {
    display.setTextSize(1);
    drawRightAligned(display, "LOW BATTERY", kRight, 46);
  } else if (developmentFixture) {
    display.setTextSize(1);
    drawRightAligned(display, "LAYOUT DEMO", kRight, 46);
  }
  display.drawFastHLine(kLeft, 68, kContentWidth, TFT_BLACK);

  // Part is placed before Operation and receives the left reading column.
  display.setTextSize(1);
  display.drawString("PART", kLeft, 82);
  display.setTextSize(3);
  display.drawString(fitText(display, model.partNumber, 340), kLeft, 102);
  display.setTextSize(2);
  display.drawString(fitText(display, model.partName, 340), kLeft, 142);

  display.drawFastVLine(kInfoDividerX, 80, 102, TFT_BLACK);
  display.setTextSize(1);
  display.drawString("OPERATION", 424, 82);
  display.setTextSize(3);
  const String operation = "OP" + String(model.operationNumber);
  display.drawString(operation, 424, 102);
  display.setTextSize(2);
  display.drawString(fitText(display, model.operationName, 350), 424, 142);

  // The thick framed status band is the most prominent changing value.
  display.drawRect(kLeft, 190, kContentWidth, 110, TFT_BLACK);
  display.drawRect(kLeft + 1, 191, kContentWidth - 2, 108, TFT_BLACK);
  display.setTextSize(1);
  display.drawString("STATUS", 40, 204);
  if (!model.notice.isEmpty()) {
    display.setTextSize(2);
    drawRightAligned(display, fitText(display, model.notice, 520), kRight - 16, 202);
  }
  display.setTextSize(4);
  display.drawString(fitText(display, statusText(model.status), 710), 40, 238);

  // Fixed three-row tool pages. There is no scrolling state or clipped row.
  display.setTextSize(2);
  display.drawString("TOOLS", kLeft, 316);
  const String pageLabel = "TOOLS " + String(page + 1) + " / " + String(pages);
  drawRightAligned(display, pageLabel, kRight, 316);

  display.setTextSize(1);
  display.drawString("TOOL", 34, 344);
  display.drawString("DESCRIPTION", 126, 344);
  display.drawString("OFFSET", 666, 344);
  display.drawFastHLine(kLeft, 360, kContentWidth, TFT_BLACK);
  display.drawFastVLine(kToolColumn1X, 338, 136, TFT_BLACK);
  display.drawFastVLine(kToolColumn2X, 338, 136, TFT_BLACK);

  const uint8_t firstTool = static_cast<uint8_t>(page * kToolRowsPerPage);
  display.setTextSize(2);
  if (model.toolCount == 0) {
    display.drawString("NO TOOL DATA AVAILABLE", 126, 378);
  } else {
    for (uint8_t row = 0; row < kToolRowsPerPage; ++row) {
      const uint8_t toolIndex = static_cast<uint8_t>(firstTool + row);
      if (toolIndex >= model.toolCount) break;
      const int y = 370 + row * 38;
      display.drawString(fitText(display, model.tools[toolIndex].tool, 68), 34, y);
      display.drawString(
          fitText(display, model.tools[toolIndex].description, 500), 126, y);
      display.drawString(fitText(display, model.tools[toolIndex].offset, 92), 666, y);
      if (row < kToolRowsPerPage - 1) {
        display.drawFastHLine(kLeft, y + 28, kContentWidth, TFT_BLACK);
      }
    }
  }
}
#endif

}  // namespace meimad::production_ui
