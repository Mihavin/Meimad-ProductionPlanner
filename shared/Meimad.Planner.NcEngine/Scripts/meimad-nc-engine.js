"use strict";

// Meimad adapter around the vendored Chevalier NC engine. The .NET host calls these functions
// with JSON strings and receives JSON strings back, so no engine object crosses the boundary.
//
//   analyze(request)      cycle-time breakdown the Meimad Server turns into an NC analysis
//   parsePreview(request) the viewer model, packed by media/model-transport.js (takePacked())
//   toolTable(request)    in-memory tool table inferred from the program (no file writes)
//   saveToolTable(request) validated manual tool table edits
//
// The machine registry is the vendored one plus the Meimad definitions in meimad/machines and
// meimad/controls (Mazak Variaxis i-500, Okuma Genos L200E-M, Haas ST-25Y, Haas VF-3SS, generic
// FANUC 0i-MC mills). Programs for controls that the interpreters do not read natively are
// translated first (meimad-dialects.js); lathe programs are executed by the Meimad custom macro
// executor (meimad-macro.js: variables, WHILE/IF/GOTO, G65/G66/M98/M97/M99 and custom macro
// calls), because the lathe interpreter reads only simple assignments and IF..GOTO. Both keep a
// line map so every segment, issue and message reports the row of the original program. The mill
// interpreter executes macros itself; mill-hooks below add the stops (M00/M01/#3006), the called
// program of every segment and FANUC custom macro calls by G/M/T codes, without changing the
// vendored files. The viewer model carries the stops (meimadStops), the called programs'
// texts (meimadUnits) and macro print output (meimadPrints).
//
// The tool-table rules mirror desktop/main.js of the upstream desktop application, which is
// not vendored because it is Electron code.

const path = require("node:path");
const { parseProgramForMachine, toolDefinitionsForMachine } = require("../src/program-model");
const { parseHaasMillProgram } = require("../src/haas-mill");
const { initialiseVariables } = require("../src/macro");
const { parseCncParameters } = require("../src/machine-parameters");
const { createSubprogramResolver } = require("../src/subprograms");
const {
  AUTO_MACHINE_ID,
  loadRegistry,
  detectMachineForSource,
  machineSummaries,
  sanitizeWorkOffsets
} = require("../src/machines");
const {
  LATHE_TOOL_TYPES,
  MILL_TOOL_TYPES,
  TOOL_HANDS,
  TOOL_TYPES,
  parseToolTable,
  serializeToolTable
} = require("../src/tool-table");
const transport = require("../media/model-transport");
const dialects = require("./meimad-dialects");
const macro = require("./meimad-macro");

const MM_PER_INCH = 25.4;
const RAPID_KINDS = new Set(["rapid", "home", "tool-change", "g30"]);
const MAX_REPORTED_MESSAGES = 60;
const MAX_MESSAGE_CHARACTERS = 400;

// Meimad Machine NC dialect -> the engine controls whose machines read that dialect, in order
// of preference, per machine type. The dialect only chooses a machine when the program does
// not name one and the Meimad Machine has no NC viewer machine configured.
const CONTROLS_BY_DIALECT = Object.freeze({
  HAAS_NGC: { mill: ["haas-ngc-mill"], lathe: ["haas-classic-lathe"] },
  FANUC_MACRO_B: { mill: ["fanuc-31i-b-plus-mill", "fanuc-0i-mc-mill"], lathe: ["fanuc-0i-tf-lathe"] },
  MAZAK_MATRIX_EIA: { mill: ["mazak-matrix2-mill"], lathe: ["fanuc-0i-tf-lathe"] },
  OKUMA_OSP: { mill: ["fanuc-31i-b-plus-mill"], lathe: ["okuma-osp-p200l-lathe"] }
});

let registryCache;
let parameterCache;
let lastPacked = "";

function registry() {
  if (!registryCache) {
    const loaded = loadRegistry({
      machineFolders: [path.join(__dirname, "machines")],
      controlFolders: [path.join(__dirname, "controls")]
    });
    if (loaded.errors.length) {
      throw new Error(`Machine definitions are invalid: ${loaded.errors.join(" | ")}`);
    }
    registryCache = loaded;
  }
  return registryCache;
}

function machineParameters(text) {
  if (typeof text !== "string" || !text.trim()) return undefined;
  if (parameterCache && parameterCache.text === text) return parameterCache.profile;
  const profile = parseCncParameters(text);
  parameterCache = { text, profile };
  return profile;
}

function machineForControls(type, controls) {
  const machines = [...registry().machines.values()].filter((machine) => machine.type === type);
  for (const control of controls) {
    const candidates = machines
      .filter((machine) => machine.control === control)
      .sort((left, right) => Number(Boolean(right.detection?.default)) - Number(Boolean(left.detection?.default)) ||
        left.id.localeCompare(right.id));
    if (candidates.length) return candidates[0];
  }
  return undefined;
}

