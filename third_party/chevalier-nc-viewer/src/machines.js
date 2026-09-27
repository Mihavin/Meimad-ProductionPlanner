"use strict";

const fs = require("node:fs");
const path = require("node:path");
const kinematics = require("../media/kinematics");

const PACKAGE_ROOT = path.join(__dirname, "..");
const BUILT_IN_MACHINE_FOLDER = path.join(PACKAGE_ROOT, "machines");
const BUILT_IN_CONTROL_FOLDER = path.join(PACKAGE_ROOT, "controls");
const DEFAULT_MACHINE_ID = "chevalier-flc-200mc";
const AUTO_MACHINE_ID = "auto";
const MACHINE_TYPES = new Set(["lathe", "mill"]);
const INTERPRETERS = new Set(["fanuc-lathe", "haas-mill", "fanuc-mill"]);
const MAX_DEFINITION_BYTES = 2 * 1024 * 1024;

function readJsonFile(filePath) {
  const stat = fs.statSync(filePath);
  if (stat.size > MAX_DEFINITION_BYTES) {
    throw new Error(`${path.basename(filePath)} is larger than ${MAX_DEFINITION_BYTES} bytes`);
  }
  try {
    return JSON.parse(fs.readFileSync(filePath, "utf8"));
  } catch (error) {
    throw new Error(`${path.basename(filePath)}: ${error.message}`);
  }
}

function jsonFiles(folder) {
  if (!folder || !fs.existsSync(folder) || !fs.statSync(folder).isDirectory()) {
    return [];
  }
  return fs.readdirSync(folder)
    .filter((name) => name.toLowerCase().endsWith(".json"))
    .sort()
    .map((name) => path.join(folder, name));
}

function codeNumber(code) {
  const match = String(code || "").trim().toUpperCase().match(/^([GM])(\d+(?:\.\d+)?)$/);
  return match ? { letter: match[1], number: Number(match[2]) } : undefined;
}

function expandCodeRange(code) {
  const match = String(code || "").trim().toUpperCase()
    .match(/^([GM])(\d+)\s*-\s*[GM]?(\d+)$/);
  if (!match) {
    return undefined;
  }
  return { letter: match[1], first: Number(match[2]), last: Number(match[3]) };
}

function validateControl(control, sourceName) {
  const problems = [];
  if (!control || typeof control !== "object" || Array.isArray(control)) {
    throw new Error(`${sourceName}: control definition must be a JSON object`);
  }
  if (typeof control.id !== "string" || !/^[a-z0-9][a-z0-9-]*$/.test(control.id)) {
    problems.push("id must be lower-case letters, digits and hyphens");
  }
  if (typeof control.name !== "string" || !control.name.trim()) {
    problems.push("name is required");
  }
  if (!INTERPRETERS.has(control.interpreter)) {
    problems.push(`interpreter must be one of ${[...INTERPRETERS].join(", ")}`);
  }
  if (!MACHINE_TYPES.has(control.machineType)) {
    problems.push(`machineType must be one of ${[...MACHINE_TYPES].join(", ")}`);
  }
  for (const listName of ["gCodes", "mCodes"]) {
    if (!Array.isArray(control[listName])) {
      problems.push(`${listName} must be an array`);
      continue;
    }
    control[listName].forEach((entry, index) => {
      if (!entry || typeof entry.code !== "string" || typeof entry.name !== "string") {
        problems.push(`${listName}[${index}] needs code and name`);
      } else if (!codeNumber(entry.code) && !expandCodeRange(entry.code)) {
        problems.push(`${listName}[${index}] has invalid code "${entry.code}"`);
      }
    });
  }
  if (problems.length) {
    throw new Error(`${sourceName}: ${problems.join("; ")}`);
  }
  return control;
}

function resolveReference(value, machine, fallback) {
  if (value === "mrzp") {
    const mrzp = machine.mrzp || {};
    return [Number(mrzp.x) || 0, Number(mrzp.y) || 0, Number(mrzp.z) || 0];
  }
  if (Array.isArray(value) && value.length === 3 && value.every(Number.isFinite)) {
    return value.slice();
  }
  return fallback ? fallback.slice() : undefined;
}

