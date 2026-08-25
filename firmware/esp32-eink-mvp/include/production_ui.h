#pragma once

#include <Arduino.h>
#include "tablet_api.h"

#if !MEIMAD_EINK_DRIVER_STUB
#include <TFT_eSPI.h>
#endif

namespace meimad::production_ui {

constexpr uint8_t kToolRowsPerPage = 3;
constexpr uint8_t kMaximumTools = 12;

struct ToolRow {
  String tool;
  String description;
  String offset;
};

struct ProductionScreenModel {
  String machineName;
  String machineNumber;
  String tabletId;
  String partNumber;
  String partName;
  int32_t operationNumber = 0;
  String operationName;
  tablet_api::TabletStatus status = tablet_api::TabletStatus::Unknown;
  String notice;
  bool lowBattery = false;
  ToolRow tools[kMaximumTools];
  uint8_t toolCount = 0;
};

ProductionScreenModel makeProductionScreen(
    const tablet_api::TabletStatusResponse& status);
ProductionScreenModel makeDevelopmentFixture(const String& tabletId);

const char* statusText(tablet_api::TabletStatus status);
uint8_t toolPageCount(uint8_t toolCount);
uint8_t normalizedToolPage(uint8_t requestedPage, uint8_t toolCount);
uint8_t previousToolPage(uint8_t currentPage, uint8_t toolCount);
uint8_t nextToolPage(uint8_t currentPage, uint8_t toolCount);

#if !MEIMAD_EINK_DRIVER_STUB
void drawProductionScreen(
    EPaper& display,
    const ProductionScreenModel& model,
    uint8_t requestedToolPage,
    bool developmentFixture);
#endif

}  // namespace meimad::production_ui
