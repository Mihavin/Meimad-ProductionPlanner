// Meimad Planner NC viewer: pure playback rules for program stops and breakpoints, single-block
// stepping, the called-program pane, the tool at a selected row and macro variable values at a
// position. No DOM: meimad-playback.js uses it in the page and the client tests run it in V8
// (PlaybackCoreTests).
//
// Positions: the vendored playback runs over the segments in execution order; every segment lasts
// its estimatedSeconds (0 when unknown). A row's playback position is the count of segments drawn
// before it ran, so a row that moves the tool emits the segments from its position up to the next
// executed row's position.
//
// Rows are keyed "main|<row>" for the NC program's own file (including programs later in the same
// file) and "<program file>|<row>" for a called program file. The engine's trace (model
// meimadTrace) lists, per row key, every execution as [position, level, step]: the playback
// position, the local-variable level and the executed block count, which orders the rows.
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.MeimadPlaybackCore = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  function blockKey(segment) {
    return `${segment?.line ?? ""}|${segment?.sourceUnit ?? ""}|${segment?.sourceLine ?? ""}`;
  }

  // [{ start, end }] per segment and [{ first, last, start, end }] per block of consecutive
  // segments from one row.
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

  function splitKey(key) {
    const separator = String(key).lastIndexOf("|");
    return { unit: String(key).slice(0, separator), row: Number(String(key).slice(separator + 1)) };
  }

  function keyOf(unit, row, inFile) {
    return `${!unit || inFile ? "main" : unit}|${row}`;
  }

  function describeKey(key) {
    if (key === "initial") return "Initial # vars";
    const { unit, row } = splitKey(key);
    return unit === "main" ? `row ${row}` : `${unit} row ${row}`;
  }

  // ----- executed rows ---------------------------------------------------------------------------

  // Every executed row in execution order: { key, unit, row, position, level, step }.
  function executionList(trace) {
    const list = [];
    for (const [key, runs] of Object.entries(trace?.rows || {})) {
      const { unit, row } = splitKey(key);
      for (const [position, level, step] of runs) list.push({ key, unit, row, position, level, step: step ?? 0 });
    }
    list.sort((left, right) => left.step - right.step || left.position - right.position);
    return list;
  }

  // The index of the row executing at playback position `activeIndex`: the last row that ran at
  // or before that segment. With `atStart` (nothing of the active segment drawn yet), the row
  // that emits it has not run, so the rows before it are current.
  function executionAt(list, activeIndex, atStart) {
    const limit = atStart ? activeIndex - 1 : activeIndex;
    let low = 0;
    let high = list.length - 1;
    let found = -1;
    while (low <= high) {
      const middle = (low + high) >> 1;
      if (list[middle].position <= limit) {
        found = middle;
        low = middle + 1;
      } else {
        high = middle - 1;
      }
    }
    return found;
  }

  // Where playback stands when row `index` has run: after its own moves (the segments up to the
  // next row's position), or, for a row without motion, where the tool is when it runs.
  function stepTarget(timeline, segments, list, index) {
    const entry = list[index];
    if (!entry) return undefined;
    const next = list[index + 1];
    const end = next ? next.position : (segments || []).length;
    if (end > entry.position && timeline.entries[end - 1]) {
      return { seconds: timeline.entries[end - 1].end, moves: true, tool: segments[end - 1]?.tool, firstSegment: entry.position, lastSegment: end - 1 };
    }
    const at = Math.min(entry.position, (segments || []).length - 1);
    return {
      seconds: entry.position < timeline.entries.length ? timeline.entries[entry.position].start : timeline.total,
      moves: false,
      tool: segments?.[at]?.tool
    };
  }

  // The executions of one row (its list indexes) in execution order.
  function rowExecutions(list, key) {
    const indexes = [];
    list.forEach((entry, index) => {
      if (entry.key === key) indexes.push(index);
    });
    return indexes;
  }

  // The main program's own row that led to row `index`: the row itself, or the last main-own row
  // before a called program's row (the call). `isMainOwn(entry)` tells the main program's own rows
  // from rows of programs later in the same file.
  function callerRow(list, index, isMainOwn) {
    for (let cursor = Math.min(index, list.length - 1); cursor >= 0; cursor -= 1) {
      if (isMainOwn(list[cursor])) return list[cursor].row;
    }
    return undefined;
  }

  // The next called-program row after `index`, with the main row that calls it.
  function nextCall(list, index, isMainOwn) {
    let caller = callerRow(list, index, isMainOwn);
    for (let cursor = index + 1; cursor < list.length; cursor += 1) {
      const entry = list[cursor];
      if (isMainOwn(entry)) caller = entry.row;
      else return { index: cursor, entry, callerRow: caller };
    }
    return undefined;
  }

  // ----- stops and breakpoints -------------------------------------------------------------------

  function applies(stop, optionalStop) {
    return stop && (stop.kind !== "M01" || optionalStop);
  }

  // The row key a stop halts on (an in-file program's rows are rows of the main file).
  function haltKey(halt, units) {
    if (halt.key) return halt.key;
    if (!halt.unit) return `main|${halt.line}`;
    return units?.[halt.unit]?.inFile ? `main|${halt.unitLine}` : `${halt.unit}|${halt.unitLine}`;
  }

  // Program stops plus a "BREAK" halt at every execution of every breakpoint row, in execution
  // order (position, then step).
  function halts(stops, breakpoints, trace) {
    const result = [...(stops || [])];
    for (const key of breakpoints || []) {
      const { unit, row } = splitKey(key);
      for (const [position, , step] of trace?.rows?.[key] || []) {
        result.push({
          executionIndex: position, step: step ?? 0, kind: "BREAK", key,
          line: unit === "main" ? row : undefined, unit: unit === "main" ? null : unit, unitLine: unit === "main" ? null : row
        });
      }
    }
    return result.sort((left, right) => left.executionIndex - right.executionIndex || (left.step ?? 0) - (right.step ?? 0));
  }

  // The first halt crossed when the active segment moves from `previousIndex` to `activeIndex`
  // (a halt at executionIndex e halts before segment e), skipping halts already passed.
  function stopCrossed(list, previousIndex, activeIndex, optionalStop, segmentCount, skip) {
    for (const halt of list || []) {
      const at = halt.executionIndex;
      if (!Number.isInteger(at) || at >= segmentCount) continue;
      if (at > previousIndex && at <= activeIndex && applies(halt, optionalStop) && !skip?.has(halt)) return halt;
    }
    return undefined;
  }

  // The halts on row execution `index` of the list, and those on rows already run at the same
  // position: after a step onto the row, Run must not halt on them again.
  function haltsUpTo(list, index, allHalts, units, optionalStop) {
    const entry = list[index];
    if (!entry) return { here: [], passed: new Set() };
    const here = [];
    const passed = new Set();
    for (const halt of allHalts || []) {
      if (halt.executionIndex !== entry.position) continue;
      const key = haltKey(halt, units);
      const ran = list.some((candidate) => candidate.position === entry.position && candidate.key === key && candidate.step <= entry.step);
      if (!ran) continue;
      passed.add(halt);
      if (key === entry.key && applies(halt, optionalStop)) here.push(halt);
    }
    return { here, passed };
  }

  // The list index of the row a running halt stops on.
  function haltExecution(list, halt, units) {
    const key = haltKey(halt, units);
    let found = list.findIndex((entry) => entry.position === halt.executionIndex && entry.key === key &&
      (halt.step === undefined || entry.step === halt.step));
    if (found < 0) found = list.findIndex((entry) => entry.position === halt.executionIndex && entry.key === key);
    return found;
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

  // ----- macro variables -------------------------------------------------------------------------

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
    const inRange = (position, step) => (at.step !== undefined ? step <= at.step : position <= at.position);
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

  return {
    buildTimeline, secondsAt, blockKey, keyOf, splitKey, describeKey,
    executionList, executionAt, stepTarget, rowExecutions, callerRow, nextCall,
    haltKey, halts, stopCrossed, haltsUpTo, haltExecution, callOf, stopText, variablesAt
  };
});
