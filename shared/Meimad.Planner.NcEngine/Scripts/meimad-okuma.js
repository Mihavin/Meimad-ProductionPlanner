"use strict";

// Okuma OSP-P200L / OSP-P200L-R lathe program executor (Programming Manual LE33-013).
//
// Okuma programs are executed here with OSP semantics and are never rewritten into FANUC cycles:
// the program is run block by block and every cycle is expanded into its own tool path. The lathe
// interpreter then only draws and times plain moves (G00/G01/G02/G03 with the feed, spindle and
// tool state), so no FANUC cycle rule or FANUC parameter takes part in an Okuma preview.
//
//   program      sequence names (N100, NT11, NAT01), "$" continuation lines, comments, block delete,
//                program names (O...), CALL O... Q.. with variable setting, RTS, GOTO N.., IF [..] N..,
//                IF [..] GOTO N.., IF name N.. (defined test), M00/M01 stops, M02/M30 end.
//   values       numbers and expressions in address words (X19.03-0.6, Z=V1+2, X=100*SIN[30]), common
//                variables V1-V200, local variables, the mathematical functions of section 1-5.
//   motion       G00, G01, G02/G03 with L radius (CALRG) or I/K, G90/G91, direct taper angle A (also an
//                A-only block followed by X Z A), automatic chamfering G75 and rounding G76 with L,
//                G50 zero shift and G50 S, G94/G95, G96/G97, G40/G41/G42, G04 F dwell, T nnttoo.
//   LAP          contour definitions G81/G82 ... G80 (skipped in the program flow, found by sequence
//                name wherever they are), G85 bar turning with descending slopes, G86 copy turning,
//                G87 finish turning, G88 continuous thread cutting, finish allowances U/W.
//   fixed cycles G31/G32/G33 thread cutting, G34/G35, compound thread cutting G71/G72 with the infeed
//                patterns M73/M74/M75, grooving and drilling G73/G74 with I/K/D/L/DA, tapping G77/G78.
//
// execute() returns the same shape as the FANUC macro executor: a flat program with a line map
// (the row of the main program, or the called program and its row), stops, notes, dwell time and
// the execution trace. A map entry of a cycle move also names the cycle, its pass and the segment
// kind, which the adapter copies to the drawn segment.

const MAX_DEPTH = 9;
const MAX_BLOCKS = 300000;
const MAX_LINES = 200000;
const MAX_TRACE_ROWS = 300000;
const MAX_TRACE_PER_ROW = 2000;
const MAX_TRACE_WRITES = 100000;
const MAX_THREAD_PASSES = 400;
const MAX_CYCLE_STEPS = 20000;
const MAX_REPEATS = 500;
const EPSILON = 1e-6;
const LAP_RELIEF = 0.1;
const PECK_RETRACT = 0.1;
const COMMON_ID_BASE = 10000;
const LOCAL_ID_BASE = 20000;
const RADIANS = Math.PI / 180;

const EXTENDED_ADDRESSES = new Set([
  "AA", "AB", "DA", "DB", "FA", "FB", "IA", "IB", "KA", "KB", "LA", "LB", "RA", "RB",
  "SA", "SB", "TA", "TB", "UA", "UB", "WA", "WB", "XA", "XB", "ZA", "ZB", "BC", "BR", "QA", "PW"
]);
const FLAGS = new Set(["CALRG"]);
const IGNORED_STATEMENTS = new Set([
  "PUT", "GET", "READ", "WRITE", "PRINT", "LPRINT", "SPRINT", "DEF", "DRAW", "DELETE", "CLEAR", "PSELECT", "END",
  "VLMON", "NOEX", "MSG", "NMSG", "TLFON", "TLFOFF", "FWRITE", "FOPEN", "FCLOS", "DNCIN", "DNCOUT", "PS", "PT", "TRANS"
]);
const FUNCTIONS = {
  SIN: (a) => Math.sin(a * RADIANS),
  COS: (a) => Math.cos(a * RADIANS),
  TAN: (a) => Math.tan(a * RADIANS),
  ATAN: (a) => Math.atan(a) / RADIANS,
  ATAN2: (b, a) => Math.atan2(b, a) / RADIANS,
  SQRT: (a) => Math.sqrt(a),
  ABS: (a) => Math.abs(a),
  BIN: (a) => a,
  BCD: (a) => a,
  ROUND: (a) => Math.round(a),
  FIX: (a) => Math.trunc(a),
  FUP: (a) => (a >= 0 ? Math.ceil(a) : Math.floor(a)),
  DROUND: (a) => Math.round(a * 1000) / 1000,
  DFIX: (a) => Math.trunc(a * 1000) / 1000,
  DFUP: (a) => (a >= 0 ? Math.ceil(a * 1000) : Math.floor(a * 1000)) / 1000,
  MOD: (a, b) => (b === 0 ? 0 : a % b)
};
const COMPARISONS = {
  EQ: (a, b) => Math.abs(a - b) < 1e-9,
  NE: (a, b) => Math.abs(a - b) >= 1e-9,
  LT: (a, b) => a < b,
  LE: (a, b) => a <= b,
  GT: (a, b) => a > b,
  GE: (a, b) => a >= b
};

function stripComments(raw) {
  return String(raw).replace(/\([^)]*\)?/g, " ").replace(/\r/g, "");
}

function commentOf(raw) {
  return [...String(raw).matchAll(/\(([^)]*)\)?/g)].map((match) => match[1].replace(/^[-\s]+|[-\s]+$/g, "")).filter(Boolean).join(" ");
}

function fmt(value) {
  if (!Number.isFinite(value)) return "0.";
  const rounded = Math.round(value * 1e4) / 1e4;
  const text = String(rounded === 0 ? 0 : rounded);
  if (/e/i.test(text)) return rounded.toFixed(4);
  return text.includes(".") ? text : `${text}.`;
}

const near = (a, b) => Math.abs(a - b) < EPSILON;
const samePoint = (a, b) => near(a.x, b.x) && near(a.z, b.z);

// ----- expressions -----------------------------------------------------------------------------------

// Parses one value starting at text[start]. With spaces = false the value ends at the first blank
// outside brackets (a plain word: X19.03-0.6); with spaces = true blanks may surround operators
// (after "=": V5 = V5 + 1). read(name, indexValue) returns a variable's value.
function parseExpression(text, start, read, spaces) {
  let index = start;
  let depth = 0;
  const skip = () => {
    if (!spaces && depth === 0) return;
    while (text[index] === " " || text[index] === "\t") index += 1;
  };
  const keyword = (names) => {
    if (!spaces && depth === 0) return undefined;
    const match = /^[A-Z]+/.exec(text.slice(index, index + 5));
    if (!match) return undefined;
    const found = names.find((name) => match[0] === name);
    return found && !/[A-Z0-9]/.test(text[index + found.length] || "") ? found : undefined;
  };

  function primary() {
    skip();
    const character = text[index];
    if (character === "[") {
      index += 1;
      depth += 1;
      const value = logical();
      skip();
      if (text[index] !== "]") throw new Error("a bracket is not closed");
      index += 1;
      depth -= 1;
      return value;
    }
    const number = /^(?:\d+\.?\d*|\.\d+)/.exec(text.slice(index));
    if (number) {
      index += number[0].length;
      return Number(number[0]);
    }
    const name = /^(?:V\d{1,3}(?![A-Z0-9])|[A-Z][A-Z0-9]*)/.exec(text.slice(index));
    if (!name) throw new Error(`a value is expected at "${text.slice(index, index + 12)}"`);
    index += name[0].length;
    if (FUNCTIONS[name[0]]) {
      skip();
      if (text[index] !== "[") throw new Error(`${name[0]} needs [ ]`);
      index += 1;
      depth += 1;
      const values = [logical()];
      skip();
      while (text[index] === ",") {
        index += 1;
        values.push(logical());
        skip();
      }
      if (text[index] !== "]") throw new Error(`${name[0]} [ is not closed`);
      index += 1;
      depth -= 1;
      return FUNCTIONS[name[0]](...values);
    }
    let subscript;
    if (text[index] === "[") {
      index += 1;
      depth += 1;
      subscript = logical();
      skip();
      if (text[index] !== "]") throw new Error(`${name[0]}[ is not closed`);
      index += 1;
      depth -= 1;
    }
    return read(name[0], subscript);
  }

  function unary() {
    skip();
    if (text[index] === "-") {
      index += 1;
      return -unary();
    }
    if (text[index] === "+") {
      index += 1;
      return unary();
    }
    if (keyword(["NOT"])) {
      index += 3;
      return unary() ? 0 : 1;
    }
    return primary();
  }

  function product() {
    let value = unary();
    for (;;) {
      const mark = index;
      skip();
      const operator = text[index];
      if (operator !== "*" && operator !== "/") {
        index = mark;
        return value;
      }
      index += 1;
      const right = unary();
      if (operator === "/" && right === 0) throw new Error("division by zero");
      value = operator === "*" ? value * right : value / right;
    }
  }

  function sum() {
    let value = product();
    for (;;) {
      const mark = index;
      skip();
      const operator = text[index];
      if (operator !== "+" && operator !== "-") {
        index = mark;
        return value;
      }
      index += 1;
      const right = product();
      value = operator === "+" ? value + right : value - right;
    }
  }

  function comparison() {
    const value = sum();
    const mark = index;
    skip();
    const operator = keyword(Object.keys(COMPARISONS));
    if (!operator) {
      index = mark;
      return value;
    }
    index += operator.length;
    return COMPARISONS[operator](value, sum()) ? 1 : 0;
  }

  function logical() {
    let value = comparison();
    for (;;) {
      const mark = index;
      skip();
      const operator = keyword(["AND", "OR", "EOR"]);
      if (!operator) {
        index = mark;
        return value;
      }
      index += operator.length;
      const right = comparison();
      if (operator === "AND") value = Math.trunc(value) & Math.trunc(right);
      else if (operator === "OR") value = Math.trunc(value) | Math.trunc(right);
      else value = Math.trunc(value) ^ Math.trunc(right);
    }
  }

  const value = logical();
  return { value, end: index };
}

// ----- program text ------------------------------------------------------------------------------------