// The explicit choice wins (the Meimad Machine's NC viewer machine, or the viewer's selection).
// For "auto", a (MACHINE: ...) program comment wins, then the engine decides lathe or mill from
// the program: a detected machine that reads the Meimad NC dialect is kept, otherwise the
// dialect's preferred control picks the machine. Returns { selection, reason }.
function resolveSelection(request) {
  const machines = registry().machines;
  const requested = String(request.machineSelection || AUTO_MACHINE_ID);
  if (requested !== AUTO_MACHINE_ID && machines.has(requested)) {
    return { selection: requested, reason: "Selected machine" };
  }
  const dialect = String(request.dialect || "").toUpperCase();
  const detection = detectMachineForSource(request.text, registry());
  const detected = machines.get(detection.id);
  // detectMachineForSource reports an explicit (MACHINE: ...) comment with this reason; its
  // confidence is also 1 for any one-sided evidence, so the reason is the only marker.
  const explicitTag = String(detection.reason || "").startsWith("Program comment");
  const unknownReason = requested !== AUTO_MACHINE_ID
    ? `NC viewer machine "${requested}" is not installed; ${detection.reason}`
    : detection.reason;
  if (!dialect || !detected || explicitTag || !CONTROLS_BY_DIALECT[dialect]) {
    return { selection: AUTO_MACHINE_ID, reason: unknownReason };
  }
  const preferred = CONTROLS_BY_DIALECT[dialect][detected.type] || [];
  if (preferred.includes(detected.control)) {
    return { selection: AUTO_MACHINE_ID, reason: unknownReason };
  }
  const machine = machineForControls(detected.type, preferred);
  if (!machine) return { selection: AUTO_MACHINE_ID, reason: unknownReason };
  return { selection: machine.id, reason: `${unknownReason}; ${dialect} selects ${machine.name}` };
}

function machineFor(text, selection) {
  const machines = registry().machines;
  if (selection !== AUTO_MACHINE_ID && machines.has(selection)) return machines.get(selection);
  return machines.get(detectMachineForSource(text, registry()).id);
}

// The program's folder is searched by file name (O1001.nc, 1001.nc, ...) and then, like a memory
// folder, by the O number each file declares, so a subprogram released as "pocket.nc" with O1001
// is found the way the Planner's release detection finds it; the machine's memory folder follows.
// Every program a resolver finds is recorded in `units` (name -> { text, path, location }) so the
// viewer can show a called program next to the main one.
function subprogramResolverFor(request, units) {
  return (machine) => {
    const memory = machine && request.programMemory ? request.programMemory[machine.id] : undefined;
    const folders = [];
    if (typeof request.documentDirectory === "string" && request.documentDirectory.trim()) folders.push(request.documentDirectory);
    if (typeof memory === "string" && memory.trim()) folders.push(memory);
    const resolver = createSubprogramResolver(request.documentDirectory || undefined, { memoryFolders: folders });
    if (!units || typeof resolver !== "function") return resolver;
    const recording = function (target) {
      const found = resolver.call(this, target);
      if (found && !units.has(found.name)) {
        units.set(found.name, { name: found.name, text: String(found.source ?? ""), path: found.path || null, location: found.location || null, inFile: false });
      }
      return found;
    };
    for (const key of Object.keys(resolver)) recording[key] = resolver[key];
    return recording;
  };
}

// Same defaults as the desktop application: explicit settings, else the G30 reference in the
// FANUC parameter export, else 250 / 100.
function runtimeSettings(request) {
  const profileSettings = machineParameters(request.machineParametersText)?.settings || {};
  const g30X = Number(request.settings?.g30X);
  const g30Z = Number(request.settings?.g30Z);
  return {
    g30X: request.settings?.g30X !== null && Number.isFinite(g30X) ? g30X : Number(profileSettings["reference.g30X"] ?? 250),
    g30Z: request.settings?.g30Z !== null && Number.isFinite(g30Z) ? g30Z : Number(profileSettings["reference.g30Z"] ?? 100),
    initialVariables: String(request.settings?.initialVariables ?? "")
  };
}

const LATHE_DWELL_BLOCK = /^\s*(?:N\d+\s*)?G0*4(?![0-9.])\s*(?:[XUP]\s*[+-]?(?:\d+(?:\.\d*)?|\.\d+)\s*)*;?\s*$/i;

// The lathe interpreter does not implement G04: "G4 X1.5" would be drawn and timed as a feed
// move to X1.5. A dwell-only block becomes a comment (same line); the macro executor counts the
// dwell time of every executed G04 block.
function neutralizeLatheDwell(lines) {
  return lines.map((line) => {
    const code = line.replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "");
    return LATHE_DWELL_BLOCK.test(code) ? `(G04 DWELL ${code.trim().replace(/[()]/g, "")})` : line;
  });
}

function identityMap(map) {
  return map.every((entry, index) => entry.line === index + 1 && !entry.unit);
}

// The lathe interpreter stops macro execution after 20000 blocks unless the parameter profile
// says otherwise; the executed program has no loops left, so every flat line runs once.
function latheParameters(profile, lineCount) {
  const limit = Math.max(20000, lineCount + 1000);
  return {
    ...(profile || {}),
    settings: { ...(profile?.settings || {}), "macro.maximumExecutionSteps": limit, "macro.maximumLineVisits": limit }
  };
}

function initialVariablesMap(request) {
  try {
    return initialiseVariables(runtimeSettings(request).initialVariables);
  } catch {
    return new Map();
  }
}

