#pragma once

#include <Arduino.h>

namespace meimad::screen_revision {

struct LastRevision {
  bool available = false;
  uint32_t revision = 0;
  String tabletId;
};

bool shouldRefresh(
    const LastRevision& lastRevision,
    const String& tabletId,
    uint32_t serverRevision);

}  // namespace meimad::screen_revision
