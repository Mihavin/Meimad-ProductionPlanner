"use strict";

// Custom macro (FANUC Macro B / Haas) executor for lathe programs. The vendored lathe
// interpreter reads only assignments, IF [..] GOTO and GOTO, and does not call subprograms, so the
// program is executed here first, block by block, the way the control runs it:
//
//   variables    #1-#33 local per macro level (G65/G66 open a level, M98/M97 share the caller's),
//                #100-#199 and #500-#999 common, #0 and unset variables vacant, #[expr] indirect;
//                system variables for modal information (#4001-#4130), positions (#5001-#5065),
//                work offsets (#5201-#5335), tool offsets (#2001-#2999), timers and date
//                (#3001/#3002/#3011/#3012), part counts (#3901/#3902), alarm #3000, stop #3006.
//   control flow WHILE [..] DOm / ENDm, DOm (endless) / ENDm, IF [..] GOTOn, IF [..] THEN ...,
//                GOTOn / GOTO#i / GOTO[expr], block delete (/).
//   calls        G65 (argument specifications I and II), G66 / G66.1 modal calls and G67, M98 P L
//                (and the FANUC eight-digit P), Haas M97, M99 / M99 P, custom macro calls by
//                G codes (parameters 6050-6059, O9010-O9019), M codes (6071-6079 subprograms
//                O9001-O9009, 6080-6089 macros O9020-O9029) and T codes (6001#5, O9000, #149).
//   output       POPEN / PCLOS / DPRNT / BPRNT are formatted as print lines; SETVN is accepted.
//
// The result is a flat program of NC blocks whose addresses hold plain numbers, which the lathe
// interpreter then draws. Every flat line keeps its origin: the row of the main program that
// ran (or called) it and, inside a called program, that program's name, row and call depth.
// Program stops (M00, M01, #3006) are reported with the flat line they follow so the viewer can
// halt playback there. The trace records, against the flat lines, every executed row (for
// breakpoints and "tool at the selected row"), every variable write with its macro level, and the
// system variables the program read, so the viewer can show variable values at any position. Sequence numbers are kept unique in the flat program, so a roughing cycle
// in a loop or in a subprogram called twice still finds its own contour.

const { evaluateExpression } = require("../src/macro");

const MAX_DEPTH = 14;
const MAX_TRACE_ROWS = 300000;
const MAX_TRACE_PER_ROW = 2000;
const MAX_TRACE_WRITES = 100000;
const MAX_TRACE_READS = 20000;
const MAX_BLOCKS = 500000;
const MAX_LINES = 200000;
const UNIQUE_SEQUENCE_BASE = 700000;
const TYPE_I_ARGUMENTS = {
  A: 1, B: 2, C: 3, I: 4, J: 5, K: 6, D: 7, E: 8, F: 9, H: 11, M: 13,
  Q: 17, R: 18, S: 19, T: 20, U: 21, V: 22, W: 23, X: 24, Y: 25, Z: 26
};
const MOVE_LETTERS = new Set(["X", "Z", "U", "W", "Y", "V", "C", "H", "B"]);
const CONTOUR_CYCLES = new Set([70, 71, 72, 73]);
// FANUC lathe G code system A modal groups read through #4001-#4030.
const GROUPS = new Map([
  [0, 1], [1, 1], [2, 1], [3, 1], [32, 1], [34, 1], [90, 1], [92, 1], [94, 1],
  [96, 2], [97, 2],
  [98, 5], [99, 5],
  [20, 6], [21, 6],
  [40, 7], [41, 7], [42, 7],
  [25, 8], [26, 8],
  [22, 9], [23, 9],
  [80, 10], [83, 10], [84, 10], [85, 10], [87, 10], [88, 10], [89, 10],
  [66, 12], [66.1, 12], [67, 12],
  [54, 14], [55, 14], [56, 14], [57, 14], [58, 14], [59, 14],
  [17, 16], [18, 16], [19, 16],
  [68, 17], [69, 17],
  [12.1, 21], [13.1, 21]
]);
const DEFAULT_MODALS = new Map([[1, 0], [2, 97], [5, 99], [6, 21], [7, 40], [8, 25], [9, 22], [10, 80], [12, 67], [14, 54], [16, 18], [17, 69], [21, 13.1]]);
const AXES = ["X", "Z", "C", "Y", "B"];
const INCREMENTAL = { U: "X", W: "Z", H: "C", V: "Y" };

function stripComments(raw) {
  // "(...)" comments and ";" end-of-block text; DPRNT/BPRNT keep their bracketed text.
  return String(raw).replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "").trim();
}

function commentOf(raw) {
  const matches = [...String(raw).matchAll(/\(([^)]*)\)?/g)].map((match) => match[1].trim()).filter(Boolean);
  return matches.join(" ");
}

function matchingBracket(text, start) {
  let depth = 0;
  for (let index = start; index < text.length; index += 1) {
    if (text[index] === "[") depth += 1;
    else if (text[index] === "]") {
      depth -= 1;
      if (depth === 0) return index;
    }
  }
  return -1;
}

function formatValue(value) {
  if (!Number.isFinite(value)) return "0.";
  const rounded = Math.round(value * 1e6) / 1e6;
  const text = String(rounded === 0 ? 0 : rounded);
  if (/e/i.test(text)) return rounded.toFixed(6).replace(/0+$/, "");
  return text.includes(".") ? text : `${text}.`;
}

// A word value is a number, #n, #[..], [..] or a signed form of these. Returns its text end.
function readValueEnd(code, start) {
  let index = start;
  while (code[index] === " ") index += 1;
  if (code[index] === "+" || code[index] === "-") index += 1;
  while (code[index] === " ") index += 1;
  if (code[index] === "[") {
    const end = matchingBracket(code, index);
    return end < 0 ? code.length : end + 1;
  }
  if (code[index] === "#") {
    index += 1;
    if (code[index] === "[") {
      const end = matchingBracket(code, index);
      return end < 0 ? code.length : end + 1;
    }
    while (/[0-9]/.test(code[index] || "")) index += 1;
    return index;
  }
  while (/[0-9.]/.test(code[index] || "")) index += 1;
  return index;
}

