"use strict";

// Haas NGC mill interpreter for the 3D preview.
//
// Positions are tracked three ways:
//   * program coordinates (`prog`): what the NC block commands in the active
//     work coordinate system, before mirror/scaling/rotation and G268;
//   * the physical tool tip in machine coordinates (`tipWorld`), which is the
//     source of truth for where every move starts;
//   * the tool tip in the part frame (work zero, attached to the table),
//     which is what the preview draws.
// Lengths are in program units (mm or inch). The machine definition is stored
// in millimetres and is scaled into program units whenever G20/G21 change.
//
// Coordinate chain for a programmed point p:
//   plane = mirror/scale/rotate(p)        (G101, G51, G68)
//   wcs   = feature * plane               (G268)
//   part  = wcs                           when G234, G254 or G268 is active
//   part  = table(b,c)^-1 * (W + wcs) - W otherwise (the table rotates the part
//                                         away from machine-aligned coordinates)
// where W is the active work offset (G54..., G52 and G92 shifts included).

const path = require("node:path");
const { evaluateExpression } = require("./macro");
const kinematics = require("../media/kinematics");
const { strokeText } = require("./stroke-font");
const { knownCodeSet } = require("./machines");

const MM_PER_INCH = 25.4;
const AXES = ["X", "Y", "Z", "A", "B", "C"];
const AXIS_KEYS = ["x", "y", "z", "a", "b", "c"];
const LINEAR_AXES = ["X", "Y", "Z"];
const ROTARY_AXES = ["A", "B", "C"];
const DEFAULT_MAX_STEPS = 250000;
const DEFAULT_MAX_SEGMENTS = 400000;
const MAX_CALL_DEPTH = 32;
const MAX_ERRORS = 200;
const CYCLE_CODES = new Set([73, 74, 76, 77, 81, 82, 83, 84, 85, 86, 89, 156]);
const WORK_OFFSET_CODES = new Set([54, 55, 56, 57, 58, 59]);
const ARGUMENT_VARIABLES = {
  A: 1, B: 2, C: 3, D: 7, E: 8, F: 9, H: 11, I: 4, J: 5, K: 6, M: 13,
  Q: 17, R: 18, S: 19, T: 20, U: 21, V: 22, W: 23, X: 24, Y: 25, Z: 26
};
const WHOLE_UNIT_ARGUMENTS = new Set(["D", "E", "F", "H", "L", "M", "S", "T"]);
const MACRO_OPTIONS = { vacant: true, bitwise: true };
const LEGACY_GLOBAL_RANGES = [[100, 199], [500, 699], [800, 999]];

// FANUC Series 30i dialect (interpreter "fanuc-mill").
// Addresses that accept a decimal point; without one they are least input
// increments unless parameter 3401#0 (DPI) selects calculator notation.
const FANUC_DECIMAL_ADDRESSES = new Set(["X", "Y", "Z", "U", "V", "W", "A", "B", "C", "I", "J", "K", "Q", "R"]);
const FANUC_CYCLE_CODES = new Set([73, 74, 76, 81, 82, 83, 84, 84.2, 84.3, 85, 86, 87, 88, 89]);
// #4102-#4130: modal address values of the last block.
const FANUC_ADDRESS_VARIABLES = { B: 2, D: 7, E: 8, F: 9, H: 11, M: 13, N: 14, O: 15, S: 19, T: 20 };

const MODAL_GROUP_OF = new Map([
  [0, 1], [1, 1], [2, 1], [3, 1],
  [17, 2], [18, 2], [19, 2],
  [90, 3], [91, 3],
  [93, 5], [94, 5], [95, 5],
  [20, 6], [21, 6],
  [40, 7], [41, 7], [42, 7], [141, 7],
  [43, 8], [44, 8], [49, 8], [143, 8], [234, 8],
  [73, 9], [74, 9], [76, 9], [77, 9], [80, 9], [81, 9], [82, 9], [83, 9],
  [84, 9], [85, 9], [86, 9], [89, 9], [156, 9],
  [98, 10], [99, 10],
  [50, 11], [51, 11],
  [54, 12], [55, 12], [56, 12], [57, 12], [58, 12], [59, 12], [154, 12],
  [268, 14], [269, 14],
  [61, 15], [64, 15],
  [68, 16], [69, 16],
  [254, 23], [255, 23]
]);
for (let code = 110; code <= 129; code += 1) {
  MODAL_GROUP_OF.set(code, 12);
}

// Plane axes for G17/G18/G19: (u, v) span the plane right-handedly about the
// normal, so G03 (CCW) is a positive rotation seen from +normal. uw/vw are the
// centre-offset words for u and v.
const PLANES = {
  17: { u: "x", v: "y", n: "z", uw: "I", vw: "J" },
  18: { u: "z", v: "x", n: "y", uw: "K", vw: "I" },
  19: { u: "y", v: "z", n: "x", uw: "J", vw: "K" }
};

function toFinite(value, fallback) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function settingValue(settings, id, fallback) {
  const value = settings?.[String(id)];
  return value === undefined || value === null || value === "" ? fallback : value;
}

function settingOn(settings, id, fallback = false) {
  const value = settingValue(settings, id, undefined);
  if (value === undefined) return fallback;
  if (typeof value === "boolean") return value;
  return /^(on|true|1|yes)$/i.test(String(value).trim());
}

// #30000-#39999 read machine parameters; NGC parameter numbers carry a
// decimal part (#30003.078), which other variables never do.
function isParameterVariable(id) {
  return id >= 30000 && id < 40000;
}

// Settings read through #20000+n (or #6000+n) as numbers: ON/OFF read 1/0 and
// the enumerated settings that probing macros test read their list position.
const SETTING_CHOICES = {
  9: [["INCH", 0], ["MM", 1], ["METRIC", 1]],
  33: [["FANUC", 0], ["YASNAC", 1]],
  40: [["RADIUS", 0], ["DIAMETER", 1]]
};

function numericSetting(id, value) {
  if (typeof value === "number") return Number.isFinite(value) ? value : undefined;
  if (typeof value === "boolean") return value ? 1 : 0;
  const text = String(value ?? "").trim().toUpperCase();
  if (!text) return undefined;
  if (Number.isFinite(Number(text))) return Number(text);
  if (/^(ON|TRUE|YES)$/.test(text)) return 1;
  if (/^(OFF|FALSE|NO)$/.test(text)) return 0;
  return (SETTING_CHOICES[id] || []).find(([name]) => text.startsWith(name))?.[1];
}

// G/M code numbers as the control prints them: 5.1 -> "05.1", 0 -> "00".
function formatCode(number) {
  const [whole, fraction] = String(number).split(".");
  return `${whole.padStart(2, "0")}${fraction ? `.${fraction}` : ""}`;
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

function distance3(a, b) {
  return Math.hypot(b.x - a.x, b.y - a.y, b.z - a.z);
}

function polylineLength(points) {
  let length = 0;
  for (let index = 1; index < points.length; index += 1) {
    length += distance3(points[index - 1], points[index]);
  }
  return length;
}

function lerp(a, b, t) {
  return a + (b - a) * t;
}

function samePoint(a, b, tolerance = 1e-9) {
  return Math.abs(a.x - b.x) <= tolerance &&
    Math.abs(a.y - b.y) <= tolerance &&
    Math.abs(a.z - b.z) <= tolerance;
}

function knownPoint(point) {
  return Boolean(point) && Number.isFinite(point.x) && Number.isFinite(point.y) && Number.isFinite(point.z);
}

function roundTo(value, step) {
  return Math.round(value / step) * step;
}

function formatNumber(value, step) {
  const decimals = Math.max(0, Math.round(-Math.log10(step)));
  return Number(roundTo(value, step).toFixed(decimals)).toString();
}

function normalizeAngle(radians) {
  const twoPi = Math.PI * 2;
  let result = radians % twoPi;
  if (result < 0) result += twoPi;
  return result;
}

function lineIntersection2(p, d, q, e) {
  const denominator = d.x * e.y - d.y * e.x;
  if (Math.abs(denominator) < 1e-12) return undefined;
  const t = ((q.x - p.x) * e.y - (q.y - p.y) * e.x) / denominator;
  return { x: p.x + d.x * t, y: p.y + d.y * t };
}

// ---------------------------------------------------------------------------
// Program sources: the main file plus any externally resolved subprograms.

function stripBlockCode(raw) {
  return String(raw).replace(/\([^)]*\)/g, " ").replace(/;.*$/, " ").trim().toUpperCase();
}