const SEQUENCE_NAME = /^N([0-9A-Z]+)(?=\s|$)/;
const PROGRAM_NAME = /^O([0-9A-Z][0-9A-Z-]*)(?=\s|$)/;
const hasG = (code, numbers) => new RegExp(`(?<![A-Z])G0*(?:${numbers.join("|")})(?![\\d.])`).test(code);

// Splits a block into its sequence name and the rest. "N100G00X50" (no blank) is read as N100.
function splitSequence(code) {
  if (/^NOEX\b/.test(code)) return { name: undefined, rest: code.replace(/^NOEX\b\s*/, "") };
  const named = SEQUENCE_NAME.exec(code);
  if (named) return { name: named[1], rest: code.slice(named[0].length).trim() };
  const numbered = /^N(\d+)(?=[A-Z])/.exec(code);
  if (numbered) return { name: numbered[1], rest: code.slice(numbered[0].length).trim() };
  return { name: undefined, rest: code };
}

// The program as blocks: comments removed, upper case, "$" continuation lines joined to the block
// they continue (rows keep their numbers), sequence names, program names and LAP contours indexed.
function createUnit(name, lines, options = {}) {
  const codes = [];
  const comments = [];
  let previous = -1;
  lines.forEach((raw, index) => {
    let code = stripComments(raw).toUpperCase().trim();
    comments.push(commentOf(raw));
    if (/^\$\s*[\w-]+\.[A-Z]{3}%?\s*$/.test(code) || code === "%") code = "";
    if (code.startsWith("$") && previous >= 0) {
      codes[previous] = `${codes[previous]} ${code.slice(1).trim()}`;
      codes.push("");
      return;
    }
    codes.push(code);
    if (code) previous = index;
  });
  const sequences = new Map();
  const names = new Array(codes.length);
  const bodies = new Array(codes.length);
  const programs = new Map();
  const programStarts = [];
  codes.forEach((code, index) => {
    const body = code.replace(/^\/\d?\s*/, "");
    const program = PROGRAM_NAME.exec(body);
    if (program && !/^O[A-Z]*\s*=/.test(body)) {
      programStarts.push(index);
      if (!programs.has(program[1])) programs.set(program[1], index);
      bodies[index] = "";
      names[index] = undefined;
      return;
    }
    const split = splitSequence(body);
    names[index] = split.name;
    bodies[index] = split.rest;
    if (split.name !== undefined) {
      if (!sequences.has(split.name)) sequences.set(split.name, []);
      sequences.get(split.name).push(index);
    }
  });
  return {
    name,
    lines,
    codes,
    comments,
    bodies,
    names,
    sequences,
    programs,
    programStarts,
    external: Boolean(options.external),
    key: options.key || name
  };
}

function programRange(unit, index) {
  let start = 0;
  let end = unit.codes.length;
  for (const programStart of unit.programStarts) {
    if (programStart <= index) start = programStart;
    else {
      end = programStart;
      break;
    }
  }
  return { start, end };
}

// A sequence name is a character string (N0123 and N123 differ); a numeric name also matches by
// value so "G85 N1" finds "N001 G81".
function findSequence(unit, name, fromIndex) {
  const range = programRange(unit, fromIndex);
  const pick = (candidates) => {
    const inRange = (candidates || []).filter((index) => index >= range.start && index < range.end);
    return inRange.find((index) => index > fromIndex) ?? inRange[0];
  };
  const exact = pick(unit.sequences.get(name));
  if (exact !== undefined) return exact;
  if (!/^\d+$/.test(name)) return undefined;
  for (const [key, candidates] of unit.sequences) {
    if (/^\d+$/.test(key) && Number(key) === Number(name)) {
      const found = pick(candidates);
      if (found !== undefined) return found;
    }
  }
  return undefined;
}

// The row of the G80 that ends the contour definition starting at a G81/G82/G83 row.
function contourEnd(unit, startIndex) {
  const range = programRange(unit, startIndex);
  for (let index = startIndex + 1; index < range.end; index += 1) {
    if (hasG(unit.bodies[index] || "", ["80"])) return index;
  }
  return undefined;
}

// ----- geometry ------------------------------------------------------------------------------------------

// Points are { x: diameter, z }. Arcs are computed with X as a radius.
function arcCenter(from, to, radius, clockwise, large) {
  const sz = from.z;
  const sr = from.x / 2;
  const dz = to.z - sz;
  const dr = to.x / 2 - sr;
  const chord = Math.hypot(dz, dr);
  if (chord < EPSILON || chord > radius * 2 + 1e-6) return undefined;
  const height = Math.sqrt(Math.max(0, radius * radius - (chord / 2) ** 2));
  const mz = sz + dz / 2;
  const mr = sr + dr / 2;
  const candidates = [
    { z: mz - (dr / chord) * height, r: mr + (dz / chord) * height },
    { z: mz + (dr / chord) * height, r: mr - (dz / chord) * height }
  ].map((center) => ({ center, sweep: Math.abs(arcSweep(from, to, center, clockwise)) }));
  candidates.sort((left, right) => (large ? right.sweep - left.sweep : left.sweep - right.sweep));
  const center = candidates[0].center;
  return { x: center.r * 2, z: center.z };
}

function arcSweep(from, to, center, clockwise) {
  const start = Math.atan2(from.x / 2 - center.r, from.z - center.z);
  const end = Math.atan2(to.x / 2 - center.r, to.z - center.z);
  const twoPi = Math.PI * 2;
  let sweep = clockwise ? start - end : end - start;
  sweep %= twoPi;
  if (sweep < 0) sweep += twoPi;
  return clockwise ? -sweep : sweep;
}

function arcPoints(element) {
  const center = { r: element.center.x / 2, z: element.center.z };
  const radius = Math.hypot(element.from.z - center.z, element.from.x / 2 - center.r);
  const sweep = arcSweep(element.from, element.to, center, element.clockwise);
  const start = Math.atan2(element.from.x / 2 - center.r, element.from.z - center.z);
  const steps = Math.max(8, Math.ceil(Math.abs(sweep) / (Math.PI / 60)));
  const points = [];
  for (let step = 1; step < steps; step += 1) {
    const angle = start + sweep * (step / steps);
    points.push({ z: center.z + Math.cos(angle) * radius, x: (center.r + Math.sin(angle) * radius) * 2 });
  }
  points.push({ x: element.to.x, z: element.to.z });
  return points;
}

// The elements of a contour as one polyline; every point knows the element it came from.
function polyline(elements, start) {
  const points = [{ x: start.x, z: start.z, element: undefined }];
  for (const element of elements) {
    if (!element.to) continue;
    const next = element.type === "arc" ? arcPoints(element) : [element.to];
    for (const point of next) {
      if (!samePoint(point, points[points.length - 1])) points.push({ x: point.x, z: point.z, element });
    }
  }
  return points;
}

const shifted = (point, dx, dz) => ({ ...point, x: point.x + dx, z: point.z + dz });

function shiftElement(element, dx, dz) {
  if (!element.to) return element;
  return {
    ...element,
    from: element.from ? shifted(element.from, dx, dz) : element.from,
    to: shifted(element.to, dx, dz),
    center: element.center ? shifted(element.center, dx, dz) : undefined
  };
}

// Cumulative infeed depths of a compound thread cycle (section 7, 5-4-2): height H, first depth D,
// finishing allowance U (all on the infeed axis' own scale), pattern 73, 74 or 75.
function threadDepths(height, first, allowance, pattern) {
  const depths = [];
  const target = Math.max(0, height - Math.max(0, allowance || 0));
  const step = first > 0 ? first : target;
  const push = (depth) => {
    const value = Math.min(target, depth);
    if (depths.length < MAX_THREAD_PASSES && (!depths.length || value > depths[depths.length - 1] + 1e-9)) depths.push(value);
  };
  if (target > 0 && step > 0) {
    if (pattern === 74) {
      for (let count = 1; count <= MAX_THREAD_PASSES && (count - 1) * step < target; count += 1) push(count * step);
    } else if (pattern === 75) {
      for (let count = 1; count <= MAX_THREAD_PASSES && Math.sqrt(count - 1) * step < target; count += 1) push(Math.sqrt(count) * step);
    } else {
      // Pattern 1: D per pass up to D before H-U, then D/2, D/4, D/8, D/8.
      const limit = target - step;
      for (let count = 1; count <= MAX_THREAD_PASSES && count * step < limit - 1e-9; count += 1) push(count * step);
      if (limit > 1e-9) push(limit);
      const base = Math.max(0, limit);
      for (const fraction of [0.5, 0.75, 0.875, 1]) push(base + (target - base) * fraction);
    }
    push(target);
  }
  if (allowance > 0 && height > 0) {
    if (depths.length < MAX_THREAD_PASSES) depths.push(height);
  }
  return depths.length ? depths : [height];
}

// ----- executor ------------------------------------------------------------------------------------------

