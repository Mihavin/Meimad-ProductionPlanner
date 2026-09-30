// Meimad Planner NC viewer: pure playback rules for program stops, single-block stepping and the
// called-program pane. No DOM: meimad-playback.js uses it in the page and the client tests run it
// in V8 (MeimadPlaybackCoreTests).
//
// The timeline matches media/preview.js: every segment lasts its estimatedSeconds (0 when
// unknown) and segments follow each other in execution order. A block is a run of consecutive
// segments from the same NC row (and the same called-program row), so a cycle or an arc drawn as
// several segments is still one block.
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.MeimadPlaybackCore = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  const EPSILON = 1e-6;

  function blockKey(segment) {
    return `${segment?.line ?? ""}|${segment?.sourceUnit ?? ""}|${segment?.sourceLine ?? ""}`;
  }

  // [{ start, end }] per segment and [{ first, last, start, end }] per block.
  function buildTimeline(segments) {
    const entries = [];
    const blocks = [];
    let elapsed = 0;
    (segments || []).forEach((segment, index) => {
      const duration = Number.isFinite(segment?.estimatedSeconds) ? Math.max(0, segment.estimatedSeconds) : 0;
      entries.push({ start: elapsed, end: elapsed + duration });
      const previous = blocks[blocks.length - 1];
      if (previous && previous.key === blockKey(segment) && previous.last === index - 1) {
        previous.last = index;
        previous.end = elapsed + duration;
      } else {
        blocks.push({ key: blockKey(segment), first: index, last: index, start: elapsed, end: elapsed + duration });
      }
      elapsed += duration;
    });
    return { entries, blocks, total: elapsed };
  }

  // Playback seconds from the viewer's playback state: the active segment and how much of it
  // has been drawn (fraction 0..1).
  function secondsAt(timeline, activeIndex, fraction) {
    if (!timeline.entries.length || activeIndex < 0) return 0;
    if (activeIndex >= timeline.entries.length) return timeline.total;
    const entry = timeline.entries[activeIndex];
    const part = Number.isFinite(fraction) ? Math.min(1, Math.max(0, fraction)) : 0;
    return entry.start + (entry.end - entry.start) * part;
  }

  function applies(stop, optionalStop) {
    return stop && (stop.kind !== "M01" || optionalStop);
  }

  // The first stop crossed when the active segment moves from `previousIndex` to `activeIndex`:
  // a stop at executionIndex e halts before segment e starts.
  function stopCrossed(stops, previousIndex, activeIndex, optionalStop, segmentCount) {
    for (const stop of stops || []) {
      const at = stop.executionIndex;
      if (!Number.isInteger(at) || at >= segmentCount) continue;
      if (at > previousIndex && at <= activeIndex && applies(stop, optionalStop)) return stop;
    }
    return undefined;
  }

  // Single block: from `seconds`, the end of the next block. A stop on the boundary about to be
  // crossed is its own block (M00/M01 blocks stop the control), so that step halts on it first.
  // Returns { seconds, stop } or undefined at the end of the program.
  function nextStep(timeline, stops, seconds, optionalStop, acknowledgedIndex) {
    const block = timeline.blocks.find((candidate) => candidate.end > seconds + EPSILON);
    if (!block) return undefined;
    const startsHere = block.start >= seconds - EPSILON;
    if (startsHere) {
      const stop = (stops || []).find((candidate) => candidate.executionIndex === block.first &&
        candidate.executionIndex !== acknowledgedIndex && applies(candidate, optionalStop));
      if (stop) return { seconds: block.start, stop };
    }
    return { seconds: block.end, block };
  }

  // What the called-program pane shows for a segment, or undefined in the main program.
  function callOf(segment) {
    if (!segment || !segment.sourceUnit) return undefined;
    return {
      unit: String(segment.sourceUnit),
      line: Number(segment.sourceLine) || 1,
      callerLine: Number(segment.line) || 1,
      depth: Number(segment.callDepth) || 1
    };
  }

  function stopText(stop) {
    if (!stop) return "";
    const where = stop.unit ? `${stop.unit} row ${stop.unitLine}` : `row ${stop.line}`;
    const kind = stop.kind === "M01"
      ? "M01 optional stop"
      : stop.kind === "#3006"
        ? "#3006 stop"
        : stop.kind === "ALARM"
          ? "Macro alarm #3000"
          : "M00 program stop";
    const message = stop.message ? ` (${stop.message})` : "";
    return stop.kind === "ALARM"
      ? `${kind} at ${where}${message}. The control stops here.`
      : `${kind} at ${where}${message}. Press Run to continue.`;
  }

  return { buildTimeline, secondsAt, stopCrossed, nextStep, callOf, stopText, blockKey };
});