// The program as the interpreter reads it: translated for the machine's control and, for lathes,
// executed by the custom macro executor (calls, loops and variables resolved) with dwells
// neutralized. Returns { text, map, notes, lineCount, dwellSeconds, external, translation, stops,
// prints, units }.
function prepareSource(request, machine) {
  const source = String(request.text || "");
  const sourceLines = source.split("\n");
  const translation = machine?.controlDefinition?.translation || null;
  const translated = dialects.translate(source, machine);
  const notes = [...translated.notes];
  let lines = translated.lines;
  let map = translated.map;
  let external = [];
  let dwellSeconds = 0;
  let stops = [];
  let prints = [];
  const units = new Map();
  if (machine?.type === "lathe") {
    const resolve = subprogramResolverFor(request, units)(machine);
    const executed = macro.execute({ lines, map }, {
      resolve,
      translate: (text) => dialects.translate(text, machine),
      dialect: String(machine.control || "").startsWith("haas") ? "haas" : "fanuc",
      initialVariables: initialVariablesMap(request),
      parameters: machineParameters(request.machineParametersText),
      workOffsets: sanitizeWorkOffsets(request.workOffsets || {}, registry())?.[machine.id],
      blockDelete: request.blockDelete === true
    });
    lines = executed.lines;
    map = executed.map;
    external = executed.external;
    notes.push(...executed.notes);
    dwellSeconds = executed.dwellSeconds;
    stops = executed.stops;
    prints = executed.prints;
    for (const [name, unit] of executed.units) if (!units.has(name)) units.set(name, unit);
    lines = neutralizeLatheDwell(lines);
  }
  return {
    text: lines.join("\n"),
    map,
    changed: !identityMap(map),
    notes,
    lineCount: sourceLines.length,
    dwellSeconds,
    external,
    translation,
    stops,
    prints,
    units
  };
}

function remapLine(map, line) {
  const entry = Number.isFinite(line) ? map[Math.trunc(line) - 1] : undefined;
  return entry ? entry.line : line;
}

function remapPoint(map, point) {
  return point && Number.isFinite(point.sourceLine)
    ? { ...point, sourceLine: remapLine(map, point.sourceLine) }
    : point;
}

function remapSegment(map, segment) {
  if (!segment || typeof segment !== "object") return segment;
  const entry = Number.isFinite(segment.line) ? map[Math.trunc(segment.line) - 1] : undefined;
  const result = { ...segment, line: entry ? entry.line : segment.line };
  if (entry?.unit) {
    result.sourceUnit = entry.unit;
    result.sourceLine = entry.unitLine;
    result.callDepth = entry.depth || 1;
  } else if (Number.isFinite(segment.sourceLine)) {
    result.sourceLine = remapLine(map, segment.sourceLine);
  }
  if (Array.isArray(segment.points)) result.points = segment.points.map((point) => remapPoint(map, point));
  for (const key of ["start", "end"]) {
    if (segment[key] && Number.isFinite(segment[key].sourceLine)) result[key] = remapPoint(map, segment[key]);
  }
  return result;
}

function remapMessage(map, message) {
  return typeof message === "string"
    ? message.replace(/\b([Rr]ow) (\d+)\b/g, (match, word, number) => `${word} ${remapLine(map, Number(number))}`)
    : message;
}

// Lathe models describe the interpreter's text; move every row reference back to the program.
function remapModel(model, prepared) {
  if (!prepared.changed) return model;
  const { map } = prepared;
  if (Array.isArray(model.segments)) model.segments = model.segments.map((segment) => remapSegment(map, segment));
  if (Array.isArray(model.compensatedSegments)) {
    model.compensatedSegments = model.compensatedSegments.map((segment) => remapSegment(map, segment));
  }
  if (Array.isArray(model.compensationIssues)) {
    model.compensationIssues = model.compensationIssues.map((issue) =>
      issue && Number.isFinite(issue.line) ? { ...issue, line: remapLine(map, issue.line) } : issue);
  }
  if (Array.isArray(model.warnings)) model.warnings = model.warnings.map((message) => remapMessage(map, message));
  if (Array.isArray(model.errors)) model.errors = model.errors.map((message) => remapMessage(map, message));
  if (model.meta) model.meta.lineCount = prepared.lineCount;
  // The sidebar rows were built from the interpreter's text; the line count is the program's.
  if (Array.isArray(model.statsRows)) {
    model.statsRows = model.statsRows.map((row) =>
      Array.isArray(row) && row[0] === "NC lines" ? [row[0], prepared.lineCount, ...row.slice(2)] : row);
  }
  return model;
}

// ----- mill hooks ----------------------------------------------------------------------------------
//
// The mill interpreter class is not exported. A probe program whose M98 reaches the resolver
// hands over the instance (the resolver runs as its method), and three methods of its prototype
// are wrapped once: executeLine records program stops and rewrites FANUC custom macro calls,
// emitSegment tags segments run inside a called program of the same file, and buildModel puts
// the stops and in-file program names on the model. If the probe fails the mill preview works as
// before, without stops or call panes.

let millHooks;

function stripMillComments(raw) {
  return String(raw).replace(/\([^)]*\)?/g, " ").replace(/;.*$/, " ").toUpperCase();
}

function parameterNumber(parameters, number) {
  const entry = parameters?.[String(number)];
  const value = typeof entry === "object" && entry !== null ? Number(entry.value) : Number(entry);
  return Number.isFinite(value) ? value : 0;
}