// options:
//   resolve(target)      external program lookup (src/subprograms.js resolver) or undefined
//   initialVariables     text of the viewer's "Initial vars" setting; "V1=3" entries preset common variables
//   blockDelete          true skips "/" blocks
function execute(main, options = {}) {
  const out = {
    lines: [], map: [], notes: [], external: [], stops: [], prints: [], units: new Map(), dwellSeconds: 0,
    callCount: 0, executedBlockCount: 0,
    cycles: { roughPasses: 0, copyPasses: 0, finishCycles: 0, lapThreadPasses: 0, threadPasses: 0, threadCycles: 0, pecks: 0, grooves: 0, taps: 0, zeroShifts: 0 }
  };
  const noted = new Set();
  const note = (text) => {
    if (noted.has(text) || out.notes.length >= 60) return;
    noted.add(text);
    out.notes.push(text);
  };
  const mainUnit = createUnit("main", main.lines, { key: "main" });
  const externalUnits = new Map();
  const common = new Map();
  for (const match of String(options.initialVariables || "").toUpperCase().matchAll(/(?<![A-Z])V(\d{1,3})\s*=\s*([-+]?(?:\d+\.?\d*|\.\d+))/g)) {
    common.set(Number(match[1]), Number(match[2]));
  }
  const state = {
    x: undefined, z: undefined, absolute: true, motion: 0, feed: undefined, perMinute: false,
    shiftX: 0, shiftZ: 0, pendingAngle: undefined, thread: undefined
  };
  const stack = [];
  const frame = () => stack[stack.length - 1];
  let stopped = false;
  let blocks = 0;
  let conditions = 0;
  const backwardJumps = new Map();
  // Per executed row: how many IF statements had been evaluated when it last ran.
  const visited = new Map();

  const trace = { rows: new Map(), rowCount: 0, writes: [], systemReads: [], used: new Set(), names: {}, scopes: {} };
  const localIds = new Map();
  const variableId = (name) => {
    const commonMatch = /^V(\d{1,3})$/.exec(name);
    const id = commonMatch ? COMMON_ID_BASE + Number(commonMatch[1]) : (localIds.get(name) ?? localIds.set(name, LOCAL_ID_BASE + localIds.size).get(name));
    trace.names[id] = name;
    trace.scopes[id] = commonMatch ? "Common" : "Local";
    trace.used.add(id);
    return id;
  };
  const rowKey = (entry) => `${entry.unit.external ? entry.unit.name : "main"}|${entry.index + 1}`;

  const rowLabel = (index) => {
    const current = frame();
    if (!current) return "End";
    const row = (index ?? current.index) + 1;
    return current.unit === mainUnit ? `Row ${row}` : `${current.unit.name} row ${row}`;
  };

  // ----- variables ------------------------------------------------------------------------------------

  // VC1-VC200 is the machining-centre spelling of the common variables; it names the same ones.
  const commonName = (name) => name.replace(/^VC(\d{1,3})$/, "V$1");

  function readVariable(rawName, subscript) {
    const name = commonName(rawName);
    const commonMatch = /^V(\d{1,3})$/.exec(name);
    if (commonMatch) {
      variableId(name);
      const number = Number(commonMatch[1]);
      if (!common.has(number)) {
        note(`${rowLabel()}: common variable ${name} is read before the program sets it; the preview uses 0 (the control keeps the last value).`);
        return 0;
      }
      return common.get(number);
    }
    for (let level = stack.length - 1; level >= 0; level -= 1) {
      if (stack[level].locals.has(name)) {
        variableId(name);
        return stack[level].locals.get(name);
      }
    }
    if (/^V[A-Z]/.test(name)) {
      note(`${rowLabel()}: system variable ${name}${subscript === undefined ? "" : `[${subscript}]`} is read as 0: the preview does not know the control's data.`);
      return 0;
    }
    throw new Error(`variable ${name} is not defined`);
  }

  function writeVariable(rawName, subscript, value, target = frame()) {
    const name = commonName(rawName);
    const commonMatch = /^V(\d{1,3})$/.exec(name);
    const id = /^V[A-Z]/.test(name) ? undefined : variableId(name);
    if (commonMatch) common.set(Number(commonMatch[1]), value);
    else if (/^V[A-Z]/.test(name)) {
      note(`${rowLabel()}: system variable ${name} is set by the program; the preview does not apply it.`);
      return;
    } else {
      const owner = [...stack].reverse().find((entry) => entry.locals.has(name)) || target;
      owner.locals.set(name, value);
    }
    if (id !== undefined && trace.writes.length < MAX_TRACE_WRITES) {
      trace.writes.push([out.lines.length, id, value, 0, rowKey(frame()), blocks]);
    }
  }

  const isDefined = (name) => /^VC?\d{1,3}$/.test(name) || stack.some((entry) => entry.locals.has(name));
  const evaluate = (text, start, spaces) => parseExpression(text, start, readVariable, spaces);

  // ----- block parsing ---------------------------------------------------------------------------------

  // Reads the words of a block. Assignments (V1=V1+1, DIA=20) are carried out as they are met;
  // assign(name, subscript, value) may redirect them (CALL registers them in the called program).
  function parseBlock(code, assign = writeVariable) {
    const block = { words: new Map(), g: [], m: [], extended: new Map(), flags: new Set(), target: undefined, toolText: undefined };
    let index = 0;
    while (index < code.length) {
      const character = code[index];
      if (character === " " || character === "\t" || character === "$") {
        index += 1;
        continue;
      }
      const rest = code.slice(index);
      if (character === "N") {
        const name = /^N([0-9A-Z]+)/.exec(rest);
        if (!name) throw new Error("N needs a sequence name");
        block.target = name[1];
        index += name[0].length;
        continue;
      }
      const named = /^(V\d{1,3}(?![A-Z0-9])|[A-Z]{2}[A-Z0-9]*)\s*(\[[^\]]*\])?\s*(=)?/.exec(rest);
      if (named && (named[3] || FLAGS.has(named[1]))) {
        if (!named[3]) {
          block.flags.add(named[1]);
          index += named[1].length;
          continue;
        }
        const subscript = named[2] ? evaluate(named[2], 0, true).value : undefined;
        const parsed = evaluate(code, index + named[0].length, true);
        if (EXTENDED_ADDRESSES.has(named[1])) block.extended.set(named[1], parsed.value);
        else assign(named[1], subscript, parsed.value);
        index = parsed.end;
        continue;
      }
      if (!/[A-Z]/.test(character)) throw new Error(`unexpected "${character}" in "${code}"`);
      let valueStart = index + 1;
      while (code[valueStart] === " " || code[valueStart] === "\t") valueStart += 1;
      let parsed;
      if (code[valueStart] === "=") parsed = evaluate(code, valueStart + 1, true);
      else if (/[-+.\d[]/.test(code[valueStart] || "")) parsed = evaluate(code, valueStart, false);
      else throw new Error(`address ${character} has no value in "${code}"`);
      if (character === "G") block.g.push(parsed.value);
      else if (character === "M") block.m.push(parsed.value);
      else block.words.set(character, parsed.value);
      if (character === "T") block.toolText = code.slice(valueStart, parsed.end).replace(/[^0-9]/g, "");
      index = parsed.end;
    }
    return block;
  }

  // ----- output ----------------------------------------------------------------------------------------

  function origin(index) {
    const current = frame();
    if (!current) return { line: 1 };
    const unitLine = (index ?? current.index) + 1;
    if (stack.length === 1) return { line: unitLine };
    return { line: current.mainLine, unit: current.name, unitLine, depth: stack.length - 1 };
  }

  function emit(text, meta, index) {
    if (out.lines.length >= MAX_LINES) {
      note(`The executed program reached ${MAX_LINES.toLocaleString("en-US")} blocks; the preview stops there.`);
      stopped = true;
      return;
    }
    out.lines.push(text);
    out.map.push(meta ? { ...origin(index), ...meta } : origin(index));
  }

  const emitComment = (text) => emit(`(${String(text).replace(/[()]/g, "")})`);

  function stopAt(kind, message) {
    const at = origin();
    return { afterLine: out.lines.length, kind, message: message || "", line: at.line, unit: at.unit || null, unitLine: at.unitLine || null, step: blocks };
  }

  const position = () => ({ x: state.x, z: state.z });
  const known = () => Number.isFinite(state.x) && Number.isFinite(state.z);

  function coordinates(point) {
    const parts = [];
    if (Number.isFinite(point.x)) parts.push(`X${fmt(point.x - state.shiftX)}`);
    if (Number.isFinite(point.z)) parts.push(`Z${fmt(point.z - state.shiftZ)}`);
    return parts.join(" ");
  }

  let feedModeEmitted;
  function feedMode(perMinute, meta, index) {
    if (feedModeEmitted === perMinute) return;
    feedModeEmitted = perMinute;
    emit(perMinute ? "G94" : "G95", meta, index);
  }

  // Draws one element from the current position and moves there. kind "rapid", "line" or "arc".
  function move(element, meta, index) {
    const from = position();
    const to = { x: element.to.x ?? from.x, z: element.to.z ?? from.z };
    const prefix = element.compensation ? `G${element.compensation} ` : "";
    const moving = !Number.isFinite(from.x) || !Number.isFinite(from.z) || !samePoint(from, to);
    if (moving || prefix) {
      if (element.type === "rapid") {
        emit(`${prefix}G00 ${coordinates(to)}`.trim(), meta, index);
      } else {
        feedMode(element.thread ? false : state.perMinute, meta, index);
        const feed = Number.isFinite(element.feed) && element.feed > 0 ? ` F${fmt(element.feed)}` : "";
        if (element.type === "arc" && element.center && known()) {
          emit(`${prefix}${element.clockwise ? "G02" : "G03"} ${coordinates(to)} I${fmt((element.center.x - from.x) / 2)} K${fmt(element.center.z - from.z)}${feed}`, meta, index);
        } else {
          emit(`${prefix}G01 ${coordinates(to)}${feed}`, meta, index);
        }
      }
    }
    state.x = to.x;
    state.z = to.z;
  }

  const rapidTo = (point, meta, index) => move({ type: "rapid", to: point }, meta, index);
  const feedTo = (point, feed, meta, index, extra = {}) => move({ type: "line", to: point, feed, ...extra }, meta, index);

  // ----- motion blocks -----------------------------------------------------------------------------------

  // The path elements of one motion block from the modal state `at` ({ x, z, absolute, motion,
  // feed, pendingAngle }); `at` is advanced to the end of the block.
  function blockElements(block, at, index) {
    for (const code of block.g) {
      if ([0, 1, 2, 3, 31, 32, 33, 34, 35].includes(code)) at.motion = code;
      if (code === 90) at.absolute = true;
      if (code === 91) at.absolute = false;
    }
    if (block.words.has("F")) at.feed = block.words.get("F");
    const hasX = block.words.has("X");
    const hasZ = block.words.has("Z");
    const hasA = block.words.has("A");
    if (!hasX && !hasZ && !hasA) return [];
    const from = { x: at.x, z: at.z };
    const absoluteValue = (letter, current) => {
      if (!block.words.has(letter)) return undefined;
      const value = block.words.get(letter);
      return at.absolute ? value : (Number.isFinite(current) ? current + value : value);
    };
    let tx = absoluteValue("X", at.x);
    let tz = absoluteValue("Z", at.z);
    const type = at.motion === 0 ? "rapid" : "line";
    const base = { feed: at.feed, thread: at.motion >= 31, index };
    const elements = [];
    const line = (start, end, extra = {}) => elements.push({ type, from: start, to: end, ...base, ...extra });
    const angle = hasA ? block.words.get("A") * RADIANS : undefined;

    if (at.pendingAngle !== undefined) {
      // The previous block gave only an angle: this block's X Z A closes the two lines.
      const first = at.pendingAngle;
      at.pendingAngle = undefined;
      if (hasX && hasZ && hasA && Number.isFinite(from.x) && Number.isFinite(from.z)) {
        const d1 = { z: Math.cos(first.angle), r: Math.sin(first.angle) };
        const d2 = { z: Math.cos(angle), r: Math.sin(angle) };
        const determinant = d1.z * d2.r - d1.r * d2.z;
        if (Math.abs(determinant) < 1e-9) throw new Error("the two angle commands are parallel; no intersection");
        const dz = tz - from.z;
        const dr = tx / 2 - from.x / 2;
        const t = (dz * d2.r - dr * d2.z) / determinant;
        const corner = { x: (from.x / 2 + d1.r * t) * 2, z: from.z + d1.z * t };
        line(from, corner, { index: first.index });
        line(corner, { x: tx, z: tz });
        at.x = tx;
        at.z = tz;
        return elements;
      }
      throw new Error("an angle-only block must be followed by a block with X, Z and A");
    }
    if (hasA && !(hasX && hasZ)) {
      if (!hasX && !hasZ) {
        at.pendingAngle = { angle, index };
        return [];
      }
      if (!Number.isFinite(from.x) || !Number.isFinite(from.z)) throw new Error("an angle command needs a known start position");
      if (hasX) {
        if (Math.abs(Math.sin(angle)) < 1e-9) throw new Error("angle A gives no intersection with the X coordinate");
        tz = from.z + ((tx - from.x) / 2) * (Math.cos(angle) / Math.sin(angle));
      } else {
        if (Math.abs(Math.cos(angle)) < 1e-9) throw new Error("angle A gives no intersection with the Z coordinate");
        tx = from.x + 2 * (tz - from.z) * (Math.sin(angle) / Math.cos(angle));
      }
    } else if (hasA) {
      note(`${rowLabel(index)}: angle A is given with both X and Z; the end point is used.`);
    }
    tx ??= at.x;
    tz ??= at.z;
    const to = { x: tx, z: tz };

    if (at.motion === 2 || at.motion === 3) {
      const clockwise = at.motion === 2;
      let center;
      if (block.words.has("L") && !block.words.has("I") && !block.words.has("K")) {
        center = Number.isFinite(from.x) && Number.isFinite(from.z)
          ? arcCenter(from, to, Math.abs(block.words.get("L")), clockwise, block.flags.has("CALRG"))
          : undefined;
        if (!center) note(`${rowLabel(index)}: arc radius L${fmt(Math.abs(block.words.get("L")))} does not reach the end point; a straight line is drawn.`);
      } else if (Number.isFinite(from.x) && Number.isFinite(from.z)) {
        center = { x: from.x + 2 * (block.words.get("I") ?? 0), z: from.z + (block.words.get("K") ?? 0) };
      }
      elements.push(center
        ? { type: "arc", from, to, clockwise, center, ...base }
        : { type: "line", from, to, ...base });
    } else if ((block.g.includes(75) || block.g.includes(76)) && block.words.has("L") && hasX !== hasZ && !hasA &&
        Number.isFinite(from.x) && Number.isFinite(from.z)) {
      // Automatic chamfering / rounding of a square corner: this block runs along one axis to the
      // corner, L is the size and its sign is the direction of the next block on the other axis.
      const size = block.words.get("L");
      const length = Math.abs(size);
      const before = hasX
        ? { x: to.x - Math.sign(to.x - from.x) * 2 * length, z: to.z }
        : { x: to.x, z: to.z - Math.sign(to.z - from.z) * length };
      const after = hasX ? { x: to.x, z: to.z + size } : { x: to.x + 2 * size, z: to.z };
      line(from, before);
      if (block.g.includes(76)) {
        const center = { x: before.x + (after.x - to.x), z: before.z + (after.z - to.z) };
        const cross = (to.z - before.z) * (after.x - to.x) / 2 - (to.x - before.x) / 2 * (after.z - to.z);
        elements.push({ type: "arc", from: before, to: after, clockwise: cross < 0, center, ...base, thread: false });
      } else {
        elements.push({ type: "line", from: before, to: after, ...base, thread: false });
      }
      at.x = after.x;
      at.z = after.z;
      return elements;
    } else {
      if ((block.g.includes(75) || block.g.includes(76)) && block.words.has("L")) {
        note(`${rowLabel(index)}: G75/G76 chamfering at an angle (A) is drawn as a sharp corner.`);
      }
      line(from, to);
    }
    at.x = tx;
    at.z = tz;
    return elements;
  }

  // ----- LAP ---------------------------------------------------------------------------------------------

  // The contour definition a LAP call names: its positioning block and its contour elements,
  // evaluated from the AP starting point with the modal state of the call.
  function lapContour(name) {
    const current = frame();
    const startIndex = findSequence(current.unit, name, current.index);
    if (startIndex === undefined || !hasG(current.unit.bodies[startIndex] || "", ["81", "82", "83"])) return undefined;
    const endIndex = contourEnd(current.unit, startIndex);
    if (endIndex === undefined) return undefined;
    let transverse = hasG(current.unit.bodies[startIndex], ["82"]);
    const at = { x: state.x, z: state.z, absolute: state.absolute, motion: state.motion, feed: state.feed, pendingAngle: undefined };
    const elements = [];
    let roughFeed;
    for (let index = startIndex; index <= endIndex; index += 1) {
      let body = current.unit.bodies[index] || "";
      if (index === startIndex && hasG(body, ["83"])) note(`${rowLabel(index)}: the blank shape of G83 (LAP4) is not used; the cycle cuts from the AP starting point.`);
      if (hasG(body, ["81", "82"])) {
        // The finish contour starts here; what G83 defined before it was the blank shape.
        transverse = hasG(body, ["82"]);
        elements.length = 0;
        at.x = state.x;
        at.z = state.z;
      }
      body = body.replace(/(?<![A-Z])G0*8[0-3](?![\d.])/g, " ").trim();
      if (!body) continue;
      let block;
      try {
        block = parseBlock(body);
      } catch (error) {
        note(`${rowLabel(index)}: ${error.message}`);
        continue;
      }
      if (block.words.has("E")) roughFeed = block.words.get("E");
      const compensation = [40, 41, 42].find((code) => block.g.includes(code));
      let made;
      try {
        made = blockElements(block, at, index);
      } catch (error) {
        note(`${rowLabel(index)}: ${error.message}`);
        continue;
      }
      made.forEach((element, order) => elements.push({ ...element, roughFeed, compensation: order === 0 ? compensation : undefined }));
      if (!made.length && compensation !== undefined) elements.push({ type: "state", compensation, index });
    }
    const first = elements.findIndex((element) => element.to);
    if (first < 0) return undefined;
    return { transverse, positioning: elements[first], elements: elements.slice(first + 1), startIndex, endIndex };
  }

  function lapAllowance(contour, start, block) {
    const u = Math.max(0, block.words.get("U") ?? 0);
    const w = Math.max(0, block.words.get("W") ?? 0);
    const begin = contour.positioning.to;
    const last = [...contour.elements].reverse().find((element) => element.to)?.to || begin;
    const signX = contour.transverse ? (Math.sign(begin.x - last.x) || 1) : (Math.sign(start.x - begin.x) || 1);
    const signZ = contour.transverse ? (Math.sign(start.z - begin.z) || 1) : (Math.sign(begin.z - last.z) || 1);
    return { dx: u * signX, dz: w * signZ, u, w };
  }

  const lapMeta = (cycle, kind, phase, pass, count) => ({ cycle, kind, cyclePhase: phase, cyclePass: pass, cyclePassCount: count });

  // G85 bar turning (AP mode I): cutting levels D apart from the AP starting point, each cut
  // parallel to the axis up to the rough contour and then along it up to the previous level;
  // descending slopes are cut level by level; a last pass follows the rough contour.
  function lapRough(block, contour) {
    const start = position();
    const depth = block.words.get("D");
    if (!(depth > 0)) {
      note(`${rowLabel()}: G85 needs a depth of cut D greater than 0.`);
      return;
    }
    const feed = block.words.get("F") ?? state.feed;
    const allowance = lapAllowance(contour, start, block);
    const begin = shifted(contour.positioning.to, allowance.dx, allowance.dz);
    const rough = polyline(contour.elements.map((element) => shiftElement(element, allowance.dx, allowance.dz)), begin);
    const transverse = contour.transverse;
    const v = (point) => (transverse ? point.z : point.x);
    const u = (point) => (transverse ? point.x : point.z);
    const make = (level, along) => (transverse ? { x: along, z: level } : { x: level, z: along });
    const direction = Math.sign(v(start) - v(begin));
    if (!direction) {
      note(`${rowLabel()}: the AP starting point is level with the contour start (${transverse ? "Zs = Za" : "Xs = Xa"}); the control reports a cycle error.`);
      return;
    }
    const above = (point, level) => direction * (v(point) - level);
    const levels = [];
    for (let count = 1; count <= MAX_CYCLE_STEPS; count += 1) {
      const level = v(start) - direction * count * depth;
      if (direction * (level - v(begin)) <= EPSILON) break;
      levels.push(level);
    }
    const count = levels.length + 1;
    const rapidApproach = contour.positioning.type === "rapid";
    const approach = (point, meta) => (rapidApproach ? rapidTo(point, meta) : feedTo(point, feed, meta));
    const relieve = (meta) => {
      const here = position();
      const towardStart = Math.sign(u(start) - u(here)) || 1;
      rapidTo(make(v(here) + direction * LAP_RELIEF, u(here) + towardStart * LAP_RELIEF), meta);
    };
    const pointAt = (place) => {
      const base = Math.min(rough.length - 1, Math.floor(place));
      const next = Math.min(rough.length - 1, base + 1);
      const t = place - base;
      return { x: rough[base].x + (rough[next].x - rough[base].x) * t, z: rough[base].z + (rough[next].z - rough[base].z) * t, element: rough[next].element };
    };
    // Stretches of the rough contour below a level: [{ from, to, open }] in contour positions.
    const runsBelow = (level) => {
      const runs = [];
      let open = above(rough[0], level) < -EPSILON ? 0 : undefined;
      for (let index = 0; index < rough.length - 1; index += 1) {
        const a = above(rough[index], level);
        const b = above(rough[index + 1], level);
        if (open === undefined && a >= -EPSILON && b < -EPSILON) open = index + a / (a - b);
        else if (open !== undefined && a < -EPSILON && b >= -EPSILON) {
          runs.push({ from: open, to: index + a / (a - b), open: false });
          open = undefined;
        }
      }
      if (open !== undefined) runs.push({ from: open, to: rough.length - 1, open: true });
      return runs;
    };
    // Along the rough contour from a position up to the previous level, a crest or the end.
    const follow = (place, limit, meta) => {
      let index = Math.floor(place + EPSILON);
      let previous = pointAt(place);
      while (index < rough.length - 1) {
        const next = rough[index + 1];
        if (above(next, v(previous)) < -EPSILON) return;
        if (above(next, limit) >= -EPSILON) {
          const a = above(previous, limit);
          const b = above(next, limit);
          const t = Math.abs(a - b) < EPSILON ? 1 : Math.min(1, Math.max(0, a / (a - b)));
          feedTo({ x: previous.x + (next.x - previous.x) * t, z: previous.z + (next.z - previous.z) * t }, next.element?.roughFeed ?? feed, meta);
          return;
        }
        feedTo(next, next.element?.roughFeed ?? feed, meta);
        previous = next;
        index += 1;
      }
    };
    let pockets = false;
    let lastMain;
    levels.forEach((level, order) => {
      const pass = order + 1;
      const roughMeta = lapMeta("G85", "lap-rough", "rough", pass, count);
      const followMeta = lapMeta("G85", "lap-contour", "contour", pass, count);
      const moveMeta = lapMeta("G85", undefined, "position", pass, count);
      const previousLevel = order === 0 ? v(start) : levels[order - 1];
      const runs = runsBelow(level);
      if (!runs.length || runs[0].from > EPSILON) return;
      const t = (v(start) - level) / (v(start) - v(begin));
      const entry = make(level, u(start) + (u(begin) - u(start)) * t);
      runs.forEach((run, runIndex) => {
        const end = pointAt(run.to);
        if (runIndex === 0) {
          approach(entry, moveMeta);
          feedTo(make(level, u(end)), feed, roughMeta);
          lastMain = run;
        } else {
          // A descending slope: over the crest before it, down to the level, then the cut.
          pockets = true;
          const from = pointAt(run.from);
          let crest = level;
          for (let index = Math.ceil(runs[runIndex - 1].to); index <= Math.floor(run.from); index += 1) {
            if (above(rough[index], crest) > 0) crest = v(rough[index]);
          }
          const clear = crest + direction * 2 * LAP_RELIEF;
          rapidTo(make(Math.max(direction * v(position()), direction * clear) * direction, u(position())), moveMeta);
          rapidTo(make(v(position()), u(from)), moveMeta);
          feedTo(make(level, u(from)), feed, roughMeta);
          feedTo(make(level, u(end)), feed, roughMeta);
        }
        out.cycles.roughPasses += 1;
        if (!run.open) follow(run.to, previousLevel, followMeta);
        relieve(moveMeta);
        if (runIndex > 0) {
          let crest = level;
          for (let index = 0; index <= Math.floor(run.from); index += 1) if (above(rough[index], crest) > 0 && index >= Math.ceil(runs[0].to)) crest = v(rough[index]);
          const clear = crest + direction * 2 * LAP_RELIEF;
          if (direction * (clear - v(position())) > 0) rapidTo(make(clear, u(position())), moveMeta);
        }
        rapidTo(make(v(position()), u(entry)), moveMeta);
      });
    });
    // The last pass along the rough contour: up to the last level's contact point, or all of it
    // when descending slopes left stock between the levels.
    const finishMeta = lapMeta("G85", "lap-contour", "rough-finish", count, count);
    const moveMeta = lapMeta("G85", undefined, "position", count, count);
    approach(begin, moveMeta);
    const stop = !pockets && lastMain && !lastMain.open ? lastMain.to : rough.length - 1;
    for (let index = 1; index <= Math.floor(stop + EPSILON); index += 1) feedTo(rough[index], rough[index].element?.roughFeed ?? feed, finishMeta);
    if (stop - Math.floor(stop + EPSILON) > EPSILON) feedTo(pointAt(stop), pointAt(stop).element?.roughFeed ?? feed, finishMeta);
    out.cycles.roughPasses += 1;
    relieve(moveMeta);
    if (rapidApproach) rapidTo(start, moveMeta);
    else {
      rapidTo(transverse ? { x: state.x, z: start.z } : { x: start.x, z: state.z }, moveMeta);
      rapidTo(start, moveMeta);
    }
    if (pockets) note("G85 descending slopes are cut level by level in the preview; the control's order of the pocket cuts may differ.");
  }

  // The contour as programmed (shifted by dx, dz), with its feeds and nose radius commands.
  function lapContourMoves(contour, dx, dz, meta, options = {}) {
    for (const element of contour.elements) {
      if (!element.to) {
        if (!options.rough) emit(`G${element.compensation}`, meta, element.index);
        continue;
      }
      const target = shiftElement(element, dx, dz);
      const feed = options.rough ? (element.roughFeed ?? options.feed) : (element.feed ?? options.feed);
      move({ ...target, type: element.type === "rapid" ? "rapid" : element.type, feed, thread: options.thread || false,
        compensation: options.rough ? undefined : element.compensation }, element.type === "rapid" ? { ...meta, kind: undefined } : meta, options.rows ? element.index : undefined);
    }
  }

  // G87 finish turning: the contour definition is executed as programmed.
  function lapFinish(block, contour) {
    const start = position();
    const allowance = lapAllowance(contour, start, block);
    const meta = lapMeta("G87", "lap-finish", "finish", 1, 1);
    const first = shiftElement(contour.positioning, allowance.dx, allowance.dz);
    move({ ...first, compensation: contour.positioning.compensation }, first.type === "rapid" ? { ...meta, kind: undefined } : meta, contour.positioning.index);
    lapContourMoves(contour, allowance.dx, allowance.dz, meta, { feed: state.feed, rows: true });
    out.cycles.finishCycles += 1;
  }

  // G86 copy turning (AP mode II): the contour shifted toward the AP starting point, D closer
  // every pass, and a last pass on the rough contour.
  function lapCopy(block, contour) {
    const start = position();
    const depth = block.words.get("D");
    if (!(depth > 0)) {
      note(`${rowLabel()}: G86 needs a depth of cut D greater than 0.`);
      return;
    }
    const feed = block.words.get("F") ?? state.feed;
    const allowance = lapAllowance(contour, start, block);
    const begin = shifted(contour.positioning.to, allowance.dx, allowance.dz);
    const transverse = contour.transverse;
    const total = transverse ? start.z - begin.z : start.x - begin.x;
    if (Math.abs(total) < EPSILON) {
      note(`${rowLabel()}: the AP starting point is level with the contour start; the control reports a cycle error.`);
      return;
    }
    const passes = Math.min(MAX_CYCLE_STEPS, Math.max(1, Math.ceil(Math.abs(total) / depth - EPSILON)));
    const rapidApproach = contour.positioning.type === "rapid";
    for (let pass = 1; pass <= passes; pass += 1) {
      const remaining = Math.max(0, 1 - (pass * depth) / Math.abs(total));
      const offX = (start.x - begin.x) * remaining;
      const offZ = (start.z - begin.z) * remaining;
      const meta = lapMeta("G86", pass === passes ? "lap-contour" : "lap-rough", pass === passes ? "rough-finish" : "copy", pass, passes);
      const moveMeta = lapMeta("G86", undefined, "position", pass, passes);
      const entry = shifted(begin, offX, offZ);
      if (rapidApproach) rapidTo(entry, moveMeta);
      else feedTo(entry, feed, moveMeta);
      lapContourMoves(contour, allowance.dx + offX, allowance.dz + offZ, meta, { feed, rough: true });
      out.cycles.copyPasses += 1;
      rapidTo(transverse ? { x: start.x, z: state.z } : { x: state.x, z: start.z }, moveMeta);
    }
    rapidTo(start, lapMeta("G86", undefined, "position", passes, passes));
  }

  // G88 continuous thread cutting (AP mode III): the contour is the thread at full depth.
  function lapThread(block, contour) {
    const start = position();
    const height = block.words.get("H");
    if (!(height > 0)) {
      note(`${rowLabel()}: G88 needs a thread height H.`);
      return;
    }
    const pattern = [73, 74, 75].find((code) => block.m.includes(code)) ?? 73;
    const allowance = contour.transverse ? block.words.get("W") : block.words.get("U");
    const depths = threadDepths(height, block.words.get("D") ?? height, allowance ?? 0, pattern);
    const begin = contour.positioning.to;
    const outward = contour.transverse ? (Math.sign(start.z - begin.z) || 1) : (Math.sign(start.x - begin.x) || 1);
    depths.forEach((cut, order) => {
      const off = outward * (height - cut);
      const dx = contour.transverse ? 0 : off;
      const dz = contour.transverse ? off : 0;
      const meta = lapMeta("G88", "osp-thread", "thread", order + 1, depths.length);
      const moveMeta = lapMeta("G88", undefined, "position", order + 1, depths.length);
      rapidTo(shifted(begin, dx, dz), moveMeta);
      lapContourMoves(contour, dx, dz, meta, { feed: block.words.get("F") ?? state.feed, thread: true });
      out.cycles.lapThreadPasses += 1;
      rapidTo(contour.transverse ? { x: state.x, z: start.z } : { x: start.x, z: state.z }, moveMeta);
      rapidTo(start, moveMeta);
    });
  }

  function lapCall(block, code) {
    const cycle = [85, 86, 87, 88].find((value) => block.g.includes(value));
    if (block.target === undefined) {
      note(`${rowLabel()}: G${cycle} needs the sequence name of the contour definition (G${cycle} N...).`);
      return;
    }
    if (!known()) {
      note(`${rowLabel()}: G${cycle} needs a known start position (the AP starting point).`);
      return;
    }
    const contour = lapContour(block.target);
    if (!contour) {
      emitComment(`${code}: contour N${block.target} not found`);
      note(`${rowLabel()}: G${cycle} names contour N${block.target}, but no G81/G82 ... G80 definition with that sequence name was found in this program.`);
      return;
    }
    if (block.g.includes(84) || block.extended.has("XA") || block.extended.has("ZA")) {
      note(`${rowLabel()}: G84 cutting-condition changes (XA/DA/FA ...) are not applied; the whole cycle uses D and F of the G85 block.`);
    }
    const modal = { motion: state.motion, feed: state.feed, absolute: state.absolute };
    if (cycle === 85) lapRough(block, contour);
    else if (cycle === 86) lapCopy(block, contour);
    else if (cycle === 87) lapFinish(block, contour);
    else lapThread(block, contour);
    // The modes of the calling block are active again after the cycle.
    Object.assign(state, modal);
  }

  // ----- fixed cycles ------------------------------------------------------------------------------------

  const taperRadius = (block, length, letter) => {
    if (block.words.has(letter)) return block.words.get(letter);
    if (block.words.has("A")) return length * Math.tan(block.words.get("A") * RADIANS);
    return 0;
  };

  // G71 (longitudinal) / G72 (transverse) compound thread cutting.
  function threadCycle(block, transverse) {
    const cycle = transverse ? "G72" : "G71";
    const start = position();
    const height = block.words.get("H");
    if (!known() || !block.words.has("X") || !block.words.has("Z") || !(height > 0)) {
      note(`${rowLabel()}: ${cycle} thread cutting needs a start position, X, Z and the thread height H.`);
      return;
    }
    const x = block.words.get("X");
    const z = block.words.get("Z");
    const lead = (block.words.get("F") ?? state.feed ?? 0) / Math.max(1, block.words.get("J") ?? 1);
    const pattern = [73, 74, 75].find((code) => block.m.includes(code)) ?? 73;
    const depths = threadDepths(height, block.words.get("D") ?? height, (transverse ? block.words.get("W") : block.words.get("U")) ?? 0, pattern);
    const outward = transverse ? (Math.sign(start.z - z) || 1) : (Math.sign(start.x - x) || 1);
    const taper = transverse ? taperRadius(block, x / 2 - start.x / 2, "K") : taperRadius(block, z - start.z, "I");
    depths.forEach((cut, order) => {
      const meta = lapMeta(cycle, "osp-thread", order === depths.length - 1 && depths.length > 1 && ((transverse ? block.words.get("W") : block.words.get("U")) > 0) ? "thread-finish" : "thread", order + 1, depths.length);
      const moveMeta = lapMeta(cycle, undefined, "position", order + 1, depths.length);
      const level = (transverse ? z : x) + outward * (height - cut);
      if (transverse) {
        rapidTo({ x: start.x, z: level }, moveMeta);
        feedTo({ x, z: level + taper }, lead, meta, undefined, { thread: true });
        rapidTo({ x, z: start.z }, moveMeta);
      } else {
        rapidTo({ x: level, z: start.z }, moveMeta);
        feedTo({ x: level + 2 * taper, z }, lead, meta, undefined, { thread: true });
        rapidTo({ x: start.x, z }, moveMeta);
      }
      rapidTo(start, moveMeta);
      out.cycles.threadPasses += 1;
    });
    out.cycles.threadCycles += 1;
  }

  // G31/G33 (longitudinal) and G32 (end face) fixed thread cutting: one pass per block; following
  // blocks repeat the cycle with a new X (or Z).
  function fixedThread(block, code) {
    const transverse = code === 32;
    const cycle = `G${code}`;
    const modal = state.thread && state.thread.code === code ? state.thread : { code };
    for (const letter of ["X", "Z", "F", "I", "K", "A", "J"]) if (block.words.has(letter)) modal[letter] = block.words.get(letter);
    state.thread = modal;
    if (!known() || modal.X === undefined || modal.Z === undefined) return;
    const start = position();
    const lead = (modal.F ?? state.feed ?? 0) / Math.max(1, modal.J ?? 1);
    const meta = lapMeta(cycle, "osp-thread", "thread", 1, 1);
    const moveMeta = lapMeta(cycle, undefined, "position", 1, 1);
    const angle = modal.A !== undefined ? Math.tan(modal.A * RADIANS) : undefined;
    if (transverse) {
      const taper = modal.K ?? (angle !== undefined ? (modal.X / 2 - start.x / 2) * angle : 0);
      rapidTo({ x: start.x, z: modal.Z }, moveMeta);
      feedTo({ x: modal.X, z: modal.Z + taper }, lead, meta, undefined, { thread: true });
      rapidTo({ x: modal.X, z: start.z }, moveMeta);
    } else {
      const taper = modal.I ?? (angle !== undefined ? (modal.Z - start.z) * angle : 0);
      rapidTo({ x: modal.X, z: start.z }, moveMeta);
      feedTo({ x: modal.X + 2 * taper, z: modal.Z }, lead, meta, undefined, { thread: true });
      rapidTo({ x: start.x, z: modal.Z }, moveMeta);
    }
    rapidTo(start, moveMeta);
    out.cycles.threadPasses += 1;
  }

  // G73 (longitudinal grooving, infeed along X) / G74 (transverse grooving and drilling, infeed
  // along Z): pecks of D with a retraction of DA, a full withdrawal every L, a shift between
  // grooves (K for G73, I for G74) up to the target, and a return to the start.
  function grooveCycle(block, transverse) {
    const cycle = transverse ? "G74" : "G73";
    const start = position();
    if (!known() || (!block.words.has("X") && !block.words.has("Z"))) {
      note(`${rowLabel()}: ${cycle} needs a start position and the target point X Z.`);
      return;
    }
    const target = { x: block.words.get("X") ?? start.x, z: block.words.get("Z") ?? start.z };
    const feed = block.words.get("F") ?? state.feed;
    const scale = transverse ? 1 : 1; // D, L and DA are on the infeed axis' own scale (diameter for X)
    const peck = (block.words.get("D") ?? 0) * scale;
    const withdrawEvery = block.words.get("L");
    const retract = block.extended.get("DA") ?? PECK_RETRACT;
    const infeed = (point) => (transverse ? point.z : point.x);
    const along = (point) => (transverse ? point.x : point.z);
    const make = (depth, place) => (transverse ? { x: place, z: depth } : { x: depth, z: place });
    const direction = Math.sign(infeed(target) - infeed(start));
    const shift = Math.abs((transverse ? block.words.get("I") : block.words.get("K")) ?? 0);
    const skip = Math.abs((transverse ? block.words.get("K") : block.words.get("I")) ?? 0);
    const places = [along(start)];
    const side = Math.sign(along(target) - along(start));
    if (shift > EPSILON && side) {
      for (let count = 1; count <= MAX_CYCLE_STEPS; count += 1) {
        const place = along(start) + side * count * shift;
        if (side * (place - along(target)) >= -EPSILON) {
          places.push(along(target));
          break;
        }
        places.push(place);
      }
    }
    if (!direction) {
      note(`${rowLabel()}: ${cycle} has no infeed: the target is at the start ${transverse ? "Z" : "X"}.`);
      return;
    }
    const total = Math.abs(infeed(target) - infeed(start));
    let steps = 0;
    places.forEach((place, order) => {
      const meta = lapMeta(cycle, "osp-groove", "peck", order + 1, places.length);
      const moveMeta = lapMeta(cycle, undefined, "position", order + 1, places.length);
      if (order > 0) rapidTo(make(infeed(start), place), moveMeta);
      let done = Math.min(skip, total);
      if (done > EPSILON) rapidTo(make(infeed(start) + direction * done, place), moveMeta);
      let sinceWithdrawal = 0;
      while (done < total - EPSILON && steps < MAX_CYCLE_STEPS) {
        const cut = peck > EPSILON ? Math.min(peck, total - done) : total - done;
        done += cut;
        sinceWithdrawal += cut;
        steps += 1;
        feedTo(make(infeed(start) + direction * done, place), feed, meta);
        out.cycles.pecks += 1;
        if (done >= total - EPSILON) break;
        if (withdrawEvery > 0 && sinceWithdrawal >= withdrawEvery - EPSILON) {
          rapidTo(make(infeed(start), place), moveMeta);
          rapidTo(make(infeed(start) + direction * Math.max(0, done - retract), place), moveMeta);
          sinceWithdrawal = 0;
        } else if (retract > EPSILON) {
          rapidTo(make(infeed(start) + direction * Math.max(0, done - retract), place), moveMeta);
        }
      }
      if (block.words.get("E") > 0) out.dwellSeconds += block.words.get("E");
      rapidTo(make(infeed(start), place), moveMeta);
      out.cycles.grooves += 1;
    });
    rapidTo(start, lapMeta(cycle, undefined, "position", places.length, places.length));
  }

  // G77 / G78 tapping: in at the lead, out at the lead.
  function tapCycle(block, code) {
    const cycle = `G${code}`;
    const start = position();
    if (!known() || !block.words.has("Z")) {
      note(`${rowLabel()}: ${cycle} tapping needs a start position and Z.`);
      return;
    }
    const meta = lapMeta(cycle, "osp-thread", "tap", 1, 1);
    const lead = block.words.get("F") ?? state.feed;
    if (block.words.has("X") && !near(block.words.get("X"), start.x)) rapidTo({ x: block.words.get("X"), z: start.z }, lapMeta(cycle, undefined, "position", 1, 1));
    const top = position();
    feedTo({ x: top.x, z: block.words.get("Z") }, lead, meta, undefined, { thread: true });
    feedTo(top, lead, meta, undefined, { thread: true });
    rapidTo(start, lapMeta(cycle, undefined, "position", 1, 1));
    out.cycles.taps += 1;
  }

  // ----- blocks ------------------------------------------------------------------------------------------

  // T nnttoo: nose radius compensation number, tool number, tool offset number (T ttoo without
  // nose radius compensation). The drawn tool is the tool number.
  function toolWord(digits) {
    const text = digits.length > 4 ? digits.padStart(6, "0") : digits.padStart(4, "0");
    const tool = text.length === 6 ? text.slice(2, 4) : text.slice(0, 2);
    return `T${tool}${text.slice(-2)}`;
  }

  function stateLine(block, withCompensation) {
    const parts = [];
    for (const code of block.g) {
      if (code === 94 || code === 95) {
        state.perMinute = code === 94;
        feedModeEmitted = state.perMinute;
        parts.push(`G${code}`);
      } else if (code === 96 || code === 97) parts.push(`G${code}`);
      else if (withCompensation && (code === 40 || code === 41 || code === 42)) parts.push(`G${code}`);
    }
    if (block.words.has("S")) parts.push(`S${fmt(block.words.get("S")).replace(/\.$/, "")}`);
    if (block.toolText) {
      const comment = frame().unit.comments[frame().index];
      parts.push(`${toolWord(block.toolText)}${comment ? ` (${comment.replace(/[()]/g, "")})` : ""}`);
    }
    // M02/M30 end the program here; OSP M98/M99 are tailstock codes the interpreter must not read.
    for (const code of block.m) if (![2, 30, 98, 99].includes(code)) parts.push(`M${code}`);
    if (parts.length) emit(parts.join(" "));
  }

  function executeBlock(code) {
    const current = frame();
    const block = parseBlock(code);
    const g = block.g;
    const first = (codes) => codes.find((value) => g.includes(value));

    if (first([85, 86, 87, 88]) !== undefined) {
      lapCall(block, code);
      return;
    }
    if (g.includes(50)) {
      if (block.words.has("S")) emit(`G50 S${fmt(block.words.get("S")).replace(/\.$/, "")}`);
      if (g.includes(90)) state.absolute = true;
      if (g.includes(91)) state.absolute = false;
      // Zero shift: the present position takes the given coordinate value.
      for (const [letter, axis, shift] of [["X", "x", "shiftX"], ["Z", "z", "shiftZ"]]) {
        if (!block.words.has(letter) || !Number.isFinite(state[axis])) continue;
        const value = state.absolute ? block.words.get(letter) : state[axis] + block.words.get(letter);
        state[shift] += value - state[axis];
        state[axis] = value;
        out.cycles.zeroShifts += 1;
      }
      for (const m of block.m) if (m === 0 || m === 1) out.stops.push(stopAt(m === 0 ? "M00" : "M01", current.unit.comments[current.index]));
      return;
    }
    if (g.includes(4)) {
      const seconds = block.words.get("F") ?? 0;
      if (seconds > 0) out.dwellSeconds += seconds;
      emitComment(`G04 DWELL ${fmt(seconds)} S`);
      return;
    }
    if (g.includes(90)) state.absolute = true;
    if (g.includes(91)) state.absolute = false;

    const fixed = first([71, 72, 73, 74, 77, 78]);
    const threadCode = first([31, 32, 33]);
    const motionCode = first([0, 1, 2, 3, 34, 35]);
    if (motionCode !== undefined || fixed !== undefined) state.thread = undefined;
    if (first([20, 21, 24, 25]) !== undefined) {
      stateLine(block, true);
      note("G20/G21/G24/G25 home position moves depend on the machine and are not drawn.");
      return;
    }
    const unknown = g.find((value) => value >= 100 || [22, 30, 37, 38, 101, 102, 103].includes(value));
    if (unknown !== undefined && motionCode === undefined) {
      stateLine(block, true);
      note(`G${unknown} (C-axis, M-tool, contour generation or torque functions) is not drawn in the X-Z turning preview.`);
      return;
    }

    const cycleBlock = fixed !== undefined || threadCode !== undefined || (state.thread && motionCode === undefined && (block.words.has("X") || block.words.has("Z")));
    const moves = block.words.has("X") || block.words.has("Z") || block.words.has("A");
    stateLine(block, !moves || cycleBlock);
    if (block.words.has("F") && fixed === undefined) state.feed = block.words.get("F");

    if (fixed === 71 || fixed === 72) threadCycle(block, fixed === 72);
    else if (fixed === 73 || fixed === 74) grooveCycle(block, fixed === 74);
    else if (fixed === 77 || fixed === 78) tapCycle(block, fixed);
    else if (threadCode !== undefined || cycleBlock) fixedThread(block, threadCode ?? state.thread.code);
    else if (moves) {
      const at = { x: state.x, z: state.z, absolute: state.absolute, motion: state.motion, feed: state.feed, pendingAngle: state.pendingAngle };
      const elements = blockElements(block, at, current.index);
      const compensation = [40, 41, 42].find((value) => g.includes(value));
      elements.forEach((element, order) => {
        move({ ...element, compensation: order === 0 ? compensation : undefined },
          element.thread && element.type !== "rapid" ? lapMeta(`G${at.motion}`, "osp-thread", "thread", 1, 1) : undefined,
          element.index);
      });
      if (!elements.length && compensation !== undefined) emit(`G${compensation}`);
      state.motion = at.motion;
      state.feed = at.feed;
      state.pendingAngle = at.pendingAngle;
      state.absolute = at.absolute;
      if (!elements.length && at.pendingAngle === undefined) {
        state.x = at.x;
        state.z = at.z;
      }
    } else {
      for (const value of g) if ([0, 1, 2, 3, 34, 35].includes(value)) state.motion = value;
    }

    for (const m of block.m) {
      if (m === 0 || m === 1) out.stops.push(stopAt(m === 0 ? "M00" : "M01", current.unit.comments[current.index]));
    }
    if (block.m.includes(2) || block.m.includes(30)) stopped = true;
  }

  // ----- program flow --------------------------------------------------------------------------------------

  function jumpTo(name) {
    const current = frame();
    const index = findSequence(current.unit, name, current.index);
    if (index === undefined) {
      note(`${rowLabel()}: sequence name N${name} was not found; execution continues with the next block.`);
      return;
    }
    if (index <= current.index) {
      // A jump back with no condition evaluated since its target last ran can never end.
      const key = `${current.unit.key}:${current.index}`;
      const seen = backwardJumps.get(key);
      if (visited.get(`${current.unit.key}:${index}`) === conditions) {
        note(`${rowLabel()}: the jump to N${name} repeats the program without a condition (continuous bar work); the preview shows one pass.`);
        stopped = true;
        return;
      }
      if (seen && seen.count >= MAX_REPEATS) {
        note(`${rowLabel()}: the jump to N${name} was taken ${MAX_REPEATS} times; the preview stops there.`);
        stopped = true;
        return;
      }
      backwardJumps.set(key, { count: (seen?.count || 0) + 1, label: rowLabel(), name });
    }
    current.index = index;
    current.jumped = true;
  }

  function externalUnit(name) {
    if (typeof options.resolve !== "function") return undefined;
    const targets = /^\d+$/.test(name) ? [Number(name), `O${name}`] : [`O${name}`, name];
    let found;
    for (const target of targets) {
      found = options.resolve(target);
      if (found) break;
    }
    if (!found) return undefined;
    const key = found.path || found.name;
    if (!externalUnits.has(key)) {
      const unit = createUnit(found.name, String(found.source ?? "").split("\n"), { external: true, key });
      externalUnits.set(key, unit);
      out.external.push({ name: found.name, location: found.location, path: found.path });
      out.units.set(found.name, { name: found.name, text: String(found.source ?? ""), path: found.path || null, location: found.location || null, inFile: false });
    }
    return externalUnits.get(key);
  }

  function call(code) {
    const match = /^CALL\s+O([0-9A-Z-]+)\s*(.*)$/.exec(code);
    if (!match) throw new Error("CALL needs a program name (CALL O...)");
    const caller = frame();
    const name = match[1];
    let location;
    for (const unit of [caller.unit, mainUnit]) {
      if (unit.programs.has(name)) {
        location = { unit, index: unit.programs.get(name) };
        break;
      }
    }
    if (!location) {
      const unit = externalUnit(name);
      if (unit) location = { unit, index: unit.programs.has(name) ? unit.programs.get(name) : 0 };
    }
    if (!location) {
      emitComment(`CALL O${name}: program not found`);
      note(`${rowLabel()}: CALL O${name} was not found in the program, its folder or the machine's program memory folder.`);
      return;
    }
    if (stack.length >= MAX_DEPTH) {
      note(`${rowLabel()}: CALL is nested deeper than ${MAX_DEPTH - 1} levels; the preview stops.`);
      stopped = true;
      return;
    }
    const label = location.unit.external ? location.unit.name : `O${name}`;
    if (!location.unit.external && !out.units.has(label)) out.units.set(label, { name: label, text: null, path: null, location: "this program", inFile: true });
    const called = {
      unit: location.unit, index: location.index, entryIndex: location.index, kind: "CALL", locals: new Map(), repeat: 1,
      jumped: false, headerSeen: false, name: label,
      mainLine: stack.length === 1 ? caller.index + 1 : caller.mainLine
    };
    // Q repeats the program; the variable setting section registers variables for the called program.
    const settings = parseBlock(match[2].replace(/(?<![A-Z])Q\s*(\d+)/, (text, count) => {
      called.repeat = Math.max(1, Number(count));
      return " ";
    }), (variable, subscript, value) => {
      if (/^V\d{1,3}$/.test(variable)) writeVariable(variable, subscript, value);
      else {
        called.locals.set(variable, value);
        const id = variableId(variable);
        if (trace.writes.length < MAX_TRACE_WRITES) trace.writes.push([out.lines.length, id, value, 0, rowKey(caller), blocks]);
      }
    });
    if (settings.words.size || settings.g.length) note(`${rowLabel()}: only variable settings (NAME=value) are read after CALL O${name}.`);
    emitComment(`CALL O${name} -> ${label}${called.repeat > 1 ? ` x${called.repeat}` : ""}`);
    caller.index += 1;
    caller.jumped = true;
    stack.push(called);
    out.callCount += 1;
  }

  function returnFromCall() {
    const current = frame();
    if (current.repeat > 1) {
      current.repeat -= 1;
      current.index = current.entryIndex;
      current.headerSeen = false;
      current.jumped = true;
      return;
    }
    stack.pop();
    if (frame()) frame().jumped = true;
  }

  function statement(code) {
    const current = frame();
    if (/^GOTO\b/.test(code)) {
      const target = /^GOTO\s*N([0-9A-Z]+)\s*$/.exec(code);
      if (!target) throw new Error("GOTO needs a sequence name (GOTO N...)");
      jumpTo(target[1]);
      return;
    }
    if (/^IF\b/.test(code)) {
      conditions += 1;
      let rest = code.slice(2).trim();
      let truth;
      if (rest.startsWith("[")) {
        const parsed = evaluate(rest, 0, true);
        truth = parsed.value !== 0;
        rest = rest.slice(parsed.end).trim();
      } else {
        const variable = /^([A-Z][A-Z0-9]*)\s*/.exec(rest);
        if (!variable) throw new Error("IF needs a [condition] or a variable name");
        truth = isDefined(variable[1]);
        rest = rest.slice(variable[0].length);
      }
      const target = /^(?:GOTO\s*)?N([0-9A-Z]+)\s*$/.exec(rest);
      if (!target) throw new Error("IF needs the sequence name to jump to (IF [...] N...)");
      if (truth) jumpTo(target[1]);
      return;
    }
    if (/^CALL\b/.test(code)) {
      call(code);
      return;
    }
    if (/^RTS\b/.test(code)) {
      if (stack.length > 1) returnFromCall();
      else {
        note(`${rowLabel()}: RTS in the main program ends it.`);
        stopped = true;
      }
      return;
    }
    if (/^(MODIN|MODOUT)\b/.test(code)) {
      note(`${rowLabel()}: MODIN/MODOUT modal subprogram calls are not simulated.`);
      return;
    }
    const word = /^([A-Z]{2}[A-Z0-9]*)(?![A-Z0-9])(?!\s*(?:\[[^\]]*\])?\s*=)/.exec(code);
    if (word && !FLAGS.has(word[1])) {
      if (!IGNORED_STATEMENTS.has(word[1])) note(`${rowLabel()}: the ${word[1]} statement is not simulated.`);
      return;
    }
    // A contour definition is not executed where it stands: G85/G86/G87/G88 call it by name.
    if (hasG(code, ["81", "82", "83"])) {
      const end = contourEnd(current.unit, current.index);
      if (end === undefined) {
        note(`${rowLabel()}: the contour definition has no G80; the following blocks run as ordinary moves.`);
        return;
      }
      current.index = end + 1;
      current.jumped = true;
      return;
    }
    if (/^G0*80\b/.test(code) && !/[XZ]/.test(code.replace(/^G0*80/, ""))) return;
    executeBlock(code);
  }

  function executeLine() {
    const current = frame();
    let code = current.unit.codes[current.index];
    if (!code) return;
    if (code.startsWith("/")) {
      if (options.blockDelete) return;
      code = code.replace(/^\/\d?\s*/, "");
    }
    if (current.unit.programStarts.includes(current.index)) {
      if (current.headerSeen) {
        if (stack.length > 1) {
          note(`${rowLabel()}: the subprogram ran into the next program without RTS; it returns there.`);
          returnFromCall();
        } else {
          note(`${rowLabel()}: the main program ran into the next program without M02; the preview stops.`);
          stopped = true;
        }
        return;
      }
      current.headerSeen = true;
      return;
    }
    current.headerSeen = true;
    visited.set(`${current.unit.key}:${current.index}`, conditions);
    if (trace.rowCount < MAX_TRACE_ROWS) {
      const key = rowKey(current);
      let list = trace.rows.get(key);
      if (!list) trace.rows.set(key, (list = []));
      if (list.length < MAX_TRACE_PER_ROW) {
        list.push([out.lines.length, 0, blocks]);
        trace.rowCount += 1;
      }
    }
    const body = current.unit.bodies[current.index];
    if (!body) return;
    statement(body);
  }

  stack.push({ unit: mainUnit, index: 0, entryIndex: 0, kind: "main", locals: new Map(), repeat: 1, jumped: false, headerSeen: false });
  while (stack.length && !stopped) {
    const current = frame();
    if (current.index >= current.unit.codes.length) {
      if (stack.length > 1) {
        note(`${current.name} ended without RTS; it returns there.`);
        returnFromCall();
        continue;
      }
      break;
    }
    blocks += 1;
    if (blocks > MAX_BLOCKS) {
      note(`Execution stopped after ${MAX_BLOCKS.toLocaleString("en-US")} blocks to prevent an endless loop.`);
      break;
    }
    current.jumped = false;
    try {
      executeLine();
    } catch (error) {
      note(`${rowLabel()}: ${error.message}`);
    }
    if (frame() === current && !current.jumped) current.index += 1;
  }
  for (const jump of backwardJumps.values()) {
    if (jump.count > 1) note(`${jump.label}: the program section from N${jump.name} ran ${jump.count + 1} times (part counter loop); the path and the time include every repetition.`);
    else if (jump.count === 1) note(`${jump.label}: the program section from N${jump.name} ran twice; the path and the time include both runs.`);
  }
  out.executedBlockCount = blocks;
  out.trace = {
    rows: trace.rows,
    writes: trace.writes,
    systemReads: trace.systemReads,
    used: [...trace.used].sort((a, b) => a - b),
    names: trace.names,
    scopes: trace.scopes
  };
  return out;
}