// Splits an NC block into words: [{ letter, text }], where letter may be ",C" / ",R" (FANUC direct
// drawing) and text is the raw value.
function splitWords(code) {
  const words = [];
  let index = 0;
  while (index < code.length) {
    const character = code[index];
    if (character === " " || character === "\t") {
      index += 1;
      continue;
    }
    let letter;
    if (character === "," && /[A-Z]/.test(code[index + 1] || "")) {
      letter = `,${code[index + 1]}`;
      index += 2;
    } else if (/[A-Z]/.test(character)) {
      letter = character;
      index += 1;
    } else {
      throw new Error(`Unexpected "${character}" in "${code}"`);
    }
    const end = readValueEnd(code, index);
    words.push({ letter, text: code.slice(index, end).replace(/\s+/g, "") });
    index = end;
  }
  return words;
}

function createUnit(name, lines, originMap, options = {}) {
  const programs = new Map();
  const programStarts = [];
  const sequences = new Map();
  lines.forEach((raw, index) => {
    const code = stripComments(raw).toUpperCase().replace(/^\/\d?/, "").trim();
    const program = code.match(/^O\s*(\d+)/) || code.match(/^:\s*(\d+)/);
    if (program) {
      programStarts.push(index);
      if (!programs.has(Number(program[1]))) programs.set(Number(program[1]), index);
    }
    const sequence = code.match(/^N\s*(\d+)/);
    if (sequence) {
      const number = Number(sequence[1]);
      if (!sequences.has(number)) sequences.set(number, []);
      sequences.get(number).push(index);
    }
  });
  return {
    name,
    lines,
    origin: (index) => originMap?.[index]?.line ?? index + 1,
    programs,
    programStarts,
    sequences,
    external: Boolean(options.external),
    key: options.key || name
  };
}

function programRange(unit, index) {
  let start = 0;
  let end = unit.lines.length;
  for (const programStart of unit.programStarts) {
    if (programStart <= index) start = programStart;
    else {
      end = programStart;
      break;
    }
  }
  return { start, end };
}

// FANUC custom macro calls by G, M and T codes, from the CNC parameter export.
function customCalls(parameters) {
  const value = (number) => {
    const entry = parameters?.fanuc?.[String(number)];
    return Number.isFinite(entry?.value) ? entry.value : 0;
  };
  const gMacros = new Map();
  const mSubprograms = new Map();
  const mMacros = new Map();
  for (let index = 0; index < 10; index += 1) {
    const g = value(6050 + index);
    if (g > 0) gMacros.set(g, 9010 + index);
    const macro = value(6080 + index);
    if (macro > 0) mMacros.set(macro, 9020 + index);
  }
  for (let index = 0; index < 9; index += 1) {
    const m = value(6071 + index);
    if (m > 0) mSubprograms.set(m, 9001 + index);
  }
  // 6001#5 TCS: T codes call O9000. The export writes bits 7..0 left to right.
  const entry = parameters?.fanuc?.["6001"];
  const bit = entry?.bits?.[5]?.value;
  const raw = String(entry?.raw ?? "");
  const tCall = bit === 1 || (bit === undefined && /^[01]{8}$/.test(raw) && raw[2] === "1");
  return { gMacros, mSubprograms, mMacros, tCall };
}