// Custom macro calls by G/M/T code (FANUC parameters 6050-6089, 6001#5) as a block rewrite.
function millCustomCall(interpreter, raw) {
  if (!interpreter.fanuc || interpreter.stack.some((frame) => frame.meimadCustom)) return undefined;
  const parameters = interpreter.parameters;
  if (!parameters) return undefined;
  const code = stripMillComments(raw);
  for (let index = 0; index < 10; index += 1) {
    const g = parameterNumber(parameters, 6050 + index);
    if (g > 0 && new RegExp(`(?<![A-Z#\\d.])G0*${g}(?![\\d.])`).test(code)) {
      return code.replace(new RegExp(`(?<![A-Z#\\d.])G0*${g}(?![\\d.])`), `G65 P${9010 + index}`);
    }
    const m = parameterNumber(parameters, 6080 + index);
    if (m > 0 && new RegExp(`(?<![A-Z#\\d.])M0*${m}(?![\\d.])`).test(code)) {
      return code.replace(new RegExp(`(?<![A-Z#\\d.])M0*${m}(?![\\d.])`), `G65 P${9020 + index}`);
    }
  }
  for (let index = 0; index < 9; index += 1) {
    const m = parameterNumber(parameters, 6071 + index);
    if (m > 0 && new RegExp(`(?<![A-Z#\\d.])M0*${m}(?![\\d.])`).test(code)) {
      return code.replace(new RegExp(`(?<![A-Z#\\d.])M0*${m}(?![\\d.])`), `M98 P${9001 + index}`);
    }
  }
  return undefined;
}

function inFileLabel(frame) {
  const code = stripMillComments(frame.unit.lines[frame.entryIndex] || "").trim().replace(/^\/\d?/, "");
  const program = code.match(/^O\s*(\d+)/);
  if (program) return `O${program[1].padStart(4, "0")}`;
  const sequence = code.match(/^N\s*(\d+)/);
  return sequence ? `N${sequence[1]}` : "Subprogram";
}

function installMillHooks() {
  if (millHooks !== undefined) return millHooks;
  millHooks = false;
  const probeMachine = [...registry().machines.values()].find((machine) => machine.type === "mill");
  let prototype;
  try {
    parseHaasMillProgram("O1000\nM98 P4321\nM30\n", {
      machine: probeMachine,
      resolveSubprogram() {
        prototype = Object.getPrototypeOf(this);
        return undefined;
      }
    });
  } catch {
    prototype = undefined;
  }
  if (!prototype || typeof prototype.executeLine !== "function" || typeof prototype.emitSegment !== "function" ||
      typeof prototype.buildModel !== "function") {
    return millHooks;
  }
  const executeLine = prototype.executeLine;
  const emitSegment = prototype.emitSegment;
  const buildModel = prototype.buildModel;
  prototype.executeLine = function meimadExecuteLine(raw) {
    this.meimadStops ||= [];
    this.meimadUnits ||= new Map();
    const frame = this.frame;
    const depth = this.stack.length;
    const stopsBefore = this.stats.stopCount;
    const errorsBefore = this.errors.length;
    const rewritten = millCustomCall(this, raw);
    executeLine.call(this, rewritten ?? raw);
    if (rewritten !== undefined && this.stack.length > depth) this.frame.meimadCustom = true;
    const stopped = this.stats.stopCount > stopsBefore;
    const alarm = this.stopRequested && this.errors.length > errorsBefore && /#3000/.test(String(raw));
    if (stopped || alarm) {
      const code = stripMillComments(raw);
      const kind = alarm ? "ALARM" : /#\s*3006\s*=/.test(code) ? "#3006" : /(?<![A-Z#\d.])M0*1(?![\d.])/.test(code) ? "M01" : "M00";
      const inside = depth > 1;
      this.meimadStops.push({
        executionIndex: this.segments.length,
        kind,
        message: this.lastComment || "",
        line: inside ? this.stack[1]?.callLine ?? frame.index + 1 : frame.index + 1,
        unit: inside ? (frame.unit.external ? frame.unit.name : inFileLabel(frame)) : null,
        unitLine: inside ? frame.index + 1 : null
      });
    }
  };
  prototype.emitSegment = function meimadEmitSegment(...args) {
    const segment = emitSegment.apply(this, args);
    if (segment && this.stack.length > 1) {
      const frame = this.frame;
      segment.callDepth = this.stack.length - 1;
      if (!frame.unit.external) {
        // A program or M97 block of the same file: the main editor keeps the calling row and
        // the call pane shows the called rows.
        const label = inFileLabel(this.stack[this.stack.length - 1]);
        segment.sourceUnit = label;
        segment.sourceLine = frame.index + 1;
        segment.line = this.stack[1]?.callLine || segment.line;
        this.meimadUnits ||= new Map();
        if (!this.meimadUnits.has(label)) {
          this.meimadUnits.set(label, { name: label, text: null, path: null, location: "this program", inFile: true });
        }
      }
    }
    return segment;
  };
  prototype.buildModel = function meimadBuildModel(...args) {
    const model = buildModel.apply(this, args);
    model.meimadStops = this.meimadStops || [];
    model.meimadInFileUnits = [...(this.meimadUnits || new Map()).values()];
    return model;
  };
  millHooks = true;
  return millHooks;
}

