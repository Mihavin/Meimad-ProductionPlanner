// Meimad Planner NC viewer: pure playback rules for program stops and breakpoints, single-block
// stepping, the called-program pane, the tool at a selected row and macro variable values at a
// position. No DOM: meimad-playback.js uses it in the page and the client tests run it in V8
// (PlaybackCoreTests).
//
// Rows are keyed "main|<row>" for the NC program's own file (including programs later in the same
// file) and "<program file>|<row>" for a called program file. The engine's trace (model
// meimadTrace) lists, per row key, every execution as [position, level, step]: the playback
// position (the segment it runs before), the local-variable level and the executed block count.
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

  function keyOf(unit, row, inFile) {
    return `${!unit || inFile ? "main" : unit}|${row}`;
  }

  function describeKey(key) {
    if (key === "initial") return "Initial # vars";
    const separator = String(key).lastIndexOf("|");
    const unit = String(key).slice(0, separator);
    const row = String(key).slice(separator + 1);
    return unit === "main" ? `row ${row}` : `${unit} row ${row}`;
  }

  // Program stops plus a "BREAK" halt at every execution of every breakpoint row, in playback order.
  function halts(stops, breakpoints, trace) {
    const result = [...(stops || [])];
    for (const key of breakpoints || []) {
      const separator = key.lastIndexOf("|");
      const unit = key.slice(0, separator);
      const row = Number(key.slice(separator + 1));
      for (const [position] of trace?.rows?.[key] || []) {
        result.push({ executionIndex: position, kind: "BREAK", key, line: unit === "main" ? row : undefined, unit: unit === "main" ? null : unit, unitLine: unit === "main" ? null : row });
      }
    }
    return result.sort((left, right) => left.executionIndex - right.executionIndex);
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
    if (stop.kind === "BREAK") return `Breakpoint at ${where}. Press Run to continue.`;
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

  // Whether a segment was drawn by the row `key` (a called program of the same file is keyed by
  // its own row of the file).
  function segmentOfRow(segment, key, units) {
    if (!segment) return false;
    const separator = key.lastIndexOf("|");
    const unit = key.slice(0, separator);
    const row = Number(key.slice(separator + 1));
    if (segment.sourceUnit) {
      const inFile = Boolean(units?.[segment.sourceUnit]?.inFile);
      return (inFile ? unit === "main" : unit === segment.sourceUnit) && Number(segment.sourceLine) === row;
    }
    return unit === "main" && Number(segment.line) === row;
  }

  // Where the tool is at an execution of row `key`: at the end of the row's own move, or, for a
  // row without motion (a macro statement, a call), where the tool is when the row runs. Selecting
  // the same row again goes to its next execution (a row in a loop or a program called again).
  function rowTarget(timeline, segments, trace, units, key, previous) {
    const executions = trace?.rows?.[key];
    if (!executions?.length) return undefined;
    let choice = 0;
    if (previous && previous.key === key) choice = (previous.choice + 1) % executions.length;
    const [position, level, step] = executions[choice];
    const block = timeline.blocks.find((candidate) => candidate.first === position);
    const own = block && segmentOfRow(segments[position], key, units);
    const seconds = own
      ? block.end
      : position < timeline.entries.length ? timeline.entries[Math.max(0, position)].start : timeline.total;
    const segment = segments[own ? block.last : Math.min(Math.max(0, position), segments.length - 1)];
    return { key, choice, count: executions.length, position, level, step, seconds, tool: segment?.tool, moves: Boolean(own) };
  }

  function scopeOf(id) {
    if (id >= 1 && id <= 33) return "Local";
    if ((id >= 100 && id <= 199) || (id >= 500 && id <= 999)) return "Common";
    if (id >= 1000) return "System";
    return "Other";
  }

  function formatVariable(value) {
    if (value === null || value === undefined) return "vacant";
    const rounded = Math.round(Number(value) * 1e6) / 1e6;
    return Number.isFinite(rounded) ? String(rounded) : String(value);
  }

  // Values of every variable the program uses, at a playback position (all writes before that
  // segment) or after a row execution (writes up to its step). Locals belong to one macro level.
  function variablesAt(trace, at) {
    const values = new Map();
    const inRange = (position, step) => (at.step !== undefined
      ? step <= at.step
      : position <= at.position);
    for (const [position, id, value, level, key, step] of trace?.writes || []) {
      if (!inRange(position, step)) continue;
      if (id >= 1 && id <= 33 && level !== at.level) continue;
      values.set(id, { value, key });
    }
    // A system variable shows the value the program last read up to this position.
    const reads = new Map();
    for (const [position, id, value] of trace?.systemReads || []) if (position <= at.position) reads.set(id, value);
    for (const [id, value] of reads) if (!values.has(id)) values.set(id, { value, key: null, read: true });
    return (trace?.used || []).map((id) => {
      const entry = values.get(id);
      return {
        variable: `#${id}`,
        value: formatVariable(entry?.value),
        scope: scopeOf(id),
        setAt: entry?.key ? describeKey(entry.key) : entry?.read ? "read" : ""
      };
    });
  }

  return { buildTimeline, secondsAt, stopCrossed, nextStep, callOf, stopText, blockKey, halts, keyOf, describeKey, segmentOfRow, rowTarget, variablesAt };
});
