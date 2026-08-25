#include "screen_revision.h"

namespace meimad::screen_revision {

bool shouldRefresh(
    const LastRevision& lastRevision,
    const String& tabletId,
    uint32_t serverRevision) {
  return !lastRevision.available
      || lastRevision.tabletId != tabletId
      || lastRevision.revision != serverRevision;
}

}  // namespace meimad::screen_revision
