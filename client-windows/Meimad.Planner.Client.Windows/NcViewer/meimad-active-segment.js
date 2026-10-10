"use strict";

// Meimad Planner: which drawn segment the tool is on, for page scripts that need more than the tool
// pose (the lathe Machine view's C and Y, the Axes card). Playback reports the last completed
// segment and the drawn part of the active one; an idle preview follows the selected row.
// window.meimadActiveSegment.current() returns { index, fraction } or null; listeners registered
// with on(callback) are called when it changes.
(() => {
  let hooks = null;
  let model = null;
  let current = null;
  const listeners = new Set();

  function length(points) {
    let total = 0;
    for (let index = 1; index < (points || []).length; index += 1) {
      const a = points[index - 1];
      const b = points[index];
      total += Math.hypot((b.x || 0) - (a.x || 0), (b.y || 0) - (a.y || 0), (b.z || 0) - (a.z || 0));
    }
    return total;
  }

  function set(next) {
    if (next && current && next.index === current.index && Math.abs(next.fraction - current.fraction) < 1e-4) return;
    if (!next && !current) return;
    current = next;
    for (const listener of [...listeners]) {
      try { listener(current); } catch (error) { console.error(error); }
    }
  }

  function fromPlayback(playback) {
    const segments = (model && model.segments) || [];
    if (!playback) return null;
    const index = Math.max(-1, Number(playback.completedIndex)) + 1;
    if (index >= segments.length) return segments.length ? { index: segments.length - 1, fraction: 1 } : null;
    const full = length(segments[index] && segments[index].points);
    const done = playback.active && playback.active.points ? length(playback.active.points) : 0;
    return { index, fraction: full > 0 ? Math.min(1, done / full) : 0 };
  }

  function fromSelection(line, executionIndex) {
    const segments = (model && model.segments) || [];
    if (Number.isFinite(executionIndex) && segments[executionIndex]) return { index: executionIndex, fraction: 1 };
    let found = -1;
    segments.forEach((segment, index) => { if (segment.line === line) found = index; });
    return found >= 0 ? { index: found, fraction: 1 } : null;
  }

  function attach(value) {
    if (!value || hooks === value || !value.on) return;
    hooks = value;
    hooks.on("modelApplied", (next) => { model = next || null; set(null); });
    hooks.on("playback", (playback) => { if (playback) set(fromPlayback(playback)); });
    hooks.on("selection", (line, executionIndex) => set(fromSelection(line, executionIndex)));
  }

  window.meimadActiveSegment = Object.freeze({
    current: () => current,
    on(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    }
  });
  if (window.meimadViewer3d) attach(window.meimadViewer3d);
  window.addEventListener("meimad:viewer3d", (event) => attach(event.detail));
})();