// Lathe stops follow flat lines; a stop is before the first segment of a later flat line.
function latheStops(model, prepared) {
  const segments = model.segments || [];
  return prepared.stops.map((stop) => {
    let executionIndex = segments.findIndex((segment) => Number(segment.line) > stop.afterLine);
    if (executionIndex < 0) executionIndex = segments.length;
    return { executionIndex, kind: stop.kind, message: stop.message, line: stop.line, unit: stop.unit, unitLine: stop.unitLine };
  });
}

// The machine is resolved from the original program (translation removes the codes detection
// reads), so the interpreter is given the machine id; the model then reports the viewer's own
// selection ("auto" or an id) and reason, as the machine list in the viewer expects.
function parseModel(request, choice) {
  const machine = machineFor(String(request.text || ""), choice.selection);
  if (machine?.type === "mill") installMillHooks();
  const prepared = prepareSource(request, machine);
  const lathe = machine?.type === "lathe";
  const model = parseProgramForMachine(prepared.text, {
    registry: registry(),
    machineSelection: machine ? machine.id : choice.selection,
    settings: runtimeSettings(request),
    machineParameters: lathe
      ? latheParameters(machineParameters(request.machineParametersText), prepared.text.split("\n").length)
      : machineParameters(request.machineParametersText),
    machineParameterPath: request.machineParameterPath || undefined,
    toolTable: request.toolTable || undefined,
    workOffsets: sanitizeWorkOffsets(request.workOffsets || {}, registry()),
    blockDelete: request.blockDelete === true,
    subprogramResolverFor: subprogramResolverFor(request, prepared.units)
  });
  const stops = lathe ? latheStops(model, prepared) : (model.meimadStops || []);
  const remappedStops = stops.map((stop) => (prepared.changed && !lathe
    ? { ...stop, line: remapLine(prepared.map, stop.line) }
    : stop));
  for (const unit of model.meimadInFileUnits || []) {
    if (!prepared.units.has(unit.name)) prepared.units.set(unit.name, unit);
  }
  delete model.meimadInFileUnits;
  const requested = String(request.machineSelection || AUTO_MACHINE_ID);
  const explicit = requested !== AUTO_MACHINE_ID && registry().machines.has(requested);
  if (model.machineSelection) {
    model.machineSelection.selection = explicit ? requested : AUTO_MACHINE_ID;
    model.machineSelection.detected = !explicit;
    model.machineSelection.reason = choice.reason || model.machineSelection.reason;
  }
  remapModel(model, prepared);
  model.meimadStops = remappedStops;
  model.meimadUnits = Object.fromEntries(prepared.units);
  model.meimadPrints = prepared.prints;
  model.meimad = prepared;
  return model;
}

function messageText(entry) {
  const text = typeof entry === "string" ? entry : entry?.message;
  return String(text ?? "").slice(0, MAX_MESSAGE_CHARACTERS);
}

function messages(list) {
  return [...new Set((Array.isArray(list) ? list : []).map(messageText).filter(Boolean))]
    .slice(0, MAX_REPORTED_MESSAGES);
}

function polylineLength(points, lathe) {
  let length = 0;
  for (let index = 1; index < (points || []).length; index += 1) {
    const previous = points[index - 1];
    const point = points[index];
    const segment = lathe
      ? Math.hypot((point.z || 0) - (previous.z || 0), ((point.x || 0) - (previous.x || 0)) / 2)
      : Math.hypot((point.x || 0) - (previous.x || 0), (point.y || 0) - (previous.y || 0),
        (point.z || 0) - (previous.z || 0));
    if (Number.isFinite(segment)) length += segment;
  }
  return length;
}

// Interpretation notes for the Meimad NC dialect on the resolved machine.
function dialectNotes(request, machine) {
  const dialect = String(request.dialect || "").toUpperCase();
  const notes = [];
  if (dialect === "OKUMA_OSP" && machine?.controlDefinition?.translation !== "okuma-osp-lathe") {
    notes.push(machine?.type === "mill"
      ? "OKUMA_OSP mill programs are interpreted with the FANUC mill interpreter; OSP-only syntax (VC variables, CALL, named sequence labels) is not simulated and its motion is not timed."
      : "OKUMA_OSP programs are interpreted with the FANUC-family interpreters; select the Okuma OSP lathe as the NC viewer machine to translate OSP syntax (LAP cycles, CALL/RTS, VC variables).");
  }
  return notes;
}