function resolveKinematicNodes(machine) {
  const rotaryCenterDistance = Number(machine.mrzp?.rotaryCenterDistance) || 0;
  return (machine.kinematics?.nodes || []).map((node) => {
    const resolved = { ...node };
    if (node.pivot !== undefined) {
      resolved.pivot = resolveReference(node.pivot, machine, [0, 0, 0]);
      // Setting 254 moves the tilt axis away from the rotary (C) centre line
      // along X; the rotary axis itself stays on the MRZP.
      if (node.pivot === "mrzp" && node.type === "rotary" && node.direction?.[2] === 0) {
        resolved.pivot[0] += rotaryCenterDistance;
      }
    }
    if (node.geometry && typeof node.geometry === "object") {
      const geometry = { ...node.geometry };
      const center = resolveReference(geometry.center, machine, [0, 0, 0]);
      const offset = resolveReference(geometry.offset, machine, [0, 0, 0]);
      geometry.center = center.map((value, index) => value + offset[index]);
      delete geometry.offset;
      resolved.geometry = geometry;
    }
    return resolved;
  });
}

function validateMachine(machine, controls, sourceName) {
  const problems = [];
  if (!machine || typeof machine !== "object" || Array.isArray(machine)) {
    throw new Error(`${sourceName}: machine definition must be a JSON object`);
  }
  if (typeof machine.id !== "string" || !/^[a-z0-9][a-z0-9-]*$/.test(machine.id) ||
      machine.id === AUTO_MACHINE_ID) {
    problems.push("id must be lower-case letters, digits and hyphens (and not \"auto\")");
  }
  if (typeof machine.name !== "string" || !machine.name.trim()) {
    problems.push("name is required");
  }
  if (!MACHINE_TYPES.has(machine.type)) {
    problems.push(`type must be one of ${[...MACHINE_TYPES].join(", ")}`);
  }
  const control = controls.get(machine.control);
  if (!control) {
    problems.push(`control "${machine.control}" is not defined`);
  } else if (control.machineType !== machine.type) {
    problems.push(`control "${control.id}" is for ${control.machineType} machines`);
  }
  for (const listName of ["gCodes", "mCodes"]) {
    if (machine[listName] === undefined) continue;
    if (!Array.isArray(machine[listName])) {
      problems.push(`${listName} must be an array of machine-specific codes`);
      continue;
    }
    machine[listName].forEach((entry, index) => {
      if (!entry || typeof entry.code !== "string" || typeof entry.name !== "string" ||
          (!codeNumber(entry.code) && !expandCodeRange(entry.code))) {
        problems.push(`${listName}[${index}] needs a valid code and a name`);
      }
    });
  }
  if (machine.units !== "mm") {
    problems.push('units must be "mm" (machine data is stored in millimetres)');
  }
  const axes = machine.axes && typeof machine.axes === "object" ? machine.axes : {};
  if (!Object.keys(axes).length) {
    problems.push("axes are required");
  }
  for (const [name, axis] of Object.entries(axes)) {
    if (!/^[A-Z]$/.test(name)) {
      problems.push(`axis "${name}" must be one capital letter`);
    }
    if (!axis || (axis.type !== "linear" && axis.type !== "rotary")) {
      problems.push(`axis ${name} type must be linear or rotary`);
      continue;
    }
    for (const bound of ["min", "max"]) {
      if (axis[bound] !== null && axis[bound] !== undefined && !Number.isFinite(axis[bound])) {
        problems.push(`axis ${name} ${bound} must be a number or null`);
      }
    }
    if (Number.isFinite(axis.min) && Number.isFinite(axis.max) && axis.min > axis.max) {
      problems.push(`axis ${name} min is greater than max`);
    }
    if (axis.rapidRate !== undefined && !(Number(axis.rapidRate) > 0)) {
      problems.push(`axis ${name} rapidRate must be positive`);
    }
  }
  let nodes = [];
  try {
    nodes = resolveKinematicNodes(machine);
    const chain = kinematics.buildChain({ nodes });
    for (const axis of chain.axes) {
      if (!axes[axis]) {
        problems.push(`kinematic axis ${axis} is not listed in axes`);
      }
    }
  } catch (error) {
    problems.push(`kinematics: ${error.message}`);
  }
  if (problems.length) {
    throw new Error(`${sourceName}: ${problems.join("; ")}`);
  }
  return { control, nodes };
}