// ----- static helpers ----------------------------------------------------------------------------------

// The programs a program text calls (CALL O...), without running it: [{ kind, target, row }].
function scanCalls(lines) {
  const calls = [];
  (lines || []).forEach((raw, index) => {
    const code = splitSequence(stripComments(raw).toUpperCase().trim().replace(/^\/\d?\s*/, "")).rest;
    const match = /^(?:CALL|MODIN)\s+O([0-9A-Z-]+)/.exec(code);
    if (match) calls.push({ kind: "CALL", target: match[1], row: index + 1 });
  });
  return calls;
}

// Program names (O...) of a program text: [{ name, index }].
function programNames(lines) {
  const names = [];
  (lines || []).forEach((raw, index) => {
    const code = stripComments(raw).toUpperCase().trim().replace(/^\/\d?\s*/, "");
    const match = PROGRAM_NAME.exec(code);
    if (match && !/^O[A-Z]*\s*=/.test(code)) names.push({ name: match[1], index });
  });
  return names;
}

// The program with its T words as tool and offset number (T ttoo), comments kept: the text tool
// definitions are inferred from.
function toolText(text) {
  return String(text).split("\n").map((line) => line.replace(/\([^)]*\)|(?<![A-Z])T\s*(\d{5,6})(?!\d)/gi, (match, digits) => {
    if (digits === undefined) return match;
    const padded = digits.padStart(6, "0");
    return `T${padded.slice(2, 4)}${padded.slice(4)}`;
  })).join("\n");
}