function analyze(requestJson) {
  const request = JSON.parse(requestJson);
  const choice = resolveSelection(request);
  const model = parseModel(request, choice);
  const prepared = model.meimad;
  const meta = model.meta || {};
  const lathe = model.kind !== "mill";
  const unitScale = meta.units === "inch" ? MM_PER_INCH : 1;
  let feedSeconds = 0;
  let rapidDistance = 0;
  let engineRapidSeconds = 0;
  for (const segment of model.segments || []) {
    const dwellAfter = lathe ? 0 : Number(segment.dwellAfterSeconds) || 0;
    const seconds = Number.isFinite(segment.estimatedSeconds)
      ? Math.max(0, segment.estimatedSeconds - dwellAfter)
      : undefined;
    if (RAPID_KINDS.has(segment.kind)) {
      rapidDistance += polylineLength(segment.points, lathe) * unitScale;
      if (seconds !== undefined) engineRapidSeconds += seconds;
    } else if (seconds !== undefined) {
      feedSeconds += seconds;
    }
  }
  const machine = registry().machines.get(model.machineDefinition?.id);
  const notes = [...dialectNotes(request, machine), ...prepared.notes];
  let dwellSeconds;
  let toolChangeCount;
  let engineToolChangeSeconds;
  if (lathe) {
    dwellSeconds = prepared.dwellSeconds;
    toolChangeCount = Number(meta.turretIndexCount) || 0;
    engineToolChangeSeconds = Number(meta.turretSeconds) || 0;
  } else {
    engineToolChangeSeconds = Number(meta.toolChangeSeconds) || 0;
    dwellSeconds = Math.max(0, (Number(meta.dwellSeconds) || 0) - engineToolChangeSeconds);
    toolChangeCount = Number(meta.toolChangeCount) || 0;
  }
  const errors = messages(model.errors);
  const warnings = messages([...notes, ...(model.warnings || [])]);
  return JSON.stringify({
    machineId: model.machineDefinition?.id || null,
    machineName: model.machineDefinition?.name || null,
    machineType: model.machineDefinition?.type || null,
    interpreter: model.machineDefinition?.control?.interpreter || null,
    translation: prepared.translation,
    selectionReason: choice.reason || model.machineSelection?.reason || null,
    kind: model.kind || null,
    units: meta.units || "mm",
    lineCount: Number(meta.lineCount) || 0,
    executedBlockCount: Number(meta.executedBlockCount) || 0,
    segmentCount: Number(meta.segmentCount) || (model.segments || []).length,
    feedSeconds,
    rapidDistanceMillimeters: rapidDistance,
    engineRapidSeconds,
    toolChangeCount,
    engineToolChangeSeconds,
    dwellSeconds,
    engineEstimatedCycleSeconds: Number.isFinite(meta.estimatedCycleSeconds) ? meta.estimatedCycleSeconds : null,
    unestimatedSegmentCount: Number(meta.unestimatedSegmentCount) || 0,
    resourceLimited: errors.concat(warnings).some((message) =>
      /resource limit|stopped after|endless loop|stopped at 200000/i.test(message)),
    subprograms: prepared.external.map((entry) => entry.name),
    warnings,
    errors
  });
}

function aiStatusForMeimad() {
  return { state: "manual", message: "Program comments and Meimad tool table" };
}

// The vendored sidebar lists program memory for mills only; lathes get the same rows here.
function latheMemoryRows(request, machine, prepared) {
  const memory = machine && request.programMemory ? request.programMemory[machine.id] : undefined;
  const folder = typeof memory === "string" && memory.trim() ? memory.trim() : undefined;
  const rows = [
    ["Program memory", folder || "Not set (Settings: program memory folder)", folder ? "path" : undefined]
  ];
  if (prepared.external.length) {
    rows.push(["Subprograms", prepared.external.map((entry) => `${entry.name} (${entry.location})`).join("; ")]);
  }
  if (prepared.translation) rows.push(["Translation", `${prepared.translation} (Meimad)`]);
  return rows;
}

function parsePreview(requestJson) {
  const request = JSON.parse(requestJson);
  const choice = resolveSelection(request);
  const model = parseModel(request, choice);
  const prepared = model.meimad;
  delete model.meimad;
  const machine = registry().machines.get(model.machineDefinition?.id);
  if (!installMillHooks() && model.kind === "mill") {
    model.warnings = ["Program stops and called-program panes are unavailable: the mill interpreter could not be hooked.", ...(model.warnings || [])];
  }
  model.toolTable = model.toolTable || {};
  model.toolTable.sourcePath = request.toolTableSourcePath || model.toolTable.sourcePath;
  model.toolTable.aiStatus = aiStatusForMeimad();
  const extraWarnings = [
    ...dialectNotes(request, machine),
    ...prepared.notes,
    ...(Array.isArray(request.resourceWarnings) ? request.resourceWarnings : [])
  ];
  if (extraWarnings.length) model.warnings = [...extraWarnings, ...(model.warnings || [])];
  if (model.kind !== "mill" && Array.isArray(model.machineRows)) {
    model.machineRows = [...model.machineRows, ...latheMemoryRows(request, machine, prepared)];
  }
  lastPacked = transport.stringify(model);
  const definition = model.machineDefinition;
  return JSON.stringify({
    kind: model.kind || null,
    segmentCount: Number(model.meta?.segmentCount) || (model.segments || []).length,
    compensationIssues: (model.compensationIssues || [])
      .filter((issue) => Number.isFinite(issue?.line) && !issue.summary)
      .map((issue) => ({ line: Math.trunc(issue.line), message: String(issue.message || "") })),
    machine: definition ? { id: definition.id, name: definition.name, type: definition.type } : null,
    selectionReason: choice.reason || model.machineSelection?.reason || null,
    settings: runtimeSettings(request),
    estimatedCycleSeconds: Number.isFinite(model.meta?.estimatedCycleSeconds) ? model.meta.estimatedCycleSeconds : null,
    errorCount: (model.errors || []).length,
    warningCount: (model.warnings || []).length
  });
}

function takePacked() {
  const packed = lastPacked;
  lastPacked = "";
  return packed;
}

function activeMachine(request) {
  const choice = resolveSelection(request);
  return machineFor(String(request.text || ""), choice.selection);
}