// options:
//   resolve(target)      external program lookup (src/subprograms.js resolver) or undefined
//   translate(text)      the dialect translation of a called program: { lines, map }
//   dialect              "haas" or "fanuc" (system variable numbering)
//   initialVariables     Map of preset variables (viewer settings)
//   parameters           FANUC CNC-PARA profile (custom macro call parameters)
//   workOffsets          { G54: { x, z, ... }, ... } saved home offsets
//   blockDelete          true skips "/" blocks
//   now                  Date for #3011 / #3012
function execute(main, options = {}) {
  const out = { lines: [], map: [], notes: [], external: [], stops: [], prints: [], units: new Map(), dwellSeconds: 0, callCount: 0, executedBlockCount: 0 };
  const noted = new Set();
  const note = (text) => {
    if (noted.has(text) || out.notes.length >= 40) return;
    noted.add(text);
    out.notes.push(text);
  };
  const haas = options.dialect === "haas";
  const now = options.now instanceof Date ? options.now : new Date();
  const custom = customCalls(options.parameters);
  const globals = new Map();
  if (options.initialVariables instanceof Map) {
    for (const [id, value] of options.initialVariables) if (Number(id) > 33) globals.set(Number(id), value);
  }
  const workOffsets = options.workOffsets || {};
  const mainUnit = createUnit("main", main.lines, main.map, { key: "main" });
  const externalUnits = new Map();
  const state = {
    modal: new Map(DEFAULT_MODALS),
    position: { X: 0, Z: 0, C: 0, Y: 0, B: 0 },
    feed: 0,
    speed: 0,
    tool: 0,
    lastM: 0,
    lastN: 0,
    lastB: 0,
    lastD: 0,
    lastH: 0,
    workOffset: 1,
    timerBase: 0,
    parts: 0,
    requiredParts: 0,
    modalMacro: undefined
  };
  const stack = [];
  const frame = () => stack[stack.length - 1];
  const sequenceNumbers = { used: new Set(), last: new Map(), pending: new Map(), next: UNIQUE_SEQUENCE_BASE };
  let stopped = false;
  let blocks = 0;
  const trace = { rows: new Map(), rowCount: 0, writes: [], systemReads: [], lastRead: new Map(), used: new Set() };
  const localsIds = new WeakMap();
  let nextLocalsId = 1;
  const localsId = (map) => {
    if (!map) return 0;
    if (!localsIds.has(map)) localsIds.set(map, nextLocalsId++);
    return localsIds.get(map);
  };
  // Rows of the main file (also its in-file programs) are "main|row"; a called file's are "name|row".
  const rowKey = (entry) => (entry.unit.external ? `${entry.unit.name}|${entry.unit.origin(entry.index)}` : `main|${entry.unit.origin(entry.index)}`);
  function traceRow(entry) {
    if (trace.rowCount >= MAX_TRACE_ROWS) return;
    const key = rowKey(entry);
    let list = trace.rows.get(key);
    if (!list) trace.rows.set(key, (list = []));
    if (list.length >= MAX_TRACE_PER_ROW) return;
    list.push([out.lines.length, localsId(entry.locals), blocks]);
    trace.rowCount += 1;
  }
  function traceWrite(id, value, level, key, afterLine = out.lines.length) {
    trace.used.add(id);
    if (trace.writes.length < MAX_TRACE_WRITES) trace.writes.push([afterLine, id, value, level, key, afterLine < 0 ? 0 : blocks]);
  }

  const rowLabel = () => {
    const current = frame();
    if (!current) return "End";
    const row = current.unit.origin(current.index);
    return current.unit === mainUnit ? `Row ${row}` : `${current.unit.name} row ${row}`;
  };

  // ----- variables --------------------------------------------------------------------------

  const axisIndex = (id, base) => AXES[id - base];
  const offsetValue = (label, axis) => {
    const offset = workOffsets[label];
    const value = offset ? Number(offset[axis.toLowerCase()]) : 0;
    return Number.isFinite(value) ? value : 0;
  };
  const workOffsetLabel = (index) => (index <= 6 ? `G${53 + index}` : `G54.1 P${index - 6}`);

  function readSystem(id) {
    if (id >= 4001 && id <= 4030) return state.modal.get(id - 4000) ?? null;
    if (id >= 4201 && id <= 4230) return state.modal.get(id - 4200) ?? null;
    switch (id) {
      case 4102: return state.lastB;
      case 4107: return state.lastD;
      case 4109: return state.feed;
      case 4111: return state.lastH;
      case 4113: return state.lastM;
      case 4114: return state.lastN;
      case 4115: return frame()?.programNumber ?? 0;
      case 4119: return state.speed;
      case 4120: return state.tool;
      case 3001: return state.timerBase;
      case 3002: return state.timerBase / 3600000;
      case 3011: return now.getFullYear() * 10000 + (now.getMonth() + 1) * 100 + now.getDate();
      case 3012: return now.getHours() * 10000 + now.getMinutes() * 100 + now.getSeconds();
      case 3901: return state.parts;
      case 3902: return state.requiredParts;
      case 3003:
      case 3004: return 0;
      default: break;
    }
    const position = (base) => {
      const axis = axisIndex(id, base);
      return axis ? state.position[axis] ?? 0 : undefined;
    };
    // #5001-#5005 block end, #5021 machine, #5041 current, #5061 skip, all in program units.
    for (const base of [5001, 5041, 5061]) {
      if (id >= base && id < base + AXES.length) return position(base);
    }
    if (id >= 5021 && id < 5021 + AXES.length) {
      const axis = axisIndex(id, 5021);
      return (state.position[axis] ?? 0) + offsetValue(workOffsetLabel(state.workOffset), axis);
    }
    // #5201 external offset, #5221 G54 ... #5321 G59 (20 per offset).
    if (id >= 5201 && id <= 5335) {
      const block = Math.floor((id - 5201) / 20);
      const axis = AXES[(id - 5201) % 20];
      if (!axis) return 0;
      if (globals.has(id)) return globals.get(id);
      return block === 0 ? 0 : offsetValue(workOffsetLabel(block), axis);
    }
    if (id >= 2001 && id <= 2999) {
      if (globals.has(id)) return globals.get(id);
      note(`Tool offset #${id} is read as 0: the preview does not know the control's tool offsets.`);
      return 0;
    }
    return undefined;
  }

  const variableView = {
    get(id) {
      trace.used.add(id);
      if (id >= 1 && id <= 33) {
        const value = frame()?.locals.get(id);
        return value === undefined ? undefined : value;
      }
      const system = id >= 2000 && id < 6000 ? readSystem(id) : undefined;
      if (system !== undefined) {
        if (trace.lastRead.get(id) !== system && trace.systemReads.length < MAX_TRACE_READS) {
          trace.lastRead.set(id, system);
          trace.systemReads.push([out.lines.length, id, system]);
        }
        return system === null ? undefined : system;
      }
      return globals.get(id);
    }
  };

  const evaluate = (expression) => evaluateExpression(expression, variableView, undefined, { vacant: true, bitwise: haas });

  function writeVariable(id, value, comment) {
    if (!Number.isInteger(id) || id <= 0) throw new Error(`#${id} cannot be written`);
    const current = frame();
    if (id <= 33) {
      if (value === null) current.locals.delete(id);
      else current.locals.set(id, value);
      traceWrite(id, value, localsId(current.locals), rowKey(current));
      return;
    }
    const readOnly = (id >= 4001 && id <= 4400) || (id >= 5001 && id <= 5080) || id === 3011 || id === 3012;
    if (id !== 3000 && id !== 3006 && id !== 3003 && id !== 3004 && !readOnly) traceWrite(id, value, 0, rowKey(current));
    switch (id) {
      case 3000:
        stopped = true;
        out.stops.push(stopAt("ALARM", `#3000=${formatValue(value ?? 0)}${comment ? ` ${comment}` : ""}`));
        note(`${rowLabel()}: macro alarm #3000=${formatValue(value ?? 0)}${comment ? ` (${comment})` : ""}; the control stops here.`);
        return;
      case 3006:
        out.stops.push(stopAt("#3006", comment || `#3006=${formatValue(value ?? 0)}`));
        return;
      case 3001:
        state.timerBase = value ?? 0;
        return;
      case 3002:
        state.timerBase = (value ?? 0) * 3600000;
        return;
      case 3901:
        state.parts = value ?? 0;
        return;
      case 3902:
        state.requiredParts = value ?? 0;
        return;
      case 3003:
      case 3004:
        return;
      default:
        break;
    }
    if ((id >= 4001 && id <= 4400) || (id >= 5001 && id <= 5080) || id === 3011 || id === 3012) {
      note(`${rowLabel()}: #${id} is read-only on the control; the write is ignored.`);
      return;
    }
    if (value === null) globals.delete(id);
    else globals.set(id, value);
  }

  // ----- program lookup and calls -----------------------------------------------------------------

  function externalUnit(target) {
    const found = typeof options.resolve === "function" ? options.resolve(target) : undefined;
    if (!found) return undefined;
    const key = found.path || found.name;
    if (!externalUnits.has(key)) {
      const translated = typeof options.translate === "function"
        ? options.translate(String(found.source))
        : { lines: String(found.source).split("\n"), map: undefined };
      const unit = createUnit(found.name, translated.lines, translated.map, { external: true, key });
      externalUnits.set(key, unit);
      out.external.push({ name: found.name, location: found.location, path: found.path });
      out.units.set(found.name, { name: found.name, text: String(found.source), path: found.path || null, location: found.location || null, inFile: false });
    }
    return externalUnits.get(key);
  }

  function findProgram(target, currentUnit) {
    if (typeof target === "number") {
      for (const unit of [currentUnit, mainUnit]) {
        if (unit.programs.has(target)) return { unit, index: unit.programs.get(target), number: target };
      }
    }
    const unit = externalUnit(target);
    if (!unit) return undefined;
    const index = typeof target === "number" && unit.programs.has(target) ? unit.programs.get(target) : 0;
    return { unit, index, number: typeof target === "number" ? target : undefined };
  }

  // A program or M97 block of the main program's own file: the viewer shows the main text.
  function inFileUnit(label) {
    if (!out.units.has(label)) out.units.set(label, { name: label, text: null, path: null, location: "this program", inFile: true });
    return label;
  }

  function call(kind, target, { repeat = 1, locals, label, argumentsText = "" } = {}) {
    const caller = frame();
    let location;
    if (kind === "M97") {
      const index = findSequence(caller.unit, target, caller.index);
      if (index !== undefined) location = { unit: caller.unit, index, number: caller.programNumber };
    } else {
      location = findProgram(target, caller.unit);
    }
    const name = kind === "M97" ? `N${target}` : typeof target === "number" ? `O${String(target).padStart(4, "0")}` : `"${target}"`;
    const callText = `${kind} ${typeof target === "number" ? `P${target}` : `(${target})`}`;
    if (!location) {
      emitComment(`(${callText}: ${name} not found)`);
      note(`${rowLabel()}: ${label || kind} ${name} was not found in the program, its folder or the machine's program memory folder.`);
      return undefined;
    }
    if (repeat <= 0) return undefined;
    if (stack.length >= MAX_DEPTH) {
      note(`${rowLabel()}: calls are nested deeper than ${MAX_DEPTH} levels; the preview stops.`);
      stopped = true;
      return undefined;
    }
    const shown = location.unit.external ? location.unit.name : name;
    emitComment(`(${callText} -> ${shown}${repeat > 1 ? ` x${repeat}` : ""}${argumentsText ? ` ${argumentsText}` : ""})`);
    if (locals && locals !== caller.locals) {
      // G65/G66 arguments open the new level's local variables.
      for (const [id, value] of locals) traceWrite(id, value, localsId(locals), rowKey(caller));
      localsId(locals);
    }
    caller.index += 1;
    caller.jumped = true;
    const programNumber = location.number ?? caller.programNumber;
    const inFileName = kind === "M97" ? `N${target}` : name;
    const called = {
      unit: location.unit,
      index: location.index,
      entryIndex: location.index,
      kind,
      locals: locals || caller.locals,
      repeat,
      loops: [],
      headerSeen: kind === "M97",
      jumped: false,
      programNumber,
      mainLine: stack.length === 1 ? caller.unit.origin(caller.index - 1) : caller.mainLine,
      name: location.unit.external ? location.unit.name : inFileUnit(inFileName)
    };
    stack.push(called);
    out.callCount += 1;
    return called;
  }

  function returnFromCall(pNumber) {
    const current = frame();
    if (current.repeat > 1) {
      current.repeat -= 1;
      current.index = current.entryIndex;
      current.loops = [];
      current.headerSeen = current.kind === "M97";
      current.jumped = true;
      return;
    }
    stack.pop();
    const caller = frame();
    if (!caller) return;
    if (Number.isFinite(pNumber)) jumpToSequence(pNumber);
    caller.jumped = true;
  }

  function findSequence(unit, number, fromIndex) {
    const candidates = unit.sequences.get(number) || [];
    if (!candidates.length) return undefined;
    const range = programRange(unit, fromIndex);
    const inRange = candidates.filter((index) => index >= range.start && index < range.end);
    // A GOTO searches forward from the block, then from the program start.
    return inRange.find((index) => index > fromIndex) ?? inRange[0] ?? candidates[0];
  }

  function jumpToSequence(number) {
    const current = frame();
    const index = findSequence(current.unit, Math.round(number), current.index);
    if (index === undefined) {
      note(`${rowLabel()}: sequence N${Math.round(number)} was not found; execution continues.`);
      return;
    }
    current.index = index;
    current.jumped = true;
  }

  function macroArguments(words) {
    const locals = new Map();
    let set = 0;
    const seen = new Set();
    for (const word of words) {
      if (["G", "L", "N", "O", "P"].includes(word.letter) || word.value === null) continue;
      if ("IJK".includes(word.letter)) {
        // Argument specification II: every repeated I, J or K starts the next set.
        if (seen.has(word.letter)) {
          set += 1;
          seen.clear();
        }
        seen.add(word.letter);
        const variable = 4 + set * 3 + "IJK".indexOf(word.letter);
        if (variable <= 33) locals.set(variable, word.value);
        continue;
      }
      const variable = TYPE_I_ARGUMENTS[word.letter];
      if (variable) locals.set(variable, word.value);
    }
    return locals;
  }

  // ----- output -------------------------------------------------------------------------------

  function origin() {
    const current = frame();
    if (!current) return { line: 1 };
    const unitLine = current.unit.origin(current.index);
    if (stack.length === 1) return { line: unitLine, locals: localsId(current.locals) };
    return { line: current.mainLine, unit: current.name, unitLine, depth: stack.length - 1, locals: localsId(current.locals) };
  }

  function emit(text) {
    if (out.lines.length >= MAX_LINES) {
      note(`The executed program reached ${MAX_LINES.toLocaleString("en-US")} blocks; the preview stops there.`);
      stopped = true;
      return;
    }
    out.lines.push(text);
    out.map.push(origin());
  }

  function emitComment(text) {
    // Inner parentheses (a program named in a comment) would end the comment early.
    emit(`(${text.slice(1, -1).replace(/[()]/g, "")})`);
  }

  // A stop halts after the flat lines emitted so far (afterLine of them).
  function stopAt(kind, message) {
    const at = origin();
    return { afterLine: out.lines.length, kind, message: message || "", line: at.line, unit: at.unit || null, unitLine: at.unitLine || null };
  }

  function uniqueSequence(key, number) {
    const pending = sequenceNumbers.pending.get(key);
    if (pending !== undefined) {
      sequenceNumbers.pending.delete(key);
      sequenceNumbers.last.set(key, pending);
      return pending;
    }
    let result = number;
    if (sequenceNumbers.used.has(number)) {
      while (sequenceNumbers.used.has(sequenceNumbers.next)) sequenceNumbers.next += 1;
      result = sequenceNumbers.next;
    }
    sequenceNumbers.used.add(result);
    sequenceNumbers.last.set(key, result);
    return result;
  }

  // P/Q of a contour cycle name blocks of the same program: a block still ahead gets its number
  // now, a block already executed keeps the number it was emitted with.
  function contourReference(number) {
    const current = frame();
    const key = `${current.unit.key}:${programRange(current.unit, current.index).start}:${number}`;
    const ahead = (current.unit.sequences.get(number) || []).some((index) => index > current.index &&
      index < programRange(current.unit, current.index).end);
    if (!ahead) return sequenceNumbers.last.get(key) ?? number;
    if (sequenceNumbers.pending.has(key)) return sequenceNumbers.pending.get(key);
    let result = number;
    if (sequenceNumbers.used.has(number)) {
      while (sequenceNumbers.used.has(sequenceNumbers.next)) sequenceNumbers.next += 1;
      result = sequenceNumbers.next;
    }
    sequenceNumbers.used.add(result);
    sequenceNumbers.pending.set(key, result);
    return result;
  }

  // ----- statements -----------------------------------------------------------------------------

  function conditionAt(code, start) {
    let index = start;
    while (code[index] === " ") index += 1;
    if (code[index] !== "[") throw new Error("IF needs a [condition]");
    const end = matchingBracket(code, index);
    if (end < 0) throw new Error("IF condition is not closed");
    return { value: evaluate(code.slice(index, end + 1)), rest: code.slice(end + 1).trim() };
  }

  function gotoTarget(text) {
    const value = evaluate(text.trim());
    if (value === null || !Number.isFinite(value)) throw new Error("GOTO target is vacant");
    return Math.round(value);
  }

  function assignment(code) {
    const match = code.match(/^#\s*(\d+|\[.*?\])\s*=\s*(.*)$/);
    if (!match) return false;
    let target = match[1];
    if (target.startsWith("[")) {
      const end = matchingBracket(code, code.indexOf("["));
      target = String(Math.round(evaluate(code.slice(code.indexOf("["), end + 1)) ?? 0));
      const rest = code.slice(end + 1).replace(/^\s*=\s*/, "");
      writeVariable(Number(target), evaluate(rest), commentOf(frame().raw));
      return true;
    }
    writeVariable(Number(target), evaluate(match[2]), commentOf(frame().raw));
    return true;
  }

  function formatPrint(content) {
    // DPRNT[TEXT*#101[53]]: text, * as a space, #n[ab] as a number with a integer and b decimals.
    let text = "";
    let index = 0;
    while (index < content.length) {
      const character = content[index];
      if (character === "*") {
        text += " ";
        index += 1;
      } else if (character === "#") {
        const match = content.slice(index).match(/^#(\d+)(?:\[(\d)(\d)\])?/);
        if (!match) {
          text += character;
          index += 1;
          continue;
        }
        const value = variableView.get(Number(match[1]));
        const decimals = match[3] !== undefined ? Number(match[3]) : 3;
        text += value === undefined || value === null ? "****" : Number(value).toFixed(decimals);
        index += match[0].length;
      } else {
        text += character;
        index += 1;
      }
    }
    return text.trim();
  }

  function resolveWords(code) {
    return splitWords(code).map((word) => {
      const numeric = /^[+-]?(\d+\.?\d*|\.\d+)$/.test(word.text);
      if (numeric || word.text === "") {
        return { letter: word.letter, text: word.text, value: word.text === "" ? null : Number(word.text), literal: true };
      }
      const value = evaluate(word.text);
      return { letter: word.letter, text: value === null ? "" : formatValue(value), value, literal: false };
    });
  }

  const has = (words, letter, value) => words.some((word) => word.letter === letter && (value === undefined || word.value === value));
  const valueOf = (words, letter) => {
    const found = words.filter((word) => word.letter === letter && word.value !== null);
    return found.length ? found[found.length - 1].value : undefined;
  };

  function trackState(words) {
    for (const word of words) {
      if (word.value === null) continue;
      switch (word.letter) {
        case "G": {
          const group = GROUPS.get(word.value);
          if (group) state.modal.set(group, word.value);
          if (word.value >= 54 && word.value <= 59) state.workOffset = word.value - 53;
          break;
        }
        case "F": state.feed = word.value; break;
        case "S": if (!has(words, "G", 50) && !has(words, "G", 92)) state.speed = word.value; break;
        case "T": state.tool = word.value; break;
        case "M": state.lastM = word.value; break;
        case "N": state.lastN = word.value; break;
        case "B": state.lastB = word.value; break;
        case "D": state.lastD = word.value; break;
        default: break;
      }
    }
    const cycle = words.some((word) => word.letter === "G" && [4, 10, 50, 65, 66, 70, 71, 72, 73, 74, 75, 76].includes(word.value));
    if (cycle) return;
    for (const word of words) {
      if (word.value === null) continue;
      if (AXES.includes(word.letter)) state.position[word.letter] = word.value;
      else if (INCREMENTAL[word.letter]) state.position[INCREMENTAL[word.letter]] = (state.position[INCREMENTAL[word.letter]] || 0) + word.value;
    }
  }

  function dwellSeconds(words) {
    if (!has(words, "G", 4)) return 0;
    const direct = valueOf(words, "X") ?? valueOf(words, "U");
    if (Number.isFinite(direct) && direct >= 0) return direct;
    const milliseconds = valueOf(words, "P");
    return Number.isFinite(milliseconds) && milliseconds >= 0 ? milliseconds / 1000 : 0;
  }

  function emitBlock(sequence, words) {
    const parts = [];
    if (sequence !== undefined) {
      const current = frame();
      const key = `${current.unit.key}:${programRange(current.unit, current.index).start}:${sequence}`;
      parts.push(`N${uniqueSequence(key, sequence)}`);
    }
    const contour = words.some((word) => word.letter === "G" && CONTOUR_CYCLES.has(word.value)) &&
      has(words, "P") && has(words, "Q");
    for (const word of words) {
      if (word.value === null && !word.literal) continue;
      if (contour && (word.letter === "P" || word.letter === "Q") && Number.isFinite(word.value)) {
        parts.push(`${word.letter}${contourReference(Math.round(word.value))}`);
      } else {
        parts.push(`${word.letter}${word.text}`);
      }
    }
    if (parts.length) emit(parts.join(" "));
    out.dwellSeconds += dwellSeconds(words);
    trackState(words);
  }

  // Executes one statement (a block, or the part of an IF .. THEN after THEN).
  function statement(code, sequence) {
    const current = frame();
    if (/^WHILE\b/.test(code)) {
      const condition = conditionAt(code, 5);
      const loop = condition.rest.match(/^DO\s*(\d+)$/);
      if (!loop) throw new Error("WHILE needs DOm");
      const id = Number(loop[1]);
      if (condition.value) {
        current.loops = current.loops.filter((entry) => entry.id !== id);
        current.loops.push({ id, start: current.index });
      } else {
        skipPastEnd(id);
      }
      return;
    }
    const endless = code.match(/^DO\s*(\d+)$/);
    if (endless) {
      const id = Number(endless[1]);
      current.loops = current.loops.filter((entry) => entry.id !== id);
      current.loops.push({ id, start: current.index, endless: true });
      return;
    }
    const end = code.match(/^END\s*(\d+)$/);
    if (end) {
      const id = Number(end[1]);
      const loop = [...current.loops].reverse().find((entry) => entry.id === id);
      if (!loop) {
        note(`${rowLabel()}: END${id} has no matching WHILE/DO${id}.`);
        return;
      }
      current.index = loop.endless ? loop.start + 1 : loop.start;
      current.jumped = true;
      return;
    }
    if (/^IF\b/.test(code)) {
      const condition = conditionAt(code, 2);
      const goTo = condition.rest.match(/^GOTO\s*(.+)$/);
      if (goTo) {
        if (condition.value !== null && condition.value !== 0) jumpToSequence(gotoTarget(goTo[1]));
        return;
      }
      const then = condition.rest.match(/^THEN\s*(.+)$/);
      if (!then) throw new Error("IF needs GOTO or THEN");
      if (condition.value !== null && condition.value !== 0) statement(then[1].trim(), undefined);
      return;
    }
    const goTo = code.match(/^GOTO\s*(.+)$/);
    if (goTo) {
      jumpToSequence(gotoTarget(goTo[1]));
      return;
    }
    if (code.startsWith("#")) {
      if (!assignment(code)) throw new Error(`invalid assignment "${code}"`);
      return;
    }
    const print = code.match(/^(DPRNT|BPRNT)\s*\[(.*)\]$/);
    if (print) {
      if (out.prints.length < 500) out.prints.push({ line: origin().line, text: formatPrint(print[2]) });
      return;
    }
    if (/^(POPEN|PCLOS|SETVN)\b/.test(code)) return;
    block(code, sequence);
  }

  function skipPastEnd(id) {
    const current = frame();
    const range = programRange(current.unit, current.index);
    let depth = 0;
    for (let index = current.index + 1; index < range.end; index += 1) {
      const code = stripComments(current.unit.lines[index]).toUpperCase().replace(/^N\s*\d+\s*/, "");
      if (new RegExp(`^WHILE\\b.*\\bDO\\s*${id}$`).test(code) || new RegExp(`^DO\\s*${id}$`).test(code)) depth += 1;
      if (new RegExp(`^END\\s*${id}$`).test(code)) {
        if (depth === 0) {
          current.index = index + 1;
          current.jumped = true;
          return;
        }
        depth -= 1;
      }
    }
    note(`${rowLabel()}: WHILE DO${id} has no END${id}; execution continues after the program.`);
    current.index = range.end;
    current.jumped = true;
  }

  function insideModalMacro() {
    return stack.some((entry) => entry.kind === "G66" || entry.kind === "G66.1");
  }

  function insideCustomCall() {
    return stack.some((entry) => entry.custom);
  }

  // Inside a macro called by a G/M/T code, those codes act as ordinary codes again.
  function customCall(target, locals) {
    const called = call("G65", target, { locals, label: "G65 P" });
    if (called) called.custom = true;
  }

  function block(code, sequence) {
    const words = resolveWords(code);
    const gValues = words.filter((word) => word.letter === "G").map((word) => word.value);
    const mValues = words.filter((word) => word.letter === "M").map((word) => word.value);
    const current = frame();

    if (gValues.includes(67)) {
      state.modalMacro = undefined;
      const rest = words.filter((word) => !(word.letter === "G" && word.value === 67));
      if (!rest.length && sequence === undefined) return;
      if (rest.length !== words.length) return block(rest.map((word) => `${word.letter}${word.text}`).join(" "), sequence);
    }
    // G66.1: every block becomes a call whose addresses are the arguments.
    if (state.modalMacro?.type === "B" && !insideModalMacro() && !gValues.some((g) => [65, 66, 66.1, 67].includes(g))) {
      if (sequence !== undefined) state.lastN = sequence;
      const locals = new Map(state.modalMacro.locals);
      for (const [id, value] of macroArguments(words)) locals.set(id, value);
      call("G66.1", state.modalMacro.target, { repeat: state.modalMacro.repeat, locals, label: "G66.1 P" });
      return;
    }
    if (gValues.includes(65) || gValues.includes(66) || gValues.includes(66.1)) {
      const p = valueOf(words, "P");
      if (!Number.isFinite(p)) {
        note(`${rowLabel()}: G65/G66 needs a P program number.`);
        return;
      }
      const repeat = Math.max(0, Math.trunc(valueOf(words, "L") ?? 1));
      const locals = macroArguments(words);
      if (gValues.includes(65)) {
        const shown = words.filter((word) => !["G", "P", "L"].includes(word.letter) && word.value !== null)
          .map((word) => `${word.letter}${word.text}`).join(" ");
        call("G65", Math.round(p), { repeat, locals, label: "G65 P", argumentsText: shown });
      } else {
        state.modalMacro = { target: Math.round(p), locals, repeat, type: gValues.includes(66.1) ? "B" : "A" };
      }
      return;
    }
    // Custom macro calls by G code (not inside a macro called that way).
    if (!insideCustomCall()) {
      const customG = gValues.find((g) => custom.gMacros.has(g));
      if (customG !== undefined) {
        const locals = macroArguments(words.filter((word) => !(word.letter === "G" && word.value === customG)));
        customCall(custom.gMacros.get(customG), locals);
        return;
      }
      const customM = mValues.find((m) => custom.mMacros.has(m));
      if (customM !== undefined) {
        const locals = macroArguments(words.filter((word) => !(word.letter === "M" && word.value === customM)));
        customCall(custom.mMacros.get(customM), locals);
        return;
      }
    }

    const callWords = new Set();
    let subprogram;
    if (mValues.includes(98)) {
      const pWord = words.find((word) => word.letter === "P");
      const lWord = valueOf(words, "L");
      let target = pWord?.value;
      let repeat = Math.max(0, Math.trunc(lWord ?? 1));
      if (pWord?.literal && /^\d{5,8}$/.test(pWord.text) && lWord === undefined && !haas) {
        // FANUC M98 Pkkkknnnn: repeat count, then the four-digit program number.
        const digits = pWord.text.padStart(8, "0");
        repeat = Math.max(1, Number(digits.slice(0, 4)));
        target = Number(digits.slice(4));
      }
      if (!Number.isFinite(target)) {
        const name = commentOf(current.raw);
        if (!name) {
          note(`${rowLabel()}: M98 needs a P program number.`);
          return;
        }
        target = name;
      }
      subprogram = { kind: "M98", target: typeof target === "number" ? Math.round(target) : target, repeat };
      ["M", "P", "L"].forEach((letter) => callWords.add(letter));
    } else if (mValues.includes(97)) {
      const p = valueOf(words, "P");
      if (!Number.isFinite(p)) {
        note(`${rowLabel()}: M97 needs a P line number.`);
        return;
      }
      subprogram = { kind: "M97", target: Math.round(p), repeat: Math.max(0, Math.trunc(valueOf(words, "L") ?? 1)) };
      ["M", "P", "L"].forEach((letter) => callWords.add(letter));
    } else if (!insideCustomCall()) {
      const customM = mValues.find((m) => custom.mSubprograms.has(m));
      if (customM !== undefined) {
        subprogram = { kind: "M98", target: custom.mSubprograms.get(customM), repeat: 1, custom: true };
        callWords.add("M");
      } else if (custom.tCall && has(words, "T")) {
        globals.set(149, valueOf(words, "T"));
        subprogram = { kind: "M98", target: 9000, repeat: 1, custom: true };
        callWords.add("T");
      }
    }

    if (mValues.includes(99)) {
      const rest = words.filter((word) => !["M", "P"].includes(word.letter));
      if (rest.length) emitBlock(sequence, rest);
      if (stack.length > 1) {
        returnFromCall(valueOf(words, "P"));
      } else {
        note(`${rowLabel()}: M99 in the main program repeats it on the control; the preview stops after one pass.`);
        stopped = true;
      }
      return;
    }

    const remaining = subprogram ? words.filter((word) => !callWords.has(word.letter)) : words;
    if (!subprogram || remaining.length || sequence !== undefined) {
      if (remaining.length || sequence !== undefined) emitBlock(sequence, remaining);
    }
    if (mValues.includes(0) || mValues.includes(1)) {
      out.stops.push(stopAt(mValues.includes(0) ? "M00" : "M01", commentOf(current.raw)));
    }
    if (subprogram) {
      const called = call(subprogram.kind, subprogram.target, { repeat: subprogram.repeat, label: subprogram.kind === "M97" ? "M97 P" : "M98 P" });
      if (called && subprogram.custom) called.custom = true;
      return;
    }
    if (mValues.includes(30) || mValues.includes(2)) {
      stopped = true;
      return;
    }
    // G66: the macro is called after every block that moves an axis.
    if (state.modalMacro?.type === "A" && !insideModalMacro() && words.some((word) => MOVE_LETTERS.has(word.letter))) {
      call("G66", state.modalMacro.target, { repeat: state.modalMacro.repeat, locals: new Map(state.modalMacro.locals), label: "G66 P" });
    }
  }

  function executeLine(raw) {
    const current = frame();
    current.raw = raw;
    let code = stripComments(raw).toUpperCase();
    if (!code || code === "%") return;
    if (code.startsWith("/")) {
      if (options.blockDelete) return;
      code = code.replace(/^\/\d?/, "").trim();
      if (!code) return;
    }
    const header = code.match(/^O\s*(\d+)/) || code.match(/^:\s*(\d+)/);
    if (header) {
      if (current.headerSeen) {
        if (stack.length > 1) {
          note(`${rowLabel()}: the subprogram ran into the next program without M99; it returns there.`);
          returnFromCall();
        } else {
          note(`${rowLabel()}: the main program ran into program O${header[1]} without M30; the preview stops.`);
          stopped = true;
        }
        return;
      }
      current.headerSeen = true;
      current.programNumber = Number(header[1]);
      return;
    }
    current.headerSeen = true;
    traceRow(current);
    let sequence;
    const numbered = code.match(/^N\s*(\d+)\s*/);
    if (numbered) {
      sequence = Number(numbered[1]);
      state.lastN = sequence;
      code = code.slice(numbered[0].length);
    }
    if (!code) {
      if (sequence !== undefined) emitBlock(sequence, []);
      return;
    }
    statement(code, sequence);
  }

  stack.push({ unit: mainUnit, index: 0, entryIndex: 0, kind: "main", locals: new Map(), repeat: 1, loops: [], headerSeen: false, jumped: false, programNumber: 0 });
  if (options.initialVariables instanceof Map) {
    for (const [id, value] of options.initialVariables) {
      if (Number(id) >= 1 && Number(id) <= 33) stack[0].locals.set(Number(id), value);
      traceWrite(Number(id), value, Number(id) <= 33 ? localsId(stack[0].locals) : 0, "initial", -1);
    }
  }
  while (stack.length && !stopped) {
    const current = frame();
    const range = stack.length > 1 ? programRange(current.unit, current.entryIndex) : { end: current.unit.lines.length };
    if (current.index >= current.unit.lines.length || current.index >= range.end) {
      if (stack.length > 1) {
        note(`${current.name} ended without M99; it returns there.`);
        returnFromCall();
        continue;
      }
      break;
    }
    blocks += 1;
    if (blocks > MAX_BLOCKS) {
      note(`Macro execution stopped after ${MAX_BLOCKS.toLocaleString("en-US")} blocks to prevent an endless loop.`);
      break;
    }
    current.jumped = false;
    try {
      executeLine(current.unit.lines[current.index]);
    } catch (error) {
      note(`${rowLabel()}: ${error.message}`);
    }
    if (frame() === current && !current.jumped) current.index += 1;
  }
  out.executedBlockCount = blocks;
  out.trace = {
    rows: trace.rows,
    writes: trace.writes,
    systemReads: trace.systemReads,
    used: [...trace.used].sort((a, b) => a - b)
  };
  return out;
}

// ----- static call scan ----------------------------------------------------------------------------

// The programs a program text calls, without running it (the viewer's subprogram tree): G65,
// G66/G66.1 and M98 by number (the FANUC eight-digit P too) or by name, and custom macro calls by
// G/M/T codes. A call whose P is computed (P#1) is reported with target null.
function scanCalls(lines, parameters, haas) {
  const custom = customCalls(parameters);
  const calls = [];
  (lines || []).forEach((raw, index) => {
    const code = stripComments(raw).toUpperCase().replace(/^\/\d?/, "").trim();
    if (!code || /^(?:#|IF|WHILE|GOTO|END|DO)/.test(code.replace(/^N\s*\d+\s*/, ""))) return;
    let words;
    try {
      words = splitWords(code.replace(/^N\s*\d+\s*/, ""));
    } catch {
      return;
    }
    const numbers = (letter) => words.filter((word) => word.letter === letter).map((word) => word.text);
    const g = numbers("G").map(Number);
    const m = numbers("M").map(Number);
    const p = words.find((word) => word.letter === "P");
    const literal = p && /^\d+$/.test(p.text);
    const row = index + 1;
    if (g.includes(65) || g.includes(66) || g.includes(66.1)) {
      calls.push({ kind: g.includes(65) ? "G65" : "G66", target: literal ? Number(p.text) : null, row });
      return;
    }
    if (m.includes(98)) {
      if (literal) {
        const digits = p.text.length > 4 && !haas ? p.text.padStart(8, "0").slice(4) : p.text;
        calls.push({ kind: "M98", target: Number(digits), row });
      } else if (!p) {
        const name = commentOf(raw);
        calls.push({ kind: "M98", target: name || null, row });
      } else {
        calls.push({ kind: "M98", target: null, row });
      }
      return;
    }
    const customG = g.find((value) => custom.gMacros.has(value));
    if (customG !== undefined) calls.push({ kind: "G65", target: custom.gMacros.get(customG), row });
    const customM = m.find((value) => custom.mMacros.has(value) || custom.mSubprograms.has(value));
    if (customM !== undefined) calls.push({ kind: "G65", target: custom.mMacros.get(customM) ?? custom.mSubprograms.get(customM), row });
    if (custom.tCall && words.some((word) => word.letter === "T")) calls.push({ kind: "M98", target: 9000, row });
  });
  return calls;
}

module.exports = { execute, splitWords, customCalls, scanCalls };