function loadRegistry({ machineFolders = [], controlFolders = [] } = {}) {
  const controls = new Map();
  const machines = new Map();
  const errors = [];
  const origins = new Map();

  for (const folder of [BUILT_IN_CONTROL_FOLDER, ...controlFolders]) {
    for (const filePath of jsonFiles(folder)) {
      try {
        const control = validateControl(readJsonFile(filePath), path.basename(filePath));
        controls.set(control.id, Object.freeze({ ...control, sourcePath: filePath }));
      } catch (error) {
        errors.push(error.message);
      }
    }
  }

  for (const folder of [BUILT_IN_MACHINE_FOLDER, ...machineFolders]) {
    for (const filePath of jsonFiles(folder)) {
      try {
        const definition = readJsonFile(filePath);
        const { control, nodes } = validateMachine(definition, controls, path.basename(filePath));
        if (origins.has(definition.id) && folder === BUILT_IN_MACHINE_FOLDER) {
          errors.push(`${path.basename(filePath)}: duplicate machine id ${definition.id}`);
          continue;
        }
        origins.set(definition.id, filePath);
        machines.set(definition.id, Object.freeze({
          ...definition,
          kinematics: { ...definition.kinematics, nodes },
          controlDefinition: control,
          sourcePath: filePath,
          builtIn: folder === BUILT_IN_MACHINE_FOLDER
        }));
      } catch (error) {
        errors.push(error.message);
      }
    }
  }
  if (!machines.has(DEFAULT_MACHINE_ID) && machines.size) {
    errors.push(`Default machine ${DEFAULT_MACHINE_ID} is missing.`);
  }
  return { machines, controls, errors };
}

let builtInRegistry;
function defaultRegistry() {
  if (!builtInRegistry) {
    builtInRegistry = loadRegistry();
    if (builtInRegistry.errors.length) {
      throw new Error(`Built-in machine definitions are invalid: ${builtInRegistry.errors.join(" | ")}`);
    }
  }
  return builtInRegistry;
}

function stripCommentsForDetection(source) {
  return String(source || "")
    .split(/\r?\n/)
    .map((line) => line.replace(/\([^)]*\)/g, " ").replace(/;.*$/, " ").toUpperCase())
    .join("\n");
}

function explicitMachineTag(source, registry) {
  const match = String(source || "").match(/\(\s*MACHINE\s*[:=]\s*([^)]+)\)/i);
  if (!match) {
    return undefined;
  }
  const requested = match[1].trim().toLowerCase().replace(/[\s_]+/g, "-");
  for (const machine of registry.machines.values()) {
    const candidates = [
      machine.id,
      machine.name.toLowerCase().replace(/[\s_]+/g, "-"),
      String(machine.model || "").toLowerCase().replace(/[\s_]+/g, "-")
    ];
    if (candidates.some((candidate) => candidate && (requested === candidate || requested.endsWith(candidate)))) {
      return machine.id;
    }
  }
  return undefined;
}