function tableForDefinitions(source, documentName, existingTable, definitions) {
  const unitCommands = [...String(source).matchAll(/\bG\s*(20|21)\b/gi)];
  const lastUnit = unitCommands.length ? unitCommands[unitCommands.length - 1][1] : undefined;
  const stem = String(documentName || "Untitled program").replace(/\.[^.\\/]*$/, "");
  return {
    schemaVersion: existingTable?.schemaVersion || "1.0",
    name: existingTable?.name || `${stem} tools`,
    units: existingTable?.units || (lastUnit === "20" ? "inch" : "mm"),
    autoGenerated: existingTable?.autoGenerated ?? true,
    tools: Object.fromEntries(definitions.map((definition) => [definition.number, definition]))
  };
}

function editableTable(table, machine, documentName) {
  const machineType = machine?.type === "mill" ? "mill" : "lathe";
  return {
    schemaVersion: table.schemaVersion || "1.0",
    name: table.name || `${documentName || "Untitled.NC"} tools`,
    units: table.units === "inch" ? "inch" : "mm",
    machineType,
    toolTypes: machineType === "mill" ? MILL_TOOL_TYPES : LATHE_TOOL_TYPES,
    tools: Object.values(table.tools || {})
      .sort((left, right) => Number(left.number) - Number(right.number))
      .map((tool) => ({
        number: Number(tool.number),
        cornerRadius: Number(tool.cornerRadius) || 0,
        tip: Number.isInteger(tool.tip) ? tool.tip : 0,
        type: TOOL_TYPES.has(tool.type) ? tool.type : "other",
        diameter: Number.isFinite(tool.diameter) ? tool.diameter : null,
        width: Number.isFinite(tool.width) ? tool.width : null,
        length: Number.isFinite(tool.length) ? tool.length : null,
        hand: TOOL_HANDS.has(tool.hand) ? tool.hand : "unknown",
        description: String(tool.description || "")
      }))
  };
}

// Tool words as the interpreter sees them (Okuma six-digit T words become four digits).
function toolInferenceText(request, machine) {
  return dialects.translate(String(request.text || ""), machine).lines.join("\n");
}

// Infers the program's tools, keeping values from request.toolTable (manual edits win).
function toolTable(requestJson) {
  const request = JSON.parse(requestJson);
  const machine = activeMachine(request);
  const base = request.toolTable || undefined;
  const definitions = toolDefinitionsForMachine(toolInferenceText(request, machine), base, machine);
  const data = tableForDefinitions(request.text, request.documentName, base, definitions);
  const xml = serializeToolTable(data, request.documentName || "");
  const table = parseToolTable(xml);
  return JSON.stringify({ table, xml, editable: editableTable(table, machine, request.documentName) });
}

// Tool types whose description may name the diameter as a bare number after the tool words.
const BARE_DIAMETER_TYPES = new Set(["end-mill", "ball-mill", "bull-nose-mill", "face-mill", "slot-mill", "drill", "reamer", "boring-head"]);