function createUnit(name, source, external) {
  const lines = String(source || "").split(/\r?\n/);
  const programStarts = [];
  const programs = new Map();
  const sequences = new Map();
  lines.forEach((raw, index) => {
    const code = stripBlockCode(raw).replace(/^\//, "");
    const program = code.match(/^O\s*(\d+)/) || code.match(/^:\s*(\d+)/);
    if (program) {
      const number = Number(program[1]);
      programStarts.push(index);
      if (!programs.has(number)) programs.set(number, index);
    }
    const sequence = code.match(/^N\s*(\d+)/);
    if (sequence) {
      const number = Number(sequence[1]);
      if (!sequences.has(number)) sequences.set(number, []);
      sequences.get(number).push(index);
    }
  });
  return { name, lines, programs, programStarts, sequences, external };
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

function findSequence(unit, number, fromIndex) {
  const candidates = unit.sequences.get(number) || [];
  if (!candidates.length) return undefined;
  const range = programRange(unit, fromIndex);
  return candidates.find((index) => index >= range.start && index < range.end) ?? candidates[0];
}

function splitComments(raw) {
  const comments = [];
  let body = "";
  let depth = 0;
  let current = "";
  for (const character of String(raw)) {
    if (character === "(") {
      if (depth === 0) current = "";
      else current += character;
      depth += 1;
      continue;
    }
    if (character === ")" && depth > 0) {
      depth -= 1;
      if (depth === 0) {
        comments.push(current.trim());
        body += " ";
      } else {
        current += character;
      }
      continue;
    }
    if (depth > 0) current += character;
    else body += character;
  }
  if (depth > 0) comments.push(current.trim());
  const semicolon = body.indexOf(";");
  if (semicolon >= 0) body = body.slice(0, semicolon);
  return { body, comments };
}

// ---------------------------------------------------------------------------
// Tool definitions inferred from Haas/CAM program comments.

const MILL_TOOL_TYPES = [
  ["probe", /\bPROBE\b/],
  ["thread-mill", /THREAD\s*MILL|THD\s*MILL/],
  ["tap", /\bTAP\b|\bTAPPING\b/],
  // Also the Hebrew CAM names used in this shop's posts: MERKUZ (spot/centre),
  // MECADED (drill), CADURI (ball), MERASEK/RESEK (roughing), FIN (finishing).
  ["spot-drill", /\bSPOT\b|\bMER[CK]UZ\b/],
  ["center-drill", /CENT(?:ER|RE)\s*DRILL/],
  ["reamer", /\bREAM/],
  ["drill", /\bDRILL\b|\bME[CK]ADED\b/],
  ["boring-head", /\bBOR(?:E|ING)\b/],
  ["chamfer-mill", /CHAMF/],
  ["engraver", /ENGRAV/],
  ["face-mill", /\bFACE\b|\bSHELL\b|FLY\s*CUTTER/],
  ["slot-mill", /\bSLOT|WOODRUFF|KEYSEAT/],
  ["ball-mill", /\bBALL\b|\bB\.N\.|\b[CK]ADURI\b/],
  ["bull-nose-mill", /\bBULL\b|CORNER\s*R/],
  ["end-mill", /END\s*MILL|ENDMILL|\bFLAT\b|\bMILL\b|\bE\/M\b|\bFIN\b|\bMERASEK\b|\bRESEK\b/]
];

function inferMillToolType(text, cornerRadius, diameter) {
  const upper = String(text || "").toUpperCase().replace(/_/g, " ");
  for (const [type, expression] of MILL_TOOL_TYPES) {
    if (expression.test(upper)) {
      if (type === "end-mill" && cornerRadius > 0) {
        return Number.isFinite(diameter) && Math.abs(cornerRadius * 2 - diameter) < 1e-6
          ? "ball-mill"
          : "bull-nose-mill";
      }
      return type;
    }
  }
  return "other";
}

function commentNumber(text, expressions) {
  for (const expression of expressions) {
    const match = text.match(expression);
    if (match) {
      const value = Number(match[1]);
      if (Number.isFinite(value) && value >= 0) return value;
    }
  }
  return undefined;
}

function parseMillToolComments(source) {
  const definitions = new Map();
  for (const raw of String(source || "").split(/\r?\n/)) {
    // Only lines that can name a tool matter; skipping the rest keeps this
    // fast on large CAM programs (it runs on every edit).
    if (!/T\s*\d|TOOL/i.test(raw)) continue;
    const { body, comments } = splitComments(raw);
    const commentText = comments.join(" ");
    const code = body.toUpperCase();
    let number;
    const header = commentText.match(/^\s*(?:N\d+\s+)?T\s*(\d{1,3})\b/i) ||
      commentText.match(/\bTOOL\s*[-#:]?\s*(\d{1,3})\b/i);
    const toolWord = code.match(/(?:^|[^A-Z#])T\s*(\d{1,3})(?![\d.])/);
    if (toolWord) number = Number(toolWord[1]);
    else if (header) number = Number(header[1]);
    if (!Number.isInteger(number) || number < 1 || number > 999) continue;
    const existing = definitions.get(number);
    if (!commentText && existing) continue;
    // CAM names join words with "_" (FIN_12_AROH_L=105).
    const text = commentText.toUpperCase().replace(/_/g, " ");
    const diameter = commentNumber(text, [
      /\bD\s*=\s*(\d*\.?\d+)/,
      /\bDIA(?:METER|M\.?|\.)?\s*[-=:]?\s*(\d*\.?\d+)/,
      /Ø\s*(\d*\.?\d+)/,
      /(\d*\.?\d+)\s*(?:IN|MM|")?\.?\s*DIA/
    ]);
    const cornerRadius = commentNumber(text, [
      /\bCR\s*=\s*(\d*\.?\d+)/,
      /\bRAD(?:IUS)?\s*[-=:]\s*(\d*\.?\d+)/,
      /(?:^|\s)R\s*(\d*\.?\d+)(?=\s|$)/
    ]);
    const length = commentNumber(text, [/\bLEN(?:GTH|\.)?\s*[-=:]\s*(\d*\.?\d+)/, /\bL\s*=\s*(\d*\.?\d+)/]);
    const definition = {
      number,
      type: inferMillToolType(text, cornerRadius || 0, diameter),
      description: commentText,
      diameter,
      cornerRadius: Number.isFinite(cornerRadius) ? cornerRadius : 0,
      length,
      tip: 0,
      tipKnown: true,
      hand: "neutral",
      recognition: "heuristic",
      confidence: commentText ? (Number.isFinite(diameter) ? 0.6 : 0.4) : 0.15,
      evidence: commentText ? `NC tool comment: ${commentText}` : "T code only",
      locked: false
    };
    if (existing) {
      const merged = { ...existing };
      for (const [key, value] of Object.entries(definition)) {
        if (value === undefined || value === "" || (key === "type" && value === "other")) continue;
        if (key === "cornerRadius" && !value && existing.cornerRadius) continue;
        if (key === "description" && existing.description) continue;
        merged[key] = value;
      }
      definitions.set(number, merged);
    } else {
      definitions.set(number, definition);
    }
  }
  return definitions;
}

function resolveMillToolDefinitions(source, toolTable) {
  const inferred = parseMillToolComments(source);
  const resolved = new Map(inferred);
  for (const configured of Object.values(toolTable?.tools || {})) {
    const number = Number(configured.number);
    if (!Number.isInteger(number) || number < 1) continue;
    const fallback = inferred.get(number) || {};
    resolved.set(number, {
      ...fallback,
      ...configured,
      number,
      type: configured.type && configured.type !== "other" ? configured.type : fallback.type || "other",
      diameter: Number.isFinite(configured.diameter) ? configured.diameter : fallback.diameter,
      cornerRadius: Number.isFinite(configured.cornerRadius) ? configured.cornerRadius : fallback.cornerRadius || 0,
      length: Number.isFinite(configured.length) ? configured.length : fallback.length,
      description: configured.description || fallback.description || ""
    });
  }
  return resolved;
}

function millToolDefinitionsForSource(source, toolTable) {
  const inferred = parseMillToolComments(source);
  const resolved = resolveMillToolDefinitions(source, toolTable);
  return [...inferred.keys()].sort((a, b) => a - b).map((number) => resolved.get(number));
}

function defaultMillTool(number) {
  return {
    number,
    type: "other",
    description: "",
    diameter: undefined,
    cornerRadius: 0,
    length: undefined,
    tip: 0,
    tipKnown: true,
    hand: "neutral",
    recognition: "heuristic",
    confidence: 0.1,
    evidence: "T code only",
    locked: false
  };
}

// ---------------------------------------------------------------------------
// Polygon helpers for G150.

function polygonArea(points) {
  let area = 0;
  for (let index = 0; index < points.length; index += 1) {
    const a = points[index];
    const b = points[(index + 1) % points.length];
    area += a.x * b.y - b.x * a.y;
  }
  return area / 2;
}

// Inward offset of a closed polygon: edges move inside by `distance`, convex
// corners are trimmed and reflex corners are rounded. Narrow regions that
// collapse are not repaired; G150 output is marked approximate.
function offsetPolygonInward(points, distance) {
  const clean = points.filter((point, index) =>
    index === 0 || Math.hypot(point.x - points[index - 1].x, point.y - points[index - 1].y) > 1e-9);
  if (clean.length > 2 &&
      Math.hypot(clean[0].x - clean[clean.length - 1].x, clean[0].y - clean[clean.length - 1].y) <= 1e-9) {
    clean.pop();
  }
  if (clean.length < 3 || !(distance > 0)) return clean.slice();
  const orientation = polygonArea(clean) >= 0 ? 1 : -1;
  const count = clean.length;
  const edges = clean.map((a, index) => {
    const b = clean[(index + 1) % count];
    const length = Math.hypot(b.x - a.x, b.y - a.y);
    const direction = { x: (b.x - a.x) / length, y: (b.y - a.y) / length };
    const normal = { x: -direction.y * orientation, y: direction.x * orientation };
    return {
      start: { x: a.x + normal.x * distance, y: a.y + normal.y * distance },
      end: { x: b.x + normal.x * distance, y: b.y + normal.y * distance },
      direction,
      vertex: b
    };
  });
  const result = [];
  for (let index = 0; index < count; index += 1) {
    const edge = edges[index];
    const next = edges[(index + 1) % count];
    const cross = (edge.direction.x * next.direction.y - edge.direction.y * next.direction.x) * orientation;
    if (cross >= -1e-9) {
      result.push(lineIntersection2(edge.start, edge.direction, next.start, next.direction) || edge.end);
    } else {
      const start = Math.atan2(edge.end.y - edge.vertex.y, edge.end.x - edge.vertex.x);
      const end = Math.atan2(next.start.y - edge.vertex.y, next.start.x - edge.vertex.x);
      let sweep = end - start;
      if (orientation > 0) {
        while (sweep > 0) sweep -= Math.PI * 2;
      } else {
        while (sweep < 0) sweep += Math.PI * 2;
      }
      const steps = Math.max(2, Math.ceil(Math.abs(sweep) / (Math.PI / 18)));
      for (let step = 0; step <= steps; step += 1) {
        const angle = start + (sweep * step) / steps;
        result.push({
          x: edge.vertex.x + Math.cos(angle) * distance,
          y: edge.vertex.y + Math.sin(angle) * distance
        });
      }
    }
  }
  return result;
}

function pointInPolygon(point, polygon) {
  let inside = false;
  for (let index = 0, previous = polygon.length - 1; index < polygon.length; previous = index, index += 1) {
    const a = polygon[index];
    const b = polygon[previous];
    if ((a.y > point.y) !== (b.y > point.y) &&
        point.x < ((b.x - a.x) * (point.y - a.y)) / (b.y - a.y) + a.x) {
      inside = !inside;
    }
  }
  return inside;
}

// Even-odd spans of a polygon on the line `cross = level`, measured along `axis`.
function scanPolygon(polygon, axis, level) {
  const cross = axis === "x" ? "y" : "x";
  const hits = [];
  for (let index = 0; index < polygon.length; index += 1) {
    const a = polygon[index];
    const b = polygon[(index + 1) % polygon.length];
    if ((a[cross] > level) !== (b[cross] > level)) {
      const t = (level - a[cross]) / (b[cross] - a[cross]);
      hits.push(a[axis] + (b[axis] - a[axis]) * t);
    }
  }
  hits.sort((left, right) => left - right);
  const spans = [];
  for (let index = 0; index + 1 < hits.length; index += 2) {
    if (hits[index + 1] - hits[index] > 1e-9) spans.push([hits[index], hits[index + 1]]);
  }
  return spans;
}

// ---------------------------------------------------------------------------
// Interpreter.

class HaasMillInterpreter {
  constructor(source, options) {
    this.options = options || {};
    this.machine = this.options.machine;
    if (!this.machine || this.machine.type !== "mill") {
      throw new Error("The Haas mill interpreter needs a mill machine definition");
    }
    this.control = this.machine.controlDefinition;
    // Dialect: Haas NGC (settings) or FANUC Series 30i (parameters).
    this.fanuc = this.control?.interpreter === "fanuc-mill";
    this.settings = this.fanuc
      ? {}
      : { ...(this.machine.controlSettings || {}), ...(this.options.controlSettings || {}) };
    this.parameters = this.fanuc
      ? { ...(this.machine.parameters || {}), ...(this.options.controlSettings || {}) }
      : {};
    this.macroOptions = this.fanuc
      ? { ...MACRO_OPTIONS, functions: { PRM: (args) => this.parameterFunction(args) } }
      : MACRO_OPTIONS;
    this.knownG = knownCodeSet(this.control, "G", this.machine);
    this.knownM = knownCodeSet(this.control, "M", this.machine);
    this.codeGroups = new Map((this.control?.gCodes || [])
      .filter((entry) => /^G\d+(?:\.\d+)?$/.test(entry.code))
      .map((entry) => [Number(entry.code.slice(1)), entry]));
    this.axisOrder = Object.keys(this.machine.axes || {});
    this.notedCodes = new Set();
    this.mainUnit = createUnit("main", source, false);
    this.externalUnits = new Map();
    this.externalPrograms = new Map();
    this.resolveSubprogram = typeof this.options.resolveSubprogram === "function"
      ? this.options.resolveSubprogram
      : undefined;
    this.maxSteps = toFinite(this.options.maxExecutionSteps, DEFAULT_MAX_STEPS);
    this.maxSegments = toFinite(this.options.maxSegments, DEFAULT_MAX_SEGMENTS);
    this.now = this.options.now instanceof Date ? this.options.now : new Date();

    this.segments = [];
    this.warningEntries = new Map();
    this.errors = [];
    this.errorKeys = new Set();
    this.tools = new Set();
    this.toolDefinitions = resolveMillToolDefinitions(source, this.options.toolTable);
    this.globals = new Map();
    this.mainLocals = new Map();
    this.stack = [];
    this.frame = undefined;
    this.defaultedOffsets = new Set();
    this.defaultOffsetRead = false;
    this.assumedDOffsets = new Set();
    this.unsetSettings = new Set();
    this.unknownParameters = new Set();
    this.limitIssues = 0;
    this.pendingDwell = 0;
    this.stats = {
      executedBlockCount: 0,
      jumpCount: 0,
      callCount: 0,
      toolChangeCount: 0,
      holeCount: 0,
      pocketCount: 0,
      engravedCharacters: 0,
      dwellSeconds: 0,
      toolChangeSeconds: 0,
      stopCount: 0,
      rotaryPositions: new Set(),
      workOffsets: new Set(),
      cycleCounts: {}
    };

    const defaultUnits = String(this.fanuc
      ? this.machine.defaultProgramUnits || "mm"
      : settingValue(this.settings, 9, this.machine.defaultProgramUnits || "mm"))
      .toUpperCase().startsWith("IN") ? "inch" : "mm";
    this.state = {
      units: defaultUnits,
      motion: 0,
      cycle: undefined,
      cycleP: undefined,
      plane: 17,
      incremental: false,
      feedMode: 94,
      feed: undefined,
      spindleSpeed: undefined,
      spindleDirection: "stop",
      coolant: new Set(),
      compensation: 40,
      compD: undefined,
      pendingCompensation: undefined,
      // NGC 100.21+ applies the length offset of the tool in the spindle
      // unless G49/H00 is programmed.
      toolLengthMode: this.autoLengthOffset() ? 43 : 49,
      toolLengthH: undefined,
      tcpc: false,
      dwo: false,
      feature: undefined,
      returnMode: 98,
      workOffset: { label: "G54", kind: "g54", index: 1 },
      g92Shift: { x: 0, y: 0, z: 0, a: 0, b: 0, c: 0 },
      rotation: undefined,
      scaling: undefined,
      mirror: {},
      cylindrical: undefined,
      tool: 0,
      pendingTool: undefined,
      prog: { x: undefined, y: undefined, z: undefined, a: 0, b: 0, c: 0 },
      tipWorld: { x: undefined, y: undefined, z: undefined },
      lastTarget: { x: 0, y: 0, z: 0, a: 0, b: 0, c: 0 },
      g28Intermediate: undefined,
      modalMacro: undefined,
      lastAddresses: {},
      blockDelete: this.options.blockDelete === true,
      pendingCorner: undefined,
      lastPocketR: undefined,
      // FANUC: G52 local shift (Haas keeps it in #5201+), G15/G16 polar
      // coordinates, tool center point control type, multi-block G68.2 P2/P3.
      localShift: { x: 0, y: 0, z: 0, a: 0, b: 0, c: 0 },
      polar: undefined,
      tcpcType: undefined,
      twpPending: undefined,
      parameterInput: false
    };
    this.stats.workOffsets.add("G54");
    this.applyUnits(defaultUnits, true);
    // Saved home (work) offsets first, so "Initial # vars" can still
    // override single values.
    this.storedWorkOffsets = this.options.workOffsets && typeof this.options.workOffsets === "object"
      ? this.options.workOffsets
      : {};
    this.applyStoredWorkOffsets(this.storedWorkOffsets);
    this.initialiseVariables(this.options.initialVariables);
    this.initialGlobals = new Map(this.globals);
  }

  // ----- home (work) offsets ---------------------------------------------------

  // First variable of a work offset row: G54-G59 #5221+, extended offsets
  // (Haas G154 Pn / G110-G129, FANUC G54.1 Pn) #14001+, and the FANUC external
  // offset (EXT, #5201+).
  workOffsetVariableBase(label) {
    const text = String(label || "").toUpperCase().replace(/\s+/g, " ").trim();
    let match = text.match(/^G(5[4-9])$/);
    if (match) return 5221 + (Number(match[1]) - 54) * 20;
    match = text.match(/^G(?:154|54\.1) ?P(\d+)$/);
    if (match) {
      const p = Number(match[1]);
      return p >= 1 && p <= (this.fanuc ? 300 : 99) ? 14001 + (p - 1) * 20 : undefined;
    }
    match = text.match(/^G1(1\d|2\d)$/);
    if (match && !this.fanuc) return 14001 + (Number(`1${match[1]}`) - 110) * 20;
    if (text === "EXT" && this.fanuc) return 5201;
    return undefined;
  }

  extendedOffsetLabel(p) {
    return this.fanuc ? `G54.1 P${p}` : `G154 P${p}`;
  }

  // Saved offsets are in millimetres: { "G54": { X: -300, Y: -200, Z: -400, C: 0 } }.
  applyStoredWorkOffsets(offsets) {
    for (const [label, values] of Object.entries(offsets || {})) {
      const base = this.workOffsetVariableBase(label);
      if (base === undefined || !values || typeof values !== "object") continue;
      for (const [axisName, raw] of Object.entries(values)) {
        const axis = axisName.toUpperCase();
        const index = this.offsetVariableIndex(axis);
        const value = Number(raw);
        if (index < 0 || raw === "" || raw === null || !Number.isFinite(value)) continue;
        this.globals.set(base + index, LINEAR_AXES.includes(axis) ? value * this.unitScale : value);
      }
    }
  }

  // The offset table shown in the preview: G54-G59 (G54-G59 and EXT on
  // FANUC), the extended offsets the program or the saved table use, with the
  // saved value, the value the run started with (saved, "Initial # vars" or
  // the placeholder default) and the value after the program (G10, macros).
  workOffsetTable() {
    const labels = this.fanuc ? ["EXT", "G54", "G55", "G56", "G57", "G58", "G59"] : ["G54", "G55", "G56", "G57", "G58", "G59"];
    const extra = new Set();
    for (const label of [...this.stats.workOffsets, ...Object.keys(this.storedWorkOffsets)]) {
      const text = String(label).toUpperCase();
      const match = text.match(/^G(?:154|54\.1) ?P(\d+)$/) || text.match(/^G1(1\d|2\d)$/);
      if (!match || this.workOffsetVariableBase(text) === undefined) continue;
      const p = text.startsWith("G1") && !text.startsWith("G154") ? Number(`1${match[1]}`) - 109 : Number(match[1]);
      extra.add(this.extendedOffsetLabel(p));
    }
    const sortedExtra = [...extra].sort((a, b) => Number(a.split("P")[1]) - Number(b.split("P")[1]));
    const axes = this.axisOrder.filter((axis) => AXES.includes(axis));
    const scale = this.unitScale;
    const toMm = (axis, value) => (Number.isFinite(value) ? (LINEAR_AXES.includes(axis) ? value / scale : value) : undefined);
    const stored = (label, axis) => {
      const entry = Object.entries(this.storedWorkOffsets).find(([key]) => this.workOffsetVariableBase(key) === this.workOffsetVariableBase(label));
      const value = Number(entry?.[1]?.[axis] ?? entry?.[1]?.[axis.toLowerCase()]);
      return entry && entry[1]?.[axis] !== "" && Number.isFinite(value) ? value : undefined;
    };
    return {
      units: "mm",
      axes,
      defaults: Object.fromEntries(axes.map((axis) => [
        axis,
        LINEAR_AXES.includes(axis) ? toMm(axis, this.mrzp[axis.toLowerCase()]) : 0
      ])),
      defaultLabel: this.fanuc ? (this.machine.mrzp?.label || "rotary centre") : "MRZP",
      rows: [...labels, ...sortedExtra].map((label) => {
        const base = this.workOffsetVariableBase(label);
        const values = {};
        for (const axis of axes) {
          const index = this.offsetVariableIndex(axis);
          const initial = toMm(axis, this.initialGlobals.get(base + index));
          const final = toMm(axis, this.globals.get(base + index));
          values[axis] = {
            saved: stored(label, axis),
            initial,
            final: final !== undefined && (initial === undefined || Math.abs(final - initial) > 1e-9) ? final : undefined
          };
        }
        return { label, used: this.stats.workOffsets.has(label) || (this.fanuc && label === "EXT"), values };
      })
    };
  }

  autoLengthOffset() {
    if (this.fanuc) return false; // FANUC: G43 H must be programmed after M06.
    return this.settings.autoToolLengthOffset !== false && this.settings.autoToolLengthOffset !== "OFF";
  }

  // ----- FANUC parameters ------------------------------------------------------

  // Machine parameter by key ("1401#1", "5114"); options.controlSettings may
  // override the machine definition.
  parameter(key, fallback) {
    const value = this.parameters[String(key)];
    return value === undefined || value === null || value === "" ? fallback : value;
  }

  parameterOn(key) {
    const value = this.parameter(key, 0);
    return value === true || Number(value) === 1 || /^(on|true|yes)$/i.test(String(value));
  }

  // PRM[number] and PRM[number, bit] in FANUC macros.
  parameterFunction(args) {
    const [number, bit] = args;
    const key = Number.isFinite(bit) ? `${Math.trunc(number)}#${Math.trunc(bit)}` : String(Math.trunc(number));
    const value = Number(this.parameter(key, undefined));
    if (Number.isFinite(value)) return value;
    this.unknownParameters.add(key);
    return 0;
  }

  // Least input increment for an address without a decimal point.
  leastIncrement(letter) {
    if ("ABC".includes(letter)) return 0.001;
    return this.state?.units === "inch" ? 0.0001 : 0.001;
  }

  // Machine position of reference position n (1: G28, 2-4: G30) in program
  // units. Haas machines return to machine zero.
  referencePosition(n = 1) {
    const reference = this.machine.referencePositions?.[String(n)] || {};
    const result = {};
    for (const axis of AXES) {
      const value = Number(reference[axis]);
      result[axis] = Number.isFinite(value) ? value * (LINEAR_AXES.includes(axis) ? this.unitScale : 1) : 0;
    }
    return result;
  }

  // One note per G code that the preview does not expand.
  noteCode(code) {
    const key = `G${code}`;
    if (this.notedCodes.has(key)) return;
    this.notedCodes.add(key);
    const entry = this.codeGroups.get(code);
    this.warn(`${this.rowLabel()}: ${key} (${entry?.name || "function"}) is not expanded in the preview; the block's motion is not drawn.`);
  }

  // ----- diagnostics -------------------------------------------------------

  // Row-specific warnings are grouped by their text so a long CAM program
  // reports each kind of problem once with a count.
  warn(message) {
    const key = message.replace(/^(?:\S+ )?[Rr]ow \d+: /, "");
    const entry = this.warningEntries.get(key);
    if (entry) entry.count += 1;
    else this.warningEntries.set(key, { message, count: 1 });
  }

  error(message, line) {
    if (this.errorKeys.has(message)) return;
    this.errorKeys.add(message);
    if (this.errors.length >= MAX_ERRORS) {
      this.errorsTruncated = (this.errorsTruncated || 0) + 1;
      return;
    }
    this.errors.push({ message, line });
  }

  rowLabel() {
    const frame = this.frame;
    if (!frame) return "";
    const lineNumber = frame.index + 1;
    return frame.unit.external ? `${frame.unit.name} row ${lineNumber}` : `Row ${lineNumber}`;
  }

  // ----- units and machine data ----------------------------------------------

  applyUnits(units, initial = false) {
    const previous = this.state.units;
    this.state.units = units;
    this.unitScale = units === "inch" ? 1 / MM_PER_INCH : 1;
    this.resolution = units === "inch" ? 0.0001 : 0.001;
    const scale = this.unitScale;
    const nodes = (this.machine.kinematics?.nodes || []).map((node) => ({
      ...node,
      origin: Array.isArray(node.origin) ? node.origin.map((value) => value * scale) : undefined,
      pivot: Array.isArray(node.pivot) ? node.pivot.map((value) => value * scale) : undefined
    }));
    this.chain = kinematics.buildChain({ nodes });
    this.axisSet = new Set(Object.keys(this.machine.axes || {}));
    this.mrzp = {
      x: toFinite(this.machine.mrzp?.x, 0) * scale,
      y: toFinite(this.machine.mrzp?.y, 0) * scale,
      z: toFinite(this.machine.mrzp?.z, 0) * scale
    };
    // A G20/G21 at the start of the program: saved offsets (in mm) apply in
    // the program's units.
    if (!initial && previous !== units && this.storedWorkOffsets && !this.segments.length) {
      this.applyStoredWorkOffsets(this.storedWorkOffsets);
      this.initialGlobals = new Map(this.globals);
    }
    if (!initial && previous !== units) {
      const factor = units === "inch" ? 1 / MM_PER_INCH : MM_PER_INCH;
      for (const point of [this.state.prog, this.state.tipWorld]) {
        for (const axis of ["x", "y", "z"]) {
          if (Number.isFinite(point[axis])) point[axis] *= factor;
        }
      }
      const setting = String(settingValue(this.settings, 9, "MM")).toUpperCase().startsWith("IN") ? "INCH" : "MM";
      if (this.segments.length) {
        this.warn(`${this.rowLabel()}: units change to ${units === "inch" ? "G20 inch" : "G21 metric"} after motion; the preview converts positions, the ${this.fanuc ? "FANUC" : "Haas"} control alarms.`);
      } else if (!this.fanuc && (units === "inch") !== (setting === "INCH")) {
        this.warn(`${this.rowLabel()}: ${units === "inch" ? "G20" : "G21"} does not match Setting 9 (${setting}) in the ${this.machine.name} definition; the control alarms unless Setting 9 is changed. The preview follows the program.`);
      }
    }
  }

  linearRapid(axis) {
    return toFinite(this.machine.axes?.[axis]?.rapidRate, 25400) * this.unitScale;
  }

  rotaryRapid(axis) {
    return toFinite(this.machine.axes?.[axis]?.rapidRate, 3000);
  }

  // ----- variables -----------------------------------------------------------

  initialiseVariables(initial) {
    const source = String(initial || "");
    for (const assignment of source.split(/[,\r\n;]+/).map((value) => value.trim()).filter(Boolean)) {
      const match = assignment.match(/^#?(\d+)\s*=\s*(.+)$/);
      if (!match || Number(match[1]) === 0) {
        this.warn(`Invalid initial macro value: ${assignment}`);
        continue;
      }
      try {
        const value = evaluateExpression(match[2], this.variableView(), undefined, this.macroOptions);
        this.setVariable(Number(match[1]), value, true);
      } catch (error) {
        this.warn(`Initial macro ${assignment}: ${error.message}`);
      }
    }
  }

  canonicalVariable(id) {
    if (this.fanuc) {
      // Tool compensation memory C: #2001-#2400 (and #2401-#2800 with V15)
      // are the first 200 entries of the #10001-#13999 tables.
      const v15 = this.parameterOn("6000#3");
      if (id >= 2001 && id <= 2200) return (v15 ? 11000 : 10000) + (id - 2000);
      if (id >= 2201 && id <= 2400) return (v15 ? 10000 : 11000) + (id - 2200);
      if (v15 && id >= 2401 && id <= 2600) return 13000 + (id - 2400);
      if (v15 && id >= 2601 && id <= 2800) return 12000 + (id - 2600);
      // #100001+ are the 50-axis forms of #5001+ (positions, skip, offsets).
      if (id >= 100001 && id <= 100250) {
        const block = Math.floor((id - 100001) / 50);
        const axis = (id - 100001) % 50;
        if (axis < 20) return 5001 + block * 20 + axis;
      }
      return id;
    }
    for (const [first, last] of LEGACY_GLOBAL_RANGES) {
      if (id >= first && id <= last) return id + 10000;
    }
    return id;
  }

  // Variable index of an axis in 20-wide offset/position tables: Haas uses
  // X Y Z A B C; FANUC numbers the machine's controlled axes in order.
  offsetVariableIndex(axis) {
    return this.fanuc ? this.axisOrder.indexOf(axis) : AXES.indexOf(axis);
  }

  axisAtVariableIndex(index) {
    return this.fanuc ? this.axisOrder[index] : AXES[index];
  }

  variableView() {
    return { get: (id) => this.getVariable(id) };
  }

  locals() {
    return this.frame?.locals || this.mainLocals;
  }

  getVariable(rawId) {
    const id = this.canonicalVariable(Number(rawId));
    if (id >= 1 && id <= 33) {
      const value = this.locals().get(id);
      return Number.isFinite(value) ? value : undefined;
    }
    const system = this.systemVariable(id);
    if (system !== undefined) return system;
    const value = this.globals.get(id);
    return Number.isFinite(value) ? value : undefined;
  }

  setVariable(rawId, value, initial = false) {
    const id = this.canonicalVariable(Number(rawId));
    if (this.fanuc) {
      this.setFanucVariable(rawId, id, value, initial);
      return;
    }
    if (isParameterVariable(Math.trunc(id))) {
      this.warn(`${this.rowLabel()}: #${id} is a machine parameter; macros cannot write parameters.`);
      return;
    }
    if (!Number.isInteger(id) || id <= 0) {
      throw new Error(`Invalid variable #${rawId}`);
    }
    if (id >= 1 && id <= 33) {
      if (value === null) this.locals().delete(id);
      else this.locals().set(id, value);
      return;
    }
    if (!initial) {
      if (id === 3000) {
        this.error(`${this.rowLabel()}: program alarm #3000=${value}${this.lastComment ? ` (${this.lastComment})` : ""}; the control stops here.`, this.currentLine());
        this.stopRequested = true;
        return;
      }
      if (id === 3006) {
        this.stats.stopCount += 1;
        this.warn(`${this.rowLabel()}: #3006 programmable stop${this.lastComment ? ` (${this.lastComment})` : ""}; the preview continues.`);
        return;
      }
      if (id === 3001) {
        this.timerBase = this.stats.executedBlockCount - toFinite(value, 0);
        return;
      }
      const setting = id >= 20001 && id <= 20999 ? id - 20000 : id >= 6001 && id <= 6250 ? id - 6000 : undefined;
      if (setting !== undefined) {
        if (value !== null) this.settings[String(setting)] = value;
        return;
      }
      if (id === 6198 || id === 6998 || id === 6999 || (id >= 5061 && id <= 5069) || (id >= 5081 && id <= 5086)) {
        this.warn(`${this.rowLabel()}: #${id} is read-only on the Haas control.`);
        return;
      }
      if ((id >= 5001 && id <= 5006) || (id >= 5021 && id <= 5026) || (id >= 5041 && id <= 5046) ||
          (id >= 4001 && id <= 4200) || id === 3026 || id === 3027 || id === 3011 || id === 3012) {
        this.warn(`${this.rowLabel()}: #${id} is read-only on the Haas control.`);
        return;
      }
    }
    if (value === null) this.globals.delete(id);
    else this.globals.set(id, value);
  }

  setFanucVariable(rawId, id, value, initial) {
    if (!Number.isInteger(id) || id <= 0) {
      throw new Error(`Invalid variable #${rawId}`);
    }
    if (id >= 1 && id <= 33) {
      if (value === null) this.locals().delete(id);
      else this.locals().set(id, value);
      return;
    }
    if (!initial) {
      if (id === 3000) {
        this.error(`${this.rowLabel()}: macro alarm #3000=${value}${this.lastComment ? ` (${this.lastComment})` : ""}; the control stops here.`, this.currentLine());
        this.stopRequested = true;
        return;
      }
      if (id === 3006) {
        this.stats.stopCount += 1;
        this.warn(`${this.rowLabel()}: #3006 stop with message${this.lastComment ? ` (${this.lastComment})` : ""}; the preview continues.`);
        return;
      }
      if (id === 3001) {
        this.timerBase = Math.round(this.elapsedSeconds() * 1000) - toFinite(value, 0);
        return;
      }
      if (id === 3002) {
        this.hourTimerBase = this.elapsedSeconds() / 3600 - toFinite(value, 0);
        return;
      }
      if (id === 3003 || id === 3004) return; // single block / feed hold control
      if ((id >= 4001 && id <= 4530) || (id >= 5001 && id <= 5140) || id === 3007 || id === 3011 || id === 3012 ||
          (id >= 1000 && id <= 1035) || (id >= 151151 && id <= 151165)) {
        this.warn(`${this.rowLabel()}: #${id} is read-only on the FANUC control.`);
        return;
      }
    }
    if (value === null) this.globals.delete(id);
    else this.globals.set(id, value);
  }

  mainProgramNumber() {
    const first = this.mainUnit.programs.keys().next();
    return first.done ? undefined : first.value;
  }

  fanucSystemVariable(id) {
    const state = this.state;
    const axisIndex = (base) => id - base;
    const axisValue = (source, index) => {
      const axis = this.axisAtVariableIndex(index);
      return axis && source ? source[axis.toLowerCase()] : undefined;
    };
    if (id >= 5001 && id <= 5020) return axisValue(state.lastTarget, axisIndex(5001));
    if (id >= 5021 && id <= 5040) {
      const axis = this.axisAtVariableIndex(axisIndex(5021));
      return axis ? this.currentJoints()?.[axis] : undefined;
    }
    if (id >= 5041 && id <= 5060) return axisValue(state.prog, axisIndex(5041));
    if (id >= 5061 && id <= 5080) return axisValue(state.skip, axisIndex(5061));
    if (id >= 5081 && id <= 5100) return this.axisAtVariableIndex(axisIndex(5081)) === "Z" ? this.lengthCompensation() : 0;
    if (id >= 5101 && id <= 5120) return 0; // servo position deviation
    if ((id >= 4001 && id <= 4030) || (id >= 4201 && id <= 4230)) return this.fanucModalValue(id % 100);
    for (const [letter, offset] of Object.entries(FANUC_ADDRESS_VARIABLES)) {
      if (id !== 4100 + offset && id !== 4300 + offset) continue;
      if (letter === "N") return state.lastSequence;
      if (letter === "O") return this.mainProgramNumber();
      return state.lastAddresses[letter];
    }
    if (id === 4130 || id === 4330) return state.workOffset.kind === "g154" ? state.workOffset.index : 0;
    if (id === 4000) return this.mainProgramNumber();
    if (id === 3001) return Math.round(this.elapsedSeconds() * 1000) - (this.timerBase || 0);
    if (id === 3002) return this.elapsedSeconds() / 3600 - (this.hourTimerBase || 0);
    if (id === 3011) {
      return this.now.getFullYear() * 10000 + (this.now.getMonth() + 1) * 100 + this.now.getDate();
    }
    if (id === 3012) {
      return this.now.getHours() * 10000 + this.now.getMinutes() * 100 + this.now.getSeconds();
    }
    if (id === 3007) {
      return this.axisOrder.reduce((bits, axis, index) =>
        bits | (Number.isFinite(state.mirror[axis.toLowerCase()]) ? 1 << index : 0), 0);
    }
    if (id >= 151151 && id <= 151165) return this.featureVariable(id - 151150);
    if (id >= 1000 && id <= 1171) return 0; // interface signals
    return this.defaultWorkOffsetVariable(id);
  }

  // #151151-#151165 [#_FCOORD]: the tilted working plane in machine and
  // workpiece coordinates (all 0 when it is not active).
  featureVariable(n) {
    const feature = this.state.feature;
    if (!feature) return 0;
    const m = feature.matrix;
    const offset = this.workOffset();
    const origin = { x: m[3], y: m[7], z: m[11] };
    if (n <= 3) return [offset.x + origin.x, offset.y + origin.y, offset.z + origin.z][n - 1];
    if (n <= 6) return [origin.x, origin.y, origin.z][n - 4];
    const column = Math.floor((n - 7) / 3);
    const row = (n - 7) % 3;
    return m[row * 4 + column];
  }

  fanucModalValue(group) {
    const state = this.state;
    switch (group) {
      case 1: return state.motion;
      case 2: return state.plane;
      case 3: return state.incremental ? 91 : 90;
      case 4: return 22;
      case 5: return state.feedMode;
      case 6: return state.units === "inch" ? 20 : 21;
      case 7: return state.compensation === 41 || state.compensation === 42 ? state.compensation : 40;
      case 8: return state.tcpc ? (state.tcpcType === 5 ? 43.5 : 43.4) : state.toolLengthMode;
      case 9: return state.cycle ? state.cycle.code : 80;
      case 10: return state.returnMode;
      case 11: return state.scaling ? 51 : 50;
      case 12: return state.modalMacro ? (state.modalMacro.type === "B" ? 66.1 : 66) : 67;
      case 13: return 97;
      case 14: return this.workOffsetCode();
      case 15: return state.cuttingMode || 64;
      case 16: return state.feature ? state.feature.code || 68.2 : state.rotation ? 68 : 69;
      case 17: return state.polar ? 16 : 15;
      case 18: return 40.1;
      case 19: return 25;
      case 21: return 13.1;
      case 22: return Object.keys(state.mirror).length ? 51.1 : 50.1;
      default: return undefined;
    }
  }

  systemVariable(id) {
    if (this.fanuc) return this.fanucSystemVariable(id);
    const state = this.state;
    if (id >= 5001 && id <= 5006) return state.lastTarget[AXIS_KEYS[id - 5001]];
    if (id >= 5041 && id <= 5046) return state.prog[AXIS_KEYS[id - 5041]];
    if (id >= 5021 && id <= 5026) return this.currentJoints()?.[AXES[id - 5021]];
    if (id >= 4001 && id <= 4023) return this.modalGroupValue(id - 4000);
    if (id >= 4101 && id <= 4126) return state.lastAddresses[String.fromCharCode(64 + (id - 4100))];
    if (id === 3026) return state.tool;
    if (id === 3027) return state.spindleDirection === "stop" ? 0 : state.spindleSpeed || 0;
    if (id === 3011) {
      return this.now.getFullYear() * 10000 + (this.now.getMonth() + 1) * 100 + this.now.getDate();
    }
    if (id === 3012) {
      return this.now.getHours() * 10000 + this.now.getMinutes() * 100 + this.now.getSeconds();
    }
    if (id === 3001) {
      if (!this.timerNoted) {
        this.timerNoted = true;
        this.warn(`${this.rowLabel()}: #3001 runs like the Haas Graphics page: 1 ms per block, with no time for dwells or moves. Probing macros that time a dwell to detect Graphics (Renishaw "TEST RUNNING IN GRAPHICS") skip their probe moves, as they do there.`);
      }
      return this.stats.executedBlockCount - (this.timerBase || 0);
    }
    if (id === 4132) {
      return state.workOffset.kind === "g154" ? 154 + state.workOffset.index / 100 : this.workOffsetCode();
    }
    if (id >= 5061 && id <= 5066) return state.skip?.[AXIS_KEYS[id - 5061]];
    if (id >= 5081 && id <= 5086) return id === 5083 ? this.lengthCompensation() : 0;
    if (id === 6198) return 1000000; // Next Generation Control identifier
    if (id >= 20001 && id <= 20999) return this.settingVariable(id - 20000);
    if (id >= 6001 && id <= 6250) return this.settingVariable(id - 6000);
    if (id === 6998 || id === 6999) {
      const parameter = this.globals.get(6996);
      if (!Number.isFinite(parameter)) return undefined;
      const value = this.parameterVariable(30000 + parameter);
      if (id === 6998) return value;
      const bit = Math.trunc(toFinite(this.globals.get(6997), 0));
      return Math.floor(Math.abs(value) / 2 ** bit) % 2;
    }
    if (isParameterVariable(Math.trunc(id))) return this.parameterVariable(id);
    return this.defaultWorkOffsetVariable(id);
  }

  // Settings as macros read them. Settings 254-257 (rotary centre distance
  // and MRZP) come from the machine definition.
  settingVariable(number) {
    let value = settingValue(this.settings, number, undefined);
    if (value === undefined) {
      if (number >= 255 && number <= 257) return this.mrzp[["x", "y", "z"][number - 255]];
      if (number === 254) return toFinite(this.machine.mrzp?.rotaryCenterDistance, 0) * this.unitScale;
      if (number === 40) value = "DIAMETER";
    }
    if (value === undefined) {
      this.unsetSettings.add(number);
      return 0;
    }
    const numeric = numericSetting(number, value);
    if (numeric === undefined) {
      this.warn(`Setting ${number} is "${value}", which the preview cannot read as a number; macros read it as vacant.`);
      return undefined;
    }
    return numeric;
  }

  // Machine parameters: parameterVariables in the machine definition, keyed
  // by macro variable number ("30003.078"). Unknown parameters read 0.
  parameterVariable(id) {
    const key = String(Number(id));
    const value = Number(this.machine.parameterVariables?.[key]);
    if (Number.isFinite(value)) return value;
    this.unknownParameters.add(key);
    return 0;
  }

  // An unset work offset reads as the value the preview draws with (X/Y/Z at
  // the MRZP), so macros that derive offsets from it agree with the drawing.
  defaultWorkOffsetVariable(id) {
    let relative;
    let label;
    let mirror;
    let tableSize;
    if (this.fanuc) {
      // G54-G59 #5221-#5340, G54.1 P1-P48 #7001-#7960, P1-P300 #14001-#19980.
      if (id >= 5221 && id <= 5340) {
        relative = id - 5221;
        label = `G${54 + Math.floor(relative / 20)}`;
      } else if (id >= 7001 && id <= 7960) {
        relative = id - 7001;
        label = `G54.1 P${Math.floor(relative / 20) + 1}`;
        mirror = 14001 + relative;
      } else if (id >= 14001 && id <= 19980) {
        relative = id - 14001;
        label = `G54.1 P${Math.floor(relative / 20) + 1}`;
        if (relative < 960) mirror = 7001 + relative;
      } else {
        return undefined;
      }
      tableSize = this.axisOrder.length;
    } else if (id >= 5221 && id <= 5346) {
      relative = id - 5221;
      label = `G${54 + Math.floor(relative / 20)}`;
      if (relative >= 120) return undefined;
    } else if (id >= 14001 && id <= 15986) {
      relative = id - 14001;
      label = `G154 P${Math.floor(relative / 20) + 1}`;
      if (relative < 400) mirror = 7001 + relative;
    } else if (id >= 7001 && id <= 7386) {
      relative = id - 7001;
      label = `G154 P${Math.floor(relative / 20) + 1}`;
      mirror = 14001 + relative;
    } else {
      return undefined;
    }
    const index = relative % 20;
    if (index >= (tableSize ?? 6)) return undefined;
    if (this.globals.has(id)) return undefined;
    if (mirror !== undefined && this.globals.has(mirror)) return this.globals.get(mirror);
    this.defaultedOffsets.add(label);
    this.defaultOffsetRead = true;
    const axis = this.axisAtVariableIndex(index);
    return LINEAR_AXES.includes(axis) ? this.mrzp[axis.toLowerCase()] : 0;
  }

  workOffsetCode() {
    const offset = this.state.workOffset;
    if (offset.kind === "g54") return 53 + offset.index;
    if (offset.kind === "g110") return 109 + offset.index;
    return this.fanuc ? 54.1 : 154;
  }

  modalGroupValue(group) {
    const state = this.state;
    switch (group) {
      case 1: return state.motion;
      case 2: return state.plane;
      case 3: return state.incremental ? 91 : 90;
      case 5: return state.feedMode;
      case 6: return state.units === "inch" ? 20 : 21;
      case 7: return state.compensation;
      case 8: return state.tcpc ? 234 : state.toolLengthMode;
      case 9: return state.cycle ? state.cycle.code : 80;
      case 10: return state.returnMode;
      case 11: return state.scaling ? 51 : 50;
      case 12: return this.workOffsetCode();
      case 14: return state.feature ? 268 : 269;
      case 15: return 64;
      case 16: return state.rotation ? 68 : 69;
      case 23: return state.dwo ? 254 : 255;
      default: return undefined;
    }
  }

  // ----- offsets and tool data -----------------------------------------------

  workOffsetBase(offset = this.state.workOffset) {
    if (offset.kind === "g54") return { base: 5221 + (offset.index - 1) * 20, legacy: undefined };
    const legacyCount = this.fanuc ? 48 : 20;
    return {
      base: 14001 + (offset.index - 1) * 20,
      legacy: offset.index <= legacyCount ? 7001 + (offset.index - 1) * 20 : undefined
    };
  }

  // Active work offset table entry in machine coordinates, without shifts.
  baseWorkOffset(offset = this.state.workOffset) {
    const { base, legacy } = this.workOffsetBase(offset);
    const label = offset.label;
    const defaults = { x: this.mrzp.x, y: this.mrzp.y, z: this.mrzp.z, a: 0, b: 0, c: 0 };
    const values = {};
    AXIS_KEYS.forEach((axis, axisIndex) => {
      const index = this.offsetVariableIndex(AXES[axisIndex]);
      let value = index >= 0 ? this.globals.get(base + index) : undefined;
      if (!Number.isFinite(value) && legacy && index >= 0) value = this.globals.get(legacy + index);
      if (!Number.isFinite(value)) {
        if (axisIndex < 3) this.defaultedOffsets.add(label);
        value = defaults[axis];
      }
      values[axis] = value;
    });
    return values;
  }

  // #5201+: the G52 line of the Haas offset table, or the FANUC external
  // work offset (EXT), which applies to every work coordinate system.
  commonOffset(axis) {
    const index = this.offsetVariableIndex(axis.toUpperCase());
    const value = index >= 0 ? this.globals.get(5201 + index) : undefined;
    return Number.isFinite(value) ? value : 0;
  }

  // Active work offset in machine coordinates, G52 and G92 shifts included.
  workOffset() {
    const values = this.baseWorkOffset();
    AXIS_KEYS.forEach((axis) => {
      values[axis] += this.commonOffset(axis) + this.state.g92Shift[axis] + (this.fanuc ? this.state.localShift[axis] : 0);
    });
    return values;
  }

  // The drawing frame is the first work offset the program selects (or G54),
  // attached to the table, with the values it has at the first move. It stays
  // fixed so G52/G92 shifts and other work offsets (such as a G59 that a macro
  // derives from G54 for a tilted plane) draw where the machine actually cuts
  // relative to that part zero. The FANUC external offset belongs to every
  // work offset, so it is part of the frame.
  displayOffset() {
    if (!this.display) {
      const offset = this.firstWorkOffset || this.state.workOffset;
      const base = this.baseWorkOffset(offset);
      const external = (axis) => (this.fanuc ? this.commonOffset(axis) : 0);
      this.display = { x: base.x + external("x"), y: base.y + external("y"), z: base.z + external("z"), label: offset.label };
    }
    return this.display;
  }

  toolDefinition(number = this.state.tool) {
    if (!this.toolDefinitions.has(number)) {
      this.toolDefinitions.set(number, defaultMillTool(number));
    }
    return this.toolDefinitions.get(number);
  }

  defaultToolLength() {
    return toFinite(this.machine.defaultToolLength, 100) * this.unitScale;
  }

  // Offset table variables: Haas #2001+ H geometry, #2201+ H wear, #2401+ D
  // geometry, #2601+ D wear; FANUC tool compensation memory C #11001+ H
  // geometry, #10001+ H wear, #13001+ D geometry, #12001+ D wear.
  offsetVariable(kind, number) {
    const bases = this.fanuc
      ? { hGeometry: 11000, hWear: 10000, dGeometry: 13000, dWear: 12000 }
      : { hGeometry: 2000, hWear: 2200, dGeometry: 2400, dWear: 2600 };
    return bases[kind] + number;
  }

  lengthOffsetValue(h) {
    if (!Number.isInteger(h) || h <= 0) return 0;
    const geometry = this.globals.get(this.offsetVariable("hGeometry", h));
    const wear = this.globals.get(this.offsetVariable("hWear", h));
    if (Number.isFinite(geometry)) return geometry + (Number.isFinite(wear) ? wear : 0);
    const definition = this.toolDefinitions.get(h);
    if (Number.isFinite(definition?.length)) return definition.length;
    return this.defaultToolLength();
  }

  // Gauge line to tool tip for the tool in the spindle.
  physicalToolLength(tool = this.state.tool) {
    if (!tool) return 0;
    return this.lengthOffsetValue(tool);
  }

  lengthCompensation() {
    const state = this.state;
    if (state.tcpc || state.toolLengthMode === 43) return this.lengthOffsetValue(state.toolLengthH);
    if (state.toolLengthMode === 44) return -this.lengthOffsetValue(state.toolLengthH);
    return 0;
  }

  // Tip error of the active length compensation: zero when G43 H matches the
  // tool, minus the tool length when G49 is active (gauge line positioning).
  tlcDelta() {
    return this.lengthCompensation() - this.physicalToolLength();
  }

  diameterOffsets() {
    // FANUC parameter 5004#2 (ODI): 0 radius, 1 diameter.
    if (this.fanuc) return this.parameterOn("5004#2");
    const value = settingValue(this.settings, 40, "DIAMETER");
    if (typeof value === "number") return value !== 0;
    return !/^RAD/i.test(String(value));
  }

  // Haas Setting 40 selects whether D offsets hold a radius or a diameter.
  compensationRadius(d = this.state.compD) {
    if (!Number.isInteger(d) || d <= 0) return 0;
    const diameterMode = this.diameterOffsets();
    const geometry = this.globals.get(this.offsetVariable("dGeometry", d));
    const wear = this.globals.get(this.offsetVariable("dWear", d));
    const wearRadius = Number.isFinite(wear) ? (diameterMode ? wear / 2 : wear) : 0;
    if (Number.isFinite(geometry)) {
      return (diameterMode ? geometry / 2 : geometry) + wearRadius;
    }
    const definition = this.toolDefinitions.get(d) || this.toolDefinitions.get(this.state.tool);
    if (Number.isFinite(definition?.diameter)) {
      this.assumedDOffsets.add(d);
      return definition.diameter / 2 + wearRadius;
    }
    this.warn(`D${String(d).padStart(2, "0")} has no diameter in the tool table or #${this.offsetVariable("dGeometry", d)}; its compensation is drawn with radius 0.`);
    return 0;
  }

  toolRadiusForDisplay() {
    const definition = this.toolDefinition();
    if (Number.isFinite(definition?.diameter)) return definition.diameter / 2;
    const geometry = this.globals.get(this.offsetVariable("dGeometry", this.state.tool));
    if (Number.isFinite(geometry)) return this.diameterOffsets() ? geometry / 2 : geometry;
    return undefined;
  }

  // ----- kinematics ------------------------------------------------------------

  machineRotary(prog, offset = this.workOffset()) {
    return {
      A: (prog.a || 0) + offset.a,
      B: (prog.b || 0) + offset.b,
      C: (prog.c || 0) + offset.c
    };
  }

  partTransform(rotary, offset) {
    return kinematics.multiply(
      kinematics.workpieceTransform(this.chain, rotary),
      kinematics.translation(offset.x, offset.y, offset.z)
    );
  }

  worldToPart(world, rotary, offset) {
    return kinematics.transformPoint(kinematics.invertRigid(this.partTransform(rotary, offset)), world);
  }

  partToWorld(part, rotary, offset) {
    return kinematics.transformPoint(this.partTransform(rotary, offset), part);
  }

  worldToDisplay(world, rotary) {
    return this.worldToPart(world, rotary, this.displayOffset());
  }

  // How program coordinates reach the machine:
  //   "part"    G234 TCPC and G268: the coordinate system rotates with the
  //             table (the program is in the part frame);
  //   "dwo"     G254: axes stay machine-aligned, but the work zero follows the
  //             table rotation (3+2 programs posted about the MRZP);
  //   "machine" otherwise: work offset plus program coordinates.
  commandMode() {
    if (this.state.tcpc || this.state.feature) return "part";
    if (this.state.dwo) return "dwo";
    return "machine";
  }

  programFramesPart() {
    return this.commandMode() === "part";
  }

  // Machine position of the work zero for the current rotary position.
  dwoOrigin(rotary, offset) {
    return kinematics.transformPoint(kinematics.workpieceTransform(this.chain, rotary), offset);
  }

  programToPlane(point) {
    const state = this.state;
    let x = point.x;
    let y = point.y;
    let z = point.z;
    if (Number.isFinite(state.mirror.x)) x = 2 * state.mirror.x - x;
    if (Number.isFinite(state.mirror.y)) y = 2 * state.mirror.y - y;
    if (Number.isFinite(state.mirror.z)) z = 2 * state.mirror.z - z;
    if (state.scaling) {
      const { center, factors } = state.scaling;
      x = center.x + (x - center.x) * factors.x;
      y = center.y + (y - center.y) * factors.y;
      z = center.z + (z - center.z) * factors.z;
    }
    if (state.rotation) {
      const { plane, center, angle } = state.rotation;
      const axes = PLANES[plane];
      const values = { x, y, z };
      const du = values[axes.u] - center[axes.u];
      const dv = values[axes.v] - center[axes.v];
      const c = Math.cos(angle);
      const s = Math.sin(angle);
      values[axes.u] = center[axes.u] + du * c - dv * s;
      values[axes.v] = center[axes.v] + du * s + dv * c;
      return values;
    }
    return { x, y, z };
  }

  planeToProgram(point) {
    const state = this.state;
    const result = { ...point };
    if (state.rotation) {
      const { plane, center, angle } = state.rotation;
      const axes = PLANES[plane];
      const du = result[axes.u] - center[axes.u];
      const dv = result[axes.v] - center[axes.v];
      const c = Math.cos(-angle);
      const s = Math.sin(-angle);
      result[axes.u] = center[axes.u] + du * c - dv * s;
      result[axes.v] = center[axes.v] + du * s + dv * c;
    }
    if (state.scaling) {
      const { center, factors } = state.scaling;
      for (const axis of ["x", "y", "z"]) {
        result[axis] = center[axis] + (result[axis] - center[axis]) / (factors[axis] || 1);
      }
    }
    for (const axis of ["x", "y", "z"]) {
      if (Number.isFinite(state.mirror[axis])) result[axis] = 2 * state.mirror[axis] - result[axis];
    }
    return result;
  }

  planeToWcs(point) {
    return this.state.feature ? kinematics.transformPoint(this.state.feature.matrix, point) : point;
  }

  wcsToPlane(point) {
    return this.state.feature
      ? kinematics.transformPoint(kinematics.invertRigid(this.state.feature.matrix), point)
      : point;
  }

  // Tool-tip machine position commanded by a WCS point at the given machine
  // rotary angles.
  wcsToWorld(wcs, rotary, offset) {
    const mode = this.commandMode();
    let world;
    if (mode === "part") {
      world = this.partToWorld(wcs, rotary, offset);
    } else {
      const origin = mode === "dwo" ? this.dwoOrigin(rotary, offset) : offset;
      world = { x: origin.x + wcs.x, y: origin.y + wcs.y, z: origin.z + wcs.z };
    }
    world.z += this.tlcDelta();
    return world;
  }

  worldToWcs(world, rotary, offset) {
    const physical = { x: world.x, y: world.y, z: world.z - this.tlcDelta() };
    const mode = this.commandMode();
    if (mode === "part") return this.worldToPart(physical, rotary, offset);
    const origin = mode === "dwo" ? this.dwoOrigin(rotary, offset) : offset;
    return { x: physical.x - origin.x, y: physical.y - origin.y, z: physical.z - origin.z };
  }

  // Maps plane coordinates (after G68/G51/G101, before G268) to the drawing
  // frame for fixed rotaries; used for the cutter-compensation overlay.
  planeToPartMatrix(rotary, offset) {
    const mode = this.commandMode();
    const origin = mode === "dwo" ? this.dwoOrigin(rotary, offset) : offset;
    const command = mode === "part"
      ? this.partTransform(rotary, offset)
      : kinematics.translation(origin.x, origin.y, origin.z);
    const toDisplay = kinematics.invertRigid(this.partTransform(rotary, this.displayOffset()));
    let matrix = kinematics.multiply(
      toDisplay,
      kinematics.multiply(kinematics.translation(0, 0, this.tlcDelta()), command)
    );
    if (this.state.feature) matrix = kinematics.multiply(matrix, this.state.feature.matrix);
    return matrix;
  }

  currentJoints() {
    const tip = this.state.tipWorld;
    if (!knownPoint(tip)) return undefined;
    const rotary = this.machineRotary(this.state.prog);
    return { X: tip.x, Y: tip.y, Z: tip.z + this.physicalToolLength(), A: rotary.A, B: rotary.B, C: rotary.C };
  }

  // Recomputes program X/Y/Z from the physical tip after offsets, length
  // compensation or coordinate frames change without axis motion.
  syncProgramFromWorld() {
    const state = this.state;
    const tip = state.tipWorld;
    const offset = this.workOffset();
    if (knownPoint(tip)) {
      const plane = this.wcsToPlane(this.worldToWcs(tip, this.machineRotary(state.prog, offset), offset));
      const program = this.planeToProgram(plane);
      state.prog.x = program.x;
      state.prog.y = program.y;
      state.prog.z = program.z;
      return;
    }
    const simple = !state.rotation && !state.scaling && this.commandMode() === "machine";
    for (const axis of ["x", "y", "z"]) {
      if (simple && Number.isFinite(tip[axis]) && !Number.isFinite(state.mirror[axis])) {
        state.prog[axis] = tip[axis] - offset[axis] - (axis === "z" ? this.tlcDelta() : 0);
      } else if (!Number.isFinite(tip[axis])) {
        state.prog[axis] = undefined;
      }
    }
  }

  // ----- segment creation --------------------------------------------------------

  // Segments from external subprograms map to the calling row of the main
  // file so editor selection stays meaningful.
  currentLine() {
    const frame = this.frame;
    if (!frame) return 1;
    return frame.unit.external ? frame.callLine || 1 : frame.index + 1;
  }

  currentRaw() {
    return this.frame ? String(this.frame.unit.lines[this.frame.index] || "").trim() : "";
  }

  checkRotaryLimits(rotary) {
    for (const axis of ROTARY_AXES) {
      const definition = this.machine.axes?.[axis];
      if (!definition) continue;
      const value = rotary[axis];
      if ((Number.isFinite(definition.min) && value < definition.min - 1e-6) ||
          (Number.isFinite(definition.max) && value > definition.max + 1e-6)) {
        this.error(
          `${this.rowLabel()}: ${axis}${formatNumber(value, 0.001)} is outside the ${this.machine.name} ${axis}-axis range ${definition.min} to ${definition.max} degrees.`,
          this.currentLine()
        );
      }
    }
  }

  checkLinearLimits(joints) {
    // Offsets derived by a macro from a placeholder offset are placeholders too.
    if (this.defaultedOffsets.has(this.state.workOffset.label) || this.defaultOffsetRead || this.limitIssues > 20) return;
    for (const axis of LINEAR_AXES) {
      const definition = this.machine.axes?.[axis];
      if (!definition || !Number.isFinite(joints[axis])) continue;
      const min = Number.isFinite(definition.min) ? definition.min * this.unitScale : -Infinity;
      const max = Number.isFinite(definition.max) ? definition.max * this.unitScale : Infinity;
      if (joints[axis] < min - this.resolution || joints[axis] > max + this.resolution) {
        this.limitIssues += 1;
        this.error(
          `${this.rowLabel()}: machine ${axis}${formatNumber(joints[axis], this.resolution)} is outside the ${axis} travel (${formatNumber(min, this.resolution)} to ${formatNumber(max, this.resolution)}).`,
          this.currentLine()
        );
      }
    }
  }

  emitSegment({ kind, points, rotaryStart, rotaryEnd, seconds, feed, extra = {} }) {
    if (this.segments.length >= this.maxSegments) {
      if (!this.segmentBudgetExceeded) {
        this.segmentBudgetExceeded = true;
        this.error(`Preview resource limit: more than ${this.maxSegments.toLocaleString("en-US")} motion segments; the rest of the program is not drawn.`);
      }
      return undefined;
    }
    const state = this.state;
    const definition = this.toolDefinition();
    const segment = {
      line: this.currentLine(),
      raw: this.currentRaw(),
      sourceUnit: this.frame?.unit.external ? this.frame.unit.name : undefined,
      sourceLine: this.frame?.unit.external ? this.frame.index + 1 : undefined,
      tool: state.tool,
      kind,
      motion: state.motion,
      feed: Number.isFinite(feed) ? feed : state.feed,
      feedMode: state.feedMode === 93 ? "inverse-time" : state.feedMode === 95 ? "per-revolution" : "per-minute",
      spindleSpeed: state.spindleSpeed,
      spindleDirection: state.spindleDirection,
      coolant: [...state.coolant],
      units: state.units,
      compensation: state.compensation,
      workOffset: state.workOffset.label,
      tcpc: state.tcpc,
      dwo: state.dwo,
      featureFrame: Boolean(state.feature),
      points,
      start: points[0],
      end: points[points.length - 1],
      rotary: {
        a0: rotaryStart.A, b0: rotaryStart.B, c0: rotaryStart.C,
        a1: rotaryEnd.A, b1: rotaryEnd.B, c1: rotaryEnd.C
      },
      toolLength: this.physicalToolLength(),
      toolDiameter: Number.isFinite(definition?.diameter) ? definition.diameter : undefined,
      toolRadius: this.toolRadiusForDisplay() ?? 0,
      toolCornerRadius: definition?.cornerRadius || 0,
      toolType: definition?.type || "other",
      nonCutting: definition?.type === "probe",
      estimatedSeconds: Number.isFinite(seconds) ? seconds : null,
      ...extra
    };
    this.segments.push(segment);
    this.stats.rotaryPositions.add(`B${formatNumber(rotaryEnd.B || 0, 0.001)} C${formatNumber(rotaryEnd.C || 0, 0.001)}`);
    return segment;
  }

  rotaryChanged(rotaryStart, rotaryEnd) {
    return ROTARY_AXES.some((axis) => Math.abs((rotaryEnd[axis] || 0) - (rotaryStart[axis] || 0)) > 1e-9);
  }

  rotarySampleCount(rotaryStart, rotaryEnd) {
    const sweep = Math.max(...ROTARY_AXES.map((axis) => Math.abs((rotaryEnd[axis] || 0) - (rotaryStart[axis] || 0))));
    return Math.max(2, Math.min(360, Math.ceil(sweep / 2)));
  }

  // Joint-space samples mapped into the drawing frame. `profile(t)` returns
  // the progress (0..1) of every axis at parameter t.
  sampleJointPath(worldStart, worldEnd, rotaryStart, rotaryEnd, profile, times) {
    return times.map((t) => {
      const progress = profile(t);
      const world = {
        x: lerp(worldStart.x, worldEnd.x, progress.X),
        y: lerp(worldStart.y, worldEnd.y, progress.Y),
        z: lerp(worldStart.z, worldEnd.z, progress.Z)
      };
      const rotary = {
        A: lerp(rotaryStart.A, rotaryEnd.A, progress.A),
        B: lerp(rotaryStart.B, rotaryEnd.B, progress.B),
        C: lerp(rotaryStart.C, rotaryEnd.C, progress.C)
      };
      return this.worldToDisplay(world, rotary);
    });
  }

  feedSeconds(length, rotaryStart, rotaryEnd, feedOverride) {
    const state = this.state;
    const feed = Number.isFinite(feedOverride) ? feedOverride : state.feed;
    if (state.feedMode === 93) {
      return Number.isFinite(feed) && feed > 0 ? 60 / feed : undefined;
    }
    let effective = length;
    if (this.fanuc && this.rotaryChanged(rotaryStart, rotaryEnd) && !state.tcpc) {
      // FANUC feeds the combined vector of all axes, degrees counted as mm.
      const degrees = (axis) => Math.abs((rotaryEnd[axis] || 0) - (rotaryStart[axis] || 0));
      effective = Math.hypot(length, degrees("A"), degrees("B"), degrees("C"));
    } else if (this.rotaryChanged(rotaryStart, rotaryEnd) && !state.tcpc) {
      // Settings 34/79 convert rotary degrees to surface distance.
      const fourth = toFinite(settingValue(this.settings, 34, 100), 100) * this.unitScale;
      const fifth = toFinite(settingValue(this.settings, 79, 100), 100) * this.unitScale;
      const arc = (axis, diameter) => Math.abs((rotaryEnd[axis] || 0) - (rotaryStart[axis] || 0)) / 360 * Math.PI * diameter;
      effective = Math.hypot(length, arc("A", fourth), arc("B", fourth), arc("C", fifth));
    }
    if (!(effective > 0)) return 0;
    if (!Number.isFinite(feed) || feed <= 0) return undefined;
    if (state.feedMode === 95) {
      const rpm = state.spindleSpeed;
      return Number.isFinite(rpm) && rpm > 0 ? (effective / (feed * rpm)) * 60 : undefined;
    }
    return (effective / feed) * 60;
  }

  // Core motion: moves the physical tool tip to `worldEnd` (machine
  // coordinates) and the rotaries to `rotaryEnd` (machine angles).
  //   planePoints: optional polyline in plane coordinates (arcs, pockets)
  //   partPoints:  optional explicit part-frame path (TCPC straight lines)
  moveWorld({ worldEnd, rotaryEnd, rapid, kind, planePoints, partPoints, feed, rapidPercent, extra }) {
    const state = this.state;
    const offset = this.workOffset();
    const startTip = state.tipWorld;
    const rotaryStart = this.machineRotary(state.prog, offset);
    const startKnown = knownPoint(startTip);
    const endKnown = knownPoint(worldEnd);
    const worldStart = {
      x: Number.isFinite(startTip.x) ? startTip.x : worldEnd.x,
      y: Number.isFinite(startTip.y) ? startTip.y : worldEnd.y,
      z: Number.isFinite(startTip.z) ? startTip.z : worldEnd.z
    };
    const rotaryMoves = this.rotaryChanged(rotaryStart, rotaryEnd);
    if (rotaryMoves) this.checkRotaryLimits(rotaryEnd);
    let segment;
    if (endKnown) {
      this.checkLinearLimits({
        X: worldEnd.x, Y: worldEnd.y, Z: worldEnd.z + this.physicalToolLength()
      });
    }
    // Closed paths (full circles) start and end at the same point.
    const closedPath = Boolean(planePoints && planePoints.length > 2 && polylineLength(planePoints) > 1e-10);
    const moves = endKnown && (!samePoint(worldStart, worldEnd, 1e-10) || rotaryMoves || closedPath);
    if (moves && (startKnown || rotaryMoves)) {
      let points;
      let seconds;
      if (rapid) {
        ({ points, seconds } = this.rapidPath(worldStart, worldEnd, rotaryStart, rotaryEnd, rapidPercent));
        if (rotaryMoves && state.tcpc) {
          this.warn(this.fanuc
            ? `${this.rowLabel()}: G00 with rotary motion under G43.4/G43.5 is drawn as the machine rapid; the exact tool center path depends on the FANUC rapid-type parameters.`
            : `${this.rowLabel()}: a rapid rotary move with G234 active does not hold the tool tip (Haas note); the preview draws the machine rapid.`);
        }
      } else {
        if (partPoints && partPoints.length >= 2) {
          points = partPoints;
        } else if (planePoints && planePoints.length >= 2 && !rotaryMoves) {
          points = planePoints.map((point) =>
            this.worldToDisplay(this.wcsToWorld(this.planeToWcs(point), rotaryEnd, offset), rotaryEnd));
          points[0] = this.worldToDisplay(worldStart, rotaryStart);
        } else if (rotaryMoves && !state.tcpc) {
          const count = this.rotarySampleCount(rotaryStart, rotaryEnd);
          const times = Array.from({ length: count + 1 }, (_, index) => index / count);
          const profile = (t) => Object.fromEntries(AXES.map((axis) => [axis, t]));
          points = this.sampleJointPath(worldStart, worldEnd, rotaryStart, rotaryEnd, profile, times);
        } else {
          points = [
            this.worldToDisplay(worldStart, rotaryStart),
            this.worldToDisplay(worldEnd, rotaryEnd)
          ];
        }
        const length = state.tcpc || (planePoints && planePoints.length > 2)
          ? polylineLength(points)
          : distance3(worldStart, worldEnd);
        seconds = this.feedSeconds(length, rotaryStart, rotaryEnd, feed);
        if (!Number.isFinite(seconds)) {
          this.warn(`${this.rowLabel()}: feed motion has no usable ${state.feedMode === 95 ? "feed or spindle speed" : state.feedMode === 93 ? "inverse-time F" : "feed rate"}; its time is not estimated.`);
        }
      }
      const planePath = planePoints && planePoints.length >= 2
        ? planePoints
        : startKnown && !rotaryMoves
          ? [
            this.wcsToPlane(this.worldToWcs(worldStart, rotaryStart, offset)),
            this.wcsToPlane(this.worldToWcs(worldEnd, rotaryEnd, offset))
          ]
          : undefined;
      segment = this.emitSegment({
        kind,
        points,
        rotaryStart,
        rotaryEnd,
        seconds,
        feed,
        extra: {
          ...(extra || {}),
          planePoints: !rotaryMoves ? planePath : undefined,
          toPart: !rotaryMoves ? this.planeToPartMatrix(rotaryEnd, offset) : undefined
        }
      });
    }
    state.tipWorld = { ...worldEnd };
    return segment;
  }

  // Haas rapids move every axis at its own maximum rate (dog-leg) unless
  // Setting 335 selects linear rapids.
  rapidPath(worldStart, worldEnd, rotaryStart, rotaryEnd, rapidPercent) {
    const deltas = {
      X: Math.abs(worldEnd.x - worldStart.x),
      Y: Math.abs(worldEnd.y - worldStart.y),
      Z: Math.abs(worldEnd.z - worldStart.z),
      A: Math.abs(rotaryEnd.A - rotaryStart.A),
      B: Math.abs(rotaryEnd.B - rotaryStart.B),
      C: Math.abs(rotaryEnd.C - rotaryStart.C)
    };
    const percent = Number.isFinite(rapidPercent) && rapidPercent > 0 ? Math.min(100, rapidPercent) / 100 : 1;
    const limit = !this.fanuc && settingOn(this.settings, 10) ? 0.5 : 1;
    const minutes = {};
    for (const axis of AXES) {
      const rate = (LINEAR_AXES.includes(axis) ? this.linearRapid(axis) : this.rotaryRapid(axis)) * percent * limit;
      minutes[axis] = deltas[axis] > 1e-12 ? deltas[axis] / rate : 0;
    }
    const total = Math.max(...Object.values(minutes));
    // Haas Setting 335 / FANUC parameter 1401#1 (LRP) select linear rapids.
    const linear = this.fanuc ? this.parameterOn("1401#1") : settingOn(this.settings, 335);
    const profile = (t) => Object.fromEntries(AXES.map((axis) => [
      axis,
      linear ? (total > 0 ? t / total : 1) : minutes[axis] > 0 ? Math.min(1, t / minutes[axis]) : 1
    ]));
    const times = new Set([0, total]);
    if (!linear) Object.values(minutes).forEach((value) => times.add(value));
    if (this.rotaryChanged(rotaryStart, rotaryEnd)) {
      const count = this.rotarySampleCount(rotaryStart, rotaryEnd);
      for (let index = 1; index < count; index += 1) times.add((total * index) / count);
    }
    const sortedTimes = [...times].filter((time) => time >= 0 && time <= total).sort((a, b) => a - b);
    const points = this.sampleJointPath(worldStart, worldEnd, rotaryStart, rotaryEnd, profile, sortedTimes);
    return { points, seconds: total * 60 };
  }

  // Moves to program coordinates (x/y/z/a/b/c; undefined keeps the axis).
  moveProgram(target, { rapid, kind, feed, rapidPercent, extra } = {}) {
    const state = this.state;
    const next = { ...state.prog };
    for (const axis of AXIS_KEYS) {
      if (Number.isFinite(target[axis])) next[axis] = target[axis];
    }
    const offset = this.workOffset();
    const rotaryStart = this.machineRotary(state.prog, offset);
    const rotaryEnd = this.machineRotary(next, offset);
    const linearCommanded = ["x", "y", "z"].some((axis) => Number.isFinite(target[axis]));
    const rotaryMoves = this.rotaryChanged(rotaryStart, rotaryEnd);
    let worldEnd;
    let keepTip = false;
    if (rotaryMoves && !linearCommanded && (state.dwo || state.feature) && knownPoint(state.tipWorld)) {
      // DWO: rotary positioning does not move X/Y/Z; the next X/Y/Z block
      // recalls the position in the rotated frame (Haas G254 notes).
      worldEnd = { ...state.tipWorld };
      keepTip = true;
    } else if (knownPoint(next)) {
      worldEnd = this.wcsToWorld(this.planeToWcs(this.programToPlane(next)), rotaryEnd, offset);
    } else {
      // Partial positions: only axes that map one-to-one can be placed.
      worldEnd = { ...state.tipWorld };
      const simple = !state.rotation && !state.scaling && this.commandMode() === "machine";
      if (simple) {
        for (const axis of ["x", "y", "z"]) {
          if (Number.isFinite(next[axis]) && !Number.isFinite(state.mirror[axis])) {
            worldEnd[axis] = offset[axis] + next[axis] + (axis === "z" ? this.tlcDelta() : 0);
          }
        }
      }
    }
    let partPoints;
    if (state.tcpc && !rapid && knownPoint(worldEnd) && knownPoint(state.tipWorld)) {
      // TCPC keeps the tool tip on a straight line relative to the table.
      partPoints = [
        this.worldToDisplay(state.tipWorld, rotaryStart),
        this.worldToDisplay(worldEnd, rotaryEnd)
      ];
    }
    const segment = this.moveWorld({ worldEnd, rotaryEnd, rapid, kind, feed, rapidPercent, extra, partPoints });
    state.prog = next;
    if (keepTip) this.syncProgramFromWorld();
    for (const axis of AXIS_KEYS) {
      if (Number.isFinite(next[axis])) state.lastTarget[axis] = next[axis];
    }
    return segment;
  }

  // Feed move along a polyline given in program coordinates, rotaries fixed.
  movePolyline(programPoints, { kind, feed, extra } = {}) {
    const state = this.state;
    if (programPoints.length < 2) return undefined;
    const end = programPoints[programPoints.length - 1];
    const planePoints = programPoints.map((point) => this.programToPlane(point));
    const offset = this.workOffset();
    const rotary = this.machineRotary(state.prog, offset);
    const worldEnd = this.wcsToWorld(this.planeToWcs(planePoints[planePoints.length - 1]), rotary, offset);
    const segment = this.moveWorld({ worldEnd, rotaryEnd: rotary, rapid: false, kind, feed, planePoints, extra });
    state.prog = { ...state.prog, x: end.x, y: end.y, z: end.z };
    state.lastTarget = { ...state.lastTarget, x: end.x, y: end.y, z: end.z };
    return segment;
  }

  // ----- program flow ------------------------------------------------------------

  pushFrame(unit, index, { kind, locals, repeat = 1 }) {
    if (this.stack.length >= MAX_CALL_DEPTH) {
      this.error(`${this.rowLabel()}: subprogram nesting is deeper than ${MAX_CALL_DEPTH} levels; the preview stops.`, this.currentLine());
      this.stopRequested = true;
      return false;
    }
    const frame = {
      unit,
      index,
      entryIndex: index,
      kind,
      locals: locals || this.locals(),
      repeat,
      loops: [],
      headerSeen: false,
      jumped: false
    };
    this.stack.push(frame);
    this.frame = frame;
    return true;
  }

  popFrame() {
    this.stack.pop();
    this.frame = this.stack[this.stack.length - 1];
  }

  findProgram(target) {
    const number = typeof target === "number" ? target : Number(String(target).match(/O?(\d+)(?:\.\w+)?\s*$/i)?.[1]);
    if (Number.isFinite(number) && this.mainUnit.programs.has(number)) {
      return { unit: this.mainUnit, index: this.mainUnit.programs.get(number) };
    }
    const key = typeof target === "number" ? `O${String(target).padStart(5, "0")}` : String(target);
    let unit = this.externalUnits.get(key);
    if (!unit && this.resolveSubprogram) {
      try {
        const resolved = this.resolveSubprogram(target);
        if (resolved && typeof resolved.source === "string") {
          unit = createUnit(resolved.name || key, resolved.source, true);
          unit.location = resolved.location === "memory" ? "memory" : "folder";
          this.externalUnits.set(key, unit);
        }
      } catch (error) {
        this.warn(`Subprogram ${key} could not be read: ${error.message}`);
      }
    }
    if (!unit) return undefined;
    this.externalPrograms.set(unit.name, unit.location || "folder");
    const start = Number.isFinite(number) && unit.programs.has(number) ? unit.programs.get(number) : 0;
    return { unit, index: start };
  }

  searchedLocations(kind) {
    if (kind === "M97" || !this.resolveSubprogram) return "this file";
    return this.resolveSubprogram.memoryFolders?.length
      ? "this file, its folder or the machine memory folder"
      : "this file or its folder (no machine memory folder is set)";
  }

  jumpToSequence(number, label = "GOTO") {
    const frame = this.frame;
    const sequence = Math.round(number);
    const target = findSequence(frame.unit, sequence, frame.index);
    if (!Number.isFinite(target)) {
      this.error(`${this.rowLabel()}: ${label}${sequence} target N${sequence} was not found (the control alarms).`, this.currentLine());
      this.stopRequested = true;
      return;
    }
    this.stats.jumpCount += 1;
    // Jumping out of WHILE bodies drops the loops left behind.
    frame.loops = frame.loops.filter((loop) => loop.start <= target && target <= loop.end);
    frame.index = target;
    frame.jumped = true;
  }

  callSubprogram(kind, target, { repeat = 1, locals, label }) {
    const caller = this.frame;
    let location;
    if (kind === "M97") {
      const index = findSequence(caller.unit, Math.round(target), caller.index);
      if (Number.isFinite(index)) location = { unit: caller.unit, index };
    } else {
      location = this.findProgram(target);
    }
    if (!location) {
      const name = kind === "M97" ? `N${target}` : typeof target === "number" ? `O${String(target).padStart(5, "0")}` : `"${target}"`;
      this.error(`${this.rowLabel()}: ${label || kind} target ${name} was not found in ${this.searchedLocations(kind)}.`, this.currentLine());
      return false;
    }
    if (repeat <= 0) return false;
    const callLine = this.currentLine();
    caller.index += 1;
    caller.jumped = true;
    if (this.pushFrame(location.unit, location.index, {
      kind,
      locals: locals || caller.locals,
      repeat
    })) {
      this.frame.headerSeen = false;
      this.frame.callLine = callLine;
      this.stats.callCount += 1;
    }
    return true;
  }

  returnFromSubprogram(pNumber) {
    const frame = this.frame;
    if (frame.repeat > 1) {
      // L repeats restart the subprogram; G65 arguments are only passed once.
      frame.repeat -= 1;
      frame.index = frame.entryIndex;
      frame.loops = [];
      frame.headerSeen = false;
      frame.jumped = true;
      return;
    }
    this.popFrame();
    if (!this.frame) return;
    if (Number.isFinite(pNumber)) this.jumpToSequence(pNumber, "M99 P");
    this.frame.jumped = true;
  }

  // ----- block reading ------------------------------------------------------------

  evaluate(expression) {
    return evaluateExpression(expression, this.variableView(), undefined, this.macroOptions);
  }

  readValue(body, start) {
    let index = start;
    while (body[index] === " " || body[index] === "\t") index += 1;
    let sign = 1;
    if (body[index] === "-" || body[index] === "+") {
      if (body[index] === "-") sign = -1;
      index += 1;
      while (body[index] === " " || body[index] === "\t") index += 1;
    }
    if (body[index] === "#") {
      let expression;
      if (body[index + 1] === "[") {
        const end = matchingBracket(body, index + 1);
        if (end < 0) throw new Error("Unclosed #[ ] indirect variable");
        expression = body.slice(index, end + 1);
        index = end + 1;
      } else {
        const match = body.slice(index).match(/^#\s*(\d+)(\.\d+)?/);
        if (!match) throw new Error(`Invalid variable at column ${index + 1}`);
        const parameter = match[2] && isParameterVariable(Number(match[1]));
        expression = `#${match[1]}${parameter ? match[2] : ""}`;
        index += parameter ? match[0].length : match[0].length - (match[2]?.length || 0);
      }
      const value = this.evaluate(expression);
      return { value: value === null ? null : sign * value, decimal: true, expression: true, end: index };
    }
    if (body[index] === "[") {
      const end = matchingBracket(body, index);
      if (end < 0) throw new Error("Unclosed [ ] expression");
      const value = this.evaluate(body.slice(index, end + 1));
      return { value: value === null ? null : sign * value, decimal: true, expression: true, end: end + 1 };
    }
    const match = body.slice(index).match(/^(\d+\.?\d*|\.\d+)/);
    if (!match) return undefined;
    return {
      value: sign * Number(match[1]),
      decimal: match[1].includes("."),
      expression: false,
      end: index + match[0].length
    };
  }

  readBlockWords(body) {
    const words = new Map();
    const comma = {};
    const order = [];
    let condition;
    let gotoTarget;
    let index = 0;
    while (index < body.length) {
      const character = body[index];
      if (character === " " || character === "\t") {
        index += 1;
        continue;
      }
      if (character === ",") {
        // ,R / ,C corner blends; FANUC also ,D (cycle clearance), ,L (TCP).
        const letter = body[index + 1];
        const value = (this.fanuc ? /[A-Z]/ : /[RC]/).test(letter || "") ? this.readValue(body, index + 2) : undefined;
        if (!value) throw new Error(`Unsupported comma address ,${letter || ""}`);
        if (value.value !== null) comma[letter] = value.value;
        index = value.end;
        continue;
      }
      if (character === "[") {
        const end = matchingBracket(body, index);
        if (end < 0) throw new Error("Unclosed [ ] condition");
        condition = this.evaluate(body.slice(index, end + 1));
        index = end + 1;
        continue;
      }
      if (body.startsWith("GOTO", index)) {
        const value = this.readValue(body, index + 4);
        if (!value || value.value === null) throw new Error("GOTO needs a sequence number");
        gotoTarget = Math.round(value.value);
        index = value.end;
        continue;
      }
      if (character >= "A" && character <= "Z") {
        const value = this.readValue(body, index + 1);
        if (!value) throw new Error(`Address ${character} has no value`);
        index = value.end;
        if (value.value === null) continue; // An undefined variable drops the address.
        let number = value.value;
        // FANUC standard decimal notation: X100 is 100 least increments
        // (0.1 mm) unless parameter 3401#0 (DPI) selects calculator notation.
        // `raw` keeps the written number for integer uses (K repeats, P/Q).
        const scaled = this.fanuc && !value.decimal && !value.expression &&
          FANUC_DECIMAL_ADDRESSES.has(character) && !this.parameterOn("3401#0");
        if (scaled) number *= this.leastIncrement(character);
        if (value.expression && "XYZIJKRUVW".includes(character)) number = roundTo(number, this.resolution);
        else if (value.expression && "ABC".includes(character)) number = roundTo(number, 0.001);
        const entry = { value: number, decimal: value.decimal, raw: value.value, scaled };
        if (!words.has(character)) words.set(character, []);
        words.get(character).push(entry);
        order.push({ letter: character, ...entry });
        continue;
      }
      throw new Error(`Unexpected character "${character}"`);
    }
    return { words, comma, condition, gotoTarget, order };
  }

  // ----- statements -----------------------------------------------------------------

  executeLine(raw) {
    const state = this.state;
    let text = String(raw);
    const trimmed = text.trim();
    if (!trimmed || trimmed === "%") return;
    if (trimmed.startsWith("/")) {
      if (state.blockDelete) return;
      // FANUC optional block skip levels /2 to /9 follow the same switch.
      text = this.fanuc ? trimmed.slice(1).replace(/^[1-9](?=\D|$)/, "") : trimmed.slice(1);
    }
    const { body, comments } = splitComments(text);
    this.lastComment = comments.join(" ");
    let code = body.toUpperCase().trim();
    if (!code) return;
    if (/^O\s*\d+/.test(code) || /^:\s*\d+/.test(code)) {
      // Program headers are markers. A second header means the program ran
      // into the next program without M30/M99.
      if (this.frame.headerSeen) {
        if (this.stack.length > 1) {
          this.warn(`${this.rowLabel()}: subprogram ran into the next program without M99; returning.`);
          this.returnFromSubprogram();
        } else {
          this.warn(`${this.rowLabel()}: the main program ran into program ${code.match(/\d+/)[0]} without M30; the preview stops.`);
          this.stopRequested = true;
        }
      }
      this.frame.headerSeen = true;
      return;
    }
    this.frame.headerSeen = true;
    if (this.fanuc && state.parameterInput) {
      this.executeParameterInput(code);
      return;
    }
    const sequence = code.match(/^N\s*(\d+)/);
    if (sequence) state.lastSequence = Number(sequence[1]);
    code = code.replace(/^N\s*\d+\s*/, "");
    if (!code) return;
    this.stats.executedBlockCount += 1;
    this.executeStatement(code, comments);
  }

  executeStatement(code, comments) {
    if (/^(?:WHILE|WH)\s*\[/.test(code)) {
      this.executeWhile(code);
      return;
    }
    const loopStart = code.match(/^DO\s*(\d)\s*$/);
    if (loopStart) {
      const id = Number(loopStart[1]);
      this.frame.loops.push({ id, start: this.frame.index, end: this.findLoopEnd(id), infinite: true });
      return;
    }
    const loopEnd = code.match(/^END\s*(\d)\s*$/);
    if (loopEnd) {
      this.executeEnd(Number(loopEnd[1]));
      return;
    }
    if (/^IF\s*\[/.test(code)) {
      this.executeIf(code, comments);
      return;
    }
    if (/^(?:DPRNT|BPRNT|POPEN|PCLOS|SETVN)\b/.test(code)) return;
    if (/^#/.test(code) && code.includes("=")) {
      this.executeAssignment(code);
      return;
    }
    if (/^GOTO/.test(code)) {
      const value = this.readValue(code, 4);
      if (!value || value.value === null) {
        this.warn(`${this.rowLabel()}: GOTO has no target.`);
        return;
      }
      this.jumpToSequence(value.value);
      return;
    }
    this.executeBlock(code, comments);
  }

  executeAssignment(code) {
    const equals = code.indexOf("=");
    const left = code.slice(0, equals).trim();
    const right = code.slice(equals + 1).trim();
    let id;
    if (left.startsWith("#[")) {
      id = Math.round(this.evaluate(left.slice(1)) ?? NaN);
    } else {
      const match = left.match(/^#\s*(\d+)$/);
      id = match ? Number(match[1]) : NaN;
    }
    if (!Number.isInteger(id) || id <= 0) {
      this.warn(`${this.rowLabel()}: invalid assignment target ${left}.`);
      return;
    }
    this.setVariable(id, this.evaluate(right));
  }

  findLoopEnd(id) {
    const frame = this.frame;
    const range = programRange(frame.unit, frame.index);
    for (let index = frame.index + 1; index < range.end; index += 1) {
      const code = stripBlockCode(frame.unit.lines[index]).replace(/^\//, "").replace(/^N\s*\d+\s*/, "");
      const match = code.match(/^END\s*(\d)\s*$/);
      if (match && Number(match[1]) === id) return index;
    }
    return undefined;
  }

  executeWhile(code) {
    const frame = this.frame;
    const match = code.match(/^(?:WHILE|WH)\s*(\[.*\])\s*DO\s*(\d)\s*$/);
    if (!match) {
      this.warn(`${this.rowLabel()}: malformed WHILE statement.`);
      return;
    }
    const id = Number(match[2]);
    const end = this.findLoopEnd(id);
    if (!Number.isFinite(end)) {
      this.error(`${this.rowLabel()}: WHILE ... DO${id} has no matching END${id}.`, this.currentLine());
      this.stopRequested = true;
      return;
    }
    const condition = this.evaluate(match[1]);
    const existing = frame.loops.findIndex((loop) => loop.start === frame.index);
    if (condition !== null && condition !== 0) {
      if (existing < 0) frame.loops.push({ id, start: frame.index, end });
    } else {
      if (existing >= 0) frame.loops.splice(existing, 1);
      frame.index = end + 1;
      frame.jumped = true;
    }
  }

  executeEnd(id) {
    const frame = this.frame;
    const loop = [...frame.loops].reverse().find((candidate) => candidate.id === id);
    if (!loop) {
      this.warn(`${this.rowLabel()}: END${id} without an active DO${id}.`);
      return;
    }
    this.stats.jumpCount += 1;
    frame.index = loop.infinite ? loop.start + 1 : loop.start;
    frame.jumped = true;
  }

  executeIf(code, comments) {
    const open = code.indexOf("[");
    const close = matchingBracket(code, open);
    if (close < 0) {
      this.warn(`${this.rowLabel()}: IF condition has no closing bracket.`);
      return;
    }
    const condition = this.evaluate(code.slice(open, close + 1));
    const rest = code.slice(close + 1).trim();
    const truthy = condition !== null && condition !== 0;
    if (/^GOTO/.test(rest)) {
      if (!truthy) return;
      const value = this.readValue(rest, 4);
      if (!value || value.value === null) {
        this.warn(`${this.rowLabel()}: IF ... GOTO has no target.`);
        return;
      }
      this.jumpToSequence(value.value);
      return;
    }
    // THEN is optional: a false condition skips the rest of the block.
    const statement = rest.replace(/^THEN\b/, "").trim();
    if (!statement) {
      this.warn(`${this.rowLabel()}: IF needs GOTO or a statement.`);
      return;
    }
    if (truthy) this.executeStatement(statement, comments);
  }

  // ----- G-code blocks ----------------------------------------------------------------

  executeBlock(code, comments) {
    if (this.fanuc) return this.executeFanucBlock(code, comments);
    const state = this.state;
    let parsed;
    try {
      parsed = this.readBlockWords(code);
    } catch (error) {
      this.warn(`${this.rowLabel()}: ${error.message}; block skipped.`);
      return;
    }
    const { words, comma } = parsed;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const entry = (letter) => words.get(letter)?.at(-1);
    const has = (letter) => words.has(letter);
    const gCodes = (words.get("G") || []).map((word) => word.value);
    const mCodes = (words.get("M") || []).map((word) => word.value);
    const hasG = (code) => gCodes.includes(code);
    for (const [letter, list] of words) {
      if (letter !== "G" && letter !== "M") state.lastAddresses[letter] = list[list.length - 1].value;
    }

    const groupsSeen = new Map();
    for (const code of gCodes) {
      if (!Number.isInteger(code) || !this.knownG.has(code)) {
        this.warn(`${this.rowLabel()}: G${code} is not a ${this.control?.name || "Haas"} G-code; ignored.`);
        continue;
      }
      const group = MODAL_GROUP_OF.get(code);
      if (group !== undefined) {
        if (groupsSeen.has(group) && groupsSeen.get(group) !== code) {
          this.warn(`${this.rowLabel()}: G${groupsSeen.get(group)} and G${code} share a modal group; the control alarms (the preview uses G${code}).`);
        }
        groupsSeen.set(group, code);
      }
    }
    for (const code of mCodes) {
      if (!Number.isInteger(code) || !this.knownM.has(code)) {
        this.warn(`${this.rowLabel()}: M${code} is not a ${this.control?.name || "Haas"} M-code; ignored.`);
      }
    }
    if (mCodes.length > 1) {
      this.warn(`${this.rowLabel()}: only one M-code per block is allowed on the Haas control.`);
    }

    // Blocks whose addresses are macro arguments.
    if (hasG(65) || hasG(66)) {
      this.executeMacroCall(parsed, hasG(66));
      return;
    }
    if (hasG(67)) state.modalMacro = undefined;

    const dataBlock = [10, 47, 100, 101, 107].some(hasG);
    for (const axis of AXES) {
      if (has(axis) && !this.axisSet.has(axis) && !dataBlock) {
        this.warn(`${this.rowLabel()}: the ${this.machine.name} has no ${axis} axis; ${axis} words are ignored.`);
        words.delete(axis);
      }
    }

    // --- modal state ----------------------------------------------------------------
    if (hasG(20) && state.units !== "inch") this.applyUnits("inch");
    if (hasG(21) && state.units !== "mm") this.applyUnits("mm");
    for (const plane of [17, 18, 19]) {
      if (hasG(plane)) state.plane = plane;
    }
    if (hasG(90)) state.incremental = false;
    if (hasG(91)) state.incremental = true;
    if (hasG(93)) state.feedMode = 93;
    if (hasG(94)) state.feedMode = 94;
    if (hasG(95)) state.feedMode = 95;
    if (hasG(98)) state.returnMode = 98;
    if (hasG(99)) state.returnMode = 99;
    if (has("F") && !hasG(47) && !hasG(10)) state.feed = value("F");
    if (has("S") && !hasG(167)) state.spindleSpeed = value("S");
    if (has("T")) {
      state.pendingTool = Math.trunc(value("T"));
      if (state.pendingTool > 0) this.tools.add(state.pendingTool);
    }
    if (has("D") && ![10, 47, 35, 156].some(hasG)) state.compD = Math.trunc(value("D"));
    this.updateWorkOffset(gCodes, words);
    // Group 01 is modal even on blocks handled by a group 00 code (G00 G53 Z0).
    if (hasG(80)) state.cycle = undefined;
    const motionCode = gCodes.find((code) => Number.isInteger(code) && code >= 0 && code <= 3);
    if (motionCode !== undefined) {
      state.motion = motionCode;
      if (state.cycle && (motionCode === 0 || motionCode === 1)) state.cycle = undefined;
    }

    // Tool length compensation (group 08). CAM posts end the last TCPC move
    // with G49 on the same block; the move still runs under TCPC.
    const lengthCodes = [43, 44, 49].some(hasG);
    if (state.tcpc && lengthCodes && AXES.some(has)) {
      const deferred = { g43: hasG(43), g44: hasG(44), h: has("H") ? Math.trunc(value("H")) : undefined };
      this.deferredLengthChange = () => {
        state.tcpc = false;
        state.toolLengthMode = deferred.g43 ? 43 : deferred.g44 ? 44 : 49;
        if (deferred.h !== undefined) state.toolLengthH = deferred.h;
        this.syncProgramFromWorld();
      };
    } else if (hasG(43) || hasG(44)) {
      state.toolLengthMode = hasG(43) ? 43 : 44;
      state.tcpc = false;
      if (has("H")) state.toolLengthH = Math.trunc(value("H"));
      else if (!Number.isInteger(state.toolLengthH)) state.toolLengthH = state.tool;
      this.syncProgramFromWorld();
    } else if (hasG(49)) {
      state.toolLengthMode = 49;
      state.tcpc = false;
      this.syncProgramFromWorld();
    } else if (hasG(234)) {
      if (!has("H")) {
        this.warn(`${this.rowLabel()}: G234 cancels the previous H code; put the H code on the G234 block.`);
      }
      if (Math.abs(state.prog.b || 0) > 1e-6 || Math.abs(state.prog.c || 0) > 1e-6) {
        this.warn(`${this.rowLabel()}: the rotary axes must be at 0 before G234 (Haas).`);
      }
      if (state.dwo) this.warn(`${this.rowLabel()}: G234 cannot be used while G254 is active (alarm 2062).`);
      state.toolLengthH = has("H") ? Math.trunc(value("H")) : state.tool;
      state.toolLengthMode = 43;
      state.tcpc = true;
      this.syncProgramFromWorld();
    } else if (hasG(143)) {
      this.warn(`${this.rowLabel()}: G143 is for machines whose rotaries both move the tool (VR series); use G234 on the ${this.machine.name}.`);
      state.toolLengthMode = 43;
      if (has("H")) state.toolLengthH = Math.trunc(value("H"));
      this.syncProgramFromWorld();
    } else if (has("H") && !hasG(37)) {
      state.toolLengthH = Math.trunc(value("H"));
      if (state.toolLengthH === 0) {
        state.toolLengthMode = 49;
        state.tcpc = false;
      }
      this.syncProgramFromWorld();
    }

    // Cutter compensation (group 07).
    let compensationStart = false;
    let compensationCancel = false;
    if (hasG(40)) {
      compensationCancel = state.compensation === 41 || state.compensation === 42;
      state.compensation = 40;
    }
    if (hasG(41) || hasG(42)) {
      const next = hasG(41) ? 41 : 42;
      compensationStart = state.compensation !== next;
      state.compensation = next;
      if (state.plane !== 17) {
        this.warn(`${this.rowLabel()}: Haas cutter compensation is only applied in the G17 XY plane.`);
      }
    }
    if (hasG(141)) state.compensation = 141;

    // Dynamic work offset and feature coordinates.
    if (hasG(254)) {
      if (state.tcpc) this.warn(`${this.rowLabel()}: G254 cannot be used while G234 is active.`);
      const offset = this.workOffset();
      if (Math.abs(offset.b) > 1e-9) {
        this.warn(`${this.rowLabel()}: the B work offset must be zero with G254 (Haas caution); ${state.workOffset.label} B is ${formatNumber(offset.b, 0.001)}.`);
      }
      state.dwo = true;
      this.syncProgramFromWorld();
    }
    if (hasG(255)) {
      state.dwo = false;
      this.syncProgramFromWorld();
    }
    if (hasG(268)) {
      this.defineFeatureFrame(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(269)) {
      state.feature = undefined;
      this.syncProgramFromWorld();
    }
    if (hasG(253)) this.orientSpindleToFeature();

    // Transformations whose axis words are data.
    if (hasG(50)) {
      state.scaling = undefined;
      this.syncProgramFromWorld();
    }
    if (hasG(51)) {
      this.defineScaling(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(69)) {
      state.rotation = undefined;
      this.syncProgramFromWorld();
    }
    if (hasG(68)) {
      this.defineRotation(words, gCodes);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(100) || hasG(101)) {
      for (const axis of AXES) {
        if (!has(axis)) continue;
        const key = axis.toLowerCase();
        if (hasG(101)) state.mirror[key] = LINEAR_AXES.includes(axis) ? value(axis) : 0;
        else delete state.mirror[key];
      }
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(52)) {
      this.setLocalShift(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(92)) {
      this.setG92(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(10)) {
      this.executeG10(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(107)) {
      this.defineCylindricalMapping(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(4)) {
      const p = entry("P");
      const seconds = p ? (p.decimal ? p.value : p.value / 1000) : 0;
      this.stats.dwellSeconds += Math.max(0, seconds);
      this.recordDwell(seconds);
      if (p) state.cycleP = p;
      return this.finishBlock(parsed, mCodes);
    }

    // --- group 00 motion --------------------------------------------------------------
    if (hasG(28)) {
      this.executeG28(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(29)) {
      this.executeG29(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(53)) {
      this.executeG53(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(266)) {
      this.executeG266(words);
      return this.finishBlock(parsed, mCodes);
    }
    const probeCode = [31, 35, 36, 37, 136].find(hasG);
    if (probeCode !== undefined) {
      this.executeProbe(probeCode, words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(12) || hasG(13)) {
      this.executeCircularPocket(hasG(12), words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(47)) {
      this.executeEngraving(words, comments);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(150)) {
      this.executeG150(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(174) || hasG(184)) {
      this.executeNonVerticalTap(hasG(174), words);
      return this.finishBlock(parsed, mCodes);
    }

    // --- canned cycles and group 01 motion ------------------------------------------------
    const cycleCode = gCodes.find((code) => CYCLE_CODES.has(code));
    if (cycleCode !== undefined) this.defineCycle(cycleCode, words, entry);
    else if (state.cycle) this.updateCycle(words, entry);

    const patternCode = [70, 71, 72].find(hasG);
    if (patternCode !== undefined) {
      this.executeBoltPattern(patternCode, words);
      return this.finishBlock(parsed, mCodes);
    }

    const axisWords = AXES.filter(has);
    const executesCycle = state.cycle && (
      axisWords.includes("X") || axisWords.includes("Y") ||
      (cycleCode !== undefined && settingOn(this.settings, 28, true))
    );
    if (executesCycle) {
      this.executeCycleBlock(words);
    } else if (state.cycle && axisWords.some((axis) => ROTARY_AXES.includes(axis))) {
      this.moveProgram(this.axisTarget(words, ROTARY_AXES), { rapid: true, kind: "rapid" });
    } else if (!state.cycle && (axisWords.length || (state.motion >= 2 && (has("I") || has("J") || has("K"))))) {
      this.executeMotion(words, comma, { compensationStart, compensationCancel }, hasG(60));
      if (state.modalMacro && axisWords.length && !this.insideModalMacro()) {
        this.executeModalMacro();
      }
    } else if (compensationStart || compensationCancel) {
      // G40/G41/G42 without motion apply to the next move.
      state.pendingCompensation = compensationStart ? "start" : "cancel";
    }
    return this.finishBlock(parsed, mCodes);
  }

  // ----- FANUC Series 30i blocks ---------------------------------------------------------------

  executeFanucBlock(code, comments) {
    const state = this.state;
    let parsed;
    try {
      parsed = this.readBlockWords(code);
    } catch (error) {
      this.warn(`${this.rowLabel()}: ${error.message}; block skipped.`);
      return;
    }
    const { words, comma } = parsed;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const raw = (letter) => words.get(letter)?.at(-1)?.raw;
    const entry = (letter) => words.get(letter)?.at(-1);
    const has = (letter) => words.has(letter);
    const gCodes = (words.get("G") || []).map((word) => word.value);
    const mCodes = (words.get("M") || []).map((word) => word.value);
    const hasG = (target) => gCodes.some((number) => Math.abs(number - target) < 1e-9);
    for (const [letter, list] of words) {
      if (letter !== "G" && letter !== "M") state.lastAddresses[letter] = list[list.length - 1].value;
    }

    // Macro modal call B (G66.1): every block becomes a call whose
    // arguments are the block's addresses.
    if (state.modalMacro?.type === "B" && ![65, 66, 66.1, 67].some(hasG) && !this.insideModalMacro()) {
      const locals = new Map(state.modalMacro.locals);
      for (const [id, number] of this.macroArguments(parsed)) locals.set(id, number);
      this.callSubprogram("G66.1", state.modalMacro.target, { repeat: 1, locals, label: "G66.1 P" });
      return;
    }

    const groupsSeen = new Map();
    let unsupportedMotion;
    for (const number of gCodes) {
      const definition = this.codeGroups.get(number);
      if (!definition) {
        this.warn(`${this.rowLabel()}: G${formatCode(number)} is not a ${this.control?.name || "FANUC"} G-code; ignored.`);
        continue;
      }
      if (definition.preview === "not-expanded") {
        this.noteCode(number);
        if (definition.group === 0 || definition.group === 1) unsupportedMotion = number;
      }
      if (definition.group > 0) {
        if (groupsSeen.has(definition.group) && groupsSeen.get(definition.group) !== number) {
          this.warn(`${this.rowLabel()}: G${formatCode(groupsSeen.get(definition.group))} and G${formatCode(number)} share modal group ${definition.group}; the control uses the last one.`);
        }
        groupsSeen.set(definition.group, number);
      }
    }
    for (const number of mCodes) {
      if (!this.knownM.has(number)) {
        this.warn(`${this.rowLabel()}: M${number} is not defined for the ${this.machine.name}; ignored.`);
      }
    }
    const mLimit = this.parameterOn("3404#7") ? 3 : 1;
    if (mCodes.length > mLimit) {
      this.warn(`${this.rowLabel()}: ${mCodes.length} M codes in one block; the control accepts ${mLimit} (parameter 3404#7 M3B).`);
    }

    if (hasG(65) || hasG(66) || hasG(66.1)) {
      this.executeMacroCall(parsed, hasG(66) ? "A" : hasG(66.1) ? "B" : false);
      return;
    }
    if (hasG(67)) state.modalMacro = undefined;

    const dataBlock = [10, 51, 51.1, 50.1, 52, 68, 68.2, 68.3, 68.4, 7.1, 107, 92, 92.1].some(hasG);
    for (const axis of AXES) {
      if (has(axis) && !this.axisSet.has(axis) && !dataBlock) {
        this.warn(`${this.rowLabel()}: the ${this.machine.name} has no ${axis} axis; ${axis} words are ignored.`);
        words.delete(axis);
      }
    }

    // --- modal state -------------------------------------------------------------------
    if (hasG(20) && state.units !== "inch") this.applyUnits("inch");
    if (hasG(21) && state.units !== "mm") this.applyUnits("mm");
    for (const plane of [17, 18, 19]) {
      if (hasG(plane)) state.plane = plane;
    }
    if (hasG(90)) state.incremental = false;
    if (hasG(91)) state.incremental = true;
    if (hasG(93)) state.feedMode = 93;
    if (hasG(94)) state.feedMode = 94;
    if (hasG(95)) state.feedMode = 95;
    if (hasG(98)) state.returnMode = 98;
    if (hasG(99)) state.returnMode = 99;
    for (const mode of [61, 62, 63, 64]) {
      if (hasG(mode)) state.cuttingMode = mode;
    }
    if (hasG(15)) state.polar = undefined;
    if (hasG(16)) state.polar = { radius: undefined, angle: undefined };
    if (has("F") && !hasG(10)) state.feed = value("F");
    if (has("S") && !hasG(10)) state.spindleSpeed = value("S");
    if (has("T")) {
      state.pendingTool = Math.trunc(value("T"));
      if (state.pendingTool > 0) this.tools.add(state.pendingTool);
    }
    if (has("D") && !hasG(10)) state.compD = Math.trunc(value("D"));
    this.updateFanucWorkOffset(gCodes, words);
    if (hasG(80)) state.cycle = undefined;
    const motionCode = gCodes.find((number) => number === 0 || number === 1 || number === 2 || number === 3);
    if (motionCode !== undefined) {
      state.motion = motionCode;
      state.cycle = undefined; // Group 01 codes cancel canned cycles.
    } else if (unsupportedMotion !== undefined && this.codeGroups.get(unsupportedMotion)?.group === 1) {
      state.motion = unsupportedMotion;
      state.cycle = undefined;
    }

    // --- tool length compensation and tool center point control (group 08) ------------
    const cancelLength = hasG(49) || hasG(49.1);
    if (state.tcpc && cancelLength && AXES.some(has)) {
      // The last TCP move is programmed together with G49.
      this.deferredLengthChange = () => {
        state.tcpc = false;
        state.tcpcType = undefined;
        state.toolLengthMode = 49;
        this.syncProgramFromWorld();
      };
    } else if (hasG(43.4) || hasG(43.5)) {
      if (has("H")) {
        state.toolLengthH = Math.trunc(value("H"));
      } else if (!Number.isInteger(state.toolLengthH)) {
        state.toolLengthH = state.tool;
        this.warn(`${this.rowLabel()}: G${hasG(43.5) ? "43.5" : "43.4"} has no H and no earlier H code; the preview uses H${state.tool}.`);
      }
      state.toolLengthMode = 43;
      state.tcpc = true;
      state.tcpcType = hasG(43.5) ? 5 : 4;
      this.syncProgramFromWorld();
    } else if (hasG(43) || hasG(44) || hasG(43.1)) {
      state.toolLengthMode = hasG(44) ? 44 : 43;
      state.tcpc = false;
      state.tcpcType = undefined;
      if (has("H")) state.toolLengthH = Math.trunc(value("H"));
      else if (!Number.isInteger(state.toolLengthH)) state.toolLengthH = state.tool;
      this.syncProgramFromWorld();
    } else if (cancelLength) {
      state.toolLengthMode = 49;
      state.tcpc = false;
      state.tcpcType = undefined;
      this.syncProgramFromWorld();
    } else if (has("H") && !hasG(10)) {
      state.toolLengthH = Math.trunc(value("H"));
      this.syncProgramFromWorld();
    }

    // --- cutter compensation (group 07) ---------------------------------------------------
    let compensationStart = false;
    let compensationCancel = false;
    const threeD = [41.2, 41.3, 41.4, 41.5, 41.6, 42.2, 42.4, 42.5, 42.6].some(hasG);
    if (hasG(40) || threeD) {
      compensationCancel = state.compensation === 41 || state.compensation === 42;
      state.compensation = 40;
    }
    if (hasG(41) || hasG(42)) {
      const next = hasG(41) ? 41 : 42;
      compensationStart = state.compensation !== next;
      state.compensation = next;
      if (state.plane !== 17) {
        this.warn(`${this.rowLabel()}: the preview draws cutter compensation in the G17 XY plane only.`);
      }
    }

    // --- dynamic fixture offset and setting error compensation ------------------------
    if (hasG(54.2)) {
      const p = Math.trunc(raw("P") ?? 0);
      state.dwo = p > 0;
      if (p > 0) {
        this.warn(`${this.rowLabel()}: G54.2 P${p}: the rotary table dynamic fixture offsets are not in the machine definition; the preview turns the active work offset with the table.`);
      }
      words.delete("P");
      this.syncProgramFromWorld();
    }
    if (hasG(54.4)) {
      if (Math.trunc(raw("P") ?? 0) > 0) {
        this.warn(`${this.rowLabel()}: G54.4 workpiece setting errors are measured on the machine; the preview assumes no error.`);
      }
      words.delete("P");
    }

    // --- coordinate rotation and tilted working plane (group 16) ------------------------
    if (hasG(69)) {
      state.feature = undefined;
      state.rotation = undefined;
      state.twpPending = undefined;
      this.syncProgramFromWorld();
    }
    if (hasG(68.2) || hasG(68.4)) {
      this.defineTiltedPlane(hasG(68.4) ? 68.4 : 68.2, words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(68.3)) {
      this.defineToolAxisPlane(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(53.1) || hasG(53.6)) {
      this.controlToolAxis(hasG(53.6));
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(68)) {
      if (has("I") || has("J") || has("K")) this.defineConversion3d(words);
      else this.defineFanucRotation(words, gCodes);
      return this.finishBlock(parsed, mCodes);
    }

    // --- transformations whose axis words are data ---------------------------------------
    if (hasG(50)) {
      state.scaling = undefined;
      this.syncProgramFromWorld();
    }
    if (hasG(51)) {
      this.defineFanucScaling(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(50.1)) {
      const named = LINEAR_AXES.filter(has);
      if (!named.length) state.mirror = {};
      for (const axis of named) delete state.mirror[axis.toLowerCase()];
      this.syncProgramFromWorld();
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(51.1)) {
      for (const axis of LINEAR_AXES) {
        if (has(axis)) state.mirror[axis.toLowerCase()] = value(axis);
      }
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(52)) {
      for (const axis of AXES) {
        if (has(axis)) state.localShift[axis.toLowerCase()] = value(axis);
      }
      this.syncProgramFromWorld();
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(92)) {
      this.setG92(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(92.1)) {
      const named = AXES.filter(has);
      for (const axis of named.length ? named : AXES) {
        state.g92Shift[axis.toLowerCase()] = 0;
        state.localShift[axis.toLowerCase()] = 0;
      }
      this.syncProgramFromWorld();
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(10)) {
      this.executeG10(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(7.1) || hasG(107)) {
      const rotary = ROTARY_AXES.find(has);
      state.cylindricalMode = rotary && value(rotary) !== 0 ? { axis: rotary, radius: value(rotary) } : undefined;
      if (state.cylindricalMode) {
        this.warn(`${this.rowLabel()}: cylindrical interpolation is drawn as the commanded ${rotary}-axis moves; arcs in the unrolled plane are not expanded.`);
      }
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(4)) {
      const x = entry("X");
      const p = entry("P");
      const seconds = x ? (x.decimal ? x.value : x.raw / 1000) : p ? (p.decimal ? p.value : p.value / 1000) : 0;
      this.stats.dwellSeconds += Math.max(0, seconds);
      this.recordDwell(seconds);
      return this.finishBlock(parsed, mCodes);
    }

    // --- group 00 motion ----------------------------------------------------------------------
    if (hasG(28) || hasG(28.2)) {
      this.executeG28(words, 1);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(30) || hasG(30.1) || hasG(30.2)) {
      const p = Math.trunc(raw("P") ?? 2);
      this.executeG28(words, p >= 2 && p <= 4 ? p : 2);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(29)) {
      this.executeG29(words);
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(27)) {
      this.moveProgram(this.axisTarget(words), { rapid: true, kind: "rapid" });
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(53) || hasG(53.2)) {
      this.executeG53(words, hasG(53.2));
      return this.finishBlock(parsed, mCodes);
    }
    if (hasG(31)) {
      this.executeProbe(31, words);
      return this.finishBlock(parsed, mCodes);
    }
    if (unsupportedMotion !== undefined && this.codeGroups.get(unsupportedMotion)?.group === 0) {
      return this.finishBlock(parsed, mCodes);
    }

    // --- canned cycles and group 01 motion -----------------------------------------------------
    const cycleCode = gCodes.find((number) => FANUC_CYCLE_CODES.has(number));
    if (cycleCode !== undefined) this.defineCycle(cycleCode, words, entry, comma);
    else if (state.cycle) this.updateCycle(words, entry, false, comma);

    const axisWords = AXES.filter(has);
    // FANUC drills in every cycle block that has an axis word or R.
    const executesCycle = state.cycle && (axisWords.length > 0 || has("R"));
    const toolVector = state.tcpc && state.tcpcType === 5 && ["I", "J", "K"].some(has) && state.motion <= 1;
    if (executesCycle) {
      this.executeCycleBlock(words);
    } else if (!state.cycle && (axisWords.length || toolVector ||
        ((state.motion === 2 || state.motion === 3) && (has("I") || has("J") || has("K"))))) {
      if (![0, 1, 2, 3].includes(state.motion)) {
        // Unexpanded interpolation (NURBS, involute, threading...): keep
        // the position by moving straight to the block end point.
        this.moveProgram(this.axisTarget(words), { rapid: false, kind: "feed", extra: { approximate: `G${formatCode(state.motion)}` } });
      } else {
        this.executeMotion(words, comma, { compensationStart, compensationCancel }, hasG(60));
      }
      if (state.modalMacro && state.modalMacro.type !== "B" && axisWords.length && !this.insideModalMacro()) {
        this.executeModalMacro();
      }
    } else if (compensationStart || compensationCancel) {
      state.pendingCompensation = compensationStart ? "start" : "cancel";
    }
    return this.finishBlock(parsed, mCodes);
  }

  updateFanucWorkOffset(gCodes, words) {
    const state = this.state;
    const code = gCodes.find((number) => [54, 55, 56, 57, 58, 59, 54.1].includes(number));
    if (code === undefined) return;
    const previous = state.workOffset.label;
    const p = words.has("P") ? Math.trunc(words.get("P").at(-1).raw) : undefined;
    if (code === 54.1 || (code === 54 && p !== undefined)) {
      if (!Number.isInteger(p) || p < 1 || p > 300) {
        this.warn(`${this.rowLabel()}: G54.1 needs P1 to P300.`);
        return;
      }
      state.workOffset = { label: `G54.1 P${p}`, kind: "g154", index: p };
      words.delete("P");
    } else {
      state.workOffset = { label: `G${code}`, kind: "g54", index: code - 53 };
    }
    this.stats.workOffsets.add(state.workOffset.label);
    if (!this.firstWorkOffset && !this.display) this.firstWorkOffset = { ...state.workOffset };
    if (previous !== state.workOffset.label) this.syncProgramFromWorld();
  }

  // G10 L50 ... G11: parameter input blocks "N<number> [P<axis>] R<value>".
  executeParameterInput(code) {
    if (/\bG\s*11(?![\d.])/.test(code)) {
      this.state.parameterInput = false;
      return;
    }
    const number = code.match(/N\s*(\d+)/);
    const valueMatch = code.match(/R\s*([-+]?(?:\d+\.?\d*|\.\d+))/);
    if (!number || !valueMatch) {
      this.warn(`${this.rowLabel()}: parameter input blocks need N (parameter) and R (value).`);
      return;
    }
    const axis = code.match(/P\s*(\d+)/);
    const key = axis ? `${number[1]}/${axis[1]}` : number[1];
    this.parameters[key] = Number(valueMatch[1]);
  }

  // ----- FANUC coordinate conversions -------------------------------------------------------

  defineFanucRotation(words, gCodes) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const plane = [17, 18, 19].find((code) => gCodes.includes(code)) || state.plane;
    const axes = PLANES[plane];
    const center = { x: state.prog.x ?? 0, y: state.prog.y ?? 0, z: state.prog.z ?? 0 };
    for (const axis of [axes.u, axes.v]) {
      const letter = axis.toUpperCase();
      if (words.has(letter)) center[axis] = value(letter);
    }
    let degrees = words.has("R") ? value("R") : toFinite(this.parameter("5410", 0), 0);
    // Parameter 5400#0 (RIN): R is incremental in G91.
    if (state.incremental && this.parameterOn("5400#0") && state.rotation) {
      degrees += (state.rotation.angle * 180) / Math.PI;
    }
    state.rotation = { plane, center, angle: (degrees * Math.PI) / 180 };
  }

  // G68 X Y Z I J K R: rotation by R about the vector (I, J, K) through
  // (X, Y, Z) (3-dimensional coordinate conversion).
  defineConversion3d(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const center = {
      x: words.has("X") ? value("X") : state.prog.x ?? 0,
      y: words.has("Y") ? value("Y") : state.prog.y ?? 0,
      z: words.has("Z") ? value("Z") : state.prog.z ?? 0
    };
    const axis = [value("I") || 0, value("J") || 0, value("K") || 0];
    if (Math.hypot(...axis) < 1e-12) {
      this.warn(`${this.rowLabel()}: G68 needs a non-zero I/J/K rotation axis.`);
      return;
    }
    const matrix = kinematics.multiply(
      kinematics.translation(center.x, center.y, center.z),
      kinematics.multiply(kinematics.rotation(axis, value("R") || 0), kinematics.translation(-center.x, -center.y, -center.z))
    );
    state.feature = { matrix, code: 68 };
    this.syncProgramFromWorld();
  }

  defineFanucScaling(words) {
    const state = this.state;
    const entry = (letter) => words.get(letter)?.at(-1);
    // Parameter 5400#7 (SCR): magnification unit 0.001 or 0.00001.
    const unit = this.parameterOn("5400#7") ? 0.001 : 0.00001;
    const magnification = (letter) => {
      const word = entry(letter);
      if (!word) return undefined;
      return word.decimal ? word.value : word.raw * unit;
    };
    let factors;
    const p = magnification("P");
    if (Number.isFinite(p)) {
      factors = { x: p, y: p, z: p };
    } else if (["I", "J", "K"].some((letter) => words.has(letter))) {
      factors = { x: magnification("I") ?? 1, y: magnification("J") ?? 1, z: magnification("K") ?? 1 };
    } else {
      const fallback = toFinite(this.parameter("5411", 1), 1);
      factors = { x: fallback, y: fallback, z: fallback };
    }
    const value = (letter) => entry(letter)?.value;
    state.scaling = {
      center: {
        x: words.has("X") ? value("X") : state.prog.x ?? 0,
        y: words.has("Y") ? value("Y") : state.prog.y ?? 0,
        z: words.has("Z") ? value("Z") : state.prog.z ?? 0
      },
      factors
    };
  }

  // G68.2 / G68.4: tilted working plane indexing. The feature coordinate
  // system is defined in the workpiece coordinate system (G68.4: in the
  // current feature system) and rotates with the table.
  defineTiltedPlane(code, words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const raw = (letter) => words.get(letter)?.at(-1)?.raw;
    const method = Math.trunc(raw("P") ?? 0);
    const q = Math.trunc(raw("Q") ?? 0);
    const label = `G${code}${method ? ` P${method}` : ""}`;
    if (code === 68.4 && !state.feature) {
      this.warn(`${this.rowLabel()}: G68.4 needs an active tilted working plane; it is applied to the workpiece coordinate system.`);
    }
    if (code === 68.4 && !this.parameterOn("11221#0")) {
      this.warn(`${this.rowLabel()}: G68.4 needs parameter 11221#0 (MTW) = 1; the control alarms otherwise.`);
    }
    const origin = { x: value("X") ?? 0, y: value("Y") ?? 0, z: value("Z") ?? 0 };
    let rotation;
    let frameOrigin = origin;
    if (method === 0) {
      const [alpha, beta, gamma] = [value("I") || 0, value("J") || 0, value("K") || 0];
      if (!alpha && !beta && !gamma && !this.parameterOn("13451#1")) {
        this.warn(`${this.rowLabel()}: ${label} I0 J0 K0 raises alarm PS5457 unless parameter 13451#1 (ATW) is 1.`);
      }
      // Euler angles: about Z, then the new X, then the new Z.
      rotation = kinematics.multiply(
        kinematics.rotation([0, 0, 1], alpha),
        kinematics.multiply(kinematics.rotation([1, 0, 0], beta), kinematics.rotation([0, 0, 1], gamma))
      );
    } else if (method === 1) {
      rotation = this.rollPitchYaw(value("I") || 0, value("J") || 0, value("K") || 0, q || 123);
      if (!rotation) {
        this.warn(`${this.rowLabel()}: ${label} Q${q} is not a rotation order (alarm PS5457).`);
        return;
      }
    } else if (method === 2 || method === 3) {
      const pending = state.twpPending && state.twpPending.code === code && state.twpPending.method === method
        ? state.twpPending
        : { code, method, points: {} };
      pending.points[q] = {
        x: value("X"), y: value("Y"), z: value("Z"),
        i: value("I"), j: value("J"), k: value("K"),
        r: value("R")
      };
      state.twpPending = pending;
      const frame = method === 2 ? this.threePointFrame(pending.points) : this.twoVectorFrame(pending.points);
      if (!frame) return; // waiting for the remaining blocks
      state.twpPending = undefined;
      if (frame.error) {
        this.warn(`${this.rowLabel()}: ${label}: ${frame.error} (alarm PS5457).`);
        return;
      }
      rotation = frame.rotation;
      frameOrigin = frame.origin;
    } else if (method === 4) {
      rotation = this.projectionAngleFrame(value("I") || 0, value("J") || 0, value("K") || 0);
      if (!rotation) {
        this.warn(`${this.rowLabel()}: ${label}: the projected axes are parallel (alarm PS5457).`);
        return;
      }
    } else {
      this.warn(`${this.rowLabel()}: ${label} is not a tilted working plane format (alarm PS5457).`);
      return;
    }
    let matrix = kinematics.multiply(kinematics.translation(frameOrigin.x, frameOrigin.y, frameOrigin.z), rotation);
    if (code === 68.4 && state.feature) matrix = kinematics.multiply(state.feature.matrix, matrix);
    state.rotation = undefined;
    state.feature = { matrix, code };
    this.syncProgramFromWorld();
  }

  // Rotations about the workpiece X, Y and Z axes in the order Q names
  // (Q123: X, then Y, then Z).
  rollPitchYaw(i, j, k, order) {
    const digits = String(order).split("");
    if (digits.length !== 3 || new Set(digits).size !== 3 || digits.some((digit) => !"123".includes(digit))) return undefined;
    const angles = { 1: i, 2: j, 3: k };
    const vectors = { 1: [1, 0, 0], 2: [0, 1, 0], 3: [0, 0, 1] };
    let rotation = kinematics.identity();
    for (const digit of digits) {
      rotation = kinematics.multiply(kinematics.rotation(vectors[digit], angles[digit]), rotation);
    }
    return rotation;
  }

  frameFromAxes(xAxis, zAxis) {
    const unit = (v) => {
      const length = Math.hypot(v[0], v[1], v[2]);
      return length > 1e-12 ? v.map((value) => value / length) : undefined;
    };
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const x = unit(xAxis);
    const z = unit(zAxis);
    if (!x || !z) return undefined;
    const y = unit(cross(z, x));
    if (!y) return undefined;
    return [x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, 0, 0, 0, 1];
  }

  // G68.2 P2: P1 is the origin, P1->P2 the X axis, P3 fixes the plane;
  // Q0 shifts the origin in the new frame and R turns it about Z.
  threePointFrame(points) {
    const [p1, p2, p3] = [points[1], points[2], points[3]];
    if (!p1 || !p2 || !p3) return undefined;
    const vector = (a) => [a.x ?? 0, a.y ?? 0, a.z ?? 0];
    const [a, b, c] = [vector(p1), vector(p2), vector(p3)];
    const x = b.map((value, index) => value - a[index]);
    const toThird = c.map((value, index) => value - a[index]);
    const xLength = Math.hypot(...x);
    if (xLength < 1e-9) return { error: "the first two points coincide" };
    const xUnit = x.map((value) => value / xLength);
    const along = toThird.reduce((sum, value, index) => sum + value * xUnit[index], 0);
    const yAxis = toThird.map((value, index) => value - along * xUnit[index]);
    if (Math.hypot(...yAxis) < 1e-9) return { error: "the three points are on one line" };
    const zAxis = [
      xUnit[1] * yAxis[2] - xUnit[2] * yAxis[1],
      xUnit[2] * yAxis[0] - xUnit[0] * yAxis[2],
      xUnit[0] * yAxis[1] - xUnit[1] * yAxis[0]
    ];
    let rotation = this.frameFromAxes(xUnit, zAxis);
    const r = points[0]?.r ?? points[1]?.r ?? points[2]?.r ?? points[3]?.r ?? 0;
    if (r) rotation = kinematics.multiply(rotation, kinematics.rotation([0, 0, 1], r));
    const shift = points[0] ? kinematics.transformVector(rotation, { x: points[0].x ?? 0, y: points[0].y ?? 0, z: points[0].z ?? 0 }) : { x: 0, y: 0, z: 0 };
    return { rotation, origin: { x: a[0] + shift.x, y: a[1] + shift.y, z: a[2] + shift.z } };
  }

  // G68.2 P3: Q1 X Y Z I J K (origin and X-axis vector), Q2 I J K (Z-axis).
  twoVectorFrame(points) {
    const first = points[1];
    const second = points[2];
    if (!first || !second) return undefined;
    const x = [first.i ?? 0, first.j ?? 0, first.k ?? 0];
    const z = [second.i ?? 0, second.j ?? 0, second.k ?? 0];
    if (Math.hypot(...x) < 1e-12 || Math.hypot(...z) < 1e-12) return { error: "a vector is zero" };
    const xLength = Math.hypot(...x);
    const xUnit = x.map((value) => value / xLength);
    const along = z.reduce((sum, value, index) => sum + value * xUnit[index], 0);
    const zPerp = z.map((value, index) => value - along * xUnit[index]);
    const angle = Math.acos(Math.min(1, Math.abs(along) / Math.hypot(...z))) * 180 / Math.PI;
    if (90 - angle >= 5) return { error: "the vectors are 5 degrees or more away from perpendicular" };
    const rotation = this.frameFromAxes(xUnit, zPerp);
    if (!rotation) return { error: "the vectors are parallel" };
    return { rotation, origin: { x: first.x ?? 0, y: first.y ?? 0, z: first.z ?? 0 } };
  }

  // G68.2 P4: X turned by I about Y and Y turned by J about X span the
  // plane; K turns the frame about its Z axis.
  projectionAngleFrame(i, j, k) {
    const a = kinematics.transformVector(kinematics.rotation([0, 1, 0], i), { x: 1, y: 0, z: 0 });
    const b = kinematics.transformVector(kinematics.rotation([1, 0, 0], j), { x: 0, y: 1, z: 0 });
    const z = [a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x];
    const length = Math.hypot(...z);
    if (length < Math.sin(Math.PI / 180)) return undefined;
    let rotation = this.frameFromAxes([a.x, a.y, a.z], z);
    if (k) rotation = kinematics.multiply(rotation, kinematics.rotation([0, 0, 1], k));
    return rotation;
  }

  // G68.3: a feature coordinate system whose Z is the current tool axis.
  defineToolAxisPlane(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const offset = this.workOffset();
    const rotary = this.machineRotary(state.prog, offset);
    const toolAxis = kinematics.toolAxisInWorkpiece(this.chain, rotary);
    const z = [toolAxis.x, toolAxis.y, toolAxis.z];
    // Parameter 12321 names the vertical axis (0: the reference tool axis Z).
    const vertical = [[0, 0, 1], [1, 0, 0], [0, 1, 0], [0, 0, 1]][Math.trunc(toFinite(this.parameter("12321", 0), 0))] || [0, 0, 1];
    let x = [vertical[1] * z[2] - vertical[2] * z[1], vertical[2] * z[0] - vertical[0] * z[2], vertical[0] * z[1] - vertical[1] * z[0]];
    if (Math.hypot(...x) < 1e-6) x = Math.abs(z[0]) > 0.9 ? [0, 1, 0] : Math.abs(z[1]) > 0.9 ? [0, 0, 1] : [1, 0, 0];
    let rotation = this.frameFromAxes(x, z);
    if (words.has("R")) rotation = kinematics.multiply(rotation, kinematics.rotation([0, 0, 1], value("R")));
    const named = LINEAR_AXES.filter((axis) => words.has(axis));
    if (named.length && named.length < 3) {
      this.warn(`${this.rowLabel()}: G68.3 needs all of X, Y and Z for the origin (alarm PS5457); the current position is used.`);
    }
    const current = { x: state.prog.x ?? 0, y: state.prog.y ?? 0, z: state.prog.z ?? 0 };
    const origin = named.length === 3 ? { x: value("X"), y: value("Y"), z: value("Z") } : current;
    state.rotation = undefined;
    state.feature = { matrix: kinematics.multiply(kinematics.translation(origin.x, origin.y, origin.z), rotation), code: 68.3 };
    this.syncProgramFromWorld();
  }

  // Rotary positions that put the tool along `normal` (workpiece frame),
  // chosen by the FANUC tool axis direction control rules: the smallest
  // master-axis move, then slave-axis move, then angles nearest 0, inside
  // the rotary ranges. Returns machine angles or undefined.
  toolAxisRotary(normal) {
    const current = this.machineRotary(this.state.prog);
    const solved = kinematics.tableToolAxisSolutions(this.chain, normal);
    if (!solved) return this.searchToolAxisRotary(normal, current);
    const { master, slave } = solved;
    const candidates = [];
    for (const solution of solved.solutions) {
      const slaveAngle = solution.singular ? current[slave] : solution[slave];
      for (const masterValue of this.rotaryEquivalents(master, solution[master], current[master])) {
        for (const slaveValue of this.rotaryEquivalents(slave, slaveAngle, current[slave])) {
          candidates.push({ [master]: masterValue, [slave]: slaveValue });
        }
      }
    }
    if (!candidates.length) return undefined;
    const nearZero = (angle) => Math.abs(kinematics.normalizeDegrees(angle));
    candidates.sort((a, b) =>
      Math.abs(a[master] - current[master]) - Math.abs(b[master] - current[master]) ||
      Math.abs(a[slave] - current[slave]) - Math.abs(b[slave] - current[slave]) ||
      nearZero(a[master]) - nearZero(b[master]) ||
      nearZero(a[slave]) - nearZero(b[slave]));
    return { ...current, ...candidates[0] };
  }

  // Equivalent positions of a rotary angle inside the axis range; a
  // continuous axis takes the equivalent nearest the current position.
  rotaryEquivalents(axis, angle, current) {
    const definition = this.machine.axes?.[axis] || {};
    if (definition.wrap || !Number.isFinite(definition.min) || !Number.isFinite(definition.max)) {
      return [roundTo(current + kinematics.normalizeDegrees(angle - current), 0.001)];
    }
    const values = [];
    for (let turn = -2; turn <= 2; turn += 1) {
      const candidate = roundTo(angle + 360 * turn, 0.001);
      if (candidate >= definition.min - 1e-6 && candidate <= definition.max + 1e-6) values.push(candidate);
    }
    return values;
  }

  // Numeric fallback for machines that are not table-table.
  searchToolAxisRotary(normal, current) {
    const score = (b, c) => {
      const rotary = { ...current, B: b, C: c };
      const axis = kinematics.toolAxisInWorkpiece(this.chain, rotary);
      return axis.x * normal.x + axis.y * normal.y + axis.z * normal.z;
    };
    let best = { value: -Infinity, b: 0, c: 0 };
    for (let c = -180; c < 180; c += 2) {
      for (let b = -180; b <= 180; b += 2) {
        const candidate = score(b, c);
        if (candidate > best.value) best = { value: candidate, b, c };
      }
    }
    for (let c = best.c - 2; c <= best.c + 2; c += 0.02) {
      for (let b = best.b - 2; b <= best.b + 2; b += 0.02) {
        const candidate = score(b, c);
        if (candidate > best.value) best = { value: candidate, b, c };
      }
    }
    return best.value > 0.99999 ? { ...current, B: roundTo(best.b, 0.001), C: roundTo(best.c, 0.001) } : undefined;
  }

  // G53.1 / G53.6: turn the rotary axes so the tool is normal to the
  // tilted working plane; the tool tip stays put (the linear axes do not
  // move) and the feature system turns with the table.
  controlToolAxis(retention) {
    const state = this.state;
    if (!state.feature) {
      this.error(`${this.rowLabel()}: G${retention ? "53.6" : "53.1"} without G68.2/G68.3 raises alarm PS5457 on the control.`, this.currentLine());
      return;
    }
    const normal = kinematics.transformVector(state.feature.matrix, { x: 0, y: 0, z: 1 });
    const target = this.toolAxisRotary(normal);
    if (!target) {
      this.error(`${this.rowLabel()}: no ${this.machine.name} rotary position inside the axis ranges points the tool along the tilted plane (alarm PS5459).`, this.currentLine());
      return;
    }
    const offset = this.workOffset();
    this.moveProgram(
      { b: roundTo(target.B - offset.b, 0.001), c: roundTo(target.C - offset.c, 0.001) },
      { rapid: state.motion !== 1, kind: state.motion !== 1 ? "rapid" : "feed" }
    );
  }

  // G43.5: I/J/K give the tool axis in the table coordinate system.
  rotaryForToolVector(words) {
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const normal = { x: value("I") || 0, y: value("J") || 0, z: value("K") || 0 };
    if (Math.hypot(normal.x, normal.y, normal.z) < 1e-12) return {};
    const target = this.toolAxisRotary(normal);
    if (!target) {
      this.warn(`${this.rowLabel()}: no rotary position inside the axis ranges gives the tool vector I${formatNumber(normal.x, 0.0001)} J${formatNumber(normal.y, 0.0001)} K${formatNumber(normal.z, 0.0001)}.`);
      return {};
    }
    const offset = this.workOffset();
    return { b: roundTo(target.B - offset.b, 0.001), c: roundTo(target.C - offset.c, 0.001) };
  }

  finishFanucBlock(parsed, mCodes) {
    const state = this.state;
    const words = parsed.words;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const raw = (letter) => words.get(letter)?.at(-1)?.raw;
    if (this.deferredLengthChange) {
      const change = this.deferredLengthChange;
      this.deferredLengthChange = undefined;
      change();
    }
    for (const code of mCodes) {
      switch (code) {
        case 0:
        case 1:
          this.stats.stopCount += 1;
          break;
        case 2:
        case 30:
          this.programEnd = code;
          this.stopRequested = true;
          return;
        case 3:
          state.spindleDirection = "cw";
          break;
        case 4:
          state.spindleDirection = "ccw";
          break;
        case 5:
        case 19:
          state.spindleDirection = "stop";
          break;
        case 6:
          this.executeToolChange();
          break;
        case 7:
        case 8:
          state.coolant.add(`M${String(code).padStart(2, "0")}`);
          break;
        case 9:
          state.coolant.clear();
          break;
        case 98:
        case 198: {
          const label = `M${code}`;
          if (!words.has("P")) {
            this.warn(`${this.rowLabel()}: ${label} needs a P program number.`);
            break;
          }
          let target = Math.trunc(raw("P"));
          let repeat = words.has("L") ? Math.max(0, Math.trunc(raw("L"))) : 1;
          // M98 Pnnnnpppp: four-digit program pppp called nnnn times.
          if (!words.has("L") && target > 9999 && !this.findProgram(target)) {
            repeat = Math.floor(target / 10000);
            target %= 10000;
          }
          this.callSubprogram(label, target, { repeat, label: `${label} P` });
          return;
        }
        case 99: {
          if (parsed.condition !== undefined && (parsed.condition === null || parsed.condition === 0)) break;
          const p = value("P");
          if (this.stack.length > 1) {
            this.returnFromSubprogram(Number.isFinite(p) ? p : undefined);
            return;
          }
          if (Number.isFinite(p)) {
            this.jumpToSequence(p, "M99 P");
            return;
          }
          this.warn(`${this.rowLabel()}: M99 in the main program loops back to the start on the control; the preview stops after one pass.`);
          this.programEnd = 99;
          this.stopRequested = true;
          return;
        }
        default:
          break;
      }
    }
    if (parsed.gotoTarget !== undefined) this.jumpToSequence(parsed.gotoTarget);
  }

  toolChangeZ() {
    const configured = Number(this.machine.toolChanger?.zMachine);
    if (Number.isFinite(configured)) return configured * this.unitScale;
    return this.referencePosition(1).Z;
  }

  finishBlock(parsed, mCodes) {
    if (this.fanuc) return this.finishFanucBlock(parsed, mCodes);
    const state = this.state;
    const value = (letter) => parsed.words.get(letter)?.at(-1)?.value;
    if (this.deferredLengthChange) {
      const change = this.deferredLengthChange;
      this.deferredLengthChange = undefined;
      change();
    }
    for (const code of mCodes) {
      switch (code) {
        case 0:
        case 1:
          this.stats.stopCount += 1;
          break;
        case 2:
        case 30:
          this.programEnd = code;
          this.stopRequested = true;
          return;
        case 3:
          state.spindleDirection = "cw";
          break;
        case 4:
          state.spindleDirection = "ccw";
          break;
        case 5:
          state.spindleDirection = "stop";
          break;
        case 6:
        case 16:
          this.executeToolChange();
          break;
        case 7:
        case 8:
        case 73:
        case 83:
        case 88:
          state.coolant.add(`M${String(code).padStart(2, "0")}`);
          break;
        case 9:
          state.coolant.clear();
          break;
        case 74:
          state.coolant.delete("M73");
          break;
        case 84:
          state.coolant.delete("M83");
          break;
        case 89:
          state.coolant.delete("M88");
          break;
        case 97: {
          const p = value("P");
          if (!Number.isFinite(p)) {
            this.warn(`${this.rowLabel()}: M97 needs a P line number.`);
            break;
          }
          this.callSubprogram("M97", Math.round(p), { repeat: Math.max(0, Math.trunc(value("L") ?? 1)), label: "M97 P" });
          return;
        }
        case 98: {
          const p = value("P");
          const target = Number.isFinite(p) ? Math.round(p) : (this.lastComment || "").trim();
          if (target === "") {
            this.warn(`${this.rowLabel()}: M98 needs a P program number or a (path).`);
            break;
          }
          this.callSubprogram("M98", target, { repeat: Math.max(0, Math.trunc(value("L") ?? 1)), label: "M98 P" });
          return;
        }
        case 99: {
          if (parsed.condition !== undefined && (parsed.condition === null || parsed.condition === 0)) break;
          const p = value("P");
          if (this.stack.length > 1) {
            this.returnFromSubprogram(Number.isFinite(p) ? p : undefined);
            return;
          }
          if (Number.isFinite(p)) {
            this.jumpToSequence(p, "M99 P");
            return;
          }
          this.warn(`${this.rowLabel()}: M99 in the main program loops back to the start on the control; the preview stops after one pass.`);
          this.programEnd = 99;
          this.stopRequested = true;
          return;
        }
        case 109:
          this.warn(`${this.rowLabel()}: M109 waits for operator input; the preview keeps #${Math.trunc(value("P") ?? 0)} unchanged.`);
          break;
        case 46:
        case 96:
          this.warn(`${this.rowLabel()}: M${code} depends on machine inputs or pallets; the preview does not jump.`);
          break;
        case 95:
          this.warn(`${this.rowLabel()}: M95 sleep mode is not timed in the preview.`);
          break;
        default:
          break;
      }
    }
    if (parsed.gotoTarget !== undefined) this.jumpToSequence(parsed.gotoTarget);
  }

  updateWorkOffset(gCodes, words) {
    const state = this.state;
    const code = gCodes.find((value) => WORK_OFFSET_CODES.has(value) || (value >= 110 && value <= 129) || value === 154);
    if (code === undefined) return;
    const previous = state.workOffset.label;
    if (WORK_OFFSET_CODES.has(code)) {
      state.workOffset = { label: `G${code}`, kind: "g54", index: code - 53 };
    } else if (code >= 110 && code <= 129) {
      state.workOffset = { label: `G${code}`, kind: "g110", index: code - 109 };
    } else {
      const index = Math.trunc(words.get("P")?.at(-1)?.value ?? NaN);
      if (!Number.isInteger(index) || index < 1 || index > 99) {
        this.warn(`${this.rowLabel()}: G154 needs P1 to P99.`);
        return;
      }
      state.workOffset = { label: `G154 P${index}`, kind: "g154", index };
      words.delete("P");
    }
    this.stats.workOffsets.add(state.workOffset.label);
    if (!this.firstWorkOffset && !this.display) this.firstWorkOffset = { ...state.workOffset };
    if (previous !== state.workOffset.label) this.syncProgramFromWorld();
  }

  // G52 writes the G52 line of the work offset table (#5201-#5206); G52 with
  // no axes (or all zero) cancels it.
  setLocalShift(words) {
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const named = AXES.filter((axis) => words.has(axis));
    AXES.forEach((axis, index) => {
      if (!named.length) this.globals.delete(5201 + index);
      else if (words.has(axis)) this.globals.set(5201 + index, value(axis));
    });
    this.syncProgramFromWorld();
  }

  // G92: the current position becomes the commanded value and G52 is
  // cancelled for those axes.
  setG92(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    AXES.forEach((axis, index) => {
      if (!words.has(axis)) return;
      const key = axis.toLowerCase();
      const current = state.prog[key];
      if (!Number.isFinite(current)) {
        this.warn(`${this.rowLabel()}: G92 ${axis} needs a known current position.`);
        return;
      }
      // Haas keeps G52 in #5201+ and G92 replaces it; on FANUC #5201+ is
      // the external offset, which G92 leaves alone.
      const shift = this.fanuc ? undefined : this.globals.get(5201 + index);
      if (Number.isFinite(shift)) {
        state.g92Shift[key] += shift;
        this.globals.delete(5201 + index);
      }
      state.g92Shift[key] += current - value(axis);
      state.prog[key] = value(axis);
    });
  }

  executeG10(words) {
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const l = Math.trunc(value("L") ?? NaN);
    const p = Math.trunc(value("P") ?? NaN);
    const incremental = this.state.incremental;
    const store = (id, number) => {
      const current = this.globals.get(id);
      this.globals.set(id, incremental && Number.isFinite(current) ? current + number : number);
    };
    if (l === 2 || l === 20) {
      let base;
      if (l === 2 && p === 0) base = 5201;
      else if (l === 2 && p >= 1 && p <= 6) base = 5221 + (p - 1) * 20;
      else if (l === 20 && p >= 1 && p <= (this.fanuc ? 300 : 99)) base = 14001 + (p - 1) * 20;
      if (!base) {
        this.warn(`${this.rowLabel()}: G10 L${l} P${p} is not a valid work offset.`);
        return;
      }
      for (const axis of AXES) {
        const index = this.offsetVariableIndex(axis);
        if (words.has(axis) && index >= 0) store(base + index, value(axis));
      }
      this.syncProgramFromWorld();
      return;
    }
    if (this.fanuc && l === 50) {
      // Parameter input mode until G11.
      this.state.parameterInput = true;
      return;
    }
    const r = value("R");
    if (l === 14) return;
    if (!Number.isFinite(p) || !Number.isFinite(r)) {
      this.warn(`${this.rowLabel()}: G10 L${l} needs P and R values.`);
      return;
    }
    // Haas L10/L11 length geometry/wear, L12/L13 diameter geometry/wear.
    // FANUC tool compensation memory C: L10 H geometry, L11 H wear, L12 D
    // geometry, L13 D wear.
    const kinds = { 10: "hGeometry", 11: "hWear", 12: "dGeometry", 13: "dWear" };
    const limit = this.fanuc ? 999 : 200;
    if (kinds[l] && p >= 1 && p <= limit) {
      store(this.offsetVariable(kinds[l], p), r);
      if (l <= 11) this.syncProgramFromWorld();
      return;
    }
    if (!this.fanuc && l === 1 && p >= 1 && p <= 200) {
      store(2200 + p, r);
      this.syncProgramFromWorld();
      return;
    }
    this.warn(`${this.rowLabel()}: G10 L${l} is not previewed.`);
  }

  defineScaling(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    let factors;
    if (words.has("P")) {
      const p = value("P");
      factors = { x: p, y: p, z: p };
    } else {
      const setting71 = toFinite(settingValue(this.settings, 71, 0), 0);
      factors = setting71
        ? { x: setting71, y: setting71, z: setting71 }
        : {
          x: toFinite(settingValue(this.settings, 188, 1), 1),
          y: toFinite(settingValue(this.settings, 189, 1), 1),
          z: toFinite(settingValue(this.settings, 190, 1), 1)
        };
    }
    state.scaling = {
      center: {
        x: words.has("X") ? value("X") : state.prog.x ?? 0,
        y: words.has("Y") ? value("Y") : state.prog.y ?? 0,
        z: words.has("Z") ? value("Z") : state.prog.z ?? 0
      },
      factors
    };
  }

  defineRotation(words, gCodes) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const plane = [17, 18, 19].find((code) => gCodes.includes(code)) || state.plane;
    const axes = PLANES[plane];
    const center = { x: state.prog.x ?? 0, y: state.prog.y ?? 0, z: state.prog.z ?? 0 };
    for (const axis of [axes.u, axes.v]) {
      const letter = axis.toUpperCase();
      if (words.has(letter)) center[axis] = value(letter);
    }
    let degrees = words.has("R") ? value("R") : toFinite(settingValue(this.settings, 72, 0), 0);
    if (state.incremental && settingOn(this.settings, 73) && state.rotation) {
      degrees += (state.rotation.angle * 180) / Math.PI;
    }
    state.rotation = { plane, center, angle: (degrees * Math.PI) / 180 };
  }

  defineCylindricalMapping(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const rotaryAxis = ROTARY_AXES.find((axis) => words.has(axis));
    if (!rotaryAxis) {
      state.cylindrical = undefined;
      return;
    }
    const radius = words.has("R") ? value("R") : words.has("Q") ? value("Q") / 2 : undefined;
    if (!(radius > 0)) {
      this.warn(`${this.rowLabel()}: G107 needs R (radius) or Q (diameter) to map motion.`);
      return;
    }
    if (!this.axisSet.has(rotaryAxis)) {
      this.warn(`${this.rowLabel()}: G107 maps onto ${rotaryAxis}, which the ${this.machine.name} does not have.`);
      return;
    }
    const linear = LINEAR_AXES.find((axis) => words.has(axis)) || "Y";
    state.cylindrical = {
      linear: linear.toLowerCase(),
      rotary: rotaryAxis.toLowerCase(),
      linearReference: words.has(linear) ? value(linear) : 0,
      rotaryReference: value(rotaryAxis),
      radius
    };
  }

  // G268: feature coordinate system relative to the WCS.
  defineFeatureFrame(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    if (state.toolLengthMode !== 43 && state.toolLengthMode !== 44 && !state.tcpc) {
      this.warn(`${this.rowLabel()}: activate G43 tool length compensation before G268.`);
    }
    const origin = {
      x: words.has("X") ? value("X") : 0,
      y: words.has("Y") ? value("Y") : 0,
      z: words.has("Z") ? value("Z") : 0
    };
    let rotation;
    if (words.has("I") || words.has("J") || words.has("K")) {
      const angles = { X: value("I") || 0, Y: value("J") || 0, Z: value("K") || 0 };
      const order = String(Math.trunc(value("Q") ?? 123)) === "321" ? ["Z", "Y", "X"] : ["X", "Y", "Z"];
      rotation = kinematics.identity();
      for (const axis of order) {
        const vector = axis === "X" ? [1, 0, 0] : axis === "Y" ? [0, 1, 0] : [0, 0, 1];
        rotation = kinematics.multiply(kinematics.rotation(vector, angles[axis]), rotation);
      }
    } else {
      // The plane normal to the spindle at the current B/C.
      const offset = this.workOffset();
      rotation = kinematics.invertRigid(this.partTransform(this.machineRotary(state.prog, offset), offset));
      rotation[3] = 0;
      rotation[7] = 0;
      rotation[11] = 0;
    }
    state.feature = {
      matrix: kinematics.multiply(kinematics.translation(origin.x, origin.y, origin.z), rotation)
    };
    this.syncProgramFromWorld();
  }

  // G253: choose B/C so the feature Z axis is parallel to the spindle.
  orientSpindleToFeature() {
    const state = this.state;
    if (!state.feature) {
      this.warn(`${this.rowLabel()}: G253 is only valid while G268 is active.`);
      return;
    }
    const normal = kinematics.transformVector(state.feature.matrix, { x: 0, y: 0, z: 1 });
    const offset = this.workOffset();
    const axisB = this.machine.axes?.B;
    const score = (b, c) => {
      const rotary = { A: 0, B: b + offset.b, C: c + offset.c };
      if (axisB && ((Number.isFinite(axisB.min) && rotary.B < axisB.min - 1e-9) ||
          (Number.isFinite(axisB.max) && rotary.B > axisB.max + 1e-9))) return -Infinity;
      const axis = kinematics.toolAxisInWorkpiece(this.chain, rotary);
      return axis.x * normal.x + axis.y * normal.y + axis.z * normal.z - Math.abs(b) * 1e-7 - Math.abs(c) * 1e-8;
    };
    let best = { value: -Infinity, b: 0, c: 0 };
    for (let c = -180; c < 180; c += 2) {
      for (let b = -180; b <= 180; b += 2) {
        const candidate = score(b, c);
        if (candidate > best.value) best = { value: candidate, b, c };
      }
    }
    for (let c = best.c - 2; c <= best.c + 2; c += 0.05) {
      for (let b = best.b - 2; b <= best.b + 2; b += 0.05) {
        const candidate = score(b, c);
        if (candidate > best.value) best = { value: candidate, b, c };
      }
    }
    if (best.value < 0.99999) {
      this.warn(`${this.rowLabel()}: G253 could not align the spindle with the feature plane inside the rotary limits.`);
      if (!Number.isFinite(best.value)) return;
    }
    this.moveProgram({ b: roundTo(best.b, 0.001), c: roundTo(best.c, 0.001) }, { rapid: true, kind: "rapid" });
  }

  // ----- tool change --------------------------------------------------------------------

  executeToolChange() {
    const state = this.state;
    const tool = state.pendingTool;
    if (!Number.isInteger(tool)) {
      this.warn(`${this.rowLabel()}: M06 without a T number.`);
      return;
    }
    const offset = this.workOffset();
    const rotary = this.machineRotary(state.prog, offset);
    const tip = state.tipWorld;
    // M06 retracts Z to the tool-change position: machine Z0 on Haas, the
    // machine definition's tool-change Z (or reference position) on FANUC.
    const changeZ = this.fanuc ? this.toolChangeZ() : 0;
    let gaugeZ = changeZ;
    if (knownPoint(tip)) {
      const current = tip.z + this.physicalToolLength();
      if (this.fanuc && current > changeZ + this.resolution) gaugeZ = current;
      else {
        this.moveWorld({
          worldEnd: { x: tip.x, y: tip.y, z: changeZ - this.physicalToolLength() },
          rotaryEnd: rotary,
          rapid: true,
          kind: "tool-change"
        });
      }
    }
    state.spindleDirection = "stop";
    state.coolant.clear();
    if (tool !== state.tool) {
      const seconds = toFinite(this.machine.toolChanger?.changeSeconds, 3);
      this.stats.toolChangeCount += 1;
      this.stats.toolChangeSeconds += seconds;
      this.recordDwell(seconds, "tool-change");
    }
    state.tool = tool;
    this.tools.add(tool);
    this.toolDefinition(tool);
    // NGC 100.21+: a tool change switches the length offset to the new tool.
    if (this.autoLengthOffset() && !state.tcpc) {
      state.toolLengthMode = 43;
      state.toolLengthH = tool;
    } else if (!this.fanuc && (state.toolLengthMode !== 49 || state.tcpc)) {
      state.toolLengthH = tool;
    }
    // FANUC keeps the modal H; the program calls G43 H for the new tool.
    state.tipWorld = {
      x: Number.isFinite(tip.x) ? tip.x : undefined,
      y: Number.isFinite(tip.y) ? tip.y : undefined,
      z: gaugeZ - this.physicalToolLength()
    };
    this.syncProgramFromWorld();
  }

  recordDwell(seconds, kind = "dwell") {
    if (!(seconds > 0)) return;
    if (!this.segments.length) {
      this.pendingDwell += seconds;
      return;
    }
    const last = this.segments[this.segments.length - 1];
    last.dwellAfterSeconds = (last.dwellAfterSeconds || 0) + seconds;
    if (kind === "tool-change") last.toolChangeAfterSeconds = (last.toolChangeAfterSeconds || 0) + seconds;
  }

  // ----- machine-coordinate moves -----------------------------------------------------------

  setProgramRotaryFromMachine(rotary, axes = ROTARY_AXES) {
    const offset = this.workOffset();
    for (const axis of axes) {
      this.state.prog[axis.toLowerCase()] = rotary[axis] - offset[axis.toLowerCase()];
    }
  }

  executeG53(words, withFeed = false) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const tip = state.tipWorld;
    const worldEnd = {
      x: words.has("X") ? value("X") : tip.x,
      y: words.has("Y") ? value("Y") : tip.y,
      z: words.has("Z") ? value("Z") - this.physicalToolLength() : tip.z
    };
    const rotaryEnd = this.machineRotary(state.prog);
    for (const axis of ROTARY_AXES) {
      if (words.has(axis)) rotaryEnd[axis] = value(axis);
    }
    // FANUC G53 always positions at rapid; G53.2 uses the feed rate.
    const rapid = this.fanuc ? !withFeed || !Number.isFinite(state.feed) : state.motion !== 1 || !Number.isFinite(state.feed);
    this.moveWorld({ worldEnd, rotaryEnd, rapid, kind: "home" });
    this.setProgramRotaryFromMachine(rotaryEnd);
    this.syncProgramFromWorld();
  }

  // G28 (reference position 1) and FANUC G30 P2-P4, through the
  // intermediate point of the named axes. Haas G28 without axes homes all.
  executeG28(words, reference = 1) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const named = AXES.filter((axis) => words.has(axis) && this.axisSet.has(axis));
    if (this.fanuc && !named.length) {
      this.warn(`${this.rowLabel()}: G${reference === 1 ? "28" : "30"} names no axis; nothing moves.`);
      return;
    }
    const axes = named.length ? named : AXES.filter((axis) => this.axisSet.has(axis));
    const direct = state.incremental && named.every((axis) => Math.abs(value(axis)) < 1e-12);
    if (named.length && !direct) {
      this.moveProgram(this.axisTarget(words, named), { rapid: true, kind: "home" });
    }
    if (named.length) state.g28Intermediate = { ...state.prog };
    if (!this.fanuc && (state.tcpc || state.dwo)) {
      this.warn(`${this.rowLabel()}: G28 ignores G234/G254; avoid it while they are active.`);
    }
    const tip = state.tipWorld;
    const home = this.referencePosition(reference);
    const worldEnd = {
      x: axes.includes("X") ? home.X : tip.x,
      y: axes.includes("Y") ? home.Y : tip.y,
      z: axes.includes("Z") ? home.Z - this.physicalToolLength() : tip.z
    };
    const rotaryEnd = this.machineRotary(state.prog);
    for (const axis of ROTARY_AXES) {
      if (!axes.includes(axis)) continue;
      // Haas Setting 108 returns rotaries to the nearest multiple of 360.
      rotaryEnd[axis] = !this.fanuc && settingOn(this.settings, 108) ? Math.round(rotaryEnd[axis] / 360) * 360 : home[axis];
    }
    this.moveWorld({ worldEnd, rotaryEnd, rapid: true, kind: "home" });
    this.setProgramRotaryFromMachine(rotaryEnd, ROTARY_AXES.filter((axis) => axes.includes(axis)));
    this.syncProgramFromWorld();
  }

  executeG29(words) {
    const state = this.state;
    const named = AXES.filter((axis) => words.has(axis));
    if (state.g28Intermediate) {
      const intermediate = {};
      for (const axis of named) {
        intermediate[axis.toLowerCase()] = state.g28Intermediate[axis.toLowerCase()];
      }
      this.moveProgram(intermediate, { rapid: true, kind: "home" });
    }
    this.moveProgram(this.axisTarget(words, named), { rapid: true, kind: "rapid" });
  }

  executeG266(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const axis = AXES[Math.trunc(value("P") ?? 0) - 1];
    if (!axis || !words.has("I")) {
      this.warn(`${this.rowLabel()}: G266 needs P (axis 1-6) and I (machine position).`);
      return;
    }
    const target = value("I");
    const worldEnd = { ...state.tipWorld };
    const rotaryEnd = this.machineRotary(state.prog);
    if (axis === "X") worldEnd.x = target;
    else if (axis === "Y") worldEnd.y = target;
    else if (axis === "Z") worldEnd.z = target - this.physicalToolLength();
    else rotaryEnd[axis] = target;
    this.moveWorld({ worldEnd, rotaryEnd, rapid: true, kind: "rapid", rapidPercent: value("E") });
    this.setProgramRotaryFromMachine(rotaryEnd);
    this.syncProgramFromWorld();
  }

  executeProbe(code, words) {
    const state = this.state;
    const named = AXES.filter((axis) => words.has(axis));
    if (code === 37) {
      this.warn(`${this.rowLabel()}: G37 feeds Z until the tool setter triggers; the preview does not simulate the contact.`);
      return;
    }
    if (!named.length) {
      this.warn(`${this.rowLabel()}: G${code} needs at least one axis target.`);
      return;
    }
    if (state.compensation === 41 || state.compensation === 42) {
      this.warn(`${this.rowLabel()}: do not use cutter compensation with G${code}.`);
    }
    this.moveProgram(this.axisTarget(words, named), { rapid: false, kind: "probe", extra: { cycle: `G${code}` } });
    // Nothing is in the way in the preview, so the skip signal never fires
    // and #5061-#5066 hold the end point.
    state.skip = { ...state.prog };
    this.warn(`${this.rowLabel()}: G${code} runs to its end point; the preview has no stock, so the probe never triggers.`);
  }

  // ----- linear/circular motion -------------------------------------------------------------

  axisTarget(words, axes = AXES) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const target = {};
    const polar = this.fanuc && state.polar ? this.polarTarget(words, axes) : undefined;
    if (polar) Object.assign(target, polar.target);
    for (const axis of axes) {
      if (!words.has(axis) || polar?.axes.includes(axis)) continue;
      const key = axis.toLowerCase();
      if (state.incremental) {
        const current = key === state.cylindrical?.linear && Number.isFinite(state.cylindricalLinear)
          ? state.cylindricalLinear
          : state.prog[key];
        if (!Number.isFinite(current)) {
          this.warn(`${this.rowLabel()}: incremental ${axis} move from an unknown position is skipped.`);
          continue;
        }
        target[key] = current + value(axis);
      } else {
        target[key] = value(axis);
      }
    }
    // G107 wraps motion of the mapped linear axis onto the rotary axis.
    const cylinder = state.cylindrical;
    if (cylinder && Number.isFinite(target[cylinder.linear])) {
      const programmed = target[cylinder.linear];
      target[cylinder.rotary] = cylinder.rotaryReference +
        ((programmed - cylinder.linearReference) / cylinder.radius) * (180 / Math.PI);
      target[cylinder.linear] = cylinder.linearReference;
      state.cylindricalLinear = programmed;
    }
    return target;
  }

  // FANUC G16: the first plane axis is the radius and the second the angle
  // (counterclockwise from the first axis). G90 measures from the program
  // origin, G91 from the current position with an incremental angle.
  polarTarget(words, axes) {
    const state = this.state;
    const plane = PLANES[state.plane];
    const radiusAxis = plane.u.toUpperCase();
    const angleAxis = plane.v.toUpperCase();
    const hasRadius = words.has(radiusAxis) && axes.includes(radiusAxis);
    const hasAngle = words.has(angleAxis) && axes.includes(angleAxis);
    if (!hasRadius && !hasAngle) return undefined;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const polar = state.polar;
    const current = { u: state.prog[plane.u], v: state.prog[plane.v] };
    let origin = { u: 0, v: 0 };
    if (state.incremental && hasRadius) {
      if (!Number.isFinite(current.u) || !Number.isFinite(current.v)) {
        this.warn(`${this.rowLabel()}: an incremental polar move needs a known position.`);
        return { target: {}, axes: [radiusAxis, angleAxis] };
      }
      origin = current;
    }
    let radius = hasRadius ? value(radiusAxis) : polar.radius;
    let angle = hasAngle
      ? (state.incremental ? (polar.angle ?? 0) + value(angleAxis) : value(angleAxis))
      : polar.angle;
    if (!Number.isFinite(radius)) radius = Math.hypot(current.u ?? 0, current.v ?? 0);
    if (!Number.isFinite(angle)) angle = Math.atan2(current.v ?? 0, current.u ?? 0) * 180 / Math.PI;
    polar.radius = radius;
    polar.angle = angle;
    const radians = (angle * Math.PI) / 180;
    return {
      axes: [radiusAxis, angleAxis],
      target: {
        [plane.u]: origin.u + radius * Math.cos(radians),
        [plane.v]: origin.v + radius * Math.sin(radians)
      }
    };
  }

  executeMotion(words, comma, { compensationStart, compensationCancel }, uniDirectional) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const repeat = this.fanuc ? 1 : Math.max(1, Math.trunc(value("L") ?? 1));
    const compensation = {
      start: compensationStart || state.pendingCompensation === "start",
      cancel: compensationCancel || state.pendingCompensation === "cancel"
    };
    state.pendingCompensation = undefined;
    if (state.feedMode === 93 && state.motion !== 0 && !words.has("F")) {
      this.warn(`${this.rowLabel()}: G93 inverse-time feed needs F on every feed block.`);
    }
    for (let loop = 0; loop < repeat; loop += 1) {
      if (state.motion === 2 || state.motion === 3) {
        this.executeArc(words, state.motion === 2, compensation);
      } else {
        const target = this.axisTarget(words);
        // FANUC G43.5: I/J/K is the tool axis in the table coordinate system.
        if (this.fanuc && state.tcpc && state.tcpcType === 5 && ["I", "J", "K"].some((letter) => words.has(letter))) {
          Object.assign(target, this.rotaryForToolVector(words));
        }
        const rapid = state.motion === 0 || uniDirectional;
        const blend = comma.R !== undefined || comma.C !== undefined;
        if (!rapid && blend && !state.pendingCorner) {
          this.queueCornerBlend(target, comma, this.compensationExtra(compensation));
        } else if (state.pendingCorner && !rapid) {
          this.flushCornerBlend(target);
          if (blend) this.queueCornerBlend(target, comma, this.compensationExtra(compensation));
          else this.moveProgram(target, { rapid: false, kind: "feed", extra: this.compensationExtra(compensation) });
        } else {
          this.flushCornerBlend();
          if (state.compensation === 141 && !rapid) {
            this.move3dCompensated(target, words);
          } else {
            this.moveProgram(target, {
              rapid,
              kind: rapid ? "rapid" : "feed",
              rapidPercent: rapid ? value("E") : undefined,
              extra: this.compensationExtra(compensation)
            });
          }
        }
      }
      compensation.start = false;
      compensation.cancel = false;
      if (!state.incremental) break; // Absolute L repeats retrace the same move.
    }
  }

  compensationExtra(compensation) {
    const state = this.state;
    if (state.compensation !== 41 && state.compensation !== 42 && !compensation.cancel) return {};
    return {
      compensationPhase: compensation.start ? "startup" : compensation.cancel ? "cancel" : "active",
      compensationSide: state.compensation === 41 ? "left" : state.compensation === 42 ? "right" : undefined,
      compensationRadius: state.compensation === 40 ? 0 : this.compensationRadius(),
      compensationD: state.compD,
      plane: state.plane,
      mirrored: ["x", "y"].filter((axis) => Number.isFinite(state.mirror[axis])).length % 2 === 1
    };
  }

  // G141: offset the block end point along the programmed I/J/K normal.
  move3dCompensated(target, words) {
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const normal = { x: value("I") || 0, y: value("J") || 0, z: value("K") || 0 };
    const length = Math.hypot(normal.x, normal.y, normal.z);
    const radius = this.compensationRadius();
    const next = { ...target };
    if (length > 1e-9) {
      for (const axis of ["x", "y", "z"]) {
        const current = Number.isFinite(next[axis]) ? next[axis] : this.state.prog[axis];
        if (Number.isFinite(current)) next[axis] = current + (normal[axis] / length) * radius;
      }
    }
    return this.moveProgram(next, { rapid: false, kind: "feed", extra: { compensationPhase: "g141" } });
  }

  // ,R / ,C blend the corner between this G01 block and the next one. The
  // generated moves keep the block's cutter-compensation state.
  queueCornerBlend(target, comma, extra = {}) {
    const state = this.state;
    const start = { ...state.prog };
    if (!knownPoint(start)) {
      this.moveProgram(target, { rapid: false, kind: "feed", extra });
      return;
    }
    const corner = { ...start };
    for (const axis of ["x", "y", "z"]) {
      if (Number.isFinite(target[axis])) corner[axis] = target[axis];
    }
    // The trimmed move and the blend belong to the block that programs ,R/,C.
    state.pendingCorner = {
      start,
      corner,
      radius: comma.R,
      chamfer: comma.C,
      extra: { ...extra, line: this.currentLine(), raw: this.currentRaw() }
    };
    // Following incremental blocks are measured from the corner.
    state.prog = { ...state.prog, ...corner };
  }

  flushCornerBlend(nextTarget) {
    const state = this.state;
    const pending = state.pendingCorner;
    if (!pending) return;
    state.pendingCorner = undefined;
    const extra = pending.extra || {};
    // Only the first generated move starts compensation.
    const following = extra.compensationPhase === "startup" ? { ...extra, compensationPhase: "active" } : extra;
    const sharp = () => this.moveProgram(
      { x: pending.corner.x, y: pending.corner.y, z: pending.corner.z },
      { rapid: false, kind: "feed", extra }
    );
    if (!nextTarget) {
      sharp();
      return;
    }
    const { u, v } = PLANES[state.plane];
    const next = { ...pending.corner, ...nextTarget };
    const d1 = { u: pending.corner[u] - pending.start[u], v: pending.corner[v] - pending.start[v] };
    const d2 = { u: next[u] - pending.corner[u], v: next[v] - pending.corner[v] };
    const l1 = Math.hypot(d1.u, d1.v);
    const l2 = Math.hypot(d2.u, d2.v);
    if (l1 < 1e-9 || l2 < 1e-9) {
      sharp();
      return;
    }
    const t1 = { u: d1.u / l1, v: d1.v / l1 };
    const t2 = { u: d2.u / l2, v: d2.v / l2 };
    const turn = Math.acos(Math.max(-1, Math.min(1, t1.u * t2.u + t1.v * t2.v)));
    const trim = Number.isFinite(pending.chamfer) ? pending.chamfer : Math.abs(pending.radius) * Math.tan(turn / 2);
    if (!(trim > 0) || trim > l1 + 1e-9 || trim > l2 + 1e-9 || turn < 1e-6) {
      this.warn(`${this.rowLabel()}: the ,R/,C corner does not fit between the two blocks; drawn sharp.`);
      sharp();
      return;
    }
    const before = { ...pending.corner, [u]: pending.corner[u] - t1.u * trim, [v]: pending.corner[v] - t1.v * trim };
    const after = { ...pending.corner, [u]: pending.corner[u] + t2.u * trim, [v]: pending.corner[v] + t2.v * trim };
    this.moveProgram({ x: before.x, y: before.y, z: before.z }, { rapid: false, kind: "feed", extra });
    if (Number.isFinite(pending.chamfer)) {
      this.moveProgram({ x: after.x, y: after.y, z: after.z }, { rapid: false, kind: "feed", extra: following });
      return;
    }
    const cross = t1.u * t2.v - t1.v * t2.u;
    const normal = cross > 0 ? { u: -t1.v, v: t1.u } : { u: t1.v, v: -t1.u };
    const radius = Math.abs(pending.radius);
    const center = { u: before[u] + normal.u * radius, v: before[v] + normal.v * radius };
    const a0 = Math.atan2(before[v] - center.v, before[u] - center.u);
    let sweep = Math.atan2(after[v] - center.v, after[u] - center.u) - a0;
    if (cross > 0) {
      while (sweep < 0) sweep += Math.PI * 2;
    } else {
      while (sweep > 0) sweep -= Math.PI * 2;
    }
    const points = this.tessellateArc(before, after, center, a0, sweep, radius, PLANES[state.plane]);
    this.movePolyline(points, { kind: cross > 0 ? "arc-ccw" : "arc-cw", extra: following });
  }

  arcPoints(start, end, words, clockwise, plane) {
    const axes = PLANES[plane];
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const su = start[axes.u];
    const sv = start[axes.v];
    const eu = end[axes.u];
    const ev = end[axes.v];
    if (words.has(axes.uw) || words.has(axes.vw)) {
      const center = { u: su + (value(axes.uw) || 0), v: sv + (value(axes.vw) || 0) };
      const radiusStart = Math.hypot(su - center.u, sv - center.v);
      const radiusEnd = Math.hypot(eu - center.u, ev - center.v);
      const tolerance = this.state.units === "inch" ? 0.001 : 0.025;
      if (Math.abs(radiusStart - radiusEnd) > tolerance) {
        this.error(`${this.rowLabel()}: the arc end point is ${formatNumber(Math.abs(radiusStart - radiusEnd), this.resolution)} off the circle of radius ${formatNumber(radiusStart, this.resolution)}; the control alarms.`, this.currentLine());
      }
      const a0 = Math.atan2(sv - center.v, su - center.u);
      const a1 = Math.atan2(ev - center.v, eu - center.u);
      const full = Math.hypot(eu - su, ev - sv) < 1e-9;
      const sweep = full
        ? (clockwise ? -Math.PI * 2 : Math.PI * 2)
        : clockwise ? -normalizeAngle(a0 - a1) : normalizeAngle(a1 - a0);
      return this.tessellateArc(start, end, center, a0, sweep, radiusStart, axes);
    }
    const r = value("R");
    if (Number.isFinite(r)) {
      const chord = Math.hypot(eu - su, ev - sv);
      const radius = Math.abs(r);
      if (chord < 1e-9) {
        this.warn(`${this.rowLabel()}: a full circle needs I/J/K; R cannot define it.`);
        return [start, end];
      }
      if (chord > 2 * radius + this.resolution) {
        this.error(`${this.rowLabel()}: R${r} is smaller than half the ${formatNumber(chord, this.resolution)} chord; the control alarms.`, this.currentLine());
        return [start, end];
      }
      const mu = (su + eu) / 2;
      const mv = (sv + ev) / 2;
      const height = Math.sqrt(Math.max(0, radius * radius - (chord / 2) ** 2));
      const nu = -(ev - sv) / chord;
      const nv = (eu - su) / chord;
      const candidates = [
        { u: mu + nu * height, v: mv + nv * height },
        { u: mu - nu * height, v: mv - nv * height }
      ].map((center) => {
        const a0 = Math.atan2(sv - center.v, su - center.u);
        const a1 = Math.atan2(ev - center.v, eu - center.u);
        return { center, a0, sweep: clockwise ? -normalizeAngle(a0 - a1) : normalizeAngle(a1 - a0) };
      });
      candidates.sort((left, right) => r < 0
        ? Math.abs(right.sweep) - Math.abs(left.sweep)
        : Math.abs(left.sweep) - Math.abs(right.sweep));
      const chosen = candidates[0];
      return this.tessellateArc(start, end, chosen.center, chosen.a0, chosen.sweep, radius, axes);
    }
    this.warn(`${this.rowLabel()}: G0${clockwise ? 2 : 3} needs R or ${axes.uw}/${axes.vw}; drawn as a line.`);
    return [start, end];
  }

  tessellateArc(start, end, center, a0, sweep, radius, axes) {
    const steps = Math.max(4, Math.min(1440, Math.ceil(Math.abs(sweep) / (Math.PI / 72))));
    const points = [];
    for (let step = 0; step <= steps; step += 1) {
      const t = step / steps;
      const angle = a0 + sweep * t;
      const point = { x: lerp(start.x, end.x, t), y: lerp(start.y, end.y, t), z: lerp(start.z, end.z, t) };
      point[axes.u] = center.u + Math.cos(angle) * radius;
      point[axes.v] = center.v + Math.sin(angle) * radius;
      points.push(point);
    }
    points[0] = { x: start.x, y: start.y, z: start.z };
    points[points.length - 1] = { x: end.x, y: end.y, z: end.z };
    return points;
  }

  executeArc(words, clockwise, compensation) {
    const state = this.state;
    this.flushCornerBlend();
    const start = { x: state.prog.x, y: state.prog.y, z: state.prog.z };
    const target = this.axisTarget(words, LINEAR_AXES);
    if (!knownPoint(start)) {
      this.warn(`${this.rowLabel()}: arc from an unknown position is skipped.`);
      Object.assign(state.prog, target);
      return;
    }
    const end = { ...start, ...target };
    const points = this.arcPoints(start, end, words, clockwise, state.plane);
    this.movePolyline(points, {
      kind: clockwise ? "arc-cw" : "arc-ccw",
      extra: this.compensationExtra(compensation)
    });
    const rotaryWords = ROTARY_AXES.filter((axis) => words.has(axis));
    if (rotaryWords.length) {
      this.warn(`${this.rowLabel()}: rotary words on G02/G03 are applied after the arc.`);
      this.moveProgram(this.axisTarget(words, rotaryWords), { rapid: false, kind: "feed" });
    }
  }

  // ----- canned drilling cycles ----------------------------------------------------------------

  defineCycle(code, words, entry, comma = {}) {
    const state = this.state;
    const previous = state.cycle;
    const same = previous && previous.code === code;
    state.cycle = {
      code,
      initialZ: previous ? previous.initialZ : state.prog.z,
      r: same ? previous.r : previous?.r,
      z: same ? previous.z : previous?.z,
      q: same ? previous.q : undefined,
      i: same ? previous.i : undefined,
      j: same ? previous.j : undefined,
      k: same ? previous.k : undefined,
      shift: same ? previous.shift : undefined,
      clearance: previous?.clearance
    };
    if (!Number.isFinite(state.cycle.initialZ)) {
      this.warn(`${this.rowLabel()}: G${formatCode(code)} starts from an unknown Z; the R plane is used as the initial plane.`);
    }
    this.updateCycle(words, entry, true, comma);
  }

  updateCycle(words, entry, defining = false, comma = {}) {
    const state = this.state;
    const cycle = state.cycle;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    if (this.fanuc) {
      // FANUC: K is the repeat count, Q the peck depth or boring shift, ,D
      // the G73/G83 clearance (modal while the cycle lasts).
      if (words.has("R")) {
        const r = value("R");
        cycle.r = state.incremental && Number.isFinite(cycle.initialZ) ? cycle.initialZ + r : r;
      }
      if (words.has("Z")) {
        const z = value("Z");
        const reference = Number.isFinite(cycle.r) ? cycle.r : cycle.initialZ;
        cycle.z = state.incremental && Number.isFinite(reference) ? reference + z : z;
      }
      if (words.has("Q")) cycle.q = Math.abs(value("Q"));
      if (Number.isFinite(comma.D)) cycle.clearance = Math.abs(comma.D);
      if (words.has("P")) state.cycleP = entry("P");
      if (defining && !Number.isFinite(cycle.z)) {
        this.warn(`${this.rowLabel()}: G${formatCode(cycle.code)} needs a Z hole depth.`);
      }
      if (defining && !Number.isFinite(cycle.r)) {
        cycle.r = Number.isFinite(cycle.initialZ) ? cycle.initialZ : cycle.z;
      }
      if (defining && !Number.isFinite(cycle.initialZ)) cycle.initialZ = cycle.r;
      return;
    }
    if (words.has("R")) {
      const r = value("R");
      cycle.r = state.incremental && Number.isFinite(cycle.initialZ) ? cycle.initialZ + r : r;
    }
    if (words.has("Z")) {
      const z = value("Z");
      const reference = Number.isFinite(cycle.r) ? cycle.r : cycle.initialZ;
      cycle.z = state.incremental && Number.isFinite(reference) ? reference + z : z;
    }
    const boring = cycle.code === 76 || cycle.code === 77;
    if (words.has("Q")) cycle.q = Math.abs(value("Q"));
    if (!boring) {
      for (const letter of ["I", "J", "K"]) {
        if (words.has(letter)) cycle[letter.toLowerCase()] = Math.abs(value(letter));
      }
    } else if (words.has("I") || words.has("J")) {
      cycle.shift = { x: value("I") || 0, y: value("J") || 0 };
    }
    if (words.has("P")) state.cycleP = entry("P");
    if (defining && !Number.isFinite(cycle.z) && cycle.code !== 156) {
      this.warn(`${this.rowLabel()}: G${cycle.code} needs a Z hole depth.`);
    }
    if (defining && !Number.isFinite(cycle.r)) {
      cycle.r = Number.isFinite(cycle.initialZ) ? cycle.initialZ : cycle.z;
    }
    if (defining && !Number.isFinite(cycle.initialZ)) cycle.initialZ = cycle.r;
  }

  executeCycleBlock(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    // Repeats: FANUC K, Haas L (0 stores the cycle without drilling).
    const repeatWord = this.fanuc ? "K" : "L";
    const loops = words.has(repeatWord) ? Math.trunc(words.get(repeatWord).at(-1).raw ?? value(repeatWord)) : 1;
    const rotaryWords = ROTARY_AXES.filter((axis) => words.has(axis));
    if (rotaryWords.length) {
      this.moveProgram(this.axisTarget(words, rotaryWords), { rapid: true, kind: "rapid" });
    }
    const first = this.axisTarget(words, ["X", "Y"]);
    if (loops === 0) {
      if (Number.isFinite(first.x) || Number.isFinite(first.y)) {
        this.moveProgram({ x: first.x, y: first.y }, { rapid: true, kind: "rapid" });
      }
      return;
    }
    const step = {
      x: state.incremental && words.has("X") ? value("X") : 0,
      y: state.incremental && words.has("Y") ? value("Y") : 0
    };
    for (let loop = 0; loop < Math.max(1, loops); loop += 1) {
      const hole = loop === 0
        ? { x: first.x ?? state.prog.x, y: first.y ?? state.prog.y }
        : { x: state.prog.x + step.x, y: state.prog.y + step.y };
      this.drillHole(hole);
    }
  }

  cycleDwellSeconds() {
    const p = this.state.cycleP;
    if (!p) return 0;
    return Math.max(0, p.decimal ? p.value : p.value / 1000);
  }

  // Q, or the I/J/K schedule, as successive peck depths.
  peckDepths(total) {
    const cycle = this.state.cycle;
    const sizes = [];
    if (Number.isFinite(cycle.i) && cycle.i > 0) {
      let size = cycle.i;
      let depth = 0;
      const minimum = Number.isFinite(cycle.k) && cycle.k > 0 ? cycle.k : size;
      while (depth < total - 1e-9 && sizes.length < 10000) {
        const step = Math.min(size, total - depth);
        sizes.push(step);
        depth += step;
        size = Math.max(minimum, size - (cycle.j || 0));
      }
    } else if (Number.isFinite(cycle.q) && cycle.q > 0) {
      let depth = 0;
      while (depth < total - 1e-9 && sizes.length < 10000) {
        const step = Math.min(cycle.q, total - depth);
        sizes.push(step);
        depth += step;
      }
    } else {
      sizes.push(total);
    }
    return sizes;
  }

  drillHole(hole) {
    const state = this.state;
    const cycle = state.cycle;
    if (!Number.isFinite(hole.x) || !Number.isFinite(hole.y)) {
      this.warn(`${this.rowLabel()}: the canned-cycle hole position is unknown.`);
      return;
    }
    if (!Number.isFinite(cycle.z) || !Number.isFinite(cycle.r)) return;
    const code = cycle.code;
    const initialZ = Number.isFinite(cycle.initialZ) ? cycle.initialZ : cycle.r;
    const { r, z } = cycle;
    // Back boring (Haas G77, FANUC G87) always returns to the initial level.
    const backBoring = code === 77 || (this.fanuc && code === 87);
    const returnZ = backBoring || state.returnMode === 98 ? initialZ : r;
    const label = Number.isInteger(code) ? `G${code}` : `G${formatCode(code)}`;
    const extra = { cycle: label };
    this.stats.holeCount += 1;
    this.stats.cycleCounts[label] = (this.stats.cycleCounts[label] || 0) + 1;
    // X/Y positioning is a rapid at the current height.
    this.moveProgram({ x: hole.x, y: hole.y }, { rapid: true, kind: "rapid", extra });
    if (backBoring) {
      this.backBore(hole, extra, initialZ);
      return;
    }
    if (code === 156) {
      this.broach(extra);
      return;
    }
    const feedTo = (depth, kind = "cycle-feed", feed) =>
      this.moveProgram({ z: depth }, { rapid: false, kind, feed, extra });
    const rapidTo = (depth) => this.moveProgram({ z: depth }, { rapid: true, kind: "rapid", extra });
    rapidTo(r);
    // Peck clearance: Haas Setting 22; FANUC ,D or parameter 5114 (G73) /
    // 5115 (G83) / 5213 (peck tapping).
    const clearance = this.fanuc
      ? toFinite(cycle.clearance ?? this.parameter(code === 73 ? "5114" : code === 83 ? "5115" : "5213", 0), 0) *
        (Number.isFinite(cycle.clearance) ? 1 : this.unitScale)
      : toFinite(settingValue(this.settings, 22, 1.27), 1.27) * this.unitScale;
    const retractAbove = this.fanuc ? 0 : toFinite(settingValue(this.settings, 52, 0), 0) * this.unitScale;
    const dwell = this.cycleDwellSeconds();
    const down = z < r ? -1 : 1;
    const total = Math.abs(z - r);
    switch (code) {
      case 81:
      case 86:
        feedTo(z);
        rapidTo(returnZ);
        break;
      case 82:
        feedTo(z);
        this.recordDwell(dwell);
        rapidTo(returnZ);
        break;
      case 83: {
        let depth = r;
        this.peckDepths(total).forEach((size, index) => {
          if (index > 0) rapidTo(depth - down * clearance);
          depth += down * size;
          feedTo(depth);
          if (Math.abs(depth - z) > 1e-9) rapidTo(r - down * retractAbove);
        });
        this.recordDwell(dwell);
        rapidTo(returnZ);
        break;
      }
      case 73: {
        let depth = r;
        let sinceRetract = 0;
        const fullRetract = Number.isFinite(cycle.k) && Number.isFinite(cycle.q) && !Number.isFinite(cycle.i)
          ? cycle.k
          : undefined;
        this.peckDepths(total).forEach((size) => {
          depth += down * size;
          feedTo(depth);
          sinceRetract += size;
          if (Math.abs(depth - z) <= 1e-9) return;
          if (fullRetract && sinceRetract >= fullRetract - 1e-9) {
            rapidTo(r);
            rapidTo(depth - down * clearance);
            sinceRetract = 0;
          } else {
            rapidTo(depth - down * clearance);
          }
        });
        this.recordDwell(dwell);
        rapidTo(returnZ);
        break;
      }
      case 74:
      case 84:
      case 84.2:
      case 84.3: {
        const feed = state.feed;
        const multiple = this.fanuc ? 1 : cycle.j || toFinite(settingValue(this.settings, 130, 1), 1) || 1;
        const retractFeed = Number.isFinite(feed) ? feed * multiple : feed;
        if (Number.isFinite(cycle.q) && cycle.q > 0) {
          let depth = r;
          this.peckDepths(total).forEach((size) => {
            depth += down * size;
            feedTo(depth, "cycle-feed", feed);
            feedTo(r, "cycle-retract", retractFeed);
            if (Math.abs(depth - z) > 1e-9) rapidTo(depth - down * clearance);
          });
        } else {
          feedTo(z, "cycle-feed", feed);
          feedTo(r, "cycle-retract", retractFeed);
        }
        if (Math.abs(returnZ - r) > 1e-12) rapidTo(returnZ);
        if (!Number.isFinite(state.spindleSpeed)) {
          this.warn(`${this.rowLabel()}: ${label} tapping needs a spindle speed (S).`);
        }
        break;
      }
      case 85:
        feedTo(z);
        feedTo(r, "cycle-retract");
        if (Math.abs(returnZ - r) > 1e-12) rapidTo(returnZ);
        break;
      case 89:
        feedTo(z);
        this.recordDwell(dwell);
        feedTo(r, "cycle-retract");
        if (Math.abs(returnZ - r) > 1e-12) rapidTo(returnZ);
        break;
      case 88:
        // FANUC G88: dwell, spindle stop, then the operator retracts by hand.
        feedTo(z);
        this.recordDwell(dwell);
        this.warn(`${this.rowLabel()}: G88 stops for a manual retraction; the preview draws it as a rapid.`);
        rapidTo(returnZ);
        break;
      case 76: {
        feedTo(z);
        this.recordDwell(dwell);
        const shift = this.boringShift();
        this.moveProgram({ x: hole.x + shift.x, y: hole.y + shift.y }, { rapid: true, kind: "rapid", extra });
        rapidTo(returnZ);
        this.moveProgram({ x: hole.x, y: hole.y }, { rapid: true, kind: "rapid", extra });
        break;
      }
      default:
        feedTo(z);
        rapidTo(returnZ);
    }
  }

  boringShift() {
    const cycle = this.state.cycle;
    if (Number.isFinite(cycle.q) && cycle.q > 0) {
      // Shift direction: Haas Setting 27, FANUC parameter 5148.
      const direction = String(this.fanuc ? this.parameter("5148", "X+") : settingValue(this.settings, 27, "X+")).toUpperCase().replace(/\s+/g, "");
      if (direction === "X-") return { x: -cycle.q, y: 0 };
      if (direction === "Y+") return { x: 0, y: cycle.q };
      if (direction === "Y-") return { x: 0, y: -cycle.q };
      return { x: cycle.q, y: 0 };
    }
    return cycle.shift || { x: 0, y: 0 };
  }

  // G77: shift, rapid below the part to R, centre, feed up to Z, and back out.
  backBore(hole, extra, initialZ) {
    const cycle = this.state.cycle;
    const shift = this.boringShift();
    const shifted = { x: hole.x + shift.x, y: hole.y + shift.y };
    this.moveProgram(shifted, { rapid: true, kind: "rapid", extra });
    this.moveProgram({ z: cycle.r }, { rapid: true, kind: "rapid", extra });
    this.moveProgram({ x: hole.x, y: hole.y }, { rapid: true, kind: "rapid", extra });
    this.moveProgram({ z: cycle.z }, { rapid: false, kind: "cycle-feed", extra });
    this.recordDwell(this.cycleDwellSeconds());
    this.moveProgram({ z: cycle.r }, { rapid: false, kind: "cycle-retract", extra });
    this.moveProgram(shifted, { rapid: true, kind: "rapid", extra });
    this.moveProgram({ z: initialZ }, { rapid: true, kind: "rapid", extra });
    this.moveProgram({ x: hole.x, y: hole.y }, { rapid: true, kind: "rapid", extra });
  }

  // G156: pecks of K from the start plane to Z at the current slice.
  broach(extra) {
    const cycle = this.state.cycle;
    const startZ = this.state.prog.z;
    if (!Number.isFinite(startZ) || !Number.isFinite(cycle.z)) return;
    const step = Number.isFinite(cycle.k) && cycle.k > 0 ? cycle.k : Math.abs(cycle.z - startZ);
    const down = cycle.z < startZ ? -1 : 1;
    let depth = startZ;
    for (let guard = 0; Math.abs(depth - cycle.z) > 1e-9 && guard < 10000; guard += 1) {
      depth = down < 0 ? Math.max(cycle.z, depth - step) : Math.min(cycle.z, depth + step);
      this.moveProgram({ z: depth }, { rapid: false, kind: "cycle-feed", extra });
      this.moveProgram({ z: startZ }, { rapid: false, kind: "cycle-retract", extra });
    }
  }

  executeBoltPattern(code, words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    if (!state.cycle) {
      this.warn(`${this.rowLabel()}: G${code} needs an active canned cycle (G73, G74, G76, G77 or G81-G89).`);
      return;
    }
    const center = { x: state.prog.x, y: state.prog.y };
    if (!Number.isFinite(center.x) || !Number.isFinite(center.y)) {
      this.warn(`${this.rowLabel()}: G${code} needs a known X/Y position.`);
      return;
    }
    const i = value("I") || 0;
    const j = value("J") || 0;
    const count = Math.max(0, Math.trunc(value("L") ?? 0));
    const degrees = (angle) => (angle * Math.PI) / 180;
    for (let index = 0; index < count; index += 1) {
      let hole;
      if (code === 70) {
        const angle = degrees(j + (index * 360) / count);
        hole = { x: center.x + i * Math.cos(angle), y: center.y + i * Math.sin(angle) };
      } else if (code === 71) {
        const angle = degrees(j + index * (value("K") || 0));
        hole = { x: center.x + i * Math.cos(angle), y: center.y + i * Math.sin(angle) };
      } else {
        const angle = degrees(j);
        hole = { x: center.x + index * i * Math.cos(angle), y: center.y + index * i * Math.sin(angle) };
      }
      this.drillHole(hole);
    }
  }

  executeNonVerticalTap(counterClockwise, words) {
    const start = { ...this.state.prog };
    const extra = { cycle: counterClockwise ? "G174" : "G184" };
    this.moveProgram(this.axisTarget(words, LINEAR_AXES), { rapid: false, kind: "cycle-feed", extra });
    this.moveProgram({ x: start.x, y: start.y, z: start.z }, { rapid: false, kind: "cycle-retract", extra });
    this.stats.holeCount += 1;
  }

  // ----- G12/G13 circular pockets -----------------------------------------------------------

  executeCircularPocket(clockwise, words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const code = clockwise ? "G12" : "G13";
    const center = { x: state.prog.x, y: state.prog.y, z: state.prog.z };
    if (!knownPoint(center)) {
      this.warn(`${this.rowLabel()}: ${code} needs a known start position at the pocket centre.`);
      return;
    }
    if (state.plane !== 17) this.warn(`${this.rowLabel()}: G12/G13 always cut in the XY plane.`);
    const d = words.has("D") ? Math.trunc(value("D")) : state.compD;
    const toolRadius = d === 0 ? 0 : this.compensationRadius(d);
    const firstRadius = value("I");
    if (!(firstRadius > 0)) {
      this.warn(`${this.rowLabel()}: ${code} needs I (circle radius).`);
      return;
    }
    const finalRadius = words.has("K") ? value("K") : undefined;
    const step = words.has("Q") ? Math.abs(value("Q")) : undefined;
    const radii = [];
    if (Number.isFinite(finalRadius) && step > 0) {
      for (let radius = firstRadius; radius < finalRadius - 1e-9 && radii.length < 10000; radius += step) {
        radii.push(radius);
      }
      radii.push(finalRadius);
    } else {
      radii.push(firstRadius);
    }
    const loops = Math.max(1, Math.trunc(value("L") ?? 1));
    const depth = value("Z");
    const extra = { cycle: code };
    const direction = clockwise ? -1 : 1;
    const halfCircle = (from, radius, z, outward) => {
      // Tangent lead-in (outward) or lead-out along a half circle of radius/2.
      const lead = radius / 2;
      const points = [];
      for (let index = 0; index <= 24; index += 1) {
        const angle = outward
          ? Math.PI + direction * Math.PI * (index / 24)
          : direction * Math.PI * (index / 24);
        points.push({ x: from.x + lead + lead * Math.cos(angle), y: from.y + lead * Math.sin(angle), z });
      }
      return points;
    };
    this.stats.pocketCount += 1;
    let currentZ = center.z;
    for (let loop = 0; loop < loops; loop += 1) {
      if (Number.isFinite(depth)) {
        currentZ = state.incremental ? currentZ + depth : depth;
        this.moveProgram({ x: center.x, y: center.y, z: currentZ }, { rapid: false, kind: "pocket", extra });
      }
      let previousRadius = 0;
      for (const radius of radii) {
        const pathRadius = radius - toolRadius;
        if (pathRadius <= this.resolution) {
          this.warn(`${this.rowLabel()}: ${code} radius ${radius} is not larger than the tool radius ${formatNumber(toolRadius, this.resolution)}.`);
          continue;
        }
        if (!previousRadius) {
          this.movePolyline(halfCircle(center, pathRadius, currentZ, true), { kind: "pocket", extra });
        } else {
          this.movePolyline([
            { x: center.x + previousRadius, y: center.y, z: currentZ },
            { x: center.x + pathRadius, y: center.y, z: currentZ }
          ], { kind: "pocket", extra });
        }
        const circle = [];
        for (let index = 0; index <= 144; index += 1) {
          const angle = direction * Math.PI * 2 * (index / 144);
          circle.push({ x: center.x + pathRadius * Math.cos(angle), y: center.y + pathRadius * Math.sin(angle), z: currentZ });
        }
        this.movePolyline(circle, { kind: "pocket", extra });
        previousRadius = pathRadius;
      }
      if (previousRadius > 0) {
        this.movePolyline(halfCircle(center, previousRadius, currentZ, false), { kind: "pocket", extra });
      }
    }
  }

  // ----- G150 general pocket ------------------------------------------------------------------

  pocketGeometry(p, start) {
    const caller = this.frame;
    let location;
    const internal = findSequence(caller.unit, Math.round(p), caller.index);
    if (Number.isFinite(internal)) location = { unit: caller.unit, index: internal };
    else location = this.findProgram(Math.round(p));
    if (!location) return undefined;
    const points = [{ x: start.x, y: start.y }];
    let incremental = this.state.incremental;
    let motion = 1;
    const range = programRange(location.unit, location.index);
    for (let index = location.index; index < range.end; index += 1) {
      const code = stripBlockCode(location.unit.lines[index]).replace(/^N\s*\d+\s*/, "");
      if (!code || /^O\s*\d+/.test(code) || code === "%") continue;
      if (/\bM99\b/.test(code)) break;
      let parsed;
      try {
        parsed = this.readBlockWords(code);
      } catch {
        continue;
      }
      const value = (letter) => parsed.words.get(letter)?.at(-1)?.value;
      for (const g of (parsed.words.get("G") || []).map((word) => word.value)) {
        if (g === 90) incremental = false;
        if (g === 91) incremental = true;
        if (g >= 1 && g <= 3) motion = g;
      }
      const current = points[points.length - 1];
      const end = {
        x: parsed.words.has("X") ? (incremental ? current.x + value("X") : value("X")) : current.x,
        y: parsed.words.has("Y") ? (incremental ? current.y + value("Y") : value("Y")) : current.y
      };
      if (motion === 2 || motion === 3) {
        const arc = this.arcPoints({ ...current, z: 0 }, { ...end, z: 0 }, parsed.words, motion === 2, 17);
        arc.slice(1).forEach((point) => points.push({ x: point.x, y: point.y }));
      } else if (end.x !== current.x || end.y !== current.y) {
        points.push(end);
      }
    }
    return points;
  }

  executeG150(words) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    const start = {
      x: words.has("X") ? value("X") : state.prog.x,
      y: words.has("Y") ? value("Y") : state.prog.y
    };
    const p = value("P");
    const q = Math.abs(value("Q") ?? NaN);
    const finalZ = value("Z");
    if (words.has("R")) state.lastPocketR = value("R");
    const rPlane = state.lastPocketR;
    if (!Number.isFinite(p) || !Number.isFinite(finalZ) || !(q > 0) || !Number.isFinite(start.x) || !Number.isFinite(start.y)) {
      this.warn(`${this.rowLabel()}: G150 needs P (geometry), Q (depth per pass), Z, and a start X/Y.`);
      return;
    }
    const outline = this.pocketGeometry(p, start);
    if (!outline || outline.length < 3) {
      this.error(`${this.rowLabel()}: G150 pocket geometry P${p} was not found (alarm 314).`, this.currentLine());
      return;
    }
    const d = words.has("D") ? Math.trunc(value("D")) : state.compD;
    const toolRadius = this.compensationRadius(d);
    const finish = Math.abs(value("K") || 0);
    const stepX = words.has("I") ? Math.abs(value("I")) : undefined;
    const stepY = words.has("J") ? Math.abs(value("J")) : undefined;
    const rough = offsetPolygonInward(outline, toolRadius + finish);
    const finishPath = offsetPolygonInward(outline, toolRadius);
    const extra = { cycle: "G150" };
    this.stats.pocketCount += 1;
    this.warn(`${this.rowLabel()}: G150 is drawn with an approximate strategy (outline, zig-zag clear, finish pass); the control's exact passes differ.`);
    // Equal passes no deeper than Q from the R plane down to Z.
    const rStart = Number.isFinite(rPlane) ? rPlane : state.prog.z;
    const passCount = Math.min(10000, Math.max(1, Math.ceil((rStart - finalZ) / q - 1e-9)));
    const levels = Array.from({ length: passCount }, (_, index) =>
      index === passCount - 1 ? finalZ : rStart - ((rStart - finalZ) * (index + 1)) / passCount);
    this.moveProgram({ x: start.x, y: start.y }, { rapid: true, kind: "rapid", extra });
    if (Number.isFinite(rPlane)) this.moveProgram({ z: rPlane }, { rapid: true, kind: "rapid", extra });
    const axis = stepY ? "x" : "y";
    const step = stepY || stepX;
    const cutClosed = (polygon, z) => {
      if (polygon.length < 2) return;
      const points = polygon.map((point) => ({ x: point.x, y: point.y, z }));
      points.push({ ...points[0] });
      this.moveProgram({ x: points[0].x, y: points[0].y }, { rapid: false, kind: "pocket", extra });
      this.movePolyline(points, { kind: "pocket", extra });
    };
    for (const z of levels) {
      this.moveProgram({ x: start.x, y: start.y }, { rapid: false, kind: "pocket", extra });
      this.moveProgram({ z }, { rapid: false, kind: "pocket", extra });
      cutClosed(rough, z);
      if (!(step > 0) || rough.length < 3) continue;
      const cross = axis === "x" ? "y" : "x";
      const values = rough.map((point) => point[cross]);
      const high = Math.max(...values);
      let forward = true;
      let previous = { x: state.prog.x, y: state.prog.y };
      for (let level = Math.min(...values) + step / 2; level < high; level += step) {
        const spans = scanPolygon(rough, axis, level);
        for (const [from, to] of forward ? spans : spans.slice().reverse()) {
          const a = axis === "x" ? { x: forward ? from : to, y: level } : { x: level, y: forward ? from : to };
          const b = axis === "x" ? { x: forward ? to : from, y: level } : { x: level, y: forward ? to : from };
          const middle = { x: (previous.x + a.x) / 2, y: (previous.y + a.y) / 2 };
          if (pointInPolygon(middle, rough)) {
            this.movePolyline([{ ...previous, z }, { ...a, z }], { kind: "pocket", extra });
          } else {
            const clearZ = Number.isFinite(rPlane) ? rPlane : z;
            this.moveProgram({ z: clearZ }, { rapid: true, kind: "rapid", extra });
            this.moveProgram({ x: a.x, y: a.y }, { rapid: true, kind: "rapid", extra });
            this.moveProgram({ z }, { rapid: false, kind: "pocket", extra });
          }
          this.movePolyline([{ ...a, z }, { ...b, z }], { kind: "pocket", extra });
          previous = b;
        }
        forward = !forward;
      }
    }
    cutClosed(finishPath, finalZ);
    if (Number.isFinite(rPlane)) this.moveProgram({ z: rPlane }, { rapid: true, kind: "rapid", extra });
  }

  // ----- G47 engraving --------------------------------------------------------------------------

  executeEngraving(words, comments) {
    const state = this.state;
    const value = (letter) => words.get(letter)?.at(-1)?.value;
    if (state.plane !== 17) this.warn(`${this.rowLabel()}: G47 engraving on a mill is only in the G17 plane.`);
    const p = Math.trunc(value("P") ?? 0);
    const comment = comments[0] || "";
    let text = "";
    if (p === 0) {
      text = comment;
    } else if (p === 1) {
      const digits = (comment.match(/#/g) || []).length;
      if (!digits && /\d/.test(comment)) {
        // A number in place of the # marks sets the next serial number.
        this.setVariable(599, Number(comment.replace(/\D/g, "")));
        return;
      }
      const serial = Math.trunc(this.getVariable(599) ?? 1);
      const width = Math.max(1, digits);
      text = comment.replace(/#+/, String(serial).padStart(width, "0").slice(-width));
      this.setVariable(599, serial + 1);
    } else if (p === 2) {
      const pad = (number) => String(number).padStart(2, "0");
      text = `${pad(this.now.getMonth() + 1)}/${pad(this.now.getDate())}/${this.now.getFullYear()} ` +
        `${pad(this.now.getHours())}:${pad(this.now.getMinutes())}:${pad(this.now.getSeconds())}`;
    } else if (p >= 32 && p <= 126) {
      text = String.fromCharCode(p);
    } else {
      this.warn(`${this.rowLabel()}: G47 P${p} is not supported.`);
      return;
    }
    if (!text) {
      this.warn(`${this.rowLabel()}: G47 has no text to engrave.`);
      return;
    }
    const origin = { x: words.has("X") ? value("X") : state.prog.x, y: words.has("Y") ? value("Y") : state.prog.y };
    if (!Number.isFinite(origin.x) || !Number.isFinite(origin.y)) {
      this.warn(`${this.rowLabel()}: G47 needs an X/Y start.`);
      return;
    }
    const depth = words.has("Z") ? value("Z") : state.prog.z;
    const returnPlane = words.has("R") ? value("R") : state.prog.z;
    const plungeFeed = words.has("E") ? value("E") : state.feed;
    const feed = words.has("F") ? value("F") : state.feed;
    const { strokes, unknown } = strokeText(text, {
      x: origin.x,
      y: origin.y,
      height: words.has("J") ? value("J") : 1,
      angle: value("I") || 0
    });
    if (unknown.length) this.warn(`${this.rowLabel()}: G47 characters ${unknown.join(" ")} are drawn as "?".`);
    this.stats.engravedCharacters += [...text].length;
    const extra = { cycle: "G47", text };
    for (const stroke of strokes) {
      if (stroke.length < 2) continue;
      this.moveProgram({ z: returnPlane }, { rapid: true, kind: "rapid", extra });
      this.moveProgram({ x: stroke[0].x, y: stroke[0].y }, { rapid: true, kind: "rapid", extra });
      this.moveProgram({ z: depth }, { rapid: false, kind: "engrave", feed: plungeFeed, extra });
      this.movePolyline(stroke.map((point) => ({ x: point.x, y: point.y, z: depth })), { kind: "engrave", feed, extra });
    }
    this.moveProgram({ z: returnPlane }, { rapid: true, kind: "rapid", extra });
  }

  // ----- macro calls ----------------------------------------------------------------------------

  macroArguments(parsed) {
    const locals = new Map();
    const scale = this.state.units === "inch" ? 0.0001 : 0.001;
    let ijkSet = 0;
    const ijkSeen = new Set();
    for (const word of parsed.order) {
      const letter = word.letter;
      if (["G", "L", "N", "O", "P"].includes(letter)) continue;
      // FANUC words are already in least increments when they have no
      // decimal point (see readBlockWords).
      const number = this.fanuc || word.decimal || WHOLE_UNIT_ARGUMENTS.has(letter) ? word.value : word.value * scale;
      if ("IJK".includes(letter)) {
        if (ijkSeen.has(letter)) {
          ijkSet += 1;
          ijkSeen.clear();
        }
        ijkSeen.add(letter);
        const variable = 4 + ijkSet * 3 + "IJK".indexOf(letter);
        if (variable <= 33) locals.set(variable, number);
        continue;
      }
      const variable = ARGUMENT_VARIABLES[letter];
      if (variable) locals.set(variable, number);
    }
    return locals;
  }

  // modal: false (G65), true or "A" (G66: call after each move), "B" (FANUC
  // G66.1: every block is a call).
  executeMacroCall(parsed, modal) {
    const value = (letter) => parsed.words.get(letter)?.at(-1)?.value;
    const p = value("P");
    const target = Number.isFinite(p) ? Math.round(p) : (this.lastComment || "").trim();
    const code = modal === "B" ? "G66.1" : modal ? "G66" : "G65";
    if (target === "") {
      this.warn(`${this.rowLabel()}: ${code} needs a P program number.`);
      return;
    }
    const repeat = Math.max(0, Math.trunc(value("L") ?? 1));
    const locals = this.macroArguments(parsed);
    if (modal) {
      this.state.modalMacro = { target, locals, repeat, type: modal === "B" ? "B" : "A" };
      return;
    }
    this.callSubprogram("G65", target, { repeat, locals, label: "G65 P" });
  }

  // Modal macro calls (G66/G66.1) do not apply inside the called macro.
  insideModalMacro() {
    return this.stack.some((frame) => frame.kind === "G66" || frame.kind === "G66.1");
  }

  executeModalMacro() {
    const macro = this.state.modalMacro;
    this.callSubprogram("G66", macro.target, { repeat: macro.repeat, locals: new Map(macro.locals), label: "G66 P" });
  }

  // ----- main loop ------------------------------------------------------------------------------

  elapsedSeconds() {
    let seconds = this.pendingDwell;
    for (const segment of this.segments) {
      seconds += (segment.estimatedSeconds || 0) + (segment.dwellAfterSeconds || 0);
    }
    return seconds;
  }

  run() {
    this.pushFrame(this.mainUnit, 0, { kind: "main", locals: this.mainLocals });
    let steps = 0;
    while (this.frame && !this.stopRequested) {
      const frame = this.frame;
      const unit = frame.unit;
      const outOfRange = frame.index >= unit.lines.length ||
        (this.stack.length > 1 && frame.index >= programRange(unit, frame.entryIndex).end);
      if (outOfRange) {
        if (this.stack.length > 1) {
          this.warn(`${frame.kind} subprogram ended without M99; returning.`);
          this.returnFromSubprogram();
          continue;
        }
        break;
      }
      steps += 1;
      if (steps > this.maxSteps) {
        this.error(`Macro execution stopped after ${this.maxSteps.toLocaleString("en-US")} blocks to prevent an endless loop.`);
        break;
      }
      frame.jumped = false;
      try {
        this.executeLine(unit.lines[frame.index]);
      } catch (error) {
        this.warn(`${this.rowLabel()}: ${error.message}`);
      }
      if (this.frame === frame && !frame.jumped) frame.index += 1;
    }
    this.frame = this.stack[0];
    this.flushCornerBlend();
    return this.buildModel();
  }

  // ----- cutter compensation overlay ------------------------------------------------------------

  buildCompensation() {
    const compensated = [];
    const issues = [];
    const runs = [];
    let current;
    this.segments.forEach((segment, executionIndex) => {
      segment.executionIndex = executionIndex;
      const phase = segment.compensationPhase;
      const eligible = phase && phase !== "g141" && Array.isArray(segment.planePoints) &&
        segment.toPart && segment.plane === 17;
      if (!eligible) {
        if (current) runs.push(current);
        current = undefined;
        return;
      }
      if (phase === "startup" && current) {
        runs.push(current);
        current = undefined;
      }
      current ||= [];
      current.push(segment);
      if (phase === "cancel") {
        runs.push(current);
        current = undefined;
      }
    });
    if (current) runs.push(current);

    for (const run of runs) {
      const pieces = [];
      for (const segment of run) {
        let side = segment.compensationSide === "right" ? -1 : 1;
        if (segment.mirrored) side = -side;
        const radius = (segment.compensationRadius || 0) * side;
        for (let index = 1; index < segment.planePoints.length; index += 1) {
          const a = segment.planePoints[index - 1];
          const b = segment.planePoints[index];
          const length = Math.hypot(b.x - a.x, b.y - a.y);
          if (length < 1e-10) continue;
          pieces.push({
            a, b, length,
            direction: { x: (b.x - a.x) / length, y: (b.y - a.y) / length },
            radius,
            segment,
            phase: segment.compensationPhase
          });
        }
      }
      if (!pieces.length) continue;
      const offsetPoint = (point, piece) => ({
        x: point.x - piece.direction.y * piece.radius,
        y: point.y + piece.direction.x * piece.radius,
        z: point.z
      });
      const outputs = new Map();
      const push = (segment, point) => {
        if (!outputs.has(segment)) outputs.set(segment, []);
        const list = outputs.get(segment);
        if (!list.length || !samePoint(list[list.length - 1], point, 1e-9)) list.push(point);
      };
      let previousEnd;
      pieces.forEach((piece, index) => {
        const next = pieces[index + 1];
        const startup = piece.phase === "startup";
        const cancel = piece.phase === "cancel";
        let start = startup ? { ...piece.a } : previousEnd || offsetPoint(piece.a, piece);
        let end = cancel ? { ...piece.b } : offsetPoint(piece.b, piece);
        if (startup && next && next.phase !== "startup") {
          // Type A start-up: the offset vector at the end of the start-up
          // block is perpendicular to the next move.
          end = offsetPoint(piece.b, next);
        } else if (!startup && !cancel && next && next.phase !== "cancel" && next.phase !== "startup") {
          const cross = piece.direction.x * next.direction.y - piece.direction.y * next.direction.x;
          const dot = piece.direction.x * next.direction.x + piece.direction.y * next.direction.y;
          const convex = piece.radius > 0 ? cross < -1e-9 : cross > 1e-9;
          if (Math.abs(cross) <= 1e-9 && dot > 0) {
            // Collinear continuation.
          } else if (convex && Math.abs(piece.radius) > 0) {
            push(piece.segment, start);
            push(piece.segment, end);
            this.checkReversal(piece, start, end, issues);
            const vertex = piece.b;
            const radius = Math.abs(piece.radius);
            const nextStart = offsetPoint(next.a, next);
            const a0 = Math.atan2(end.y - vertex.y, end.x - vertex.x);
            let sweep = Math.atan2(nextStart.y - vertex.y, nextStart.x - vertex.x) - a0;
            if (piece.radius > 0) {
              while (sweep > 0) sweep -= Math.PI * 2;
            } else {
              while (sweep < 0) sweep += Math.PI * 2;
            }
            const steps = Math.max(2, Math.ceil(Math.abs(sweep) / (Math.PI / 36)));
            for (let step = 1; step <= steps; step += 1) {
              const angle = a0 + (sweep * step) / steps;
              push(piece.segment, {
                x: vertex.x + Math.cos(angle) * radius,
                y: vertex.y + Math.sin(angle) * radius,
                z: lerp(end.z, nextStart.z, step / steps)
              });
            }
            previousEnd = nextStart;
            return;
          } else {
            const intersection = lineIntersection2(start, piece.direction, offsetPoint(next.a, next), next.direction);
            if (intersection) end = { x: intersection.x, y: intersection.y, z: piece.b.z };
          }
        }
        push(piece.segment, start);
        push(piece.segment, end);
        this.checkReversal(piece, start, end, issues);
        previousEnd = end;
      });
      for (const [segment, planePoints] of outputs) {
        if (planePoints.length < 2) continue;
        const points = planePoints.map((point) => kinematics.transformPoint(segment.toPart, point));
        compensated.push({
          line: segment.line,
          raw: segment.raw,
          tool: segment.tool,
          kind: "compensated",
          sourceKind: segment.kind,
          executionIndex: segment.executionIndex,
          compensationPhase: segment.compensationPhase,
          compensationInterference: Boolean(segment.compensationInterference),
          points,
          start: points[0],
          end: points[points.length - 1],
          units: segment.units,
          feed: segment.feed,
          feedMode: segment.feedMode,
          toolRadius: segment.compensationRadius,
          rotary: segment.rotary,
          toolLength: segment.toolLength,
          estimatedSeconds: segment.estimatedSeconds
        });
      }
    }
    return { compensated, issues };
  }

  checkReversal(piece, start, end, issues) {
    if (piece.phase === "startup" || piece.phase === "cancel" || !Math.abs(piece.radius)) return;
    const along = (end.x - start.x) * piece.direction.x + (end.y - start.y) * piece.direction.y;
    if (along >= -1e-7) return;
    const segment = piece.segment;
    if (segment.compensationInterference) return;
    segment.compensationInterference = true;
    const message = `Cutter compensation interference at row ${segment.line} (T${segment.tool}, D${segment.compensationD ?? "?"}): the ${formatNumber(Math.abs(piece.radius), this.resolution)} offset reverses the ${formatNumber(piece.length, this.resolution)} programmed move; the control alarms or gouges.`;
    issues.push({
      line: segment.line,
      severity: "error",
      message,
      radius: Math.abs(piece.radius),
      programmedLength: piece.length,
      tool: segment.tool,
      d: segment.compensationD,
      block: `row ${segment.line}`
    });
  }

  // One error per D register when many rows interfere; the rows keep their
  // own issues for the editor markers.
  summarizeCompensationIssues(issues) {
    const groups = new Map();
    for (const issue of issues) {
      const key = `${issue.tool}|${issue.d}|${issue.radius}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(issue);
    }
    const summaries = [];
    for (const group of groups.values()) {
      if (group.length < 3) {
        summaries.push(...group);
        continue;
      }
      const first = group[0];
      const shortest = Math.min(...group.map((issue) => issue.programmedLength));
      const dLabel = `D${first.d ?? "?"}`;
      const assumed = Number.isInteger(first.d) && this.assumedDOffsets.has(first.d) && !Number.isFinite(this.globals.get(this.offsetVariable("dGeometry", first.d)));
      const hint = assumed
        ? ` ${dLabel} uses the tool diameter because #${this.offsetVariable("dGeometry", first.d)} is not set. If the post outputs tool-centre paths for wear compensation, set #${this.offsetVariable("dGeometry", first.d)}=0 in "Initial # vars".`
        : "";
      summaries.push({
        line: first.line,
        severity: "error",
        summary: true,
        message: `Cutter compensation interference at ${group.length} rows (T${first.tool}, ${dLabel}), first at row ${first.line}: the ${formatNumber(first.radius, this.resolution)} offset reverses programmed moves as short as ${formatNumber(shortest, this.resolution)}; the control alarms or gouges.${hint}`
      });
    }
    return summaries;
  }

  // ----- model --------------------------------------------------------------------------------

  formattedWarnings() {
    return [...this.warningEntries.values()].map(({ message, count }) =>
      count > 1 ? `${message} (${count - 1} more row${count === 2 ? "" : "s"} like this)` : message);
  }

  buildModel() {
    const state = this.state;
    const { compensated, issues } = this.buildCompensation();
    let motionSeconds = 0;
    let dwellSeconds = this.pendingDwell;
    let unestimated = 0;
    const emptyBounds = () => ({ minX: Infinity, maxX: -Infinity, minY: Infinity, maxY: -Infinity, minZ: Infinity, maxZ: -Infinity });
    const bounds = emptyBounds();
    const cutBounds = emptyBounds();
    const extend = (target, point) => {
      if (point.x < target.minX) target.minX = point.x;
      if (point.x > target.maxX) target.maxX = point.x;
      if (point.y < target.minY) target.minY = point.y;
      if (point.y > target.maxY) target.maxY = point.y;
      if (point.z < target.minZ) target.minZ = point.z;
      if (point.z > target.maxZ) target.maxZ = point.z;
    };
    this.segments.forEach((segment, index) => {
      segment.executionIndex = index;
      if (Number.isFinite(segment.estimatedSeconds)) motionSeconds += segment.estimatedSeconds;
      else unestimated += 1;
      if (segment.dwellAfterSeconds) {
        dwellSeconds += segment.dwellAfterSeconds;
        segment.estimatedSeconds = (segment.estimatedSeconds || 0) + segment.dwellAfterSeconds;
      }
      delete segment.planePoints;
      delete segment.toPart;
      if (segment.kind === "tool-change" || segment.kind === "home") return;
      for (const point of segment.points) {
        extend(bounds, point);
        if (segment.kind !== "rapid") extend(cutBounds, point);
      }
    });
    compensated.forEach((segment) => segment.points.forEach((point) => {
      extend(bounds, point);
      extend(cutBounds, point);
    }));
    if (!Number.isFinite(bounds.minX)) {
      Object.assign(bounds, { minX: 0, maxX: 1, minY: 0, maxY: 1, minZ: 0, maxZ: 1 });
    }
    if (this.defaultedOffsets.size) {
      this.warn(`Work offsets ${[...this.defaultedOffsets].join(", ")} are not defined, so they are placed at the ${this.fanuc ? "rotary centre" : "MRZP"}; machine positions and travel checks need their values (for example #5221=-300,#5222=-200,#5223=-400 in "Initial # vars").`);
    }
    const definitionName = this.machine.sourcePath ? path.basename(this.machine.sourcePath) : "the machine definition";
    if (this.unknownParameters.size) {
      const list = [...this.unknownParameters].sort((a, b) => parseFloat(a) - parseFloat(b)).map((key) => (this.fanuc ? key : `#${key}`));
      this.warn(this.fanuc
        ? `Macros read PRM[] parameters ${list.join(", ")}, which are not in parameters of ${definitionName}; the preview reads 0.`
        : `Macros read machine parameters ${list.join(", ")}, which are not in parameterVariables of ${definitionName}; the preview reads 0.`);
    }
    if (this.unsetSettings.size) {
      const list = [...this.unsetSettings].sort((a, b) => a - b);
      this.warn(`Macros read Settings ${list.join(", ")}, which are not in controlSettings of ${definitionName}; the preview reads 0 (OFF).`);
    }
    const tilted = this.segments.some((segment) =>
      Math.abs(segment.rotary.b0 || 0) + Math.abs(segment.rotary.b1 || 0) +
      Math.abs(segment.rotary.c0 || 0) + Math.abs(segment.rotary.c1 || 0) > 1e-9);
    const unverified = [
      this.machine.mrzp?.verified === false ? this.machine.mrzp.label || "MRZP (Settings 255-257)" : undefined,
      this.machine.rotaryConvention?.verified === false ? "rotary directions" : undefined
    ].filter(Boolean);
    if (tilted && unverified.length) {
      this.warn(`${this.machine.name}: the ${unverified.join(" and ")} in ${definitionName} ${unverified.length > 1 ? "are unverified placeholders" : "is an unverified placeholder"}; confirm before relying on tilted-plane positions.`);
    }
    if (this.programEnd === undefined && !this.segmentBudgetExceeded && !this.stopRequested) {
      this.warn("The program has no M30 or M02 end.");
    }
    if (this.errorsTruncated) {
      this.errors.push({ message: `${this.errorsTruncated} more preview errors were not listed.` });
    }
    const summaries = this.summarizeCompensationIssues(issues);
    const errors = [...new Set([...this.errors.map((entry) => entry.message), ...summaries.map((issue) => issue.message)])];
    const compensationIssues = [
      ...issues,
      ...summaries.filter((issue) => issue.summary),
      ...this.errors.filter((entry) => Number.isFinite(entry.line)).map((entry) => ({
        line: entry.line,
        severity: "error",
        message: entry.message
      }))
    ];
    const variables = {};
    for (const [id, value] of [...this.globals.entries()].sort((a, b) => a[0] - b[0])) {
      // Common variables: FANUC #100-#999 (and #98000+); Haas #10000-#10999
      // with the legacy three-digit ranges shown by their short numbers.
      if (this.fanuc) {
        if ((id >= 100 && id <= 999) || (id >= 98000 && id <= 98999)) variables[id] = value;
        continue;
      }
      if (id < 10000 || id > 10999) continue;
      const legacy = id - 10000;
      const displayId = LEGACY_GLOBAL_RANGES.some(([first, last]) => legacy >= first && legacy <= last) ? legacy : id;
      variables[displayId] = value;
    }
    for (const [id, value] of [...this.mainLocals.entries()].sort((a, b) => a[0] - b[0])) {
      variables[id] = value;
    }
    const tools = [...this.tools].filter((number) => number > 0).sort((a, b) => a - b);
    const toolDefinitions = tools.map((number) => this.toolDefinition(number));
    const cycleSummary = Object.entries(this.stats.cycleCounts)
      .filter(([, count]) => count > 0)
      .map(([code, count]) => `${code}×${count}`)
      .join(", ");
    const estimatedCycleSeconds = motionSeconds + dwellSeconds;
    const externalPrograms = [...this.externalPrograms].map(([name, location]) => ({ name, location }));
    const fromMemory = externalPrograms.filter((program) => program.location === "memory").length;
    const externalSummary = externalPrograms.length
      ? [fromMemory ? `${fromMemory} from machine memory` : "", externalPrograms.length - fromMemory ? `${externalPrograms.length - fromMemory} from the program folder` : ""]
        .filter(Boolean).join(", ")
      : "—";
    const statsRows = [
      ["NC lines", this.mainUnit.lines.length],
      ["Executed blocks", this.stats.executedBlockCount],
      ["Macro jumps", this.stats.jumpCount],
      ["Subprogram calls", this.stats.callCount],
      ["External programs", externalSummary],
      ["Path segments", this.segments.length],
      ["Tool changes", this.stats.toolChangeCount],
      ["Holes", this.stats.holeCount],
      ["Cycles", cycleSummary || "—"],
      ["Pockets (G12/G13/G150)", this.stats.pocketCount],
      ["Engraved characters", this.stats.engravedCharacters],
      ["Work offsets", [...this.stats.workOffsets].join(", ")],
      ["B/C orientations", this.stats.rotaryPositions.size],
      this.fanuc
        ? ["TCP / tilted-plane moves", `${this.segments.filter((segment) => segment.tcpc).length} / ${this.segments.filter((segment) => segment.featureFrame).length}`]
        : ["TCPC / DWO moves", `${this.segments.filter((segment) => segment.tcpc).length} / ${this.segments.filter((segment) => segment.dwo).length}`],
      ["Estimated cycle", estimatedCycleSeconds, "duration"],
      ["Tool change time", this.stats.toolChangeSeconds, "duration"],
      ["Dwell time", this.stats.dwellSeconds, "duration"],
      ["Unestimated moves", unestimated],
      ["Compensated paths", compensated.length],
      ["Preview errors", errors.length],
      ["Tools found", tools.length],
      ["Units", state.units]
    ];
    return {
      kind: "mill",
      coordinateSystem: "xyz",
      segments: this.segments,
      compensatedSegments: compensated,
      tools,
      toolDefinitions,
      toolRadii: Object.fromEntries(toolDefinitions.map((definition) => [
        definition.number,
        Number.isFinite(definition.diameter) ? definition.diameter / 2 : 0
      ])),
      toolTable: {
        name: this.options.toolTable?.name || "NC comment inference",
        units: this.options.toolTable?.units || state.units,
        sourcePath: ""
      },
      errors,
      compensationIssues,
      warnings: this.formattedWarnings(),
      bounds,
      cutBounds: Number.isFinite(cutBounds.minX) ? cutBounds : undefined,
      // Drawing frame: the first work offset used, in machine coordinates.
      frame: { ...this.displayOffset(), units: state.units },
      stock: undefined,
      settings: {
        initialVariables: this.options.initialVariables || "",
        controlSettings: this.settings
      },
      macroVariables: variables,
      workOffsetTable: this.workOffsetTable(),
      statsRows,
      meta: {
        lineCount: this.mainUnit.lines.length,
        segmentCount: this.segments.length,
        compensatedSegmentCount: compensated.length,
        executedBlockCount: this.stats.executedBlockCount,
        jumpCount: this.stats.jumpCount,
        callCount: this.stats.callCount,
        externalPrograms,
        toolChangeCount: this.stats.toolChangeCount,
        holeCount: this.stats.holeCount,
        pocketCount: this.stats.pocketCount,
        engravedCharacters: this.stats.engravedCharacters,
        motionSeconds,
        dwellSeconds,
        toolChangeSeconds: this.stats.toolChangeSeconds,
        estimatedCycleSeconds,
        unestimatedSegmentCount: unestimated,
        units: state.units,
        workOffsets: [...this.stats.workOffsets],
        defaultedWorkOffsets: [...this.defaultedOffsets],
        orientations: [...this.stats.rotaryPositions],
        programEnd: this.programEnd === undefined ? null : `M${String(this.programEnd).padStart(2, "0")}`,
        externalSubprograms: [...this.externalUnits.values()].map((unit) => unit.name)
      }
    };
  }
}

function parseHaasMillProgram(source, options = {}) {
  return new HaasMillInterpreter(source, options).run();
}

module.exports = {
  millToolDefinitionsForSource,
  offsetPolygonInward,
  parseHaasMillProgram,
  parseMillToolComments,
  resolveMillToolDefinitions
};