// Evidence that a lathe program is written for OSP (never for FANUC or Haas), and evidence that
// it is not. Used only to choose a machine when neither the user nor the Meimad Machine named one.
function evidence(text) {
  const code = String(text).slice(0, 256 * 1024).split("\n").map((line) => stripComments(line).toUpperCase()).join("\n");
  const count = (expression) => (code.match(expression) || []).length;
  const osp = [];
  const other = [];
  const add = (list, hits, reason) => {
    if (hits) list.push(reason);
  };
  add(osp, count(/^\s*N[0-9A-Z]+\s+G0*8[123](?![\d.])/gm) && count(/(?<![A-Z])G0*8[5-8]\s+N[0-9A-Z]+/g), "LAP contour definition and call (G81/G82 ... G80, G85/G87 N...)");
  add(osp, count(/(?<![A-Z])T\d{6}(?!\d)/g), "six-digit T word");
  add(osp, count(/^\s*(?:N[0-9A-Z]+\s+)?V\d{1,3}\s*=/gm), "common variable V..=");
  add(osp, count(/(?:^|\s)(?:GOTO\s+N[0-9A-Z]+|CALL\s+O[0-9A-Z]+|RTS)(?:\s|$)/gm), "GOTO N.. / CALL O.. / RTS");
  add(osp, count(/(?<![A-Z])[XZ]-?\d+\.?\d*[-+]\d/g), "arithmetic in a coordinate word");
  add(osp, count(/(?<![A-Z])G0*4\s+F\s*[\d.]/g), "G04 F dwell");
  add(osp, count(/(?<![A-Z])G0*7[1234](?![\d.])[^\n]*(?<![A-Z])D\s*[\d.][^\n]*/g) && !count(/(?<![A-Z])G0*7[1-6](?![\d.])[^\n]*(?<![A-Z])[PQ]\s*\d/g), "G71-G74 fixed cycles with D");
  add(osp, count(/^\s*\$[\w-]+\.(?:MIN|SSB|SUB|LIB)%?\s*$/gm), "OSP file header");
  add(other, count(/#\s*\d+|#\s*\[/g), "# macro variables");
  add(other, count(/(?<![A-Z])G0*7[0-3](?![\d.])[^\n]*(?<![A-Z])P\s*\d+[^\n]*(?<![A-Z])Q\s*\d+/g), "G70-G73 with P and Q");
  add(other, count(/(?<![A-Z])M0*9[78]\s*P\s*\d/g), "M98 P subprogram call");
  add(other, count(/(?<![A-Z])G0*(?:28|30)\s*[UW]/g), "G28/G30 U W reference return");
  add(other, count(/(?<![A-Z])G0*76\s*P\d{6}/g), "G76 six-digit P");
  add(other, count(/(?<![A-Z])G0*6[56]\s*P\s*\d/g), "G65/G66 P macro call");
  add(other, count(/(?<![A-Z])G0*9[89](?![\d.])/g), "G98/G99 feed modes");
  return { osp, other };
}

module.exports = { execute, scanCalls, programNames, toolText, evidence, parseExpression, threadDepths, stripComments };