// Tool definitions read from released tool-table rows ({ number, description }) with the same
// heuristics the tool-change comments get, so the Operation's tool table replaces the program
// comments as the source of type, diameter and nose radius. A bare "D10" or "8.5MM" in a CAM
// description also counts as the diameter.
function toolDefinitionsFromRows(requestJson) {
  const request = JSON.parse(requestJson);
  const machine = activeMachine(request);
  const lathe = machine?.type === "lathe";
  const rows = (Array.isArray(request.rows) ? request.rows : [])
    .filter((row) => Number.isInteger(row.number) && row.number >= 1 && row.number <= 999);
  const lines = rows.map((row) => {
    const description = String(row.description || "").replace(/[()]/g, " ").replace(/\s+/g, " ").trim();
    const number = String(row.number).padStart(2, "0");
    const word = lathe ? `T${number}${number}` : `T${row.number} M06`;
    return description ? `${word} (${description})` : word;
  });
  const definitions = toolDefinitionsForMachine(lines.join("\n"), undefined, machine);
  const byNumber = new Map(definitions.map((tool) => [Number(tool.number), tool]));
  return JSON.stringify(rows.map((row) => {
    const tool = byNumber.get(row.number) || {};
    const type = TOOL_TYPES.has(tool.type) ? tool.type : "other";
    const text = String(row.description || "").toUpperCase().replace(/_/g, " ");
    let diameter = Number.isFinite(tool.diameter) ? tool.diameter : null;
    if (diameter === null) {
      const fallback = text.match(/(?:^|[\s(-])D\s*(\d*\.?\d+)(?![\d.])/) || text.match(/(\d*\.?\d+)\s*MM\b/) || text.match(/Ø\s*(\d*\.?\d+)/);
      if (fallback && Number(fallback[1]) > 0) diameter = Number(fallback[1]);
    }
    if (diameter === null && BARE_DIAMETER_TYPES.has(type)) {
      // The shop's CAM names put the diameter right after the tool words: "FIN 12 AROH L=105",
      // "BALL 6 R3", "MERASEK 12". Angles belong to chamfer and spot tools, which are excluded.
      const bare = text.match(/^[A-Z][A-Z .\/-]*\s(\d*\.?\d+)(?:\s|$)/);
      if (bare && Number(bare[1]) > 0) diameter = Number(bare[1]);
    }
    return {
      number: row.number,
      type,
      diameter,
      length: Number.isFinite(tool.length) && tool.length > 0 ? tool.length : null,
      cornerRadius: Number(tool.cornerRadius) || 0,
      tip: Number.isInteger(tool.tip) ? tool.tip : 0,
      width: Number.isFinite(tool.width) ? tool.width : null,
      description: String(row.description || "").trim()
    };
  }));
}

function textField(value, label, maximumLength) {
  if (typeof value !== "string") throw new TypeError(`${label} must be text.`);
  const normalized = value.trim();
  if (normalized.length > maximumLength) throw new Error(`${label} cannot exceed ${maximumLength} characters.`);
  return normalized;
}

function optionalSize(value, label, number) {
  if (value === null || value === undefined || value === "") return undefined;
  const size = Number(value);
  if (!Number.isFinite(size) || size < 0 || size > 100000) {
    throw new Error(`Tool T${number} has an invalid ${label}.`);
  }
  return size;
}

function saveToolTable(requestJson) {
  const request = JSON.parse(requestJson);
  const payload = request.edited;
  const current = request.toolTable || { tools: {} };
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new TypeError("Invalid tool table payload.");
  const name = textField(payload.name, "Tool table name", 200) || `${request.documentName || "Untitled.NC"} tools`;
  if (payload.units !== "mm" && payload.units !== "inch") throw new Error('Tool table units must be "mm" or "inch".');
  if (!Array.isArray(payload.tools) || payload.tools.length > 999) {
    throw new TypeError("Tool table tools must be an array of at most 999 rows.");
  }
  const expected = new Set(Object.keys(current.tools || {}).map(Number));
  const seen = new Set();
  const tools = {};
  payload.tools.forEach((entry, index) => {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) throw new TypeError(`Tool row ${index + 1} is invalid.`);
    const number = Number(entry.number);
    if (!Number.isInteger(number) || number < 1 || number > 999) throw new Error(`Tool row ${index + 1} has an invalid tool number.`);
    if (!expected.has(number) || seen.has(number)) throw new Error(`Tool T${number} is not a unique active-program tool.`);
    seen.add(number);
    const cornerRadius = Number(entry.cornerRadius);
    if (!Number.isFinite(cornerRadius) || cornerRadius < 0 || cornerRadius > 10000) {
      throw new Error(`Tool T${number} has an invalid corner/nose radius.`);
    }
    const tip = entry.tip === undefined ? 0 : Number(entry.tip);
    if (!Number.isInteger(tip) || tip < 0 || tip > 9) throw new Error(`Tool T${number} TIP must be an integer from 0 through 9.`);
    if (typeof entry.type !== "string" || !TOOL_TYPES.has(entry.type)) throw new Error(`Tool T${number} has an unsupported type.`);
    const hand = entry.hand === undefined ? "neutral" : entry.hand;
    if (typeof hand !== "string" || !TOOL_HANDS.has(hand)) throw new Error(`Tool T${number} has an unsupported hand.`);
    tools[number] = {
      ...(current.tools?.[number] || {}),
      number,
      cornerRadius,
      tip,
      tipKnown: true,
      type: entry.type,
      diameter: optionalSize(entry.diameter, "diameter", number),
      width: optionalSize(entry.width, "width", number),
      length: optionalSize(entry.length, "length", number),
      hand,
      description: textField(entry.description, `Tool T${number} description`, 500),
      recognition: "manual",
      confidence: 1,
      evidence: "Manually edited in the Meimad NC viewer tool table editor.",
      locked: true
    };
  });
  if (seen.size !== expected.size) throw new Error("The editor must retain every tool used by the active NC program.");
  const xml = serializeToolTable({
    schemaVersion: current.schemaVersion || "1.0",
    name,
    units: payload.units,
    autoGenerated: false,
    tools
  }, request.documentName || "");
  const table = parseToolTable(xml);
  return JSON.stringify({ table, xml, editable: editableTable(table, activeMachine(request), request.documentName) });
}

// Every machine the viewer can select, with its control's translation (if any).
function machines() {
  const translations = new Map([...registry().machines.values()]
    .map((machine) => [machine.id, machine.controlDefinition?.translation || null]));
  return JSON.stringify(machineSummaries(registry()).map((summary) => ({
    ...summary,
    translation: translations.get(summary.id) || null
  })));
}

// A tool table XML file (the viewer's optional fallback table) as the engine's table object.
function parseToolTableXml(xml) {
  return JSON.stringify(parseToolTable(String(xml || "")));
}

function sanitizeOffsets(json) {
  return JSON.stringify(sanitizeWorkOffsets(JSON.parse(json || "{}"), registry()) || {});
}

// The interpreter's view of a program (tests and diagnostics): translated text with its line map.
function prepare(requestJson) {
  const request = JSON.parse(requestJson);
  const choice = resolveSelection(request);
  const machine = machineFor(String(request.text || ""), choice.selection);
  const prepared = prepareSource(request, machine);
  return JSON.stringify({
    machineId: machine?.id || null,
    translation: prepared.translation,
    lines: prepared.text.split("\n"),
    map: prepared.map,
    notes: prepared.notes,
    subprograms: prepared.external,
    stops: prepared.stops,
    prints: prepared.prints
  });
}

module.exports = { analyze, parsePreview, takePacked, toolTable, toolDefinitionsFromRows, saveToolTable, machines, parseToolTableXml, sanitizeOffsets, prepare };