// Scores program content for mill versus lathe dialects. The scores are only a
// hint: an explicit (MACHINE: ...) comment or the user's selection wins.
function detectMachineType(source) {
  const code = stripCommentsForDetection(source);
  const text = String(source || "").toUpperCase();
  const count = (expression) => (code.match(expression) || []).length;
  let mill = 0;
  let lathe = 0;
  const reasons = [];
  const add = (type, points, reason, hits) => {
    if (!hits) return;
    if (type === "mill") mill += points * Math.min(hits, 5);
    else lathe += points * Math.min(hits, 5);
    reasons.push({ type, reason });
  };
  add("mill", 3, "M06 tool change", count(/\bM0?6\b/g));
  add("mill", 3, "G43 H tool length", count(/\bG43\b[^\n]*\bH\s*\d/g));
  add("mill", 2, "Y-axis words", count(/(?:^|[^A-Z#])Y\s*[-+]?[\d.#[]/gm));
  add("mill", 2, "G17 plane", count(/\bG17\b/g));
  add("mill", 4, "Haas 5-axis codes", count(/\bG(?:234|254|255|268|269|253)\b/g));
  add("mill", 4, "FANUC 5-axis codes", count(/\bG0*(?:68\.[234]|53\.[16]|43\.[45]|54\.[124])(?![\d.])/g));
  add("mill", 2, "Haas work offsets/accuracy codes", count(/\bG(?:154|187|103|110|111|112)\b/g));
  add("mill", 2, "drilling cycle with R plane", count(/\bG(?:73|8[1-9])\b[^\n]*\bR\s*[-+]?[\d.]/g));
  add("mill", 1, "B/C rotary words", count(/(?:^|\s)[BC]\s*[-+]?\d/gm));
  add("mill", 2, "D/H offsets", count(/\bG4[12]\b[^\n]*\bD\s*\d/g));
  add("lathe", 3, "four-digit turret T code", count(/\bT\d{4}\b/g));
  add("lathe", 2, "U/W incremental words", count(/(?:^|[^A-Z#])[UW]\s*[-+]?[\d.]/gm));
  add("lathe", 2, "constant surface speed", count(/\bG96\b/g));
  add("lathe", 2, "G50 spindle clamp", count(/\bG50\b\s*S/g));
  add("lathe", 3, "G71/G72 stock removal with U/W", count(/\bG7[12]\b[^\n]*\b[UW]\s*[-+]?[\d.]/g));
  add("lathe", 2, "G76 six-digit P", count(/\bG76\b[^\n]*\bP\d{6}/g));
  add("lathe", 1, "G18 plane", count(/\bG18\b/g));
  if (/\bHAAS\b|\bUMC\b|\bVF-?\d/.test(text)) {
    mill += 4;
    reasons.push({ type: "mill", reason: "Haas mill comment" });
  }
  if (/STOCK\s+TURN|\bLATHE\b|CHEVALIER|\bFLC\b/.test(text)) {
    lathe += 4;
    reasons.push({ type: "lathe", reason: "lathe comment" });
  }
  return { mill, lathe, reasons };
}

function escapeRegExp(text) {
  return String(text).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function codePattern(code) {
  const parsed = codeNumber(code);
  if (!parsed) return undefined;
  const [whole, fraction] = String(parsed.number).split(".");
  return new RegExp(`\\b${parsed.letter}0*${whole}${fraction ? `\\.${fraction}` : ""}(?![\\d.])`, "g");
}

// Machine-specific evidence from a definition's "detection" block: keywords
// anywhere in the program (comments included) and codes in the NC words.
function machineHintScore(machine, source) {
  const hints = machine.detection || {};
  const text = String(source || "").toUpperCase();
  const code = stripCommentsForDetection(source);
  let score = 0;
  const reasons = [];
  for (const keyword of hints.keywords || []) {
    if (new RegExp(`\\b${escapeRegExp(String(keyword).toUpperCase())}`).test(text)) {
      score += 5;
      reasons.push(`"${keyword}" in the program`);
    }
  }
  const found = [];
  for (const entry of hints.codes || []) {
    const pattern = codePattern(entry);
    const hits = pattern ? (code.match(pattern) || []).length : 0;
    if (hits) {
      score += 2 * Math.min(hits, 5);
      found.push(String(entry).toUpperCase());
    }
  }
  if (found.length) {
    reasons.push(`${machine.controlDefinition?.shortName || machine.name} codes ${found.slice(0, 4).join(" ")}`);
  }
  return { score, reasons };
}

// Detection reads the start of the program: headers and the first operations
// identify the machine, and scanning megabytes of CAM output on every edit
// delayed the editor.
const DETECTION_SAMPLE_CHARACTERS = 256 * 1024;

function detectMachineForSource(fullSource, registry = defaultRegistry()) {
  const source = String(fullSource || "").slice(0, DETECTION_SAMPLE_CHARACTERS);
  const tagged = explicitMachineTag(source, registry);
  if (tagged) {
    return { id: tagged, confidence: 1, reason: "Program comment (MACHINE: ...)" };
  }
  const scores = detectMachineType(source);
  const preferred = scores.mill > scores.lathe ? "mill" : scores.lathe > scores.mill ? "lathe" : undefined;
  const fallback = registry.machines.get(DEFAULT_MACHINE_ID) || [...registry.machines.values()][0];
  if (!preferred) {
    return {
      id: fallback?.id,
      confidence: 0,
      reason: "No distinguishing mill or lathe codes; using the default machine"
    };
  }
  const candidates = [...registry.machines.values()].filter((machine) => machine.type === preferred);
  const ranked = candidates
    .map((candidate, index) => ({ candidate, index, ...machineHintScore(candidate, source) }))
    .sort((left, right) => right.score - left.score ||
      Number(Boolean(right.candidate.detection?.default)) - Number(Boolean(left.candidate.detection?.default)) ||
      Number(right.candidate.builtIn) - Number(left.candidate.builtIn) ||
      left.index - right.index);
  const best = ranked[0];
  const machine = best?.candidate || fallback;
  if (best?.score > 0) scores.reasons.unshift({ type: preferred, reason: best.reasons.join(", ") });
  const total = scores.mill + scores.lathe;
  const evidence = scores.reasons
    .filter((entry) => entry.type === preferred)
    .map((entry) => entry.reason)
    .slice(0, 4);
  return {
    id: machine?.id,
    confidence: total ? Math.abs(scores.mill - scores.lathe) / total : 0,
    reason: `Detected ${preferred} program: ${evidence.join(", ")}`
  };
}

function resolveMachineSelection(selection, source, registry = defaultRegistry()) {
  const requested = String(selection || AUTO_MACHINE_ID).trim() || AUTO_MACHINE_ID;
  if (requested !== AUTO_MACHINE_ID && registry.machines.has(requested)) {
    return {
      machine: registry.machines.get(requested),
      selection: requested,
      detected: false,
      reason: "Selected machine"
    };
  }
  const detection = detectMachineForSource(source, registry);
  const machine = registry.machines.get(detection.id) ||
    registry.machines.get(DEFAULT_MACHINE_ID) ||
    [...registry.machines.values()][0];
  return {
    machine,
    selection: AUTO_MACHINE_ID,
    detected: true,
    reason: requested !== AUTO_MACHINE_ID
      ? `Machine "${requested}" is not defined; ${detection.reason}`
      : detection.reason,
    confidence: detection.confidence
  };
}

function machineSummaries(registry = defaultRegistry()) {
  return [...registry.machines.values()]
    .map((machine) => ({
      id: machine.id,
      name: machine.name,
      type: machine.type,
      control: machine.controlDefinition?.name || machine.control,
      builtIn: machine.builtIn
    }))
    .sort((left, right) => left.name.localeCompare(right.name));
}

// JSON-safe machine description for the preview webview.
function publicMachine(machine) {
  if (!machine) {
    return undefined;
  }
  return {
    id: machine.id,
    name: machine.name,
    manufacturer: machine.manufacturer,
    model: machine.model,
    type: machine.type,
    description: machine.description,
    control: {
      id: machine.controlDefinition?.id || machine.control,
      name: machine.controlDefinition?.name || machine.control,
      interpreter: machine.controlDefinition?.interpreter
    },
    units: machine.units,
    axes: machine.axes,
    mrzp: machine.mrzp,
    rotaryConvention: machine.rotaryConvention,
    referencePositions: machine.referencePositions,
    spindle: machine.spindle,
    toolChanger: machine.toolChanger,
    workholding: machine.workholding,
    defaultToolLength: machine.defaultToolLength,
    kinematics: {
      type: machine.kinematics?.type,
      description: machine.kinematics?.description,
      nodes: (machine.kinematics?.nodes || []).map((node) => ({
        id: node.id,
        parent: node.parent,
        label: node.label,
        type: node.type,
        axis: node.axis,
        direction: node.direction,
        pivot: node.pivot,
        origin: node.origin,
        sense: node.sense,
        scale: node.scale,
        toolMount: Boolean(node.toolMount),
        workpieceMount: Boolean(node.workpieceMount),
        geometry: node.geometry || null,
        model: node.model || null
      }))
    },
    sourcePath: machine.sourcePath,
    builtIn: machine.builtIn
  };
}

// Machine-specific codes (for example the builder's M codes) are listed on
// the machine definition and take precedence over the control's.
function controlCodeEntry(control, word, machine) {
  const parsed = codeNumber(word);
  if (!control || !parsed) {
    return undefined;
  }
  const own = parsed.letter === "G" ? machine?.gCodes : machine?.mCodes;
  const list = [...(own || []), ...((parsed.letter === "G" ? control.gCodes : control.mCodes) || [])];
  for (const entry of list) {
    const exact = codeNumber(entry.code);
    if (exact && exact.letter === parsed.letter && exact.number === parsed.number) {
      return entry;
    }
    const range = expandCodeRange(entry.code);
    if (range && range.letter === parsed.letter &&
        parsed.number >= range.first && parsed.number <= range.last) {
      return entry;
    }
  }
  return undefined;
}

function knownCodeSet(control, letter, machine) {
  const numbers = new Set();
  const lists = letter === "G" ? [control?.gCodes, machine?.gCodes] : [control?.mCodes, machine?.mCodes];
  for (const entry of lists.flatMap((list) => list || [])) {
    const exact = codeNumber(entry.code);
    if (exact) {
      numbers.add(exact.number);
      continue;
    }
    const range = expandCodeRange(entry.code);
    if (range) {
      for (let number = range.first; number <= range.last; number += 1) {
        numbers.add(number);
      }
    }
  }
  return numbers;
}

function formatCodeDocumentation(control, entry, word) {
  if (!entry) {
    return undefined;
  }
  const group = entry.group === undefined
    ? ""
    : ` (group ${String(entry.group).padStart(2, "0")}${entry.group === 0 ? ", non-modal" : ""})`;
  const lines = [`**${String(word).toUpperCase()} - ${entry.name}**${group}`];
  const addresses = Object.entries(entry.addresses || {});
  if (addresses.length) {
    lines.push("", ...addresses.map(([address, meaning]) => `- \`${address}\` ${meaning}`));
  }
  if (entry.notes) {
    lines.push("", entry.notes);
  }
  lines.push("", `_${control.name}_`);
  return lines.join("\n");
}

// Saved home (work) offsets: { machineId: { "G54": { X: -300, ... }, "G54.1 P7": {...} } }
// in millimetres/degrees. Unknown machines, labels, axes and non-numbers are
// dropped; empty rows and machines are removed.
const WORK_OFFSET_LABEL = /^(EXT|G5[4-9]|G1[12]\d|G154 ?P\d{1,3}|G54\.1 ?P\d{1,3})$/;

function sanitizeWorkOffsets(input, registry = defaultRegistry()) {
  const result = {};
  if (!input || typeof input !== "object" || Array.isArray(input)) return result;
  for (const [machineId, rows] of Object.entries(input)) {
    const machine = registry.machines.get(machineId);
    if (!machine || machine.type !== "mill" || !rows || typeof rows !== "object") continue;
    const axes = new Set(Object.keys(machine.axes || {}));
    const clean = {};
    for (const [rawLabel, values] of Object.entries(rows)) {
      const label = String(rawLabel).toUpperCase().replace(/\s+/g, " ").trim();
      if (!WORK_OFFSET_LABEL.test(label) || !values || typeof values !== "object") continue;
      const row = {};
      for (const [rawAxis, value] of Object.entries(values)) {
        const axis = String(rawAxis).toUpperCase();
        const number = typeof value === "string" && !value.trim() ? NaN : Number(value);
        if (axes.has(axis) && Number.isFinite(number) && Math.abs(number) < 1e6) row[axis] = number;
      }
      if (Object.keys(row).length) clean[label] = row;
    }
    if (Object.keys(clean).length) result[machineId] = clean;
  }
  return result;
}

module.exports = {
  sanitizeWorkOffsets,
  AUTO_MACHINE_ID,
  DEFAULT_MACHINE_ID,
  controlCodeEntry,
  defaultRegistry,
  detectMachineForSource,
  detectMachineType,
  formatCodeDocumentation,
  knownCodeSet,
  loadRegistry,
  machineSummaries,
  publicMachine,
  resolveMachineSelection
};
