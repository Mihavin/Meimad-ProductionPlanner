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
// translated first (meimad-dialects.js); lathe subprogram calls, which the lathe interpreter does
// not resolve, are inlined from the program folder or the machine's program memory folder
// (meimad-subprograms.js). Both keep a line map so every segment, issue and message reports the
// row of the original program.
//
// The tool-table rules mirror desktop/main.js of the upstream desktop application, which is
// not vendored because it is Electron code.

const path = require("node:path");
const { parseProgramForMachine, toolDefinitionsForMachine } = require("../src/program-model");
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
const subprograms = require("./meimad-subprograms");

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

function subprogramResolverFor(request) {
  return (machine) => {
    const memory = machine && request.programMemory ? request.programMemory[machine.id] : undefined;
    return createSubprogramResolver(request.documentDirectory || undefined, {
      memoryFolders: typeof memory === "string" && memory.trim() ? [memory] : []
    });
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
// move to X1.5. A dwell-only block becomes a comment (same line), and staticLatheDwellSeconds
// adds the dwell time from the text before this change.
function neutralizeLatheDwell(lines) {
  return lines.map((line) => {
    const code = line.replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "");
    return LATHE_DWELL_BLOCK.test(code) ? `(G04 DWELL ${code.trim().replace(/[()]/g, "")})` : line;
  });
}

// The lathe interpreter does not time G04. Count programmed dwells (X/U seconds, P
// milliseconds) the way the previous Meimad parser did, outside comments.
function staticLatheDwellSeconds(lines) {
  let seconds = 0;
  for (const raw of lines) {
    const code = raw.replace(/\([^)]*\)?/g, " ").replace(/;.*$/, "").toUpperCase();
    if (!/\bG0*4(?![0-9.])/.test(code)) continue;
    const word = (letter) => {
      const match = new RegExp(`${letter}\\s*([+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))`).exec(code);
      return match ? Number(match[1]) : undefined;
    };
    const direct = word("X") ?? word("U");
    const milliseconds = word("P");
    if (Number.isFinite(direct) && direct >= 0) seconds += direct;
    else if (Number.isFinite(milliseconds) && milliseconds >= 0) seconds += milliseconds / 1000;
  }
  return seconds;
}

function identityMap(map) {
  return map.every((entry, index) => entry.line === index + 1 && !entry.unit);
}

// The program as the interpreter reads it: translated for the machine's control, with lathe
// subprograms inlined and lathe dwells neutralized. Returns { text, map, notes, lineCount,
// dwellSeconds, external, translation }.
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
  if (machine?.type === "lathe") {
    const resolve = subprogramResolverFor(request)(machine);
    const expanded = subprograms.expand({ lines, map }, resolve, (text) => dialects.translate(text, machine).lines);
    lines = expanded.lines;
    map = expanded.map;
    external = expanded.external;
    notes.push(...expanded.notes);
    dwellSeconds = staticLatheDwellSeconds(lines);
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
    translation
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

// The machine is resolved from the original program (translation removes the codes detection
// reads), so the interpreter is given the machine id; the model then reports the viewer's own
// selection ("auto" or an id) and reason, as the machine list in the viewer expects.
function parseModel(request, choice) {
  const machine = machineFor(String(request.text || ""), choice.selection);
  const prepared = prepareSource(request, machine);
  const model = parseProgramForMachine(prepared.text, {
    registry: registry(),
    machineSelection: machine ? machine.id : choice.selection,
    settings: runtimeSettings(request),
    machineParameters: machineParameters(request.machineParametersText),
    machineParameterPath: request.machineParameterPath || undefined,
    toolTable: request.toolTable || undefined,
    workOffsets: sanitizeWorkOffsets(request.workOffsets || {}, registry()),
    subprogramResolverFor: subprogramResolverFor(request)
  });
  const requested = String(request.machineSelection || AUTO_MACHINE_ID);
  const explicit = requested !== AUTO_MACHINE_ID && registry().machines.has(requested);
  if (model.machineSelection) {
    model.machineSelection.selection = explicit ? requested : AUTO_MACHINE_ID;
    model.machineSelection.detected = !explicit;
    model.machineSelection.reason = choice.reason || model.machineSelection.reason;
  }
  remapModel(model, prepared);
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
    if (dwellSeconds > 0) {
      notes.push("Lathe G04 dwell is counted from the program text (loops and macro branches are not expanded for dwell).");
    }
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
    subprograms: prepared.external
  });
}

module.exports = { analyze, parsePreview, takePacked, toolTable, toolDefinitionsFromRows, saveToolTable, machines, parseToolTableXml, sanitizeOffsets, prepare };
